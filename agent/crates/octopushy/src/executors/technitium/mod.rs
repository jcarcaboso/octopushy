//! The `technitium` executor: what OpenTofu cannot do for an application cluster, and what it
//! cannot see. `certificates` reads what members present, with no credentials. `inspect` tells
//! the plan what they report, `detach` takes out the members an approved plan removes, `reconcile`
//! joins the ones it adds, `refresh-block-lists` forces a download, and `verify` connects to every
//! member directly, because a rebuilt secondary is invisible from the primary.
//!
//! The admin password goes only over a connection to the exact certificate an operator trusted
//! for that member. There is no way to turn that off.

mod api;
mod dns;

use std::collections::BTreeSet;
use std::net::{IpAddr, SocketAddr};
use std::time::{Duration, Instant};

use base64::Engine;
use serde::{Deserialize, Serialize};
use serde_json::{Map, Value, json};

use api::{Api, ApiError, ClusterState, Offered, Peer, sha256_hex};

use super::{ExecContext, ExecResult, ExecutorConfig, Failure, Success};

const PASSWORD: &str = "technitium.password";
/// Joining can include the whole first sync of the configuration.
const JOIN_TIMEOUT: Duration = Duration::from_secs(120);
const DNS_TIMEOUT: Duration = Duration::from_secs(3);
/// More than this tells nothing new, and each is a question to every member on every pass.
const MAX_BLOCKED_PROBES: usize = 5;
const RECOVERY_DOC: &str = "docs/technitium-lxc.md";

#[derive(Debug, Deserialize)]
#[serde(rename_all = "camelCase")]
struct Input {
    cluster_domain: String,
    minimum_version: String,
    members: Vec<Member>,
    /// Whether this cluster has been formed: a primary without one has then been rebuilt.
    #[serde(default)]
    formed: bool,
    #[serde(default)]
    settings: Map<String, Value>,
    #[serde(default)]
    blocked: Vec<String>,
    #[serde(default)]
    allowed: Vec<String>,
    /// Names on the downloaded lists, which is what shows they are enforced.
    #[serde(default)]
    list_probes: Vec<String>,
    #[serde(default)]
    forward_zones: Vec<String>,
    control: Option<String>,
    #[serde(default = "default_sync_wait")]
    sync_wait_seconds: u64,
    #[serde(default = "default_join_settle")]
    join_settle_seconds: u64,
    #[serde(default)]
    join: Vec<String>,
    #[serde(default)]
    remove: Vec<Removal>,
    #[serde(default)]
    prune_entries: bool,
}

fn default_sync_wait() -> u64 {
    90
}

fn default_join_settle() -> u64 {
    JOIN_TIMEOUT.as_secs()
}

#[derive(Debug, Clone, Deserialize)]
#[serde(rename_all = "camelCase")]
struct Member {
    guest: String,
    role: String,
    address: String,
    endpoint: String,
    #[serde(default = "default_dns_port")]
    dns_port: u16,
    /// The certificate an operator trusted for this member. Nothing is sent without one.
    certificate_sha256: Option<String>,
}

fn default_dns_port() -> u16 {
    53
}

/// A member the approved plan removes, as it was bound when it joined.
#[derive(Debug, Clone, Deserialize)]
#[serde(rename_all = "camelCase")]
struct Removal {
    guest: String,
    address: Option<String>,
    peer_id: Option<i64>,
    peer_name: Option<String>,
}

impl Member {
    fn is_primary(&self) -> bool {
        self.role == "primary"
    }

    fn label(&self) -> String {
        format!("{} ({})", self.guest, self.address)
    }
}

impl Input {
    fn primary(&self) -> Result<&Member, Failure> {
        self.members
            .iter()
            .find(|m| m.is_primary())
            .ok_or_else(|| Failure::msg("the input names no primary"))
    }

    fn secondaries(&self) -> impl Iterator<Item = &Member> {
        self.members.iter().filter(|m| !m.is_primary())
    }
}

/// What every action needs to reach a member.
struct Access<'a> {
    password: &'a str,
    timeout: Duration,
}

pub async fn run(cfg: &ExecutorConfig, ctx: &ExecContext) -> ExecResult {
    let input: Input = ctx.input()?;
    if ctx.action == "certificates" {
        return certificates(cfg, &input).await;
    }
    let access = Access {
        password: ctx.secrets.get(PASSWORD)?,
        timeout: cfg.proxmox_timeout,
    };
    match ctx.action.as_str() {
        "inspect" => inspect(ctx, &input, &access).await,
        "detach" => detach(ctx, &input, &access).await,
        "reconcile" => reconcile(ctx, &input, &access).await,
        "refresh-block-lists" => refresh(&input, &access).await,
        "verify" => verify(cfg, ctx, &input, &access).await,
        other => Err(Failure::msg(format!(
            "unknown technitium action {other:?}; expected certificates, inspect, detach, reconcile, refresh-block-lists, or verify"
        ))),
    }
}

fn pem(der: &[u8]) -> String {
    let encoded = base64::engine::general_purpose::STANDARD.encode(der);
    let lines: Vec<&str> = encoded
        .as_bytes()
        .chunks(64)
        .map(|l| std::str::from_utf8(l).unwrap_or_default())
        .collect();
    format!(
        "-----BEGIN CERTIFICATE-----\n{}\n-----END CERTIFICATE-----\n",
        lines.join("\n")
    )
}

fn certificate_report(member: &Member, der: &[u8]) -> Value {
    json!({
        "guest": member.guest,
        "endpoint": member.endpoint,
        "sha256": sha256_hex(der),
        "pem": pem(der),
    })
}

