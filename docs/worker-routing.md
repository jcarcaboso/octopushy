# Worker routing by reachability

An agent at one site may reach endpoints that an agent at another site cannot. The controller does not need to be
told which agent reaches what. Before it hands out work that connects somewhere, it asks the connected agents
whether they reach that endpoint, from the same place the step will connect from. It then hands the work only to an
agent with fresh evidence that it does. Labels are no longer needed for this, and no per-target agent mapping
exists to keep up to date.

The controller is still the only scheduler. Agents answer probes the controller sends, and never claim, bid on,
forward or run work they were not assigned. Everything else that decides an assignment applies as before:
executors and capabilities, labels, agent and host capacity, the plan agent being preferred for its apply, trust,
approvals, observed-only refusal, guest and state locks, and handling of uncertain attempts. Reachability only
narrows which agents qualify.

## What a step connects to

Requirements are derived from what the step runs and which credentials it is released
(`AssignmentBuilder.ActionFor`, `AssignmentBuilder.SecretsFor` and `RunContext.ForStep`), so routing and credential
release cannot drift apart. The fact that a context holds a value proves nothing: every run context has a Proxmox
connection, including runs that never use it.

| Step | Connects to | From |
| --- | --- | --- |
| Observe, verify (including a container's host-key read and a VM's address from the guest agent), verify-absent, unprotect, reprotect | Proxmox API, the connection's endpoint | agent process (`proxmox` executor) |
| Provision or retire plan | Proxmox API | tool runtime, OpenTofu image |
| Provision or retire apply | Proxmox API; also the snippet host over SSH (its `address:port`) when the apply is released the node's host key, that is when it uploads a snippet | tool runtime, OpenTofu image |
| Configure preview | nothing | (any agent with the executor) |
| Configure playbook, approved playbook, key rotation steps | the guest over SSH, `access.address:port` | tool runtime, Ansible image |
| Pipeline Ansible stage: preview / run | nothing / the stage's guest over SSH | tool runtime, Ansible image |
| Pipeline OpenTofu stage | **not modeled** | — |

A pipeline's OpenTofu stage gets no endpoint and no provider credential from the server, and its providers are the
workflow's own, so nothing can be derived for it. Such a step goes to any agent that otherwise qualifies, including
an older one, and its reachability is **not checked**. Neither the server nor the UI calls it reachable.

A step that connects to nothing, such as a configure preview, can also go to an agent that does not report
reachability.

## Evidence

- **Per session.** Evidence belongs to the agent's current connection (`AgentSession`), in memory. A reconnect
  starts from nothing. It is not stored in the database.
- **Per endpoint and context.** The key is the protocol, host, port, TLS server name, the trust used to judge the
  certificate (system, a CA at a specific store version, or none), the context, and for container runtimes the image.
  Changing any of these, for example the endpoint, the CA, `verifyTls`, the image, or a trusted definition that
  changes one of them, gives a new key, and older evidence no longer applies to it. The agent's runtime and
  container network are fixed for the life of a session.
- **Timed by the controller.** Ages are measured from when the server received an answer. Agent clocks are never
  used.
- **Freshness.** Positive evidence counts for 10 minutes and is probed again after 5. Negative evidence backs off
  from 30 seconds, doubling, up to 5 minutes. No negative result is permanent. If an agent does not answer within
  the probe's timeout plus 30 seconds, the result is `no-answer`, which counts as negative. These values are
  constants in `WorkerReachability` and are not part of the protocol.
- **Answers.** A report is accepted only for an outstanding probe of the same live session. Unsolicited, replayed
  and stale reports are dropped. A report with more than 8 certificates, a certificate over 16 KiB, or an
  overlong detail counts as an `error`, never as evidence. Reports are ephemeral: they are not part of the attempt
  result outbox and are never acknowledged.

## When probes are sent

- **On demand.** At dispatch, a queued attempt whose candidates lack current evidence causes one probe per
  candidate and endpoint, deduplicated while one is outstanding. The attempt's waiting reason says why it waits,
  for example `A: … timeout 40s ago (…); next probe in 20s; B: … probing`, or `old: update the agent to report
  reachability`. Dispatch never waits for an answer, and a run is never marked blocked because of a pending probe.
  An answer wakes dispatch. Guest SSH is probed only this way, once a configure or pipeline attempt that needs it
  is queued, never while the guest is still being provisioned.
- **In the background.** Every minute, and when an agent connects, the API endpoint of each installation's latest
  observation is refreshed, as long as its revision is still eligible and trusted.
- **Bounded.** Each session has at most 8 probes outstanding. A probe is sent only while fewer than 32 messages are
  queued on the session's 256-message outbound channel, so assignments always have room. Each agent runs at most 4
  probes at a time and answers more than 32 queued probes with `error`. Each probe has a timeout of 15 seconds, or
  120 seconds when a container has to be started and its image may need pulling first.

Probes are only sent for endpoints from trusted definitions and dispatchable work. Work from an untrusted or
ineligible revision, held changes, and installations that are only observed go through their existing checks
first and never cause a probe. Nothing is scanned, and an agent never chooses a destination.

