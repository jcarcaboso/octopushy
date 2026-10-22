# Octopushy frontend

The web interface for Octopushy: Vite, React 19, TypeScript, react-router, and
TanStack Query, with a typed client generated from the backend's OpenAPI document.
In production Caddy serves the built files and forwards `/api` to the backend, so the
browser sees a single origin and the session cookie needs no CORS.

## Develop

Requirements: Node 24 and pnpm 12.14.0 (pinned in `package.json`; use Corepack
where available); the backend running on `127.0.0.1:8080` (see the
repository's `justfile`: `just dev-db`, then `just backend`).

```sh
pnpm install --frozen-lockfile
pnpm run dev       # http://127.0.0.1:5173, /api is proxied to http://127.0.0.1:8080
```

On a fresh install the app shows the setup page; the one-time setup code is in the
server's data directory, file `setup-code` (for development: `../.dev/server/setup-code`).

## Generate the API client

`src/api/schema.d.ts` is generated from `../contracts/openapi/octopushy.json` and
committed, so builds never need the contracts directory. After the backend's API
changes:

```sh
pnpm run gen:api   # or `just openapi` from the repository root, which rebuilds the document first
```

Calls go through `openapi-fetch` (`src/api/client.ts`); TanStack Query hooks are in
`src/api/queries.ts`. Problem responses (RFC 7807) become `ApiError`s whose message is
the problem's title.

## Check and build

```sh
pnpm run typecheck # tsc -b
pnpm run lint      # eslint
pnpm test          # vitest (jsdom + Testing Library)
pnpm run build     # typecheck, then vite build into dist/
pnpm run preview   # serve dist/ locally (no /api proxy)
pnpm audit        # reports/fails on known vulnerabilities, including dev dependencies
```

`pnpm-lock.yaml` was imported from the existing npm lock without upgrading
application dependencies; it is the sole frontend lockfile. CI and the Containerfile
both use the pinned pnpm release and frozen installs. Tests use jsdom and mocks;
they do not need a running backend or homelab credentials.

## Container

The image builds from the repository root (the build context) and runs Caddy as an
unprivileged user on port 8081:

```sh
podman build -f frontend/Containerfile -t octopushy-frontend:dev .
podman run --rm -p 8081:8081 -e OCTOPUSHY_BACKEND=backend:8080 octopushy-frontend:dev
```

`OCTOPUSHY_BACKEND` (default `backend:8080`) is the backend's API address as Caddy
reaches it. For a quick local try against a backend on the host:

```sh
podman run --rm --network host -e OCTOPUSHY_BACKEND=127.0.0.1:8080 octopushy-frontend:dev
```

Caddy (`Caddyfile`) serves `/srv` with a fallback to `index.html` for client-side
routes, caches hashed `/assets/*` for a year and revalidates everything else, compresses
with zstd or gzip, and sets a strict Content-Security-Policy (`default-src 'self'`, no
inline scripts, `frame-ancestors 'none'`), `Referrer-Policy: no-referrer`, and
`X-Content-Type-Options: nosniff`. It listens on plain HTTP; put TLS in front of it
(a proxy on a private network keeps its `X-Forwarded-Proto`) or change the site
address so Caddy manages certificates itself.

## Layout

```text
src/api/         generated schema, client, type aliases, query hooks
src/components/  Badge, Card, Callout, Dialog, CodeBlock, DefinitionView, LogViewer,
                 RelativeTime, RunTable, HostTable, GuestTable, LabelEditor, …
src/lib/         formatting, relative time, plan classification, log buffer
src/pages/       one file per page; run/, agents/, variables/ hold their parts
src/styles/      tokens (light and dark), base, layout, components, pages
```