/// Reads what each member presents. This client sends nothing but a bare request, so it can
/// run before anything is trusted; what it finds is only a candidate until an operator trusts it.
async fn certificates(cfg: &ExecutorConfig, input: &Input) -> ExecResult {
    let mut found = Vec::new();
    let mut unreachable = Vec::new();
    for member in &input.members {
        let (http, offered) = api::http_client(None, cfg.proxmox_timeout)?;
        let answered = http.get(format!("{}/", member.endpoint)).send().await;
        let der = offered.lock().unwrap_or_else(|e| e.into_inner()).clone();
        match der {
            Some(der) => found.push(certificate_report(member, &der)),
            None => unreachable.push(json!({
                "guest": member.guest,
                "error": answered.err().map(|e| e.to_string()).unwrap_or_else(|| "it offered no certificate".into()),
            })),
        }
    }
    Ok(Success::outputs(
        json!({ "certificates": found, "unreachable": unreachable }),
    ))
}

/// What a member says about itself, or why it says nothing.
enum Seen {
    Down(String),
    Refused(String),
    Broken(String),
    /// It presented another certificate than the trusted one, so it was sent nothing.
    Impostor(Vec<u8>),
    Up(Api, ClusterState),
}

fn offered_elsewhere(offered: &Offered, member: &Member) -> Option<Vec<u8>> {
    let der = offered.lock().unwrap_or_else(|e| e.into_inner()).clone()?;
    let pin = member.certificate_sha256.as_deref()?;
    (!sha256_hex(&der).eq_ignore_ascii_case(pin)).then_some(der)
}

async fn connect(member: &Member, access: &Access<'_>) -> Result<Seen, Failure> {
    // Fail closed: a client without a pin would accept anyone.
    let pin = member.certificate_sha256.as_deref().ok_or_else(|| {
        Failure::msg(format!(
            "no certificate is trusted for {}, so its password was not sent",
            member.label()
        ))
    })?;
    let (http, offered) = api::http_client(Some(pin), access.timeout)?;
    let api = match Api::login(&http, &member.endpoint, access.password).await {
        Ok(api) => api,
        Err(error) => {
            if let Some(der) = offered_elsewhere(&offered, member) {
                return Ok(Seen::Impostor(der));
            }
            return Ok(match error {
                ApiError::Refused(e) => Seen::Refused(e),
                ApiError::Unreachable(e) => Seen::Down(e),
                ApiError::Api(e) | ApiError::Lost(e) => Seen::Broken(e),
            });
        }
    };
    Ok(match api.cluster_state().await {
        Ok(state) => Seen::Up(api, state),
        Err(ApiError::Unreachable(e)) => Seen::Down(e),
        Err(e) => Seen::Broken(e.to_string()),
    })
}

fn impostor_recovery(member: &Member) -> String {
    format!(
        "{} presents another certificate than the one trusted, so it was sent nothing. If it was rebuilt, compare the new certificate on the cluster's page with the member's, and Trust cluster; if not, something else is answering at this address. See 'A changed certificate' in {RECOVERY_DOC}.",
        member.label()
    )
}

/// The primary's session and state; without one nothing about the cluster can be decided.
async fn primary_session(
    input: &Input,
    access: &Access<'_>,
) -> Result<(Api, ClusterState), Failure> {
    let primary = input.primary()?;
    match connect(primary, access).await? {
        Seen::Up(api, state) => Ok((api, state)),
        Seen::Down(e) | Seen::Broken(e) => Err(primary_down(primary, &e)),
        Seen::Refused(e) => Err(Failure::msg(format!(
            "The primary {} refused the admin password ({e}). Set the store entry to the password the cluster uses; see 'The admin password' in {RECOVERY_DOC}.",
            primary.label()
        ))),
        Seen::Impostor(der) => Err(Failure::with_outputs(
            impostor_recovery(primary),
            json!({ "certificates": [certificate_report(primary, &der)] }),
        )),
    }
}

fn primary_down(primary: &Member, error: &str) -> Failure {
    Failure::msg(format!(
        "The primary {} cannot be reached: {error}. Nothing was changed. The secondaries keep answering with what they have, but no configuration can change until it is back; see 'When the primary is down' in {RECOVERY_DOC}.",
        primary.label()
    ))
}

fn peer_of<'a>(primary: &'a ClusterState, member: &Member, name: Option<&str>) -> Option<&'a Peer> {
    primary
        .cluster_nodes
        .iter()
        .find(|p| p.ip_addresses.iter().any(|a| a == &member.address))
        .or_else(|| {
            primary
                .cluster_nodes
                .iter()
                .find(|p| name.is_some_and(|n| p.name == n))
        })
}

/// Where a secondary stands, as the primary and the member itself see it.
#[derive(Debug, Clone, Copy, PartialEq, Eq, Serialize)]
#[serde(rename_all = "lowercase")]
enum Standing {
    /// In the cluster, as far as both can tell.
    Joined,
    /// Joined, at an address other than the one the primary has for it.
    Moved,
    /// Not in the cluster, and the primary does not list it: it can be joined.
    Absent,
    /// The primary lists it, but it is not in a cluster: it was rebuilt from empty.
    Stale,
    /// In a cluster the primary does not know.
    Foreign,
    Unreachable,
    Refused,
    /// Its certificate is not the trusted one.
    Unverified,
}

