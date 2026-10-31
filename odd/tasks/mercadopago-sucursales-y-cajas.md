# Mercado Pago — stores and checkouts (E2)

## Objective

Register each Ways sucursal as a Mercado Pago **store** and each punto de venta as a Mercado Pago **POS**
(`external_pos_id`), and let an Admin assign a Point Smart terminal to a punto de venta. This is what E3 (dynamic
QR charge) and E4 (Point charge) need to create orders. Builds on E1 (`odd/tasks/mercadopago-conexion-oauth.md`).

## Decisions

- (2026-10-31, from the code map) The Mercado Pago POS is the **punto de venta**: Ways has no "caja" entity apart from
  it; turnos, comprobantes and numeración key on `id_punto_venta`, and an active device is 1:1 with it. The Mercado
  Pago store is the PV's **sucursal** (one store → many POS, matching MP's model).
- (owner, 2026-10-31) The address Mercado Pago requires lives **on the sucursal** (new structured columns), not in a
  Mercado Pago table. The sucursales screen is in the owner's unmerged stack (`sucursales-t2…t4`, PR #557), so the
  address task waits for it to land; work that does not depend on it goes first.

## Mercado Pago facts (verified 2026-10-31, mercadopago.com.ar/developers reference)

- Store: `POST /users/{user_id}/stores` (user_id = the seller from OAuth). Send `name`, `external_id`
  (alphanumeric ≤60) and a full `location` (`street_number` string, `street_name`, `city_name`, `state_name`,
  `latitude`, `longitude`, `reference`) — the docs contradict each other on what is required. `name`, street, city and
  state reject digits/special characters (`INVALID_NAME`, `INVALID_STATE_NAME`): real addresses like "Av. 9 de Julio"
  or accents may be rejected → must be tested live.
- POS: `POST /v2/pos` with `X-Idempotency-Key` (1–64). `store_id` or `external_store_id` required; `external_id`
  letters+digits only, ≤40, unique per seller (409 `pos_already_exists`); `name` ≤45; `config.qr.operating_mode`
  `pdv` (no `url`). Response has `id` and `qr_response`.
