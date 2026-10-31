//! The agent channel: one long-lived mutual-TLS gRPC stream to the server,
//! reconnected with capped exponential backoff and jitter.
//!
//! Per connection: send `Hello`, wait for `Welcome`, renew the certificate if
//! due, resend retained logs and every unacknowledged result, then heartbeat,
//! execute assignments, and stream logs and results until the stream ends.

use std::sync::Arc;
use std::time::{Duration, Instant, SystemTime};

use anyhow::{Context, Result, anyhow, bail};
use octopushy_proto::v1::{
    AgentMessage, Heartbeat, Hello, ServerMessage, agent_channel_client::AgentChannelClient,
    agent_message::Kind as Out, server_message::Kind as In,
};
use tokio::sync::mpsc;
use tokio_stream::wrappers::ReceiverStream;
use tokio_util::sync::CancellationToken;
use url::Url;

use crate::attempts::{AttemptManager, SessionCursor};
use crate::identity::{self, Identity};
use crate::paths::StatePaths;
use crate::tools::RuntimeKind;
use crate::{renewal, tls};

/// Largest gRPC message either way. Assignments carry repository files and
/// results carry saved plans, which exceed gRPC's 4 MiB default.
pub const MAX_MESSAGE_BYTES: usize = 64 * 1024 * 1024;
const WELCOME_TIMEOUT: Duration = Duration::from_secs(30);
const FLUSH_INTERVAL: Duration = Duration::from_millis(500);
const RENEWAL_CHECK_INTERVAL: Duration = Duration::from_secs(600);
/// How long a draining agent waits for the last acknowledgements.
const DRAIN_ACK_GRACE: Duration = Duration::from_secs(10);

/// What a connection needs.
pub struct Agent {
    pub paths: StatePaths,
    pub manager: Arc<AttemptManager>,
    pub tool_runtime: RuntimeKind,
    /// Executors usable with the runtime probed at startup.
    pub executors: Vec<String>,
    /// Features usable with the runtime probed at startup.
    pub capabilities: Vec<String>,
}

enum End {
    /// The certificate was renewed; reconnect at once with the new identity.
    Renewed,
    /// The server ended the stream.
    Closed,
    /// Shutdown finished.
    Done,
}

/// Capped exponential backoff with jitter: 1 s doubling up to 60 s, each delay
/// drawn from its upper half.
pub struct Backoff {
    failures: u32,
    base: Duration,
    cap: Duration,
}

impl Backoff {
    pub fn new(base: Duration, cap: Duration) -> Self {
        Self {
            failures: 0,
            base,
            cap,
        }
    }

    pub fn reset(&mut self) {
        self.failures = 0;
    }

    pub fn next_delay(&mut self) -> Duration {
        let exp = self.base.saturating_mul(1u32 << self.failures.min(16));
        let delay = exp.min(self.cap);
        self.failures = self.failures.saturating_add(1);
        let half = delay / 2;
        half + half.mul_f64(fastrand::f64())
    }
}

/// Runs connections until `shutdown` is cancelled and the agent has drained.
pub async fn run(agent: Arc<Agent>, shutdown: CancellationToken) -> Result<()> {
    let mut backoff = Backoff::new(Duration::from_secs(1), Duration::from_secs(60));
    let mut draining = false;
    loop {
        let identity = identity::load(&agent.paths)?;
        if identity.cert.is_expired(SystemTime::now()) {
            tracing::error!(
                "the agent certificate has expired; enroll again with `octopushy agent enroll --force`"
            );
        }
        let started = Instant::now();
        match session(&agent, &identity, &shutdown, &mut draining).await {
            Ok(End::Renewed) => {
                backoff.reset();
                continue;
            }
            Ok(End::Done) => return Ok(()),
            Ok(End::Closed) => tracing::warn!("the server closed the agent channel"),
            Err(e) => tracing::warn!("agent channel: {e:#}"),
        }
        if started.elapsed() > Duration::from_secs(30) {
            backoff.reset();
        }
        if draining && agent.manager.is_idle() {
            tracing::info!(
                "shutting down while disconnected; {} unacknowledged results stay in the outbox",
                agent.manager.unacknowledged()
            );
            return Ok(());
        }
        let delay = backoff.next_delay();
        tracing::info!("reconnecting in {:.1}s", delay.as_secs_f64());
        tokio::select! {
            _ = tokio::time::sleep(delay) => {}
            _ = shutdown.cancelled(), if !draining => {
                draining = true;
                agent.manager.drain();
            }
        }
    }
}