fn standing(member: &Member, seen: &Seen, primary: &ClusterState, domain: &str) -> Standing {
    match seen {
        Seen::Down(_) | Seen::Broken(_) => Standing::Unreachable,
        Seen::Refused(_) => Standing::Refused,
        Seen::Impostor(_) => Standing::Unverified,
        Seen::Up(_, state) if !state.cluster_initialized => {
            if peer_of(primary, member, None).is_some() {
                Standing::Stale
            } else {
                Standing::Absent
            }
        }
        Seen::Up(_, state) => match peer_of(primary, member, Some(&state.dns_server_domain)) {
            Some(_) if state.cluster_domain.as_deref() != Some(domain) => Standing::Foreign,
            Some(peer) if peer.ip_addresses.iter().any(|a| a == &member.address) => {
                Standing::Joined
            }
            Some(_) => Standing::Moved,
            None => Standing::Foreign,
        },
    }
}

async fn undeclared(api: &Api, input: &Input) -> Result<(Vec<String>, Vec<String>), ApiError> {
    let extra = |live: Vec<String>, declared: &[String]| -> Vec<String> {
        live.into_iter().filter(|e| !declared.contains(e)).collect()
    };
    Ok((
        extra(api.lines("/api/blocked/export").await?, &input.blocked),
        extra(api.lines("/api/allowed/export").await?, &input.allowed),
    ))
}

fn rebuilt_primary(primary: &Member) -> Failure {
    Failure::msg(format!(
        "The primary {} reports no cluster, although this cluster was formed: it was rebuilt from empty. Forming again would split the cluster, so nothing was changed. See 'A rebuilt primary' in {RECOVERY_DOC}.",
        primary.label()
    ))
}

async fn inspect(ctx: &ExecContext, input: &Input, access: &Access<'_>) -> ExecResult {
    let (primary_api, primary) = primary_session(input, access).await?;
    if input.formed && !primary.cluster_initialized {
        primary_api.logout().await;
        return Err(rebuilt_primary(input.primary()?));
    }
    let mut members = Vec::new();
    let mut changed = Vec::new();
    for member in input.secondaries() {
        let seen = connect(member, access).await?;
        let found = standing(member, &seen, &primary, &input.cluster_domain);
        ctx.log.agent(format!("{}: {found:?}", member.label()));
        let name = match &seen {
            Seen::Up(_, state) => Some(state.dns_server_domain.as_str()),
            _ => None,
        };
        let peer = peer_of(&primary, member, name);
        members.push(json!({
            "guest": member.guest,
            "address": member.address,
            "state": found,
            "peerId": peer.map(|p| p.id),
            "peerName": peer.map(|p| p.name.as_str()),
        }));
        match seen {
            Seen::Up(api, _) => api.logout().await,
            Seen::Impostor(der) => changed.push(certificate_report(member, &der)),
            _ => {}
        }
    }
    let (blocked, allowed) = if primary.cluster_initialized {
        undeclared(&primary_api, input)
            .await
            .map_err(|e| Failure::msg(format!("Reading the primary's lists failed: {e}")))?
    } else {
        Default::default()
    };
    primary_api.logout().await;
    Ok(Success::outputs(json!({
        "formed": primary.cluster_initialized,
        "members": members,
        "undeclared": { "blocked": blocked, "allowed": allowed },
        "certificates": changed,
    })))
}

/// The peer this removal stands for, and no other: its id on the primary, or both its name and
/// its address. Being undeclared says nothing about whose it is.
fn exact_peer<'a>(state: &'a ClusterState, removal: &Removal) -> Option<&'a Peer> {
    let secondaries = || state.cluster_nodes.iter().filter(|p| p.kind == "Secondary");
    if let Some(id) = removal.peer_id
        && let Some(peer) = secondaries().find(|p| p.id == id)
    {
        return Some(peer);
    }
    let has_address = |p: &Peer| {
        removal
            .address
            .as_ref()
            .is_some_and(|a| p.ip_addresses.contains(a))
    };
    match (&removal.peer_name, removal.peer_id) {
        (Some(name), _) => secondaries().find(|p| &p.name == name && has_address(p)),
        // Only an address was ever known: the join never reported back.
        (None, None) => secondaries().find(|p| has_address(p)),
        (None, Some(_)) => None,
    }
}

async fn detach(ctx: &ExecContext, input: &Input, access: &Access<'_>) -> ExecResult {
    let (primary, state) = primary_session(input, access).await?;
    let mut failed = Vec::new();
    let mut detached = Vec::new();
    for removal in &input.remove {
        let Some(peer) = exact_peer(&state, removal) else {
            ctx.log.agent(format!("{} has already left", removal.guest));
            detached.push(removal.guest.clone());
            continue;
        };
        if input
            .secondaries()
            .any(|m| peer.ip_addresses.contains(&m.address))
        {
            failed.push(format!(
                "{} holds the address of a declared member, so {} was not removed.",
                peer.name, removal.guest
            ));
            continue;
        }
        ctx.log
            .agent(format!("removing {} from the cluster", peer.name));
        let id = peer.id.to_string();
        let form = [("secondaryNodeId", id.as_str())];
        if let Err(error) = primary
            .call("/api/admin/cluster/primary/removeSecondary", &form)
            .await
        {
            // This removal is approved, and removeSecondary needs the peer to answer. Only the
            // record is left to delete; a peer that answers later still believes it is a member.
            ctx.log.agent(format!(
                "{} did not leave ({error}); deleting it from the primary",
                peer.name
            ));
            let _ = primary
                .call("/api/admin/cluster/primary/deleteSecondary", &form)
                .await;
        }
        // What the primary lists decides, not what a call answered.
        match primary.cluster_state().await {
            Ok(after) if exact_peer(&after, removal).is_none() => {
                detached.push(removal.guest.clone());
            }
            Ok(_) => failed.push(format!("{} is still in the cluster.", peer.name)),
            Err(e) => failed.push(format!(
                "Whether {} left is unknown: {e}. The next plan tries again.",
                peer.name
            )),
        }
    }
    primary.logout().await;
    let outputs = json!({ "detached": detached });
    if failed.is_empty() {
        Ok(Success::outputs(outputs))
    } else {
        // The plan still holds these removals, because the apply has not run.
        Err(Failure::with_outputs(failed.join(" "), outputs))
    }
}

