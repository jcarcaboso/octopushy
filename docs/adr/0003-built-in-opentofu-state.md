# Carry OpenTofu state in assignments, not through a loopback backend

Users should never configure state, so the controller keeps it. Agents must not reach
the controller's database, so state has to travel on the agent channel.

The obvious design is a loopback-only OpenTofu `http` backend in the agent that forwards to
the controller. It does not fit: a container on the runtime's default network has its own
loopback, so it cannot reach one on the host, and the packaged service runs podman that way.
Reaching it takes host networking for every tool container, which removes isolation we keep
on purpose, or binding to a bridge address, which is no longer loopback-only and differs per
runtime. The `http` backend also has no workspaces, and every attempt would need a server and
a credential.

Instead, state is an artifact: in the assignment going out, in the result coming back, and
snapshots during an apply, while OpenTofu uses a generated `local` backend file in the attempt's
workspace with a path relative to its root. There is one code path for every runtime, and
the channel, its authentication, the outbox and restart recovery already exist. Delivering
state in the assignment means authorization is the assignment: an agent never asks for
state, and a plan has no way to send any back.

The costs: OpenTofu's own locking no longer protects the state, so the controller locks at
assignment and fences late writers, and plans take no lock, relying on OpenTofu's stale-plan
check. Plaintext state sits on the agent's disk for an attempt, as saved plans already do. A
machine lost during an apply loses what finished since OpenTofu's last write, up to its fixed
minimum of 20 seconds or the length of one resource, the same as with a remote backend. The
`local` backend's intermediate writes also omit an apply's first resource when it starts with
no state at all, so a guest without state gets an empty seed instead; measured on 1.13.2,
that makes them match what an `http` backend receives.
