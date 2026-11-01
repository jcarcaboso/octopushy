# Ansible end to end

`run.sh` configures a throwaway SSH container with a real `ansible-playbook`, through the
backend, a real agent and the mock Proxmox. No real infrastructure is involved.

```sh
just e2e-ansible          # the Ansible image, built here, in rootless podman
just e2e-ansible local    # the same program run directly, as the internal agent does
```

`local` needs `/tmp` on a tmpfs, because the program refuses to run otherwise. Each run starts
its own PostgreSQL and picks free ports, so it can run beside `just e2e` and whatever else uses
the development database. Logs stay in `.dev/ansible`.

It proves, against real tools:

- A provisioned guest is configured by itself, once, with what `prepare` resolved.
- A secret var holding `{{`, a quote and a backslash, and a secret file, arrive intact.
- A task that prints secrets by mistake is masked. Neither secrets nor keys are in attempt logs,
  a run's evidence, the agent's state, the guest, or a command line, and no secret directory stays.
- A repository's `ansible.cfg` and a play's vars cannot turn off host key checking, replace the
  `ssh` binary, or set a log file.
- A task whose templated name holds a secret leaves it in neither evidence nor the agent's disk.
- A declared outcome waits for approval, and approving runs the action once more.
- An action can reuse what the last successful run prepared.
- A wrong pin fails, says so, and runs nothing.
- A killed agent or a lost guest leaves the run uncertain, and the guest's lock stays. That lock
  then stops the next run as a known failure.

`test_runner.py` tests `octopushy-ansible` with a fake `ansible-playbook`, and runs in
`scripts/checks/offline.sh`.