async fn reconcile(ctx: &ExecContext, input: &Input, access: &Access<'_>) -> ExecResult {
    let (primary_api, state) = primary_session(input, access).await?;
    if !state.cluster_initialized {
        primary_api.logout().await;
        return Err(Failure::msg(format!(
            "The primary {} reports no cluster, so no member can be joined to it. Inspect the apply and plan again.",
            input.primary()?.label()
        )));
    }
    let mut failed = Vec::new();
    let mut joined = Vec::new();
    for guest in &input.join {
        let Some(member) = input
            .members
            .iter()
            .find(|m| &m.guest == guest && !m.is_primary())
        else {
            failed.push(format!("{guest} is not a secondary of this cluster."));
            continue;
        };
        match join(ctx, input, member, access, &state).await? {
            Ok(()) => joined.push(guest.clone()),
            Err(problem) => failed.push(problem),
        }
    }

    if input.prune_entries {
        let (blocked, allowed) = undeclared(&primary_api, input)
            .await
            .map_err(|e| Failure::msg(format!("Reading the primary's lists failed: {e}")))?;
        for (path, entries) in [
            ("/api/blocked/delete", &blocked),
            ("/api/allowed/delete", &allowed),
        ] {
            for entry in entries {
                ctx.log
                    .agent(format!("removing the undeclared entry {entry}"));
                if let Err(e) = primary_api.call(path, &[("domain", entry)]).await {
                    failed.push(format!("Removing {entry} failed: {e}."));
                }
            }
        }
    }

    // The peers as they stand now, so that a member is known by more than its address.
    let after = primary_api.cluster_state().await.ok();
    primary_api.logout().await;
    let members: Vec<Value> = joined
        .iter()
        .filter_map(|guest| {
            let member = input.members.iter().find(|m| &m.guest == guest)?;
            let peer = peer_of(after.as_ref()?, member, None)?;
            Some(json!({ "guest": guest, "peerId": peer.id, "peerName": peer.name }))
        })
        .collect();
    let outputs = json!({ "joined": joined, "members": members });
    if failed.is_empty() {
        Ok(Success::outputs(outputs))
    } else {
        Err(Failure::with_outputs(failed.join(" "), outputs))
    }
}

/// `Err` only when the outcome is unknown; a known failure is the inner error, so that one
/// member's trouble does not stop the others.
async fn join(
    ctx: &ExecContext,
    input: &Input,
    member: &Member,
    access: &Access<'_>,
    primary: &ClusterState,
) -> Result<Result<(), String>, Failure> {
    let seen = connect(member, access).await?;
    let found = standing(member, &seen, primary, &input.cluster_domain);
    let result = match (&seen, found) {
        (_, Standing::Joined) => Ok(()),
        (Seen::Up(api, _), Standing::Moved) => {
            ctx.log.agent(format!(
                "{}: telling the cluster its new address",
                member.label()
            ));
            api.call(
                "/api/admin/cluster/updateIpAddress",
                &[("ipAddresses", &member.address)],
            )
            .await
            .map(|_| ())
            .map_err(|e| format!("Updating the address of {} failed: {e}.", member.label()))
        }
        (Seen::Up(api, _), Standing::Absent) => {
            ctx.log.agent(format!("{}: joining", member.label()));
            match send_join(input, member, api, access.password, primary).await {
                Ok(()) => Ok(()),
                // Only a refusal says the join did not happen.
                Err(ApiError::Api(e) | ApiError::Refused(e)) => {
                    Err(format!("Joining {} failed: {e}.", member.label()))
                }
                Err(lost @ (ApiError::Unreachable(_) | ApiError::Lost(_))) => {
                    settle_lost_join(ctx, input, member, access, &lost.to_string()).await?;
                    Ok(())
                }
            }
        }
        (Seen::Impostor(_), _) => Err(impostor_recovery(member)),
        (_, other) => Err(format!(
            "{} cannot be joined: {}",
            member.label(),
            problem_of(other, member, primary, input)
        )),
    };
    if let Seen::Up(api, _) = seen {
        api.logout().await;
    }
    Ok(result)
}

async fn send_join(
    input: &Input,
    member: &Member,
    api: &Api,
    password: &str,
    primary: &ClusterState,
) -> Result<(), ApiError> {
    let url = format!("https://{}:{}/", primary.dns_server_domain, api_port(input));
    let address = &input
        .primary()
        .map_err(|f| ApiError::Api(f.message().to_owned()))?
        .address;
    // Technitium has no way to pin here: the secondary takes the primary as it finds it, which
    // is the one leg an operator's trust does not cover. It is in docs/technitium-lxc.md.
    api.call_within(
        "/api/admin/cluster/initJoin",
        &[
            ("secondaryNodeIpAddresses", member.address.as_str()),
            ("primaryNodeUrl", url.as_str()),
            ("primaryNodeIpAddress", address.as_str()),
            ("ignoreCertificateErrors", "true"),
            ("primaryNodeUsername", "admin"),
            ("primaryNodePassword", password),
        ],
        Some(JOIN_TIMEOUT),
    )
    .await
    .map(|_| ())
}

