# Pipeline end to end

`run.sh` runs a service's pipeline through the backend and a real agent, with real OpenTofu and a
real `ansible-playbook`, against a throwaway SSH container. The guest is provisioned against the
mock Proxmox first. No real infrastructure is involved.

```sh
just e2e-pipelines          # the Ansible and OpenTofu images, in rootless podman
just e2e-pipelines local    # the same programs run directly, as the internal agent does
```

Each run starts its own PostgreSQL and picks free ports, so it can run beside anything else that
uses the development database. Logs stay in `.dev/pipelines`.

It proves, against real tools:

- A service waits for its guest's provision and setup, and says so.
- Every stage is planned and previewed, and nothing changes before the one approval, which names
  one document covering them all. A wrong digest is refused.
- Stages apply in order, each OpenTofu stage in a state of its own, with secrets, inputs and the
  service's folder reaching the playbook. No secret reaches a log, evidence, or the agent's disk.
- Each input hash gets one automatic run.
- With an automatic policy a new commit deploys by itself.
- A failed stage stops the rest, leaves the stages before it applied, and marks neither the guest
  nor anything else. Retrying continues from that stage with no new approval.
- Deleting a service's folder shows it as missing, starts nothing, and removes nothing.
