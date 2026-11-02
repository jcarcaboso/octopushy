# Services end to end

`run.sh` deploys the `ha-db` example through the real product: the backend, a real agent, real OpenTofu with the real
`proxmox-lxc` root and bpg provider, and a real `ansible-playbook`, against a throwaway systemd container made from the
pinned Debian 13 template, with real PostgreSQL 17 and real VictoriaMetrics in it. Only Proxmox is a stand-in, so this proves
nothing about Proxmox, or about an unprivileged LXC container.

```sh
just services            # as the internal agent runs tools; needs /tmp on a tmpfs
just services podman     # the Ansible image, built here
```

It installs for real, so it needs outbound access: Debian's mirrors, `github.com`, the OpenTofu registry and
`download.proxmox.com`. It has its own PostgreSQL and free ports, and removes what it starts. `KEEP=1` leaves the containers
for a look. Logs stay in `.dev/services`.

It proves, against real tools:

- Provisioning hands Proxmox the login key and the startup order, and `access.hostKey` has no scan behind it: setup blocks
  on the unset pin, naming it, until it is set from inside the container, as `pct exec` would.
- Services wait for setup, say so, and have run nothing until then. They wait again for trust of their protected secrets.
- Two services take turns on their guest, and each has its own history.
- The allowed address logs in. The wrong address is refused by `pg_hba.conf`, with the right password. A wrong password is
  refused. VictoriaMetrics refuses queries without its password and with a wrong one.
- The relabel rule keeps `ha_value` and drops the rest. The dump is whole, and restores.
- A second run changes no file, restarts nothing, and loses no data.
- Neither password is in attempt logs, a run's evidence, the agent's disk or logs, the server's logs, the repository, the
  state, Octopushy's database as text, or a command line sampled on the agent and the guest. On the guest, the PostgreSQL password is nowhere
  and the VictoriaMetrics password is in one file of mode 0600.
- A change to one service runs that service only, and configures nothing else.
- A failed service leaves its sibling and its guest in sync.
- `deploy` refuses to lower retention and says what to do. `shrink-retention` waits under `destructive: manual`.
- A password in the store alone starts nothing. A commit raising `credentials_revision` rolls it, and the old one stops working.
- A real OpenTofu plan that replaces the stateful container fails before approval, with no apply, and says nothing of `-exclude`.
- Deleting a service's folder waits for a person, stops the service, and leaves its data on disk.

What is not here: real Proxmox, an unprivileged container, and the Proxmox firewall.
