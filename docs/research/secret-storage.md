# Secret storage: Jenkins and GitLab reference points

**Reviewed: October 14, 2026.** Scope: how Jenkins stores, references, and
delivers credentials, compared with GitLab CI/CD variables and the existing
`nodes` automation, to inform Octopushy's variable/secret store. Official
documentation and local repository review, not a security audit or a tested
implementation. The implications are engineering proposals, not decisions.

## Conclusion

**Copy Jenkins' interface, not its key storage.** Jenkins gets the usage model
right: definitions reference a stable credential ID, never the value; a job
binds the value only for the duration of a step, as an environment variable or
a file; values are masked in logs on a best-effort basis. [J2], [J3]

Its storage model is weaker than Octopushy should accept. The key-encryption
key sits unencrypted beside the data, values use unauthenticated AES-CBC, and
encrypted values are bound to one instance, so moving them is awkward. [J1], [J4]

GitLab's variables are the closest match to the requested model: one key/value
list where each entry has a visibility setting and a delivery type. [G1]

## Jenkins

**Storage.** `$JENKINS_HOME/secrets/master.key` is a key-encryption key protecting
the other keys in that directory, such as `hudson.util.Secret`. Values use AES in
CBC mode with PKCS#5 padding and random IVs. The master key is stored
unencrypted; the documentation states it "does not protect against attackers with
direct access to that file", recommends `0700` on `secrets/`, and suggests
excluding that directory from backups. [J1]

**Credentials.** Types include secret text, username/password, secret file, SSH
private key, and certificates. *Global* scope is for jobs; *System* scope is for
the controller's own functions, such as agent connections. Pipelines handle
credentials "only ... via their credential IDs"; an ID cannot be changed once
set. [J2]

**Use-site binding.** `withCredentials` defines environment variables "active
within the scope of the step". A file credential is copied to a temporary
location and deleted when the build completes, but concurrent builds on the same
node can read it. Log masking "could of course be trivially circumvented", and
mangled values are detected only on a best-effort basis. [J3]

**Configuration as code.** JCasC resolves `${KEY}` from `/run/secrets` files, a
properties file, Vault, cloud secret managers, or environment variables, which
it calls unsafe for secrets. Its encrypted values are tied to one instance's key.
*Strict secret resolution* aborts a reload with unresolved secrets instead of
silently using empty strings. [J4]

## GitLab CI/CD variables

Each variable has a visibility: *Visible*, *Masked* (replaced in logs; at least
8 characters, no spaces), or *Masked and hidden* (never revealed in the UI
again; only selectable at creation). *Protected* limits a variable to protected
branches and tags; *File* type writes the value to a temporary file and exposes
its path. Variables can be scoped to instance, group, project, and environment.
GitLab states masking "is not a guaranteed way to prevent malicious users from
accessing variable values". [G1]

## How `nodes` uses Jenkins credentials today

- **IDs live in Git, values do not.** `homelab/config/dns/providers.json` names
  credential IDs such as `homelab-technitium-api-token-home`; jobs bind them with
  `string(...)` or `file(...)` inside `withCredentials`.
- **Per-target naming convention.** Each Proxmox host has a credential named
  `homelab-proxmox-token-<host-id>`, and jobs bind only the token for the
  validated target host.
- **A wizard lists required IDs, never values.**
  `jenkins/scripts/setup-proxmox-credentials` reads the host catalog, shows every
  required credential ID, and opens the form for each; it never reads or stores
  token values.
- **Non-secret settings are stored as credentials.** The `liveVerified` flags are
  credentials, suggesting a need for plain values kept outside Git.
- **Renames need a transition.** The README says to keep the old credential until
  builds of older revisions finish.
- **Git publication has guardrails.** The updater using `homelab-git` refuses a
  stale tested base, limits its patch to two generated files, and never
  force-pushes.

Sources: `homelab/config/dns/providers.json`, `jenkins/service-planner.Jenkinsfile`,
`jenkins/scripts/setup-proxmox-credentials`, `services/jenkins/README.md`.

## Implications for Octopushy (proposal)

| Observed behavior | Octopushy proposal |
|---|---|
| Definitions reference credentials by stable ID | Git references store keys only; values never enter Git |
| One value, bound as env var or file at the use site | Delivery form belongs to the Git binding, not the stored entry |
| Master key unencrypted beside the data | Key-encryption key kept outside the database and its backups |
| Unauthenticated AES-CBC | Authenticated encryption bound to the key name and version |
| Encrypted values tied to one instance | Portable encrypted export bundle independent of the instance key |
| Masking is best effort | Mask, but also keep values out of logs by design; never rely on masking |
| Unresolved secrets can become empty strings | Unresolved reference blocks the operation, never substitutes an empty value |
| Visible / masked / hidden settings | Plain or secret visibility; secret is write-only and cannot be downgraded |
| Temporary secret files readable by concurrent builds | Per-attempt private directory, wiped after the attempt |
| Renaming requires waiting for old builds | Refuse deleting a key still referenced by any in-flight run's revision |
| Wizard lists required IDs | Snapshot index shows referenced-but-missing and unused keys |

## Sources

- **[J1] Jenkins — Storing secrets (developer documentation):**
  https://docs.jenkins.io/dev-docs/security/secrets.html
- **[J2] Jenkins — Using credentials:**
  https://www.jenkins.io/doc/book/using/using-credentials/
- **[J3] Jenkins — Credentials Binding pipeline steps:**
  https://www.jenkins.io/doc/pipeline/steps/credentials-binding/
- **[J4] Jenkins Configuration as Code — Handling secrets:**
  https://github.com/jenkinsci/configuration-as-code-plugin/blob/master/docs/features/secrets.adoc
- **[G1] GitLab — CI/CD variables:** https://docs.gitlab.com/ci/variables/
