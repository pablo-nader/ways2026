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

- [x] **T1 — Backend: `dominios`, host resolution, session guard, login by username.**
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
  - Done as two work-unit commits (route: delegated writer; triggers: 2+ non-trivial files and
    preparation reading): `c1a1586a` (table, RLS, platform ABM, resolver, backstops) and `d84153a6`
    (middleware, `IDominioActual`, guard, login by username, `GET /api/publico/sitio`).
  - Implementation notes that differ from the plan above: `ux_dominios_host` is UNIQUE only among live
    rows (`WHERE deleted_at IS NULL`, like `ux_usuarios_usuario`), so a deleted host can be loaded
    again; the middleware answers its own errors (it runs before `UseExceptionHandler` and before the
    MCP connector, which rewrites `Request.Host` on its routes); `/api/salud` and `tauri.localhost`
    are exempt/platform; `GET /api/publico/sitio` only reads `IDominioActual`, it does no query of its
    own; the resolver reads `dominios` in Login mode from its own DI scope and the tenant name with the
    platform context.
- [x] **T2 — Web: login by username or mail, platform screen for domains.**
  Route: delegated (writer trigger: Login page, api client, platform page, tests). PR 2.
  - Login reads `GET /api/publico/sitio` and shows "Usuario o mail" under a tenant host.
  - Platform tenant screen gets a "Dominios" section: list, add, mark verified, delete.
  - Done as two work-unit commits: `09b62edb` (login by usuario or mail) and `d355db04` (Dominios
    screen). Implementation notes that differ from the plan above: there is no tenant detail page, so
    "Dominios" is its own Root-only page at `/organizacion/dominios` (menu entry under Organización)
    with a tenant selector built from `opcionesDeTenantAsignable`; the login field routes by `@`
    (the server does not forbid `@` in a username, so such a user logs in with the mail); if
    `GET /api/publico/sitio` fails the form falls back to mail-only with a notice; `AuthContext.iniciarSesion`
    now takes `{ mail } | { usuario }`.
- [x] **T3 — Web: routes `/admin` (current app), `/online` (storefront shell) and `/` (landing shell).**
  Route: delegated (writer trigger: Vite entries, router basename, API fallback, redirects, tests).
  PR 3.
  - Vite entries: `index.html` → landing shell, `admin.html` → current app with
    `basename="/admin"`, `online.html` → storefront shell. `pos.html` unchanged.
  - API: `MapFallback` serves the entry by path prefix; old paths (`/login`, `/pos`, `/descargar`,
    and every back-office route) redirect to `/admin/...` for a transition period.
  - Platform host (`aipos.site`): `/` redirects to `/admin`.
  - Done as two work-unit commits (route: delegated writer; triggers: 2+ non-trivial files and
    preparation reading): `5f414fbf` (web: entries, basename, dev rewrite, shells) and `d484920f` (API:
    fallback by prefix, redirects, docs 07 and 08). Implementation notes that differ from the plan above:
    the list of old top-level routes is `RutasDeLaSpa.RutasHeredadasDelBackOffice` (26 segments) and
    `App.tsx` is its source of truth, enforced by `RutasDeLaSpaTests` (it parses the `path="/..."` of
    `App.tsx`); the redirect is a middleware before `UseStaticFiles` (the static default file would
    otherwise serve the landing on the platform root), not part of `MapFallback`; the only raw anchor to
    the back office, in `Gastos.tsx`, became a router `Link`; the dev server and `vite preview` rewrite
    `/admin/*` and `/online/*` with a plugin whose rule lives in `src/config/entradasDeVite.ts`.
  - Open for the desktop app (`src/Ways.Desktop/scripts/sync-frontend-local.mjs`, not touched): it
    copies everything in `dist/` except `index.html`, so `admin.html` and `online.html` now also land in
    `ui/` (not ignored by that `.gitignore`, harmless at runtime). Skip them in that script.

## Verification evidence

T1 (commits `c1a1586a`, `d84153a6`):

- `dotnet build src/Ways.Api/Ways.Api.csproj -warnaserror --no-incremental`: 0 warnings, 0 errors.
- `dotnet test tests/Ways.Domain.Tests`: 1124 passed (RED observed first: the new tests did not compile
  before `Dominio` / `PoliticaDeDominio` existed).
- `dotnet test tests/Ways.Application.Tests`: 785 passed (golden `inventario-de-dependientes.txt` gains
  `Tenant | dominios | id_tenant | marcado`; the three `ServicioDeDominios` writes join the frozen
  list of non-retrying writes).
