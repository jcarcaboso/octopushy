# Recorded tool output

What the real tools printed for the inputs in `contracts/agent/v1/examples`, which the fakes in
`contract_outputs.rs` print again. Recorded once in the agent image (`tofu` 1.13.1, ansible-core
2.21.5) and committed; the examples' README says how they are used.

- `tofu-version.json`, `tofu-show.json`: `tofu version -json`, and `tofu show -json` of the plan
  of `examples/lab/modules/opentofu/proxmox-vm` with the variables of `opentofu/preview.input.json`,
  bpg/proxmox 0.117.0. A create needs no refresh, so no Proxmox was reached.
- `tofu-output.json`: `tofu output -json` after an apply of a root with the outputs of
  `proxmox-vm` (`vm_id`, `ipv4_addresses`, `mac_addresses`) and the values a created VM has, since
  nothing could be applied to a Proxmox.
- `ansible-version.txt`, `ansible-list-tasks.txt`, `prepared.json`: `--version`, `--list-tasks` of
  the example lab's `configure.yml`, and what its `prepare.yml` wrote.
- `recap.json`, `recap-outcome.json`: the line of the `octopushy_recap` callback, after
  `octopushy-recap: `, of a playbook run on the agent with `-c local`: one that changed a file and
  skipped a task, and one that reported the declared outcome `deferred` as the example lab's
  `tasks/activity.yml` does.
- `recap-reports.json`: the same line from a playbook that reported `certificate_sha256` and
  `cluster_role` with `set_stats` as `octopushy_reports`, recorded with ansible-core 2.21.5 and
  `-c local` on November 21, 2026.
