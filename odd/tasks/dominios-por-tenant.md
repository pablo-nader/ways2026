# Dominios por tenant y reorganización de rutas (rebanada 0 de la tienda online)

## Objective

Each tenant can point its own domain (for example `waysautoservicio.com`) at Ways. The server
resolves the tenant from the request host before any session exists, the login under a tenant
domain accepts the username or the mail, and the web app is reorganized so that `/` will hold
the public landing, `/admin` holds the current back office and POS, and `/online` will hold the
customer storefront.

## Problem / why

- Today the tenant comes only from the session claim `ways:id_tenant` (`ContextoDeUsuarioHttp`),
  so there is no way to know the tenant on a public page.
- Login is by mail only because the tenant was unknown before login (`docs/08`, "etapa 1").
- The owner wants every commerce to live on its own domain, with the landing at the root and the
  storefront at `/online`, while `aipos.site` stays the platform domain.

## Scope (authorized by the owner, 2026-10-11, autonomous mode)

Branch `pnader/landing-customer-purchase-0d0d67`, stacked PRs to `main`, judgment-day before
each PR, auto-merge plus Auto-fix enabled on each PR (CLAUDE.md, PR validation gate).

Out of scope here: the landing content and the storefront themselves (feature
`landing-del-comercio`), customer accounts, orders, payments, article publication.

## Data model (evidence; no prior approval needed since 2026-10-11)

Table `dominios` (organization scope, like `tenants` and `empresas`):

| column | type | rule |
|---|---|---|
| `id_dominio` | bigint PK identity | |
| `id_tenant` | bigint NOT NULL FK → `tenants` | |
| `host` | text NOT NULL | lower-case, no port; UNIQUE global; CHECK: no scheme, no slash, no whitespace |
| `es_principal` | boolean NOT NULL DEFAULT false | partial unique index: at most one per tenant |
| `verificado_en` | timestamptz NULL | NULL until the platform marks it; unverified hosts do not resolve |
| audit | `EntidadBase` columns | soft delete only |

RLS: standard `dominios_tenant` policy plus `dominios_login_lectura` (`app_modo() = 'login'`),
copied from `usuarios_login_lectura`, because host resolution runs before any session.
No changes in `tenants` or `usuarios`; `aipos.site` (and the development hosts) are configured,
not stored.

## Constraints

- TDD mode: default policy (RED before GREEN when a deterministic runner exists). Runners:
  `dotnet test tests/Ways.Domain.Tests` and `tests/Ways.Api.Tests` (integration, needs Docker;
  never two suites against the same daemon); `npx vitest run` in `src/Ways.Web` (never two
  vitest runs at once), plus `npx tsc -b` and `npx oxlint`.
- Project skills: db-error-backstops, rls-migration-backfills, mutation-proof-tests,
  update-404-first, dto-contract-honesty, claims-match-code, ef-retry-safe-writes,
  single-read-under-lock, react-async-state, web-descriptor-tests, web-test-data-gates.
- Receipt-driven development: off (clone-local). PR gate: judgment-day before the PR.
- Delivery: `auto-chain`, `stacked-to-main`, one PR per task below; forecast ~1.100 authored
  lines in total, ~350 per PR.
- Conventional commits in Spanish, no AI attribution.
- The desktop POS (Tauri, `pos.html` served from `tauri.localhost`) must keep working untouched.

## Tasks

- [ ] **T1 — Backend: `dominios`, host resolution, session guard, login by username.**
  Route: delegated (writer trigger: domain, infrastructure, API, tests). PR 1.
  - Entity `Dominio` + EF configuration + migration with RLS policies.
  - `IDominioActual` (scoped): `EsPlataforma`, `Host`, `IdTenant?`, `NombreComercio?`; resolved by
    a middleware right after `UseForwardedHeaders`, from `Host` without port, lower-case, against
    `Plataforma:Hosts` (configuration; defaults `aipos.site`, `localhost`, `127.0.0.1`) or the
    `dominios` table (verified, not deleted), with a 60 s memory cache invalidated on writes.
    Unknown host: `/api/*` answers 404 `dominio_desconocido`; any other path serves a minimal
    static "dominio no configurado" page.
  - Session guard: a session whose `ways:id_tenant` differs from the host's tenant is rejected
    (401, cookie cleared). Platform sessions are accepted on any host.
  - `POST /api/auth/login` accepts `{ usuario?, mail?, password }`: `usuario` only under a tenant
    host (else 400 `usuario_requiere_dominio`), looked up by `(id_tenant, nombre_usuario)`.
  - `GET /api/publico/sitio` (anonymous, Login mode): `{ esPlataforma, nombreComercio?,
    admiteUsuario }` so the login page knows what to ask.
  - Platform endpoints: `GET/POST/DELETE /api/plataforma/tenants/{id}/dominios`,
    `POST .../dominios/{idDominio}/verificar` (sets `verificado_en`), with 404-first and the
    23505 backstop for the unique host.
  - Tests: unit for host normalization/validation and the guard decision; integration for the
    resolution, the guard, the login by username and the platform endpoints.
- [ ] **T2 — Web: login by username or mail, platform screen for domains.**
  Route: delegated (writer trigger: Login page, api client, platform page, tests). PR 2.
  - Login reads `GET /api/publico/sitio` and shows "Usuario o mail" under a tenant host.
  - Platform tenant screen gets a "Dominios" section: list, add, mark verified, delete.
- [ ] **T3 — Web: routes `/admin` (current app), `/online` (storefront shell) and `/` (landing shell).**
  Route: delegated (writer trigger: Vite entries, router basename, API fallback, redirects, tests).
  PR 3.
  - Vite entries: `index.html` → landing shell, `admin.html` → current app with
    `basename="/admin"`, `online.html` → storefront shell. `pos.html` unchanged.
  - API: `MapFallback` serves the entry by path prefix; old paths (`/login`, `/pos`, `/descargar`,
    and every back-office route) redirect to `/admin/...` for a transition period.
  - Platform host (`aipos.site`): `/` redirects to `/admin`.

## Verification evidence

(recorded per task as commits land)

## Delivery slices

- PR 1: T1. PR 2: T2 (stacked on PR 1). PR 3: T3 (stacked on PR 2).

## Next step

T1.
