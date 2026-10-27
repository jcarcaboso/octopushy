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

Remove the one-time handoff file after successful login. For an assistant,
provide it through the application's private secret entry, not ordinary chat.
The public CA option is unnecessary when the controller uses a publicly
trusted certificate. There is no TLS-verification bypass.

The CLI saves a mode-0600 credential under the current user's private directory:

```text
$XDG_CONFIG_HOME/octopushy/operator/credential.json
```

It uses `$HOME/.config` when `XDG_CONFIG_HOME` is absent. `--config` selects a
different private file. Shared-readable files, symlinks and hardlinks are
refused. The saved origin cannot be overridden per request. Redirects and
automatic HTTP retries are disabled.

## Use the existing API

```sh
octopushy operator request /api/inventory
octopushy operator request /api/managed-worker-preparation
printf '%s\n' '{"draining":true}' |
  octopushy operator request /api/agents/AGENT_UUID --method PATCH --json-stdin
```

Creating a grant does not trust a repository revision or approve any run.
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

See [managed worker OS updates](managed-worker-updates.md) for the update flow.
