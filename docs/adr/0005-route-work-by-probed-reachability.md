# Route work by reachability the controller probes, not by location labels

Agents at different sites reach different endpoints. Labels could steer work to the right site, but they have to
be kept up to date by hand for every new target, and nothing checks them. Letting agents discover targets and
claim work themselves would create a second scheduler and give agents a say in where work goes.

Instead, the controller remains the only scheduler and asks. For each endpoint a queued step connects to, as
derived from its action and released credentials, it probes the connected agents from that step's context. It
then assigns the step only to an agent with fresh positive evidence. Probes carry no credentials, target only
endpoints from trusted definitions and dispatchable work, and never block dispatch. Evidence lives in memory with
the agent's session.

This decision fails closed. An agent that cannot report reachability gets no work that connects anywhere. A
context that cannot be probed faithfully, such as one behind a proxy, waits rather than being assumed reachable.
Positive evidence shows a route and a protocol, not identity: an SSH answer is not the pinned host. The step's
own connection still enforces the pin before it authenticates, so a duplicate private address at another site
leads to a refused connection, not a change to the wrong machine.

The costs: a server upgrade makes network work wait until agents are upgraded. Container runtimes start a short-lived
relay container per probe. A pipeline's OpenTofu stages, whose connections are the workflow's own, are routed without
evidence and are reported as unchecked. Labels remain as optional constraints.

Details: [worker routing](../worker-routing.md).