fn api_port(input: &Input) -> u16 {
    input
        .primary()
        .ok()
        .and_then(|p| url::Url::parse(&p.endpoint).ok())
        .and_then(|u| u.port_or_known_default())
        .unwrap_or(53443)
}

/// A join restarts the member's web service, so its answer can be lost after it joined. Both
/// nodes have to agree that it did; otherwise nobody knows, and the attempt says so.
async fn settle_lost_join(
    ctx: &ExecContext,
    input: &Input,
    member: &Member,
    access: &Access<'_>,
    error: &str,
) -> Result<(), Failure> {
    let primary = input.primary()?;
    let deadline = Instant::now() + Duration::from_secs(input.join_settle_seconds);
    loop {
        let mut joined = false;
        if let Seen::Up(api, state) = connect(member, access).await? {
            joined = state.cluster_initialized;
            api.logout().await;
        }
        if joined && let Seen::Up(api, state) = connect(primary, access).await? {
            let listed = peer_of(&state, member, None).is_some();
            api.logout().await;
            if listed {
                return Ok(());
            }
        }
        if Instant::now() >= deadline {
            return Err(Failure::Uncertain(format!(
                "The answer to joining {} was lost ({error}), and the two nodes do not show it joined. Check both before planning again.",
                member.label()
            )));
        }
        ctx.sleep(Duration::from_millis(500).min(Duration::from_secs(5)))
            .await?;
    }
}

async fn refresh(input: &Input, access: &Access<'_>) -> ExecResult {
    let (api, _) = primary_session(input, access).await?;
    let result = api.get("/api/settings/forceUpdateBlockLists").await;
    api.logout().await;
    result
        .map_err(|e| Failure::msg(format!("Forcing the block lists to download failed: {e}.")))?;
    Ok(Success::outputs(json!({ "refreshed": true })))
}

#[derive(Debug, Clone, Serialize)]
struct Check {
    name: &'static str,
    ok: bool,
    detail: String,
    /// Waiting cannot change this outcome.
    #[serde(skip)]
    settled: bool,
}

impl Check {
    fn pass(name: &'static str, detail: impl Into<String>) -> Self {
        Self {
            name,
            ok: true,
            detail: detail.into(),
            settled: true,
        }
    }

    /// Could be sync lag.
    fn lagging(name: &'static str, detail: impl Into<String>) -> Self {
        Self {
            name,
            ok: false,
            detail: detail.into(),
            settled: false,
        }
    }

    fn fail(name: &'static str, detail: impl Into<String>) -> Self {
        Self {
            name,
            ok: false,
            detail: detail.into(),
            settled: true,
        }
    }
}

#[derive(Debug, Serialize)]
#[serde(rename_all = "camelCase")]
struct MemberReport {
    guest: String,
    address: String,
    role: String,
    ok: bool,
    #[serde(skip_serializing_if = "Option::is_none")]
    version: Option<String>,
    /// Whether a question over DNS showed this member blocking. No question, no claim.
    blocking_verified: bool,
    checks: Vec<Check>,
    #[serde(skip_serializing_if = "Option::is_none")]
    recovery: Option<String>,
}

async fn verify(
    cfg: &ExecutorConfig,
    ctx: &ExecContext,
    input: &Input,
    access: &Access<'_>,
) -> ExecResult {
    let deadline = Instant::now() + Duration::from_secs(input.sync_wait_seconds);
    loop {
        ctx.check_cancelled()?;
        let (reports, changed) = pass(input, access).await?;
        let failing: Vec<&MemberReport> = reports.iter().filter(|r| !r.ok).collect();
        let outputs =
            json!({ "ok": failing.is_empty(), "members": reports, "certificates": changed });
        if failing.is_empty() {
            return Ok(Success::outputs(outputs));
        }
        let waiting = failing
            .iter()
            .any(|r| r.checks.iter().any(|c| !c.ok && !c.settled));
        if !waiting || Instant::now() >= deadline {
            let message = failing
                .iter()
                .map(|r| {
                    let reason = r
                        .checks
                        .iter()
                        .filter(|c| !c.ok)
                        .map(|c| c.detail.as_str())
                        .collect::<Vec<_>>()
                        .join("; ");
                    format!(
                        "{} ({}): {reason}. Recovery: {}",
                        r.guest,
                        r.address,
                        r.recovery.as_deref().unwrap_or(RECOVERY_DOC)
                    )
                })
                .collect::<Vec<_>>()
                .join(" | ");
            return Err(Failure::with_outputs(message, outputs));
        }
        let names: Vec<&str> = failing.iter().map(|r| r.guest.as_str()).collect();
        ctx.log
            .agent(format!("waiting for {} to catch up", names.join(", ")));
        ctx.sleep(cfg.poll_interval).await?;
    }
}

async fn pass(
    input: &Input,
    access: &Access<'_>,
) -> Result<(Vec<MemberReport>, Vec<Value>), Failure> {
    let mut members = Vec::new();
    for member in &input.members {
        members.push((member, connect(member, access).await?));
    }
    let primary = members.iter().find_map(|(member, seen)| match seen {
        Seen::Up(_, state) if member.is_primary() => Some(state.clone()),
        _ => None,
    });
    // A member that is down or rebuilt is reported as itself. That its peers see it as
    // Unreachable is the same fact, not a failure of theirs.
    let broken: Vec<String> = members
        .iter()
        .filter(|(_, seen)| !matches!(seen, Seen::Up(_, state) if state.cluster_initialized))
        .map(|(member, _)| member.address.clone())
        .collect();
    let mut reports = Vec::new();
    let mut changed = Vec::new();
    for (member, seen) in members {
        if let Seen::Impostor(der) = &seen {
            changed.push(certificate_report(member, der));
        }
        reports.push(report(input, member, seen, primary.as_ref(), &broken).await);
    }
    Ok((reports, changed))
}

