# Uptime

The Inventory shows whether each node, machine and service answers, and how often it did over the last 24 hours, 7 days
and 30 days. Nothing is configured for it: what is checked comes from the definitions.

## What is checked

From the head revision of the current repository source, when it is valid, and only for installations an operator trusted
in the mode they are in now (see [installation trust](installation-trust.md)):

| Check | Of | How | Up when |
| --- | --- | --- | --- |
| `proxmox-api` | each host's Proxmox API endpoint the trust covers | a TLS handshake, any certificate | the handshake completes |
| `guest-ssh` | each active guest with an address (`access.address`, or its static or reserved address) | the SSH identification line, on `access.port` or 22 | an SSH server answers |
| `service-http` | each of a service's `links`, and `https://<name>/` for each [name its stages set](workflows.md#names-a-stage-sets) that no link already opens | one `GET` of the URL, its status line only | the status is 2xx, 3xx, 401 or 403 |

An API endpoint several hosts share, such as a cluster's, is checked once and speaks for the installation, not for one
host.

A service's links are where people open it. They are listed in its `service.yaml`, are shown in the Inventory, and change
nothing a run does:

```yaml
schemaVersion: 1
workflow: vaultwarden@1
links:
  - https://vault.home.example.org/
```

Each link must be an absolute `http` or `https` URL with a host and no credentials; at most 16. A link is checked as a
browser would see it: over TLS, the certificate must be trusted by the agent's own trust store.

A workflow can also declare the host names a stage sets, such as the DNS records of compose's `dns` stage ([names a stage
sets](workflows.md#names-a-stage-sets)). Each is opened and checked as `https://<name>/`, so its certificate too must be
one the agent trusts. A name served over `http`, on another port or path, or with a certificate the agent does not trust
is better written as a link with that host: the link then stands for the name.

## Who checks

Agents do, as they do all network work; the server never connects anywhere itself. An agent that announces `uptime-v1`
answers uptime probes from its own process: an SSH banner read, and `http`, one HTTP/1.1 `GET` whose status line alone is
read and reported. Nothing else is sent: no credential, cookie or body.

- **The agents that reach it.** Each check keeps a pool: the agents that connected to its target and spoke its protocol
  in the last day. A check goes to the one of them that reached it last, then to the rest of the pool, then to the
  other agents by name. One agent at a time; the first that connects settles it, whatever the target answers, so an
  HTTP 503 is down without asking anyone else.
- **A failed connection moves on.** An agent that cannot connect (a timeout, a refused or unreachable address) may only
  lack a route, so the next agent is asked at once. The check is down when every connected agent failed and one of
  them is in the pool, with what that agent saw; or when nobody is in the pool, so no agent has reached the target at
  all. When the agents in the pool are not connected, or one of them did not answer or could not check, and the
  others all fail, nothing is recorded.
- **The pool is in memory.** It starts empty when the server starts, and is learned again from the first answers.
- **About once a minute.** New checks are spread over the minute. At most 8 uptime probes are awaited per agent, and none
  is sent while 32 messages are queued for it, so assignments always have room.
- **No answer is not down.** A probe the agent does not answer within its timeout plus 30 seconds is dropped, and the next
  agent is asked. So is an agent that says it cannot check faithfully (`unsupported`), such as when its environment would
  send the request through a proxy. Neither counts as a failed connection.
- **No agent, no history.** While no connected agent announces `uptime-v1`, nothing is checked, the Inventory says so, and
  the history has a gap.

Uptime probes share the reachability probe messages but never count as reachability evidence, and reachability answers
never count as uptime: their probe IDs differ (`up-…`). See [worker routing](worker-routing.md).

## What is kept

- **The current state** of each check: up or down, since when, what the agent last said, how long it took, and which
  agent.
- **Quarter hours** for 30 days: how many answers said up and down, and the response time of those that said up.

`GET /api/uptime` returns every check with its state, the 24h, 7d and 30d percentages of answers that said up, the last
96 quarter hours, the average response time of each, and the last 30 days. A check whose address, endpoint or link
changes is a new check with its own history.

## What it does not prove

- **Up is a route and a protocol.** It does not show that a credential works or that the service does what it should. A
  service whose health needs more than its status code says so in its own workflow's verification.
- **It is what the agents see.** Down means none of the connected agents could reach the target, among them one that
  used to; a failure may still be the network between them and the target.
- **The Proxmox check does not verify identity.** It accepts any certificate, so it says that something answers at the
  endpoint, not that it is the installation. Work that uses credentials still goes through trust and reachability.
