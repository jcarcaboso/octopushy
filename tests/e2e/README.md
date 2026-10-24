# Mock Proxmox fixtures

This fixture deliberately tests the optional **split-token mode**. The product
also supports one `connection.token`; see [credentials](../../docs/proxmox-credentials.md).
The fixture installation requires two distinct store references:
`proxmox/korriban/read-only-token` and `proxmox/korriban/privileged-token`.
`run.sh` and `demo.sh` import different synthetic identities at those exact
keys. These scripts have no shared-token fallback or automatic credential copy.
The executor wire alias is still `proxmox.token`; the controller chooses its role.

`proxmox.json` declares the read-only identity in `token` and the privileged
identity in `additionalTokens[0]`. The Rust mock accepts only its primary token
and the explicitly listed additional identities. A mocked-HTTPS regression
loads this fixture, authenticates the reader, performs a native operation with
the writer, and rejects an unknown token. No full mock harness was run.

The mock shares the same privileges between both identities; it does not test
real PVE ACL enforcement or establish read-only
provider compatibility. **Real live/full-lifecycle acceptance is blocked:**
bpg/proxmox v0.117.0 can refresh missing disk/EFI formats through
`storage/content/{volume}`, requiring `Datastore.Audit` plus `VM.Config.Disk`
on the owning VM, or broad `Datastore.Allocate` on storage. Fresh create planning
does not prove refresh/update/destroy support. Do not grant mutation permissions
to read-only credentials, fall back to privileged credentials, or use
`-refresh=false`. See the [credential guide](../../docs/proxmox-credentials.md)
and [pinned research](../../docs/research/proxmox-credential-split.md).

Offline fixture/import checks (no services, builds or harness execution):

```sh
python3 -m unittest discover -s tests/e2e -p 'test_*.py'
bash -n tests/e2e/run.sh tests/e2e/demo.sh
```