async fn report(
    input: &Input,
    member: &Member,
    seen: Seen,
    primary: Option<&ClusterState>,
    broken: &[String],
) -> MemberReport {
    let mut version = None;
    let (checks, recovery) = match seen {
        Seen::Down(e) | Seen::Broken(e) => {
            let text = if member.is_primary() {
                format!("the primary cannot be reached: {e}")
            } else {
                format!("it cannot be reached: {e}")
            };
            (
                vec![Check::fail("reachable", text)],
                Some(if member.is_primary() {
                    format!(
                        "the secondaries keep serving; see 'When the primary is down' in {RECOVERY_DOC}."
                    )
                } else {
                    format!(
                        "start the container, and allow the agent and its peers on port 53443 (firewall.adminSources); see 'Firewall' in {RECOVERY_DOC}."
                    )
                }),
            )
        }
        Seen::Refused(e) => (
            vec![Check::fail(
                "login",
                format!("it refused the admin password: {e}"),
            )],
            Some(format!(
                "members share the store entry named by credentials; see 'The admin password' in {RECOVERY_DOC}."
            )),
        ),
        Seen::Impostor(_) => (
            vec![Check::fail(
                "certificate",
                "it presents another certificate than the one trusted",
            )],
            Some(impostor_recovery(member)),
        ),
        Seen::Up(api, state) => {
            version = Some(state.version.clone());
            let primary_name = primary.map(|p| p.dns_server_domain.as_str());
            let checks = member_checks(input, member, &api, &state, primary_name, broken).await;
            api.logout().await;
            let recovery = recovery_for(input, member, &state, primary, &checks);
            (checks, recovery)
        }
    };
    let queried: Vec<&Check> = checks.iter().filter(|c| c.name == "blocking").collect();
    MemberReport {
        guest: member.guest.clone(),
        address: member.address.clone(),
        role: member.role.clone(),
        ok: checks.iter().all(|c| c.ok),
        version,
        blocking_verified: !queried.is_empty() && queried.iter().all(|c| c.ok),
        checks,
        recovery,
    }
}

fn stale_recovery(input: &Input, peer: &Peer) -> String {
    let endpoint = input
        .primary()
        .map(|p| p.endpoint.trim_end_matches('/').to_owned())
        .unwrap_or_default();
    format!(
        "the primary still lists it as {} with id {}. Sign in to {endpoint} and call GET /api/admin/cluster/primary/deleteSecondary?secondaryNodeId={}, then Plan now on the cluster to join it again; see 'A rebuilt member' in {RECOVERY_DOC}.",
        if peer.state.is_empty() {
            "a member"
        } else {
            &peer.state
        },
        peer.id,
        peer.id
    )
}

fn problem_of(found: Standing, member: &Member, primary: &ClusterState, input: &Input) -> String {
    match found {
        Standing::Stale => peer_of(primary, member, None)
            .map(|peer| stale_recovery(input, peer))
            .unwrap_or_else(|| "the primary still lists it.".to_owned()),
        Standing::Foreign => {
            "it is in a cluster the primary does not list it in. Leave that cluster first."
                .to_owned()
        }
        Standing::Refused => {
            format!("it refused the admin password; see 'The admin password' in {RECOVERY_DOC}.")
        }
        _ => "it cannot be reached.".to_owned(),
    }
}

/// The exact way out, once it is known. A rebuilt member needs its stale record gone, and
/// nothing does that automatically: removing a member is destructive under `policy.destructive`.
fn recovery_for(
    input: &Input,
    member: &Member,
    state: &ClusterState,
    primary: Option<&ClusterState>,
    checks: &[Check],
) -> Option<String> {
    let failed: Vec<&str> = checks.iter().filter(|c| !c.ok).map(|c| c.name).collect();
    if failed.is_empty() {
        return None;
    }
    if !state.cluster_initialized {
        if member.is_primary() {
            return Some(format!("see 'A rebuilt primary' in {RECOVERY_DOC}."));
        }
        return Some(match primary.and_then(|p| peer_of(p, member, None)) {
            Some(peer) => stale_recovery(input, peer),
            None => "Plan now on the cluster to join it.".to_owned(),
        });
    }
    if failed.contains(&"peers") || failed.contains(&"membership") {
        return Some(format!(
            "allow members on each other's ports 53 and 53443 (firewall.adminSources and dnsSources), and compare the peers with cluster.yaml; see 'Firewall' in {RECOVERY_DOC}."
        ));
    }
    if failed.contains(&"version") {
        return Some(format!(
            "update this guest's technitium inputs to at least {}.",
            input.minimum_version
        ));
    }
    if failed.contains(&"blocking") && !input.list_probes.is_empty() {
        return Some(format!(
            "if a list is the reason, check that it downloads and still holds its listProbes, then Plan now; see 'Changing a blocklist' in {RECOVERY_DOC}."
        ));
    }
    Some(format!(
        "Plan now on the cluster to put it back; hand edits are reverted. If this stays, compare this member with the primary; see 'Ownership' in {RECOVERY_DOC}."
    ))
}

