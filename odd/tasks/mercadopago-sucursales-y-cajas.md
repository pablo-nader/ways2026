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

## Next step

T2 (waits for #557); T3 can start now on top of T1.
