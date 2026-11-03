# Operator CLI access

The `octopushy operator` commands use a named operator grant. They do not use a
worker certificate, guest reporting token, enrollment token or Proxmox token.
The controller keeps only a SHA-256 hash of the grant secret.

## Authorize once

Sign into the normal console, open **CLI access**, choose a name and expiry, and
explicitly authorize the grant. Its secret is displayed once. A grant has the
account's API authority, including explicit plan approvals. Keep it as private
as the account password.

Provide the secret through protected stdin, not an argument, shell history,
chat message or environment variable:

```sh
octopushy operator login \
  --server https://your-controller \
  --ca-file /path/to/controller-public-ca.pem \
  --token-stdin < /path/to/private-one-time-grant-file
octopushy operator whoami
```

For a controller with no TLS proxy in front, use its `http://` address and leave
out `--ca-file`:

```sh
octopushy operator login \
  --server http://10.1.0.50:8081 \
  --token-stdin < /path/to/private-one-time-grant-file
```

Remove the one-time handoff file after successful login. For an assistant,
provide it through the application's private secret entry, not ordinary chat.
The public CA option is unnecessary when the controller uses a publicly
trusted certificate. There is no TLS-verification bypass.

## Plain HTTP

Over `http://` nothing is encrypted. **Anyone on the network path can read the
token**, along with the requests and replies, and use it with the account's API
authority, including plan approvals, until it expires or is revoked. That covers
the token when the console creates it, and every CLI request afterwards. It also
covers any proxy set through `http_proxy` in the CLI's environment, which the CLI
honours as usual. The controller keeps only a hash, but that does not protect the
token in transit.

This is accepted for a homelab on a network you control, where the web interface
already has no TLS proxy in front of it. Use HTTPS on any network you do not
trust, and choose the shortest expiry that works over HTTP.

No server setting is needed: the controller accepts operator grants on either
scheme, and a grant works on both. The checks are the same: format, hash,
revocation, expiry, and only a console session can create or manage grants. The
CLI prints one warning on stderr per command when the origin is `http://`, and
never prints the token. Its JSON output on stdout is unaffected.

`--ca-file` is refused with an `http://` origin, because a CA verifies TLS and
plain HTTP has no certificate to verify. A saved credential that combines the two
is refused too.

The CLI saves a mode-0600 credential under the current user's private directory:

```text
$XDG_CONFIG_HOME/octopushy/operator/credential.json
```

It uses `$HOME/.config` when `XDG_CONFIG_HOME` is absent. `--config` selects a
different private file. Shared-readable files, symlinks and hardlinks are
refused. The saved origin cannot be overridden per request. Redirects and
automatic HTTP retries are disabled on both schemes, so an `https://` login never
falls back to plain HTTP.

## Use the existing API

```sh
octopushy operator request /api/inventory
printf '%s\n' '{"draining":true}' |
  octopushy operator request /api/agents/AGENT_UUID --method PATCH --json-stdin
```

Creating a grant does not trust an installation or approve any run.
Mutations still pass through the existing authenticated APIs, trust checks and
saved-plan approval. Requests record the account and named grant as the actor.
Do not automate approvals without reviewing their exact target and fingerprints.

Only a console session can create, list or revoke other grants. A CLI grant
cannot mint replacement grants. Expiry and revocation are checked on each
authenticated request. Revocation does not cancel work already dispatched.

```sh
octopushy operator logout
```

Logout revokes the server grant before deleting its local copy. If the grant
has already expired or been revoked, logout removes the unusable local copy.
Use the console to revoke lost credentials. `login --replace` explicitly
replaces the local file but does not revoke a displaced server grant.