async fn member_checks(
    input: &Input,
    member: &Member,
    api: &Api,
    state: &ClusterState,
    primary_name: Option<&str>,
    broken: &[String],
) -> Vec<Check> {
    let mut checks = Vec::new();
    if !state.cluster_initialized {
        checks.push(Check::fail(
            "cluster",
            "it reports clusterInitialized=false, so it answers DNS with none of the cluster's blocking",
        ));
        return checks;
    }
    if state.cluster_domain.as_deref() != Some(&input.cluster_domain) {
        checks.push(Check::fail(
            "cluster",
            format!(
                "it is in the cluster {}, not {}",
                state.cluster_domain.as_deref().unwrap_or("?"),
                input.cluster_domain
            ),
        ));
        return checks;
    }
    checks.push(Check::pass(
        "cluster",
        format!("in {}", input.cluster_domain),
    ));
    checks.push(if at_least(&state.version, &input.minimum_version) {
        Check::pass("version", state.version.clone())
    } else {
        Check::fail(
            "version",
            format!(
                "it runs {}, below the minimum {}",
                state.version, input.minimum_version
            ),
        )
    });
    checks.push(membership(input, state));
    checks.push(peers(state, broken));
    checks.push(settings(input, api).await);
    checks.push(list_check("blocked", "/api/blocked/export", &input.blocked, api).await);
    checks.push(list_check("allowed", "/api/allowed/export", &input.allowed, api).await);
    if !input.forward_zones.is_empty() {
        checks.push(zones(input, api).await);
    }
    checks.extend(dns_checks(input, member, primary_name).await);
    checks
}

fn at_least(version: &str, minimum: &str) -> bool {
    let parts = |v: &str| {
        v.split('.')
            .map(|p| p.parse::<u64>().unwrap_or(0))
            .collect::<Vec<_>>()
    };
    parts(version) >= parts(minimum)
}

fn membership(input: &Input, state: &ClusterState) -> Check {
    let mut problems = Vec::new();
    for member in &input.members {
        let expected = if member.is_primary() {
            "Primary"
        } else {
            "Secondary"
        };
        match state
            .cluster_nodes
            .iter()
            .find(|p| p.ip_addresses.contains(&member.address))
        {
            Some(peer) if peer.kind == expected => {}
            Some(peer) => problems.push(format!(
                "{} is a {} here, declared {}",
                member.label(),
                peer.kind,
                member.role
            )),
            None => problems.push(format!("{} is not listed", member.label())),
        }
    }
    for peer in &state.cluster_nodes {
        if !input
            .members
            .iter()
            .any(|m| peer.ip_addresses.contains(&m.address))
        {
            problems.push(format!("{} is not declared", peer.name));
        }
    }
    if problems.is_empty() {
        Check::pass("membership", "the members match cluster.yaml")
    } else {
        Check::lagging("membership", problems.join(", "))
    }
}

fn peers(state: &ClusterState, broken: &[String]) -> Check {
    let down: Vec<String> = state
        .cluster_nodes
        .iter()
        .filter(|p| p.state != "Connected" && p.state != "Self")
        .filter(|p| !p.ip_addresses.iter().any(|a| broken.contains(a)))
        .map(|p| format!("{} is {}", p.name, p.state))
        .collect();
    if down.is_empty() {
        Check::pass("peers", "every peer is Connected")
    } else {
        Check::lagging("peers", down.join(", "))
    }
}

/// Lists are compared as sets, and a list a member has never held reads as null.
fn same(live: Option<&Value>, declared: &Value) -> bool {
    let live = live.unwrap_or(&Value::Null);
    let set = |v: &Value| {
        v.as_array()
            .map(|a| a.iter().map(Value::to_string).collect::<BTreeSet<_>>())
    };
    match declared {
        Value::Array(_) => set(live).unwrap_or_default() == set(declared).unwrap_or_default(),
        _ => live == declared,
    }
}

async fn settings(input: &Input, api: &Api) -> Check {
    let live = match api.get("/api/settings/get").await {
        Ok(live) => live,
        Err(e) => return Check::lagging("settings", format!("reading its settings failed: {e}")),
    };
    let differs: Vec<String> = input
        .settings
        .iter()
        .filter(|(name, declared)| !same(live.get(name.as_str()), declared))
        .map(|(name, declared)| {
            format!(
                "{name} is {}, declared {declared}",
                live.get(name.as_str()).unwrap_or(&Value::Null)
            )
        })
        .collect();
    if differs.is_empty() {
        Check::pass("settings", "the shared settings match")
    } else {
        Check::lagging("settings", differs.join(", "))
    }
}

async fn list_check(name: &'static str, path: &str, declared: &[String], api: &Api) -> Check {
    let live = match api.lines(path).await {
        Ok(live) => live,
        Err(e) => return Check::lagging(name, format!("reading it failed: {e}")),
    };
    let missing: Vec<&String> = declared.iter().filter(|d| !live.contains(d)).collect();
    let extra: Vec<&String> = live.iter().filter(|l| !declared.contains(l)).collect();
    if missing.is_empty() && extra.is_empty() {
        return Check::pass(name, format!("{} entries", declared.len()));
    }
    let mut detail = Vec::new();
    if !missing.is_empty() {
        detail.push(format!("missing from {name}: {}", listing(&missing)));
    }
    if !extra.is_empty() {
        detail.push(format!("in {name} but not declared: {}", listing(&extra)));
    }
    Check::lagging(name, detail.join("; "))
}