- `dotnet test tests/Ways.IntegrationTests` (the integration project is `Ways.IntegrationTests`, there
  is no `Ways.Api.Tests`): 3214 passed, 0 failed, 26 m 45 s, run once, no other suite in parallel.
  First run had 32 red in `ConectorMcpFlujoTests`: the connector rewrites `Request.Host` to its public
  URL on its own routes, so the first placement of the middleware (after `UseExceptionHandler`) saw an
  unknown host. Fixed by running it right after `UseForwardedHeaders`, as planned.
- `dotnet format --verify-no-changes` over the changed `.cs` files: only the known baseline noise
  (IDE0008, ENDOFLINE, IMPORTS, and WHITESPACE on lines this task did not write).
- Commit 1 checked on its own in a detached worktree: builds, Domain/Application tests green, and the
  `DominiosPlataforma`, `SuperficieDeAutorizacion` and `UsuariosYLogin` integration tests green.

Mutation evidence (mutate the clause, run the 61 `Dominios`/`SuperficieDeAutorizacion`/
`PlataformaHostsConfigurados` tests, watch it go red, revert; the Domain mutants ran on the 1124):

| Mutant | Red tests |
|---|---|
| guard: `idTenantDeLaSesion == idTenantDelHost` -> `true` | cookie cross-tenant, bearer cross-tenant (Domain: 2) |
| guard: `... == ...` -> `false` | cookie, bearer, login by usuario (same tenant rejected) |
| guard: drop `esSesionDePlataforma` / drop `idTenantDelHost is null` | Domain: 2 / 1 |
| resolver: drop `VerificadoEn != null` | unverified host, verify-then-resolve |
| resolver: drop `IgnoreQueryFilters(["Tenant"])` | 13 (every resolution) |
| resolver: tenant dado de baja still resolves | tenant-de-baja |
| migration: drop `dominios_login_lectura` | 14 (resolution + RLS login test) |
| no `Invalidar` on verify / on delete | verify-then-resolve / delete-then-404 (one each) |
| login by usuario without `id_tenant` | login por usuario (two tenants, same usuario) |
| login by mail not scoped to the host tenant | mail of another tenant, root at a tenant host |
| `AgregarAsync` with the retrying strategy | transient `40001` test (`Intentos` 2) |
| invalid body validated before the tenant 404 | 404-first test |
| no `host_duplicado` translation | two concurrent altas of the same host (rendezvous) |
| principal flag not moved | move-principal test |
| `/api/salud` not exempt | health test |
| endpoints without `SoloPlataforma` | authorization test + `SuperficieDeAutorizacion` GET walker |

Not covered and left honest: no audit rows for domain changes; the 60 s cache lifetime itself is not
tested (only its invalidation), and with several instances another one can serve a stale answer for up
to 60 s.

### Judgment-day of T1 (target 1db684e8, round 0, 2026-10-11)

Judges A and B: no CRITICAL or HIGH finding. `JUDGMENT: APPROVED`. Informational ledger
(not fixed, recorded as follow-ups):

- Both: `ServicioDeDominios.AgregarAsync` accepts a host that is also a platform host
  (`aipos.site`, `localhost`, `tauri.localhost`, any `Plataforma:Hosts`); it verifies fine but
  never resolves because the middleware checks platform hosts first. Follow-up: reject it with
  400 `host_de_plataforma`.
- Both: an unknown host costs a DI scope plus a query per request; the cache holds only hits.
  Follow-up: cache the negative result for the same 60 s and consider a per-host single-flight.
- Both: `DominioActual` defaults to platform, so `SesionCorrespondeAlHost` fails open when the
  middleware did not run (not reachable today). Follow-up: tri-state "unresolved".
- A: a tenant that ever had a domain row can never be dado de baja (`marcado` dependent, raw SQL
  ignores `deleted_at`). Follow-up with the owner.
- B: login by `usuario` makes account lockout reachable by guessing a username (5 failures).
  Accepted trade-off of the owner's request; mitigation (per-IP throttle) is a follow-up.
- B: `login-dispositivo` ignores the host's tenant; the guard rejects the cookie on the next
  request, the bearer stays valid on the device's own domain. Follow-up: scope it like `/login`.

T2 (commits `09b62edb`, `d355db04`), run from `src/Ways.Web`:

- `npx tsc -b`: exit 0, no output.
- `npx oxlint`: exit 0, 16 warnings, all `only-export-components` already on the branch (the
  `AuthContext.tsx` one moved from line 14 to 15 with the new import), no errors.
- `npx vitest run` (whole suite, once, no other run in parallel): 175 files, 4495 tests passed, 136 s.

Mutation evidence (mutate the clause, run the colocated tests, watch them go red, revert byte-exact):