fn hello(agent: &Agent) -> AgentMessage {
    AgentMessage {
        kind: Some(Out::Hello(Hello {
            agent_version: crate::VERSION.to_owned(),
            executors: agent.executors.clone(),
            running_attempt_ids: agent.manager.active_ids(),
            tool_runtime: agent.tool_runtime.as_str().to_owned(),
            capabilities: agent.capabilities.clone(),
        })),
    }
}

async fn send_all(tx: &mpsc::Sender<AgentMessage>, messages: Vec<AgentMessage>) -> Result<()> {
    for m in messages {
        tx.send(m)
            .await
            .map_err(|_| anyhow!("the outgoing stream closed"))?;
    }
    Ok(())
}

async fn session(
    agent: &Agent,
    identity: &Identity,
    shutdown: &CancellationToken,
    draining: &mut bool,
) -> Result<End> {
    let server =
        Url::parse(&identity.record.server_url).context("invalid server URL in agent.json")?;
    let endpoint = tls::endpoint(
        &server,
        &identity.ca_pem,
        Some((&identity.cert_pem, &identity.key_pem)),
    )?;
    let channel = endpoint.connect().await.map_err(|e| {
        anyhow!(
            "connecting to {server}: {}",
            tls::describe_connect_error(&e)
        )
    })?;
    let mut client = AgentChannelClient::new(channel.clone())
        .max_decoding_message_size(MAX_MESSAGE_BYTES)
        .max_encoding_message_size(MAX_MESSAGE_BYTES);

    let (tx, rx) = mpsc::channel::<AgentMessage>(256);
    tx.send(hello(agent)).await.expect("the receiver is alive");
    let mut inbound = client
        .connect(ReceiverStream::new(rx))
        .await
        .map_err(|s| {
            anyhow!(
                "the server refused the agent channel: {} ({:?})",
                s.message(),
                s.code()
            )
        })?
        .into_inner();

    let welcome = match tokio::time::timeout(WELCOME_TIMEOUT, inbound.message()).await {
        Ok(Ok(Some(ServerMessage {
            kind: Some(In::Welcome(w)),
        }))) => w,
        Ok(Ok(Some(other))) => bail!("expected Welcome, got {other:?}"),
        Ok(Ok(None)) => bail!("the server closed the stream before Welcome"),
        Ok(Err(s)) => bail!("stream error before Welcome: {}", s.message()),
        Err(_) => bail!("no Welcome within {}s", WELCOME_TIMEOUT.as_secs()),
    };
    if !welcome.agent_id.is_empty() && welcome.agent_id != identity.record.agent_id {
        tracing::warn!(
            "the server calls this agent {}, but it enrolled as {}",
            welcome.agent_id,
            identity.record.agent_id
        );
    }
    let max = welcome.max_concurrency.max(1);
    agent.manager.set_concurrency(max);
    let heartbeat_every = Duration::from_secs(match welcome.heartbeat_seconds {
        0 => 10,
        n => u64::from(n),
    });
    tracing::info!(
        agent_id = %identity.record.agent_id, %server, max_concurrency = max,
        heartbeat_seconds = heartbeat_every.as_secs(), "connected"
    );

    if identity.cert.needs_renewal(SystemTime::now()) {
        match renewal::renew(channel.clone(), &agent.paths, identity).await {
            Ok(()) => return Ok(End::Renewed),
            Err(e) => tracing::warn!("certificate renewal failed, retrying later: {e:#}"),
        }
    }

    let mut cursor = SessionCursor::default();
    send_all(&tx, agent.manager.collect(&mut cursor)).await?;

    let wake = agent.manager.wake_handle();
    let now = tokio::time::Instant::now();
    let mut heartbeat = tokio::time::interval_at(now + heartbeat_every, heartbeat_every);
    let mut flush = tokio::time::interval(FLUSH_INTERVAL);
    let mut renewal_check =
        tokio::time::interval_at(now + RENEWAL_CHECK_INTERVAL, RENEWAL_CHECK_INTERVAL);
    for t in [&mut heartbeat, &mut flush, &mut renewal_check] {
        t.set_missed_tick_behavior(tokio::time::MissedTickBehavior::Delay);
    }
    let mut idle_since: Option<Instant> = None;

    loop {
        if *draining && agent.manager.is_idle() {
            send_all(&tx, agent.manager.collect(&mut cursor)).await?;
            if agent.manager.unacknowledged() == 0 {
                return Ok(End::Done);
            }
            if idle_since.get_or_insert_with(Instant::now).elapsed() > DRAIN_ACK_GRACE {
                tracing::warn!(
                    "shutting down with {} unacknowledged results in the outbox",
                    agent.manager.unacknowledged()
                );
                return Ok(End::Done);
            }
        }
        tokio::select! {
            msg = inbound.message() => match msg {
                Ok(Some(m)) => handle(agent, m),
                Ok(None) => return Ok(End::Closed),
                Err(s) => bail!("stream error: {} ({:?})", s.message(), s.code()),
            },
            _ = heartbeat.tick() => {
                let hb = AgentMessage {
                    kind: Some(Out::Heartbeat(Heartbeat {
                        running_attempt_ids: agent.manager.active_ids(),
                    })),
                };
                send_all(&tx, vec![hb]).await?;
            }
            _ = flush.tick() => send_all(&tx, agent.manager.collect(&mut cursor)).await?,
            _ = wake.notified() => send_all(&tx, agent.manager.collect(&mut cursor)).await?,
            _ = renewal_check.tick() => {
                let current = identity::load(&agent.paths)?;
                if current.cert.needs_renewal(SystemTime::now()) {
                    match renewal::renew(channel.clone(), &agent.paths, &current).await {
                        Ok(()) => {
                            send_all(&tx, agent.manager.collect(&mut cursor)).await?;
                            return Ok(End::Renewed);
                        }
                        Err(e) => tracing::warn!("certificate renewal failed, retrying later: {e:#}"),
                    }
                }
            }
            _ = shutdown.cancelled(), if !*draining => {
                *draining = true;
                tracing::info!("shutting down: stopping cancellable attempts and waiting for the rest");
                agent.manager.drain();
            }
        }
    }
}

