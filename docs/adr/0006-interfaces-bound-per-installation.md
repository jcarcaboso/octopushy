# Bind interfaces per installation, as data a stage declares it fills

A lab can span sites that each run their own shared systems: two houses, each with its own DNS servers. A service
needs whatever its own site provides, so a slot such as `dns` cannot be bound once for the whole lab, and repeating
the servers and their token in every service would put the same values in many places and the token in many hands.

Instead, a binding is a file, `labs/<lab>/interfaces/<slot>/<name>.yaml`, holding inputs and secrets, and an
installation names one binding per slot: `interfaces: { dns: moraband }`. A workflow stage of `manages: service`
declares the slot it fills: `interface: dns`. That stage gets the binding's inputs as one extra var named after the
slot and the binding's secrets as extra vars of their own. On an installation that binds no such slot, the stage is
not run. The workflows repository says what a slot holds, in `interfaces/<slot>.schema.json` at the stage's release,
which validates every binding a service gets.

Octopushy still knows nothing of DNS: a slot is a name, and a binding is data handed to the stages that ask for it.
This replaces binding a slot to a workflow, recorded on November 2, 2026. Where the records go and how they are
written is the stage's own playbook, so swapping DNS servers is one line in the installation, and swapping the kind
of server is a new workflow release, as any other change of what a stage does.

The costs: the stage runs on the service's run, so it connects to the guest like any Ansible stage and reaches the
slot's systems from the agent, delegating its tasks there; routing does not model that second endpoint. A binding's
secrets reach only stages that declare its slot, but every such stage of every service on the installation. Nothing
removes what a stage wrote when a service is deleted, as for any service today. Only Ansible stages fill slots until
OpenTofu stages get secrets.

Details: [workflows, Interfaces](../workflows.md#interfaces).
