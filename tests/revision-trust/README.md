# Fixture execution trust

`fixture.sh` is sourced only by the existing end-to-end harnesses. Calling
`trust_fixture_revision /absolute/fixture/path EXACT_COMMIT` explicitly
authorizes that locally authored fixture revision through the normal API.
It requires the caller's authenticated `call()` function and `api` base URL.
It is not an auto-trust daemon or a server-side bypass.

The helper checks the configured fixture path, `main` branch, exact loaded
commit and current-source metadata. It submits the exact review commit and
comparison basis, refusing unavailable/truncated/ineligible reviews. A stale
POST fails rather than fetching and trusting some other revision. Never use it
to trust an arbitrary remote head or an ordinary lab repository.

The browser demo does not call it: review/trust each revision in the Repository
page, then approve infrastructure changes separately.

The offline tests mock the HTTP boundary and do not start a controller, read
credentials or contact infrastructure:

```sh
PYTHONDONTWRITEBYTECODE=1 python3 -m unittest discover -s tests/revision-trust -v
```

They require the existing harness tools Bash and `jq`; CI includes them in
`scripts/checks/offline.sh`. Backend API/dispatch/migration tests and frontend
review interactions are separate suites. No live Proxmox harness is implied by
these helper tests passing.
