# Fixture installation trust

`fixture.sh` is sourced only by the existing end-to-end harnesses. Calling
`trust_fixture_installations /absolute/fixture/path EXACT_COMMIT` waits until
that locally authored fixture commit is loaded, then trusts each valid Proxmox
installation with untrusted endpoints through the normal API. It requires the
caller's authenticated `call()` function and `api` base URL. It is not an
auto-trust daemon or a server-side bypass.

The helper checks the configured fixture path, `main` branch, exact loaded
commit and current-source metadata, and submits exactly the endpoints the
inventory shows. Never use it for an ordinary lab repository.

The browser demo does not call it: trust each installation on its page, then
approve infrastructure changes separately.

The offline tests mock the HTTP boundary and do not start a controller, read
credentials or contact infrastructure:

```sh
PYTHONDONTWRITEBYTECODE=1 python3 -m unittest discover -s tests/installation-trust -v
```

They require the existing harness tools Bash and `jq`; CI includes them in
`scripts/checks/offline.sh`.
