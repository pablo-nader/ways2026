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
  trusts only what the protected state says.
- Endpoints: start (Admin, returns the authorization URL), callback (anonymous, exchanges the
  code, stores the connection, redirects back to the tenant's web), read state (Admin, never
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
- [ ] T2 — Renewal: background job that refreshes tokens before expiry, rotates the refresh
  token, marks the connection "reconnect needed" when refresh fails. Route: delegated.
- [ ] T3 — Web: connect / state / disconnect UI with its tests. Route: delegated.

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

## Next step

Open the T1 PR (stacked-to-main). Then T2 (renewal job) and T3 (web + the login-CSRF fix above).
