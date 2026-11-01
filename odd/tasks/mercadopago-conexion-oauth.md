# Mercado Pago — connection per empresa (OAuth) — stage E1

## Objective

Let an Admin connect the Mercado Pago account of each empresa to Ways with OAuth ("Conectar
Mercado Pago"), store its tokens encrypted, keep them renewed, and disconnect it. This is the
base for every later stage of the Mercado Pago program (E2 stores/POS, E3 QR charge, E4 Point,
E5 manual approval offline, E6/E7 reconciliation).

## Problem / why

The owner wants digital charges in the POS to close the sale automatically and to reconcile the
Mercado Pago balance, fees and outgoing payments against Ways. Every one of those calls needs an
access token of the seller's account. Ways is a multi-tenant SaaS, so each seller must link its
own account without pasting secrets (OAuth), and Ways never holds the money.

## Decisions (owner, 2026-10-31)

- OAuth, not a pasted Access Token (owner: easier for the seller and safer).
- One connection **per empresa** (empresa = CUIT, docs/09 §17; the ARCA certificate follows the
  same rule). A tenant with one empresa gets "per tenant" for free. Two empresas sharing one
  Mercado Pago account is not supported until a real case appears.
- The connection is tied to the empresa's treasury account (`cuentas_tesoreria`) that represents
  that Mercado Pago account; later reconciliation compares one against the other.
- Skipped E0 (sandbox spike) by owner's choice; its open questions stay recorded below and are
  resolved inside the stages that depend on them.

## Mercado Pago facts this stage relies on (verified 2026-10-31, mercadopago.com.ar/developers)

- Authorization URL `https://auth.mercadopago.com/authorization?client_id&response_type=code&platform_id=mp&state&redirect_uri`
  (+ PKCE `code_challenge`/`code_challenge_method=S256`, optional per app, mandatory once enabled).
- Token endpoint `POST https://api.mercadopago.com/oauth/token`, `grant_type=authorization_code`
  (code valid 10 minutes) or `grant_type=refresh_token` (requires the `offline_access` scope).
  Access token lasts 180 days; every refresh returns a NEW refresh token that must replace the
  old one. The response carries the seller's `user_id`.
- Redirect URI must exactly match one registered in the app and be static: per-tenant data
  travels in `state`, never in the redirect URI.

## Design

- Table `conexiones_mercadopago` (operational, tenant-scoped, RLS): empresa, treasury account
  (same empresa), seller `user_id`, access and refresh tokens encrypted with AES-256-GCM
  (pattern of `certificados_fiscales`, master key versioned in configuration, never in the DB;
  AAD binds the blob to tenant + empresa + connection), access-token expiry, state
  (activa / desconectada), who connected and when. At most one active connection per empresa
  and one active connection per Mercado Pago `user_id` across all tenants (later webhooks
  resolve the empresa from it).
- `state`: a DataProtection-protected, time-limited (10 min) payload with tenant, empresa,
  treasury account, user, PKCE verifier and the origin to return to. The callback is anonymous
  (the redirect URI is one fixed host, where the tenant's session cookie may not exist) and
  only reads the protected state to learn where to send the browser back (T3 changed this, see
  "Decisions (T3)": it no longer exchanges anything).
- Endpoints: start (Admin, returns the authorization URL), callback (anonymous, redirects back to
  the tenant's web with `code` + `state` in the URL fragment), complete (Admin, exchanges the code
  inside the session that started the flow and stores the connection), read state (Admin, never
  returns tokens), disconnect (Admin, wipes the token blobs).
- Configuration: `Ways:MercadoPago:ClientId`, `ClientSecret`, `RedirectUri`, URLs of the
  authorization and API hosts, master keys.

## Scope

- Backend: entity, migration, encryption store, OAuth client, the four endpoints, renewal job.
- Web: section in the empresa (or treasury account) screen to connect, see state, disconnect.
- Docs: doc 10 §07 (new table), configuration notes.

## Out of scope (later stages)

Stores/POS in Mercado Pago (E2 — will build on the new `sucursales` from #547), charging (E3/E4),
offline manual approval (E5), reconciliation (E6/E7), webhooks.

## Open questions carried from the skipped E0

- Does the webhook signature validate for QR orders (docs contradict themselves)? → E3.
- Do the reports identify outgoing payments/transfers from the account? → E7.
- Is there an endpoint for the available balance? → E6.
- Refresh-token lifetime is undocumented → T2 treats a failed refresh as "reconnect needed".

## Constraints

- Tokens never in plain text in the DB, logs, responses or audit rows.
- Every new constraint ships its error-translation backstop (`db-error-backstops`).
- Admin only. Other-tenant / missing empresa answers 404 before payload validation.

## Delivery strategy

`ask-on-risk` with the standing chain preference `stacked-to-main`: one PR per task, each through
judgment-day. Forecast ~1,500 authored lines over three tasks; T1 alone may exceed the ~400-line
heuristic because model + encryption + OAuth exchange are one coherent behavior.

## Tasks

- [x] T1 — Backend: `conexiones_mercadopago` (migration, entity, RLS, unique indexes with
  backstops), token encryption store, OAuth client, endpoints start / callback / read /
  disconnect, unit + integration tests, doc 10 §07. Route: delegated (2+ non-trivial files).
- [x] T2 — Renewal: background job that refreshes tokens before expiry, rotates the refresh
  token, marks the connection "reconnect needed" when refresh fails. Route: delegated.
- [x] T3 — Web: connect / state / disconnect UI with its tests, plus the login-CSRF fix (callback only redirects,
  authenticated `POST .../completar` exchanges). Route: delegated.

## Progress

- 2026-10-31: document created; worktree `.claude/worktrees/mercadopago-e1`, branch
  `pnader/mercadopago-oauth-t1` from `origin/main` 44d30768.

- 2026-10-31: T1 done (route: delegated writer). Evidence: `dotnet build Ways.slnx -c Release`: 0 errors;
  `Ways.Domain.Tests`: 2110 passed; `Ways.Application.Tests`: 1508 passed (includes the inventory golden and
  `ModeloDeCuentasDeTesoreriaTests`, both updated); `Ways.IntegrationTests` full suite: 5750 passed in 1 h 18 m
  (includes `SuperficieDeAutorizacion` and `AislamientoDeTenant` guards). Mutation evidence (applied, test red,
  reverted): disconnect `Estado == Activa` guard -> `DesconectarBorraLosBlobs...`; start 404-before-config ->
  `UnaEmpresaInexistente...`; `ResultadoDeCarrera` neutered -> both `SiOtra...Gana...` backstop tests; RLS removed from
  the migration -> `LaPoliticaDeTenant...` and `LaCoberturaDePoliciesEsCompleta`; token kind removed from the AAD ->
  `ElBlobDeAccesoNoSeDescifraComoTokenDeRenovacion`.

## Model summary (T1)

Table `conexiones_mercadopago` (doc 10 §07): tenant + empresa + cuenta de tesorería (composite FK with empresa, via new
alternate key `ak_cuentas_tesoreria_id_cuenta_tesoreria_id_empresa_id_tenant`), `id_usuario_mercadopago` bigint, six nullable
bytea cipher columns, `estado` text ('activa'|'desconectada'), dates. CHECKs: estado, activa => material present with sizes,
desconectada => date + no material. Partial uniques `WHERE estado = 'activa'`: `(id_tenant, id_empresa)` and
`(id_usuario_mercadopago)` (global). RLS standard. Migration `ConexionesMercadoPago`.

## Decisions (T1)

- Parallel `CifradoDeTokensMercadoPago` (own keys `Ways:MercadoPago:*`), fiscal cipher untouched (its tests read its source).
- `state` carries its expiry inside the DataProtection payload, checked against `IRelojDelSistema`
  (`ITimeLimitedDataProtector` needs an extra package and a controllable clock).
- Connection id reserved with `nextval` before encrypting so the AAD includes it.
- No audit rows: the callback has no authenticated actor and the audit catalog needs a new action; the row records
  `id_usuario_conecto` and dates instead. Revisit if the owner wants `mercadopago.*` audit actions.
- Web return route: `/organizacion/empresas?mercadopago=<conectado|cancelado|error|cuenta_en_uso|ya_conectada>&empresa=<id>`
  (no per-empresa web route exists); T3 must read it.
- Invalid or expired `state` -> plain 400 (origin unknown). Origin is `scheme://host` of the authenticated start request
  (Host header; only the signed-in Admin can influence it).
- Disconnect does not call Mercado Pago (no documented revocation). A cuenta or user with any connection row cannot be
  deleted (reference guard). `RenovarAsync` exists in the client with unit tests; no job (T2).
- Empty `Ways:MercadoPago` placeholders added to `appsettings.json`.

## Review (T1)

- 2026-10-31 judgment-day on 2f28455e: **APPROVED** in round one, no CRITICAL from either judge. RDD is off
  (clone-local), so no native review ran.
- Both judges flagged the same WARNING (info, not auto-fixed): the `state` is not bound to the browser that started
  the flow, so an Admin could send the authorization URL to someone else's Mercado Pago owner and, if that person
  consents, link that account to the Admin's empresa (OAuth login-CSRF). Accepted change: T3 moves the completion
  behind the authenticated web — the callback only redirects `code` + `state` to the tenant's origin, and an
  authenticated Admin endpoint completes the exchange after checking that the session user and tenant match the
  state. This also neutralizes judge B's suggestion about the Host-derived origin (the code would only reach a
  session that started the flow).
- Suggestions kept as follow-ups: catch `NotSupportedException` from a non-JSON 2xx in `ClienteOAuthMercadoPago`;
  `ResultadoDeCarrera` evaluated twice in the callback catch; record who disconnected (`id_usuario_desconecto`).

## Model change (T2)

`conexiones_mercadopago` gains the state `requiere_reconexion` and the column `fecha_requiere_reconexion timestamptz NULL`
(migration `RenovacionDeConexionesMercadoPago`: DDL only, no backfill). CHECKs: `ck_conexiones_mercadopago_estado` now admits the
third value; `..._activa` also requires `fecha_requiere_reconexion IS NULL`; new `ck_conexiones_mercadopago_requiere_reconexion`
(date present, `fecha_desconexion` null, the six cipher columns NULL). The partial unique indexes stay `WHERE estado = 'activa'`, so a
`requiere_reconexion` row holds neither slot and T1's start endpoint (it only blocks on `activa`) lets the empresa connect again.
Doc 10 §07 updated.

## Decisions (T2)

- Cipher material is wiped on `requiere_reconexion` (same as `desconectada`): the refresh token is useless and keeping it only widens
  what a DB leak exposes. A decrypt failure (missing master key, tampered row) is NOT treated as a rejection: it is logged and the row
  is left alone, so a configuration mistake never wipes live connections.
- The HTTP call runs while the row lock is held (client timeout 15 s). The alternative (call outside, re-check under lock) would let
  two processes spend the same one-shot refresh token and lose a rotation; with the lock the second waits, re-reads and skips. Cost:
  a disconnect of that empresa waits up to 15 s. Never two MP calls per connection per run (non-retrying execution strategy, one pass).
- Only a 400 whose JSON body has `error: invalid_grant` = `requiere_reconexion` (`mercadopago_renovacion_rechazada`); 5xx/408/429/
  timeout/network = untouched, retried next run. Any other 4xx (invalid_client, 401, 403, 404, non-JSON 400) =
  `mercadopago_credenciales_rechazadas`: row untouched, one Error log, the run stops (judgment-day round 1, below). The code exchange
  is unchanged.
- A `user_id` different from the stored one never swaps accounts: `requiere_reconexion`, new tokens discarded.
- Read endpoint contract: with no `activa`, if the latest connection of the empresa is `requiere_reconexion` it answers
  `{ conectada: false, estado: "requiere_reconexion", idCuentaTesoreria, nombreCuenta, idUsuarioMercadoPago, fechaConexion }`
  (no expiry). A later disconnect brings back the empty state. The record shape did not change (no extra field), T3 reads `estado`.
- Settings: `Ways:MercadoPago:DiasDeAnticipacionRenovacion` (30), `IntervaloRenovacionHoras` (6), `RenovacionAutomatica` (true; the
  integration fixture sets it to `false` so the job never runs in tests). Non-positive values fall back to the defaults.
- The service takes `ahora` as a parameter (tests pass it) and exposes `RenovarConexionAsync` per connection, which the run loop calls.

## Evidence (T2)

- `dotnet build Ways.slnx -c Release`: 0 errors.
- `Ways.Domain.Tests`: 2110 passed. `Ways.Application.Tests` (full, includes the structural guards): 1529 passed. Filtered
  integration set (`MercadoPago`, `SuperficieDeAutorizacion`, `AislamientoDeTenant`, `Cobertura`, `UpDownUp`,
  `InventarioDeDependientes`): 77 passed. The full integration suite was left to CI.
- RED was not observed first for the service tests (they need the new types to compile); mutation evidence replaces it. Mutants
  applied, test red, reverted: selection predicate `<=` -> `>=` -> `LaCorridaSeleccionaSoloLasConexionesDentroDeLaVentana...` and the
  two-tenant tests; post-lock `LeToca` re-check removed -> `UnaConexionYaRenovadaPorOtroProceso...` and
  `UnaConexionFueraDeLaVentana...`; post-lock `Estado == Activa` filter removed -> `UnaConexionDesconectadaEntreLaSeleccion...`;
  4xx-vs-transient branch (`when` guard) widened to every error -> the five `UnaFallaTransitoria...` cases and the isolation test.

## Judgment-day T2 round 1

- JD2-1 fixed: `ClienteOAuthMercadoPago` classifies as a rejection of the connection only a 400 with `"error":"invalid_grant"` (body
  parsed defensively). Every other 4xx except 408/429 throws `mercadopago_credenciales_rechazadas`; the service returns
  `CredencialesRechazadas`, leaves the row alone and breaks the run (`ResumenDeRenovacion.Interrumpida`), logging one Error without
  tokens or secret.
- JD2-2 fixed: after MP accepted the refresh, mismatch handling, `Cifrar`, `SaveChanges` and `Commit` use `CancellationToken.None`; any
  failure there logs an Error ("la rotación se perdió") and returns the new `RotacionPerdida` (counted in `RotacionesPerdidas`), never
  `Fallida`. Lock order unchanged.
- Tests: client unit theory (400 invalid_grant vs. invalid_client / invalid_request / non-JSON / empty / array / nested, 401, 403, 404);
  integration: credentials rejection leaves BOTH tenants' rows untouched, second never called, exactly one Error without secrets
  (4 statuses); SaveChanges interceptor failure -> `RotacionPerdida`, row unchanged, Error log, run counts it; cancellation fired inside
  SaveChanges still persists the rotation.
- Mutation evidence (applied, test red, reverted, green): `invalid_grant` check forced true -> 7 unit cases red; `break` of the circuit
  breaker commented out -> the 4 credentials cases red; `SaveChangesAsync(ct)` instead of `None` -> `UnApagadoDespues...` red.
- Verification: build 0 errors; `Ways.Application.Tests` 1537 passed; integration `MercadoPago` filter 55 passed.

## Open risks (T2)

- If MP rotates the refresh token and our commit then fails (DB down mid-run), the new refresh token is lost; whether MP keeps the old
  one valid is undocumented. The run now reports it as `RotacionPerdida` (Error log); the next run retries the old token and, if MP
  answers `invalid_grant`, the connection goes to `requiere_reconexion`.
- The background loop itself (delay, timer, exception swallowing) is not driven by a test: only `Habilitada`/disabled start is. A fake
  `TimeProvider` package is not referenced in the repo.
- Several app instances each run the job; the row lock makes it safe, not efficient.

## Decisions (T3)

- Login-CSRF closed (T1 judgment-day WARNING, both judges). `GET /api/integraciones/mercadopago/callback` (still anonymous) no
  longer exchanges: it unprotects the `state` only for origin + empresa and 302s to
  `{origen}/organizacion/empresas#mercadopago=completar&empresa=<id>&code=<c>&state=<s>` (or `#mercadopago=cancelado&empresa=<id>`).
  The FRAGMENT (not the query) keeps the code out of server logs and Referer. Invalid/expired state, or a return with neither `code`
  nor `error`: plain 400 as before.
- New `POST /api/empresas/{idEmpresa}/mercadopago/conexion/completar` `{ code, state }` (Admin, same group/policy as `/inicio`, passes
  the non-GET allowlist guard). Order: 404 empresa -> 400 `solicitud_invalida` (empty / over 512 / over 2048) -> 400
  `mercadopago_estado_invalido` -> 403 `mercadopago_estado_ajeno` (empresa and user of the state must equal the route and the session user;
  nothing is exchanged. The tenant is not compared on its own: the route empresa is already resolved inside the session tenant, so matching
  it binds the tenant. A tenant clause was tried and dropped because no test could kill it) -> 503 `mercadopago_no_configurado` -> exchange. Results: 200 read model, 409
  `mercadopago_ya_conectada`, 409 `mercadopago_cuenta_en_uso`, 502 `mercadopago_intercambio_fallido` (every other failure of the
  exchange: MP rejects, empresa/cuenta gone, deactivated cuenta). The 23505 backstops stay in the endpoint (`ResultadoDeCarrera`).
- T1's `?mercadopago=<resultado>` return query is gone; the web reads only the fragment.
- Web: section "Mercado Pago" BELOW the empresas grid (`SeccionMercadoPago`, one `ConexionMercadoPago` card per empresa), rendered only for
  Admin (route also admits Root, but the endpoints need a tenant in the session). Not in the edit modal: the connection has its own
  lifecycle (redirects, disconnect) and does not save with the empresa's "Guardar". Treasury accounts come from
  `GET /api/cuentas-tesoreria`, filtered by empresa + `activo`; a single active account is preselected. Without accounts the card explains
  one must be created and links to `/caja/cuentas-tesoreria`.
- Hash flow: the section reads `location.hash` on mount, clears it with `history.replaceState` BEFORE the first await, then POSTs
  `.../completar` and refreshes that empresa's card. No ref guard for StrictMode: the synchronous clear already makes the second effect run
  a no-op, and a ref would be a guard no test can kill (mutation: removing the clear and the ref fails the StrictMode test with two POSTs).
- "Conectar" does not release `ocupado` after a successful POST (the page navigates away); the URL is only followed when it is http(s).

## Evidence (T3)

- `dotnet build Ways.slnx -c Release`: 0 errors. Integration filter `MercadoPago|SuperficieDeAutorizacion|AislamientoDeTenant`: 76 passed
  (re-run after dropping the tenant clause). `Ways.Application.Tests`: 1537 passed. Web: `npm run build` OK, `oxlint` 0 errors (only existing
  warnings), full `vitest run`: 283 files / 8926 tests passed (a first run alongside the integration suite had one unrelated timeout in
  `GrillaDeItemsDeCompra`, green alone and in the clean full run). RED was not observed first for the endpoint tests (they need the new DTO
  to compile); mutation evidence replaces it.
- Mutation evidence for the session-vs-state check (applied, test red, reverted): without the user clause ->
  `UnStateIniciadoPorOtroUsuario...` red (200 instead of 403); without the empresa clause -> `UnStateDeOtraEmpresaDelMismoTenant...` red.
  Without a tenant clause (the third mutant) all three stayed green, which is why that clause was removed. Web: `limpiarFragmento()` moved after the POST -> `borra el fragmento ANTES del POST` red; clear and ref removed -> the
  StrictMode test and the seven mapped-message tests red.
- Not covered by a test: a second Admin of the same tenant (an Admin cannot create Admins), so the user clause is proven with a `state`
  forged through the protector with the same tenant/empresa and another user id.

## Follow-ups (T3)

- Login-CSRF follow-up from the T1 review: RESOLVED here.
- Still open from T1: catch `NotSupportedException` from a non-JSON 2xx in `ClienteOAuthMercadoPago` (handled only for the new
  `ClienteApiMercadoPago` in E2 T1, which maps it to `mercadopago_respuesta_invalida`; the OAuth client is untouched); record who
  disconnected.
- A 502 `mercadopago_intercambio_fallido` covers several causes (MP rejection, deactivated cuenta); the web shows one generic message.

## Judgment-day T3

- 2026-10-31 on 5d864d06: **APPROVED** in round one, no CRITICAL. Both judges confirm the login-CSRF is closed
  (foreign or forged state → 403 before any Mercado Pago call; no open redirect; code only in the fragment, cleared
  before the first await).
- Shared, low severity (info): after `window.location.assign` the card keeps `ocupado`, so a bfcache restore (browser
  Back from Mercado Pago) leaves it stuck on "Conectando…" until a reload → reset on `pageshow` with `persisted`.
- Judge B only (suspect, info): a failed re-read after connect/disconnect keeps the stale state with no error; the
  callback comment overstates "el código no queda en logs" (the callback's own query still carries it); the fragment
  is cleared only when the Admin section mounts; the open disconnect confirmation ignores `bloqueado`.

## Status

E1 complete: T1 #555 (merged), T2 #558, T3 (this PR). All follow-ups above stay open for a small polish slice.

## Next step

E2 (stores/POS in Mercado Pago, on top of `sucursales`).