- Point: `GET /terminals/v1/list?store_id&pos_id` → terminals `{id "TYPE__SERIAL", pos_id, store_id,
  operating_mode}`; `PATCH /terminals/v1/setup` sets `PDV` (terminal restart needed). Binding a terminal to a store/POS
  is done **in the Mercado Pago app** (scan the terminal's QR), not via API; one terminal per POS.
- Not verified: store/POS update, get, search and delete; whether the seller token needs a scope; rate limits.

## Tasks

- [x] T1 — Calling Mercado Pago as the seller: a service that returns a valid access token for an empresa (renews
  first when it is close to expiry, using the E1 renewal path; `requiere_reconexion`/no connection → typed error), and
  a typed API client base (bearer, idempotency key, error envelope mapping, 401 → connection needs reconnection,
  never logs tokens). Unit + integration tests with the fake MP handler. Route: delegated.
  Done 2026-10-31 (see Progress).
- [ ] T2 — Sucursal address (after #557 lands): structured address columns on `sucursales` + its ABM form, docs/10.
- [ ] T3 — Store sync: link table sucursal ↔ MP store (`id_store` MP, external_id), "registrar en Mercado Pago"
  per sucursal, update when the address changes.
- [ ] T4 — POS sync: link table punto de venta ↔ MP POS (`id_pos` MP, `external_id` derived from the PV id), created
  under its sucursal's store; moving a PV to another sucursal re-homes the POS.
- [ ] T5 — Point: list the seller's terminals, assign one to a punto de venta (one per PV), switch it to PDV mode,
  with the instructions to bind it in the Mercado Pago app.
- [ ] T6 — Web: Mercado Pago status per sucursal and per punto de venta in their screens.

## Delivery strategy

`ask-on-risk`, standing chain `stacked-to-main`, one PR per task through judgment-day. T1 stacks on E1 T3
(`pnader/mercadopago-oauth-t3`); T2+ rebase onto main once #557 and the E1 stack are in.

## Progress

- 2026-10-31: document created; branch `pnader/mercadopago-e2-t1` from `pnader/mercadopago-oauth-t3` d5d1a8e6.
- 2026-10-31 T1 done (delegated writer, one work-unit commit). New: `IProveedorDeTokenMercadoPago` /
  `ProveedorDeTokenMercadoPago` (Application), `IClienteApiMercadoPago` / `ClienteApiMercadoPago` + `ErrorDeApiMercadoPago`,
  `ServicioDeConexionMercadoPago.MarcarRequiereReconexionPorRechazoAsync`, option `HorasDeMargenDelToken`. No endpoint, no
  migration, no model change. Files outside the brief's list: `src/Ways.Application/MercadoPago/Abstracciones.cs` and
  `ApiDeMercadoPago.cs` are inside `MercadoPago/**`; `tests/Ways.IntegrationTests/MercadoPagoApiTests.cs` matches `MercadoPago*.cs`.
- Verification: build 0 errors; Domain 2110/2110; Application 1588/1588 (includes the structural guards); integration filter
  `MercadoPago|SuperficieDeAutorizacion|AislamientoDeTenant` 102/102. Full integration suite left to CI.
- Mutation evidence (mutate, run, revert; the suite was rebuilt after each revert):
  - margin predicate `<=` -> `<`: unit `ElLimiteDelMargenEsInclusivoYUnTickDespuesYaNoRenueva` and the 1440-minute case fail;
    predicate -> `false`: 10 integration tests fail (renewal-before-use, invalid_grant, expired token).
  - `EsUsable` -> `true` (the "never return an expired token" guard): `SiLaRenovacionFallaYElTokenYaVencioNuncaSeEntregaElVencido`
    (503/429/401) and `UnTokenQueVenceEnMenosDeLaHolguraNoSeEntregaSiLaRenovacionFalla` fail.
  - 401 branch: dropping the `MarcarTokenRechazadoAsync` call fails `UnRechazo401PasaLaConexionARequiereReconexion...`;
    changing the detected status to 403 fails the unit 401 test and the 403 typed-error case.
  - the three conjuncts of the guarded UPDATE: without `AccessTokenExpiraEn` fails `...VencimientoYaCambio`; without `Id` fails
    `...OtraEmpresaConElMismoVencimiento`; without `Estado` fails `...SeDesconectoDespuesDeLeerElToken`.

## T1 decisions

- Margin: `HorasDeMargenDelToken`, default 24 h (non-positive -> default), distinct from the 30-day background window. A token
  that expires within the margin (inclusive) or already expired is renewed through `RenovarConexionAsync` before use.
- Transient-failure policy: after any renewal outcome other than `RequiereReconexion` the connection is read again; the stored token
  is returned only if it still has more than a 1-minute holgura, otherwise `mercadopago_no_disponible` (503). So a 503/429/timeout or
  rejected platform credentials inside the margin do not cut calls while the token is valid, and an expired token is never returned.
  The holgura exists so that a token about to expire is not sent: its 401 would be mistaken for a revocation and wipe the connection.
- Error codes: `mercadopago_no_conectada` (409), `mercadopago_requiere_reconexion` (409), `mercadopago_no_disponible` (503),
  `mercadopago_solicitud_rechazada` (502, `ErrorDeApiMercadoPago` with status, code and message sanitized: no token, no control
  characters, <= 200 chars), `mercadopago_respuesta_invalida` (502, any 2xx whose body is empty, `null`, not JSON or not the type).
  Unreadable material surfaces the existing `mercadopago_tokens_ilegibles`.
- 401 handling: the guarded UPDATE also matches the `access_token_expira_en` of the token that was used, so a renewal that committed
  in between (new expiry) is never wiped by a stale 401. The client marks with `CancellationToken.None`.
- Serializer: `JsonSerializerDefaults.Web` + `SnakeCaseLower`, nulls omitted, numbers readable from strings. The token is never cached
  and `TokenDelVendedorMercadoPago.ToString()` hides it. Only the path (no query) of a call is logged.
- `ListarTerminalesAsync` assumes `{data:{terminals:[...]}, paging:{total,offset,limit}}` with numeric `pos_id`/`store_id`; this is from
  the reference, not yet exercised against the live API (T5 must confirm it).
- Not covered: the `RotacionPerdida` renewal outcome (needs a failing commit) falls in the same branch as the other failures.

## Judgment-day T1 round 1

Both blind judges agreed on two confirmed findings; both fixed in one fix commit (`fix(mercadopago): el token del vendedor no reintenta
la renovación en cada llamada y los errores de lectura son 503`). T1 now includes a migration (`fecha_ultimo_intento_renovacion`,
nullable, no RLS or backfill impact) and a doc 10 section 07 update, contrary to the "no migration, no model change" line above.

- E2T1-1 (fixed): the pre-use renewal retried on every call inside the 24 h margin, taking `FOR UPDATE` and calling Mercado Pago with
  the lock held. Now: column `conexiones_mercadopago.fecha_ultimo_intento_renovacion` set on `Fallida` / `CredencialesRechazadas` /
  `RotacionPerdida` (the last one with a separate best-effort UPDATE, since its transaction is gone); option
  `MinutosDeBackoffDeRenovacion` (default 15); `ServicioDeRenovacionDeConexionesMercadoPago.RenovarAntesDeUsarAsync` takes the row with
  `FOR UPDATE SKIP LOCKED` (returns `Ocupada` instead of waiting) and re-checks the backoff under the lock (`EnEspera`). The provider
  skips the renewal while the stored token is usable (> 1 min) and `EstaEnBackoff`; an unusable token ignores the backoff and always
  retries, so a failing renewal still ends in 503 `mercadopago_no_disponible`. Any non-cancellation exception from the renewal is
  logged (no tokens) and falls back to the stored token when it is usable. Background job: unchanged (still waits for the lock and does
  not honor the backoff, so the next run still retries), but it now records the attempt time on failure.
- E2T1-2 (fixed): `ClienteApiMercadoPago` sends with `ResponseHeadersRead`, reads the 4xx error body through the same guard as the 2xx
  body (timeout / `HttpRequestException` / `IOException` -> 503 `mercadopago_no_disponible`, caller cancellation stays a cancellation),
  with its own 15 s body timeout (`PlazoDeLecturaDelCuerpo`, since `HttpClient.Timeout` no longer covers the body) and at most 16 KB read
  (longer is treated as unreadable: no code, no message). A failing `MarcarTokenRechazadoAsync` is logged and the caller still gets
  409 `mercadopago_requiere_reconexion`.
- Tests: unit (`EstaEnBackoff`, option defaults, error-read guard, bound, mark failure) and integration (backoff within / after /
  expired token / holgura, concurrent call does not wait for an in-flight renewal using the fake MP handler's `AntesDeResponder`,
  unexpected exception falls back, attempt recorded by the job and by lost rotation).
- Mutation evidence (mutate, run, revert): backoff predicate `<` -> `<=` fails the boundary unit tests; dropping the future-date guard
  fails the `+5 min` case; predicate -> `false` fails 3 unit tests and 4 integration tests (backoff x3 statuses and the retry-after-backoff
  test); `SKIP LOCKED` -> plain `FOR UPDATE` fails both concurrent-call tests (they time out waiting for the lock); not recording the
  attempt fails the 3 backoff integration cases. Error read: guards removed fails 4 unit tests (timeout, hang, both network cuts); no
  16 KB bound fails the oversized-body test; unguarded mark fails the mark-failure test.
- Known residual: an unexpected (non-`ErrorDominio`) exception rolls the transaction back, so no attempt time is recorded and the next
  call retries; only expected failures are backed off.

## Next step

T2 waits for #557 and T3+ depend on T2, so nothing else starts before #557 lands. After this fix commit the T1 delta goes to a scoped
judgment-day re-judgment (frozen findings plus the fix delta).