fn listing(names: &[&String]) -> String {
    names
        .iter()
        .map(|n| n.as_str())
        .collect::<Vec<_>>()
        .join(", ")
}

async fn zones(input: &Input, api: &Api) -> Check {
    let live = match api.get("/api/zones/list").await {
        Ok(live) => live,
        Err(e) => return Check::lagging("zones", format!("reading its zones failed: {e}")),
    };
    let names: Vec<&str> = live["zones"]
        .as_array()
        .map(|z| z.iter().filter_map(|z| z["name"].as_str()).collect())
        .unwrap_or_default();
    let missing: Vec<&String> = input
        .forward_zones
        .iter()
        .filter(|z| !names.contains(&z.as_str()))
        .collect();
    if missing.is_empty() {
        Check::pass("zones", "it serves the forward zones")
    } else {
        Check::lagging("zones", format!("it does not serve {}", listing(&missing)))
    }
}

async fn dns_checks(input: &Input, member: &Member, primary_name: Option<&str>) -> Vec<Check> {
    let mut checks = Vec::new();
    let server = match member.address.parse::<IpAddr>() {
        Ok(ip) => SocketAddr::new(ip, member.dns_port),
        Err(_) => {
            return vec![Check::fail(
                "dns",
                format!("{} is not an IP address", member.address),
            )];
        }
    };
    let enabled = input
        .settings
        .get("enableBlocking")
        .and_then(Value::as_bool)
        .unwrap_or(true);
    let lists = input
        .settings
        .get("blockListUrls")
        .and_then(Value::as_array)
        .is_some_and(|l| !l.is_empty());
    if enabled {
        if lists && input.list_probes.is_empty() {
            // Never a pass by silence: a declared entry says nothing about a list.
            checks.push(Check::fail(
                "lists",
                "block lists are declared with no listProbes, so nothing shows they are enforced",
            ));
        }
        let names = input
            .blocked
            .iter()
            .take(MAX_BLOCKED_PROBES)
            .chain(input.list_probes.iter().take(MAX_BLOCKED_PROBES));
        for name in names {
            checks.push(match dns::query(server, name, DNS_TIMEOUT).await {
                Ok(answer) if answer.blocked => {
                    Check::pass("blocking", format!("{name} is blocked"))
                }
                Ok(answer) => Check::lagging(
                    "blocking",
                    format!("{name} is not blocked here (rcode {})", answer.rcode),
                ),
                Err(e) => Check::lagging("blocking", format!("asking for {name} failed: {e}")),
            });
        }
    }
    let Some(control) = input.control.as_deref().or(primary_name) else {
        return checks;
    };
    checks.push(match dns::query(server, control, DNS_TIMEOUT).await {
        Ok(answer) if answer.resolves() => Check::pass("control", format!("{control} resolves")),
        Ok(answer) => Check::lagging(
            "control",
            format!(
                "the control {control} does not resolve here (rcode {})",
                answer.rcode
            ),
        ),
        Err(e) => Check::lagging(
            "control",
            format!("asking for the control {control} failed: {e}"),
        ),
    });
    checks
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn versions_compare_by_number() {
        assert!(at_least("15.6.2", "15.6.2"));
        assert!(at_least("15.10.0", "15.6.2"));
        assert!(!at_least("15.6.0", "15.6.2"));
        assert!(!at_least("9.9.9", "15.6.2"));
    }

    #[test]
    fn a_list_a_member_never_held_equals_an_empty_one() {
        assert!(same(Some(&Value::Null), &json!([])));
        assert!(same(None, &json!([])));
        assert!(same(Some(&json!(["b", "a"])), &json!(["a", "b"])));
        assert!(!same(Some(&Value::Null), &json!(["a"])));
        assert!(!same(Some(&json!(false)), &json!(true)));
    }

    #[test]
    fn a_certificate_prints_as_pem() {
        let text = pem(&[1u8; 100]);
        assert!(text.starts_with("-----BEGIN CERTIFICATE-----\n"));
        assert!(
            text.lines()
                .all(|l| l.len() <= 64 || l.starts_with("-----"))
        );
    }

    fn state(peers: Value) -> ClusterState {
        serde_json::from_value(json!({ "clusterNodes": peers })).unwrap()
    }

    #[test]
    fn a_removal_only_ever_matches_its_own_peer() {
        let live = state(json!([
            { "id": 1, "name": "p", "ipAddresses": ["10.0.0.1"], "type": "Primary" },
            { "id": 2, "name": "d", "ipAddresses": ["10.0.0.4"], "type": "Secondary" },
        ]));
        let removal = |id, name: Option<&str>, address: &str| Removal {
            guest: "g".into(),
            address: Some(address.into()),
            peer_id: id,
            peer_name: name.map(str::to_owned),
        };
        assert!(exact_peer(&live, &removal(Some(9), Some("b"), "10.0.0.2")).is_none());
        // Its address was taken over, by a node of another name.
        assert!(exact_peer(&live, &removal(Some(9), Some("b"), "10.0.0.4")).is_none());
        // Never the primary, whatever it was told.
        assert!(exact_peer(&live, &removal(Some(1), Some("p"), "10.0.0.1")).is_none());
        assert_eq!(
            exact_peer(&live, &removal(Some(2), None, "x"))
                .unwrap()
                .name,
            "d"
        );
        assert_eq!(
            exact_peer(&live, &removal(None, Some("d"), "10.0.0.4"))
                .unwrap()
                .name,
            "d"
        );
        assert_eq!(
            exact_peer(&live, &removal(None, None, "10.0.0.4"))
                .unwrap()
                .name,
            "d"
        );
    }
}
