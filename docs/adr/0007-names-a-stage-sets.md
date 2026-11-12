# A stage declares the names it sets, and they are opened and checked like links

A service whose workflow sets DNS records, such as compose's `dns` stage with its `dns_records` input, showed nothing of them
in the Inventory: only `links` from `service.yaml` were opened and checked, so most services repeated their name by hand or
had no address at all. Octopushy still knows no DNS, so it cannot read `dns_records` itself.

Instead, a stage of `manages: service` says which of its inputs are the host names it sets, with a path into `{ inputs }`:
`names: inputs.dns_records[].name`. Where that stage runs (a stage filling an interface slot runs only where the
installation binds it), each name is shown on the service, opened from the Inventory, and checked for uptime as
`https://<name>/`, after the service's own `links`. A link that already opens a host replaces that name's URL, so a
service that needs a path, a port or `http` writes a link. Names change nothing a run does.

## Considered options

- **The stage reports what it set**, with `set_stats` through the recap. It shows what happened rather than what was asked,
  but it opens the first channel for a playbook to hand data back, which "no stage outputs" ruled out, and needs storage and
  rules for clearing names. The compose `dns` stage reconciles its records, so after a successful run the inputs and the
  DNS server agree; the difference is only a record edited by hand.
- **A `ui` hint** in `workflow.yaml`. The server reads `ui` only for the form and generated values, and uptime is not
  form work. A stage field also ties a name to the stage that sets it, so a stage that does not run sets none.
- **Shown only, not checked.** A name is where the service is reached, so it is what people want opened and checked.

## Consequences

- The check is over https and accepts only certificates the agent trusts ([uptime](../uptime.md)). A name served with a
  private CA the agent lacks reads as down until the agent trusts it, or the service writes an `http` link for that host.
- The names follow the head revision, as links do: a name is checked from the commit that adds it, before the stage has
  set it, and reads as down until it has.
- `names` is a new field of a stage, refused by earlier Octopushy versions, so a workflows release using it needs an
  Octopushy that reads it.