## How a probe connects

A probe carries no credential: no API token, no SSH key, no CA, nothing from the variable store.

| Context | How the agent connects |
| --- | --- |
| agent process | Directly, from the agent's own network namespace and resolver. If the agent's environment would send the endpoint through a proxy, the result is **unsupported**. This uses the same matcher reqwest uses, so the answer matches what the `proxmox` executor's client would do (`proxied` in the proxmox crate, tested against a real request). |
| tool, `local` runtime | Directly. Local tools run with a cleared environment in the agent's namespace, so the agent's proxy variables never reach them. |
| tool, `podman` / `docker` | Through a relay container of the step's image, started with the tool containers' flags (`--cap-drop=all`, `no-new-privileges`, read-only root, the same user mapping and `--network`), with nothing mounted and none of the agent's environment. The container opens one TCP connection with `python3` (the Ansible image) or `nc` (busybox in the OpenTofu image) and relays it over its stdin and stdout. The agent then performs the protocol itself. If the container sees a proxy variable (podman copies them from its own environment, docker from its client configuration), the result is **unsupported**. An image with neither tool, or without `/bin/sh`, is also **unsupported**. |

A proxy variable in the step's own `env` (from its definition) makes the endpoint **unsupported** before any agent
is asked. Probes never go through proxies. A context that cannot be probed faithfully is reported as such, and its
work waits.

The protocol:

- **TLS:** a full handshake. With `verifyTls` and no CA, the agent verifies the certificate with its own trust
  store, as the executor does. Otherwise it accepts any certificate but still verifies that the handshake is signed
  by the presented key (`AcceptAnyCertificate`), and returns the chain. The controller judges a chain against the
  configured CA alone and the endpoint's name. It does no revocation check, downloads no missing certificates, and
  fetches nothing. With `verifyTls: false`, it compares the leaf fingerprints that agents saw.
- **SSH:** the server's identification line (`SSH-2.0-…`). Nothing is sent and nothing is authenticated.

Statuses are `reachable`, `refused`, `timeout`, `dns`, `unreachable`, `protocol` (something else answered),
`tls-untrusted`, `unsupported` and `error`.

## What evidence does not prove

- **A positive probe proves a recent route and protocol, nothing more.** It does not show that a credential works,
  that a privilege is held, that the workflow will succeed, or that the endpoint is still there. Normal failures
  keep their existing meaning, and an attempt is never retried elsewhere because of a probe.
- **SSH probes do not verify identity.** The banner says an SSH server answered, not which machine it is. If the
  same private address exists at two sites, both agents may report it reachable, and either may get the work. The
  step's own connection then fails closed on the pinned host key (the guest's pin, or the snippet host's key in
  `knownHosts`) before it authenticates or changes anything. Octopushy does not claim it can automatically find
  the correct target over SSH.
- **With `verifyTls: false`, the target's identity is unknown.** When fresh evidence from different agents shows
  different leaf certificates at the same endpoint, no agent is chosen for that endpoint, and the waiting reason
  explains why. When the certificates match, this does not make unverified TLS safe.
- **Uncertain changes stay uncertain.** A probe cannot resolve an apply or playbook whose outcome is unknown, and
  such work is never replayed on another agent.
- **Trust in the tool container.** For tool contexts, a certificate is judged by the connection's configured trust,
  not by the CA bundle of the tool's image.
- **Health.** What one agent reaches says nothing about the health of a resource. Probe failures never touch
  installation observations, and no failure from one agent is recorded against a host.

## Labels

Labels are still optional, additional constraints. An attempt still requires every label its lab sets, and
reachability is checked only among agents that have them. Labels are never reinterpreted or removed
automatically.

Labels that only existed to send work to the right site, such as `site-a` on agent A and `agentLabels: [site-a]`
on the lab, are no longer needed. Removing them is optional and done once, the usual way: edit `execution.agentLabels`
in the lab repository through the normal trusted change process, and the agent's labels on **Agents**. Do this only
after the agents report reachability. Without labels, any agent with fresh evidence qualifies. Labels that express
something other than location, such as which agents may hold a lab's credentials at all, should be kept.

## Upgrading

The wire change is additive: `ReachabilityProbe` (server message 5), `ReachabilityReport` (agent message 7), and the
Hello capability `reachability-v1`. Older agents and servers ignore what they do not know.

**Upgrade the server first, and expect network work to wait until the agents are updated.** An agent that does
not report reachability no longer qualifies for any step that connects somewhere: provision, observe, verify,
protection, configure and pipeline Ansible runs. It waits with `update the agent to report reachability` and is
never sent work without that evidence. Configure previews and pipeline OpenTofu stages still run on older agents.
Once one agent per site is updated, its work flows again.

## Where it shows

**Agents** lists, for each connected agent, the endpoint, context and image, its state, the time since the last
answer, the probe detail and, for collected certificates, the leaf fingerprint. It also names agents that do not
report reachability. An attempt's waiting reason gives the per-agent states for the endpoints it waits on.
`GET /api/agents` returns `reportsReachability` and `reachability` for each agent.