| Mutant | Red tests |
|---|---|
| login: swap the `@` predicate | 4 unit tests plus both component tests that assert the request body |
| login: drop `admiteUsuario &&` | unit test "bajo la plataforma todo es un correo" |
| login: drop the generation gate of the site read | "una respuesta vieja del sitio no pisa a la lectura más nueva" |
| login: drop the `enviandoRef` guard | "dos envíos del mismo tick salen como una sola request" |
| dominios: drop the generation gate in `cargarDominios` | "una lista vieja que llega tarde no pisa la del tenant elegido después" |
| dominios: `elegirTenant` without bumping the generation | same test |
| dominios: drop the ref guard in `agregar` / `verificar` / `eliminar` | one same-tick double-fire test each |
| dominios: drop `limpiarMensajes()` in `elegirTenant` / `agregar` / `verificar` / `eliminar` / `pedirEliminar` / `cancelarEliminar` | one test each (rule 16) |
| dominios: send `esPrincipal: false` / skip `trim()` | "agrega el host sin espacios con su marca de principal…" |

Not covered and left honest: the "Dominios" screen has no end-to-end test against the real API (the
component tests mock `api/cliente`); the date in the verification column uses the browser's local
time zone.

### Judgment-day of T2 (target 7d2a0d51, round 0, 2026-10-11)

Judges A and B: no CRITICAL or HIGH finding. `JUDGMENT: APPROVED`. Coincident warnings fixed in
`67782536` (voseo copy in Dominios; the list is re-read after a 404 or an uncertain write failure
while the message stays; comment matched to the code; dead `setCargandoDominios(false)`
removed; "Marcar verificado" label-in-name). Verified with `npx tsc -b`, `npx oxlint` and the
full `npx vitest run` (175 files, 4500 tests) plus mutation evidence on the three re-reads; no
new judge round. Left as follow-ups: the server allows `@` in a username, so a username with
`@` can only log in by mail (document or forbid server-side); a failed add leaves `aria-invalid`
on the Host field for non-host errors; "Reintentar" after a failed list read clears a typed host.

T3 (commits `5f414fbf`, `d484920f`):

- `npx tsc -b`: exit 0, no output. `npx oxlint`: exit 0, only the `only-export-components` warnings
  already on the branch. `npx vite build`: `dist/` holds `index.html`, `admin.html`, `online.html`,
  `pos.html`; `pos.html` is byte-identical in source and still loads `/src/pos/main.tsx`.
- `npx vitest run` (whole suite, once, no other run in parallel): 180 files, 4527 tests passed, 125 s.
- `dotnet build src/Ways.Api/Ways.Api.csproj -warnaserror`: 0 warnings, 0 errors.
- `dotnet test tests/Ways.IntegrationTests --filter "FullyQualifiedName~Fallback|...~Rutas|...~Dominios|...~Salud"`:
  196 passed. Full `dotnet test tests/Ways.IntegrationTests` (once, alone): 3300 passed, 0 failed, 28 m 18 s
  (3214 of T1 plus the 86 new).
- Smoke: `vite` dev and `vite preview` answer `/` with the landing entry, `/admin`, `/admin/articulos/edit/3`
  with `admin.html`, `/online/x` with `online.html`, `/pos.html` with the POS entry.
- `dotnet format --verify-no-changes` over the new `.cs` files: only the known baseline noise (IDE0008,
  ENDOFLINE, IMPORTS); the one real hit (IDE0047) was fixed.

Mutation evidence (mutate the clause, run the 86 `RutasDeLaSpa*` tests, watch them go red, revert):

| Mutant | Red tests |
|---|---|
| root redirect without `EsPlataforma` | tenant root serves the landing (1) |
| root redirect with `!EsPlataforma` | platform root x3 methods/queries, every platform host, tenant root (7) |
| `EsRutaHeredada` matches any segment, not the first | `/api/pos`, `/api/login`, `/admin/pos`, `/online/pos`, `/assets/pos`, `/admin/login`, prefix-served entries (13+) |
| redirect without the GET/HEAD gate | POST `/login` and POST `/` redirect tests (2) |
| fallback without the `/api` guard | `/api/no-existe`, `/api/pos`, `/api/login` and the three old `RutaDeRespaldoTests` (6) |
| `basename` removed from `BrowserRouter` | both `App.test.tsx` cases |

Not covered and left honest: no end-to-end check against a real `vite build` output served by the API
(the tests write their own `wwwroot`); the dev plugin is proven by its pure function plus a manual smoke,
not by a Vitest run of the server; a missing static file with an extension (`/assets/x.js`) still gets
the framework's 401 and not the landing, as before.

## Delivery slices

- PR 1: T1. PR 2: T2 (stacked on PR 1). PR 3: T3 (stacked on PR 2).

## Next step

Feature `landing-del-comercio` (the real landing and the storefront content). T3 waits for its
judgment-day round before PR 3.
