//! The `technitium` executor: what OpenTofu cannot do for an application cluster, and what it
//! cannot see. `inspect` tells the plan what the members report, `reconcile` joins and removes
//! the members an approved plan changed, `refresh-block-lists` forces a download, and `verify`
//! connects to every member directly, because a rebuilt secondary is invisible from the primary.

mod api;
mod dns;

use std::collections::BTreeSet;
use std::net::{IpAddr, SocketAddr};
use std::time::{Duration, Instant};

use reqwest::Client;
use serde::{Deserialize, Serialize};
use serde_json::{Map, Value, json};

use api::{Api, ApiError, ClusterState, Peer};

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
    #[serde(default = "yes")]
    verify_tls: bool,
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
    #[serde(default)]
    forward_zones: Vec<String>,
    control: Option<String>,
    #[serde(default = "default_sync_wait")]
    sync_wait_seconds: u64,
    #[serde(default)]
    join: Vec<String>,
    #[serde(default)]
    remove: Vec<String>,
    #[serde(default)]
    prune_entries: bool,
}

fn yes() -> bool {
    true
}

fn default_sync_wait() -> u64 {
    90
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
}

fn default_dns_port() -> u16 {
    53
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

pub async fn run(cfg: &ExecutorConfig, ctx: &ExecContext) -> ExecResult {
    let input: Input = ctx.input()?;
    let password = ctx.secrets.get(PASSWORD)?;
    let http = api::http_client(input.verify_tls, cfg.proxmox_timeout)?;
    match ctx.action.as_str() {
        "inspect" => inspect(ctx, &input, &http, password).await,
        "reconcile" => reconcile(ctx, &input, &http, password).await,
        "refresh-block-lists" => refresh(&input, &http, password).await,
        "verify" => verify(cfg, ctx, &input, &http, password).await,
        other => Err(Failure::msg(format!(
            "unknown technitium action {other:?}; expected inspect, reconcile, refresh-block-lists, or verify"
        ))),
    }
}

/// What a member says about itself, or why it says nothing.
enum Seen {
    Down(String),
    Refused(String),
    Broken(String),
    Up(Api, ClusterState),
}

async fn connect(http: &Client, member: &Member, password: &str) -> Seen {
    let api = match Api::login(http, &member.endpoint, password).await {
        Ok(api) => api,
        Err(ApiError::Refused(e)) => return Seen::Refused(e),
        Err(ApiError::Unreachable(e)) => return Seen::Down(e),
        Err(ApiError::Api(e)) => return Seen::Broken(e),
    };
    match api.cluster_state().await {
        Ok(state) => Seen::Up(api, state),
        Err(ApiError::Unreachable(e)) => Seen::Down(e),
        Err(e) => Seen::Broken(e.to_string()),
    }
}

/// The primary's session and state; without one nothing about the cluster can be decided.
async fn primary_session(
    input: &Input,
    http: &Client,
    password: &str,
) -> Result<(Api, ClusterState), Failure> {
    let primary = input.primary()?;
    match connect(http, primary, password).await {
        Seen::Up(api, state) => Ok((api, state)),
        Seen::Down(e) | Seen::Broken(e) => Err(primary_down(primary, &e)),
        Seen::Refused(e) => Err(Failure::msg(format!(
            "The primary {} refused the admin password ({e}). Set the store entry to the password the cluster uses; see 'The admin password' in {RECOVERY_DOC}.",
            primary.label()
        ))),
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
}

fn standing(member: &Member, seen: &Seen, primary: &ClusterState, domain: &str) -> Standing {
    match seen {
        Seen::Down(_) | Seen::Broken(_) => Standing::Unreachable,
        Seen::Refused(_) => Standing::Refused,
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

async fn inspect(ctx: &ExecContext, input: &Input, http: &Client, password: &str) -> ExecResult {
    let (primary_api, primary) = primary_session(input, http, password).await?;
    if input.formed && !primary.cluster_initialized {
        primary_api.logout().await;
        return Err(rebuilt_primary(input.primary()?));
    }
    let mut members = Vec::new();
    for member in input.secondaries() {
        let seen = connect(http, member, password).await;
        let found = standing(member, &seen, &primary, &input.cluster_domain);
        ctx.log.agent(format!("{}: {found:?}", member.label()));
        members.push(json!({ "guest": member.guest, "address": member.address, "state": found }));
        if let Seen::Up(api, _) = seen {
            api.logout().await;
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
    })))
}

async fn reconcile(ctx: &ExecContext, input: &Input, http: &Client, password: &str) -> ExecResult {
    let (primary_api, state) = primary_session(input, http, password).await?;
    if !state.cluster_initialized {
        primary_api.logout().await;
        return Err(Failure::msg(format!(
            "The primary {} reports no cluster, so no member can be joined to it. Inspect the apply and plan again.",
            input.primary()?.label()
        )));
    }
    let mut failed = Vec::new();

    let mut moved = Vec::new();
    for guest in &input.join {
        let Some(member) = input
            .members
            .iter()
            .find(|m| &m.guest == guest && !m.is_primary())
        else {
            failed.push(format!("{guest} is not a secondary of this cluster."));
            continue;
        };
        match join(ctx, input, member, http, password, &state).await? {
            Ok(Some(name)) => moved.push(name),
            Ok(None) => {}
            Err(problem) => failed.push(problem),
        }
    }

    // Only as many as the approved plan removed: anything more is somebody else's peer. A peer
    // nobody approved removing is not this step's business; verification reports it.
    if !input.remove.is_empty() {
        let extras: Vec<&Peer> = state
            .cluster_nodes
            .iter()
            .filter(|p| p.kind == "Secondary" && !moved.contains(&p.name))
            .filter(|p| {
                !input
                    .secondaries()
                    .any(|m| p.ip_addresses.contains(&m.address))
            })
            .collect();
        if extras.len() > input.remove.len() {
            failed.push(format!(
                "The cluster has undeclared peers ({}) beyond the {} removal(s) this plan approved, so none was removed.",
                extras
                    .iter()
                    .map(|p| p.name.as_str())
                    .collect::<Vec<_>>()
                    .join(", "),
                input.remove.len()
            ));
        } else {
            for peer in extras {
                ctx.log
                    .agent(format!("removing {} from the cluster", peer.name));
                remove_peer(ctx, &primary_api, peer, &mut failed).await;
            }
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
    primary_api.logout().await;
    if failed.is_empty() {
        Ok(Success::outputs(
            json!({ "joined": input.join, "removed": input.remove }),
        ))
    } else {
        Err(Failure::with_outputs(
            failed.join(" "),
            json!({ "failed": failed }),
        ))
    }
}

async fn remove_peer(ctx: &ExecContext, primary: &Api, peer: &Peer, failed: &mut Vec<String>) {
    let id = peer.id.to_string();
    let form = [("secondaryNodeId", id.as_str())];
    let Err(error) = primary
        .call("/api/admin/cluster/primary/removeSecondary", &form)
        .await
    else {
        return;
    };
    // This removal is approved, and removeSecondary needs the peer to answer. Only the record is
    // left to delete; a peer that does answer afterwards still believes it is a member.
    ctx.log.agent(format!(
        "{} did not leave ({error}); deleting it from the primary",
        peer.name
    ));
    if let Err(e) = primary
        .call("/api/admin/cluster/primary/deleteSecondary", &form)
        .await
    {
        failed.push(format!("Removing {} failed: {e}.", peer.name));
    }
}

/// `Err` only when the outcome is unknown; a known failure is the inner error, so that one
/// member's trouble does not stop the others. Gives the name of a member that moved.
async fn join(
    ctx: &ExecContext,
    input: &Input,
    member: &Member,
    http: &Client,
    password: &str,
    primary: &ClusterState,
) -> Result<Result<Option<String>, String>, Failure> {
    let seen = connect(http, member, password).await;
    let found = standing(member, &seen, primary, &input.cluster_domain);
    let result = match (&seen, found) {
        (_, Standing::Joined) => Ok(None),
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
            .map(|_| Some(state_name(&seen)))
            .map_err(|e| format!("Updating the address of {} failed: {e}.", member.label()))
        }
        (Seen::Up(api, _), Standing::Absent) => {
            ctx.log.agent(format!("{}: joining", member.label()));
            let joined = send_join(input, member, api, password, primary).await;
            match joined {
                Ok(()) => Ok(None),
                Err(ApiError::Unreachable(e)) => {
                    return settle_lost_join(ctx, member, http, password, &e)
                        .await
                        .map(|()| Ok(None));
                }
                Err(e) => Err(format!("Joining {} failed: {e}.", member.label())),
            }
        }
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

fn state_name(seen: &Seen) -> String {
    match seen {
        Seen::Up(_, state) => state.dns_server_domain.clone(),
        _ => String::new(),
    }
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
    let ignore = if input.verify_tls { "false" } else { "true" };
    api.call(
        "/api/admin/cluster/initJoin",
        &[
            ("secondaryNodeIpAddresses", member.address.as_str()),
            ("primaryNodeUrl", url.as_str()),
            ("primaryNodeIpAddress", address.as_str()),
            ("ignoreCertificateErrors", ignore),
            ("primaryNodeUsername", "admin"),
            ("primaryNodePassword", password),
        ],
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

/// A join restarts the member's web service, so its answer can be lost after it joined. Ask.
async fn settle_lost_join(
    ctx: &ExecContext,
    member: &Member,
    http: &Client,
    password: &str,
    error: &str,
) -> Result<(), Failure> {
    let deadline = Instant::now() + JOIN_TIMEOUT;
    while Instant::now() < deadline {
        if let Seen::Up(api, state) = connect(http, member, password).await {
            let joined = state.cluster_initialized;
            api.logout().await;
            if joined {
                return Ok(());
            }
            break;
        }
        ctx.sleep(Duration::from_secs(5)).await?;
    }
    Err(Failure::Uncertain(format!(
        "The answer to joining {} was lost ({error}), and it does not report a cluster. Check both nodes before planning again.",
        member.label()
    )))
}

async fn refresh(input: &Input, http: &Client, password: &str) -> ExecResult {
    let (api, _) = primary_session(input, http, password).await?;
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
    checks: Vec<Check>,
    #[serde(skip_serializing_if = "Option::is_none")]
    recovery: Option<String>,
}

async fn verify(
    cfg: &ExecutorConfig,
    ctx: &ExecContext,
    input: &Input,
    http: &Client,
    password: &str,
) -> ExecResult {
    let deadline = Instant::now() + Duration::from_secs(input.sync_wait_seconds);
    loop {
        ctx.check_cancelled()?;
        let reports = pass(input, http, password).await?;
        let failing: Vec<&MemberReport> = reports.iter().filter(|r| !r.ok).collect();
        let outputs = json!({ "ok": failing.is_empty(), "members": reports });
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

async fn pass(input: &Input, http: &Client, password: &str) -> Result<Vec<MemberReport>, Failure> {
    let mut members = Vec::new();
    for member in &input.members {
        members.push((member, connect(http, member, password).await));
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
    for (member, seen) in members {
        reports.push(report(input, member, seen, primary.as_ref(), &broken).await);
    }
    Ok(reports)
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
        Seen::Up(api, state) => {
            version = Some(state.version.clone());
            let primary_name = primary.map(|p| p.dns_server_domain.as_str());
            let checks = member_checks(input, member, &api, &state, primary_name, broken).await;
            api.logout().await;
            let recovery = recovery_for(input, member, &state, primary, &checks);
            (checks, recovery)
        }
    };
    MemberReport {
        guest: member.guest.clone(),
        address: member.address.clone(),
        role: member.role.clone(),
        ok: checks.iter().all(|c| c.ok),
        version,
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
    if enabled {
        for name in input.blocked.iter().take(MAX_BLOCKED_PROBES) {
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
}