fn handle(agent: &Agent, message: ServerMessage) {
    match message.kind {
        Some(In::Assignment(a)) => agent.manager.accept(a),
        Some(In::Cancel(c)) => {
            tracing::info!(attempt = %c.attempt_id, "cancel requested");
            agent.manager.cancel(&c.attempt_id);
        }
        Some(In::ResultAck(ack)) => {
            tracing::debug!(attempt = %ack.attempt_id, "result acknowledged");
            agent.manager.ack(&ack.attempt_id);
        }
        Some(In::Welcome(w)) => {
            agent.manager.set_concurrency(w.max_concurrency.max(1));
        }
        None => tracing::debug!("ignoring an empty server message"),
    }
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn backoff_grows_caps_and_resets() {
        let mut b = Backoff::new(Duration::from_secs(1), Duration::from_secs(60));
        let d: Vec<Duration> = (0..10).map(|_| b.next_delay()).collect();
        assert!(d[0] >= Duration::from_millis(500) && d[0] <= Duration::from_secs(1));
        assert!(d[3] >= Duration::from_secs(4) && d[3] <= Duration::from_secs(8));
        assert!(d.iter().all(|x| *x <= Duration::from_secs(60)));
        assert!(d[9] >= Duration::from_secs(30));
        b.reset();
        assert!(b.next_delay() <= Duration::from_secs(1));
    }
}
