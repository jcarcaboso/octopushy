# A service that does not speak HTTP is checked by a TCP connection

A database, such as the Postgres that Home Assistant's recorder uses, had no uptime check: links were `http` or `https`,
and agents checked nothing else of a service. A link may now be `tcp://host:port`. An agent that announces
`uptime-tcp-v1` opens a connection to it from its own process and closes it at once, having sent nothing: up when it
opens. It is a separate capability so that an older agent, which would answer an unknown protocol as an error, is
never asked; until an agent that can is connected, the check stays pending.

## Considered options

- **A check per protocol**, such as Postgres's SSL request answered with `S` or `N`. It proves the service itself
  answers, still without a credential, but every kind of server would need its own, and the engine would grow a list of
  products. A port that accepts a connection is the generic part, and readiness stays with the container's healthcheck
  and the workflow's verification.
- **A sidecar serving HTTP** from `pg_isready`. No engine change, but every database would carry another container and
  port.

## Consequences

- Up says that something listens, not that it is the service, nor that it is ready. A restart that leaves the port
  closed is down.
- Only the agent process makes the connection, never a tool container: a container's relay may accept the probe's end of
  the connection before it has opened its own, so it cannot say that one opened.
