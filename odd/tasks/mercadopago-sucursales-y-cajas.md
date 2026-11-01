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

## Mercado Pago facts (verified 2026-10-31 and 2026-11-01, mercadopago.com.ar/developers reference; not exercised live)

- Store: `POST /users/{user_id}/stores` (user_id = the seller from OAuth). Send `name`, `external_id`
  (alphanumeric ≤60) and a full `location` (`street_number` string, `street_name`, `city_name`, `state_name`,
  `latitude`, `longitude`, `reference`) — the docs contradict each other on what is required. `name`, street, city and
  state reject digits/special characters (`INVALID_NAME`, `INVALID_STATE_NAME`): real addresses like "Av. 9 de Julio"
  or accents may be rejected → must be tested live.
- Store update stays `PUT /users/{user_id}/stores/{id}`. Store search `GET /users/{user_id}/stores/search?external_id=`
  answers `{paging, results:[...]}`, but NO MATCH answers **404 `store_not_found`**, not an empty list.
- POS create: `POST /v2/pos` (201), `X-Idempotency-Key` required (1–64; same key + same payload returns the existing one,
  same key + different payload is 400 `idempotency_key_already_used`). Body: `name` (≤45; letters, digits, hyphen,
  underscore and inner spaces only), `store_id` (string of digits) or `external_store_id`, `external_id` (letters+digits
  ≤40, unique per seller), optional `config.qr.operating_mode` (`pdv`) and `config.qr.category`. NO `fixed_amount`.
  Response: `id` (int), `name`, `external_id`, `store_id`, `external_store_id`, `status`,
  `qr_response.{uuid,image,template_document,template_image,qr_code}`. Errors: 404 `store_not_found`, 409
  `pos_already_exists`.
- POS update: `PATCH /v2/pos/{id}` (not PUT); every field optional; `store_id` CAN change; 404 `pos_not_found`.
- POS search: `GET /v2/pos?external_id=...` → `{paging:{total,offset,limit}, data:[POS...]}` (key `data`, not `results`).
- Point (verified 2026-11-01): `GET /terminals/v1/list?store_id&pos_id&limit(1-50)&offset` -> `{data:{terminals:[{id "TYPE__SERIAL",
  pos_id int, store_id, external_pos_id, operating_mode PDV|STANDALONE|SELF_SERVICE|UNDEFINED}]}, paging:{total,offset,limit}}`. Only
  NEWLAND_N950 and PAX_A910 are supported (the type carries its own underscore: `PAX_A910__8701`). `PATCH /terminals/v1/setup` with
  `{"terminals":[{"id":"...","operating_mode":"PDV"}]}` answers 200 and echoes the body (terminal restart needed). Errors: 403
  `terminal_not_allowed_action` (wrong model), 403 `store_pos_not_found` (the terminal has no store/POS), 412 (the POS already has a
  terminal in PDV/SUSPENDED). Binding a terminal to a store + POS happens ONLY in the Mercado Pago mobile app (turn the terminal on,
  scan the QR on its screen, pick store and POS); one terminal per POS.
- Not verified: store get and delete; POS delete; what a PATCH to a deleted POS answers (assumed 404); whether the
  seller token needs a scope; rate limits.

## Tasks

- [x] T1 — Calling Mercado Pago as the seller: a service that returns a valid access token for an empresa (renews
  first when it is close to expiry, using the E1 renewal path; `requiere_reconexion`/no connection → typed error), and
  a typed API client base (bearer, idempotency key, error envelope mapping, 401 → connection needs reconnection,
  never logs tokens). Unit + integration tests with the fake MP handler. Route: delegated.
  Done 2026-10-31 (see Progress).
- [x] T2 — Sucursal address: structured address columns on `sucursales` + its ABM form, docs/10. Route: delegated
  (writer touches domain, service, migration, API contract, web modal and tests: 2+ non-trivial files).
- [x] T3 — Store sync: link table sucursal ↔ MP store (`id_store` MP, external_id), "registrar en Mercado Pago"
  per sucursal, update when the address changes.
- [x] T4 — POS sync: link table punto de venta ↔ MP POS (`id_pos` MP, `external_id` derived from the PV id), created
  under its sucursal's store; moving a PV to another sucursal re-homes the POS.
- [x] T5 — Point: list the seller's terminals, assign one to a punto de venta (one per PV), switch it to PDV mode,
  with the instructions to bind it in the Mercado Pago app.
- [x] T6 — Web: Mercado Pago status per sucursal and per punto de venta in their screens.

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

## T2 design (2026-11-01)

The sucursales stack (#549 #550 #557 #573 #580) is in main, so T2 branches from main (`pnader/mercadopago-e2-t2`).

- New nullable columns on `sucursales`, all optional (existing rows and sucursales without Mercado Pago need none):
  `calle varchar(120)`, `altura varchar(20)` (street number: a string, "S/N" or "1234 bis" are real), `ciudad varchar(80)`,
  `provincia varchar(40)`, `codigo_postal varchar(10)`, `latitud numeric(9,6)`, `longitud numeric(9,6)`,
  `referencia varchar(200)`.
- `provincia` is one of the 24 Argentine jurisdictions (closed list in Domain, `ProvinciasArgentinas`), stored with its
  canonical name; the web offers it as a select. Mercado Pago's `state_name` comes from it in T3.
- CHECKs: `latitud BETWEEN -90 AND 90`, `longitud BETWEEN -180 AND 180`, and both-or-neither
  (`(latitud IS NULL) = (longitud IS NULL)`). The service validates the same before writing (400 with a typed code), so
  the CHECKs are a backstop only.
- Text fields are trimmed, empty → NULL, over-length → 400. No completeness rule in T2: T3 decides what Mercado Pago needs
  and reports what is missing.
- Create and update of a sucursal accept the address; the listing projects it. Audit payload carries the address.
- No backfill, no RLS change (same table).

## T2 progress (2026-11-01)

Commit `5516a04d` on `pnader/mercadopago-e2-t2` (route: delegated writer).

- Domain: `DireccionDeSucursal` (record + `Normalizar`), `ProvinciasArgentinas`, address properties and `AplicarDireccion` on
  `Sucursal`; `PayloadDeAuditoria.AltaDeSucursal/EdicionDeSucursal` carry the address (`RenombreDeSucursal` became `EdicionDeSucursal`).
- Application/API: `SucursalAlta`, `SucursalEdicion` and `SucursalListado` carry the eight fields; the PUT replaces the address
  whole; the existence 404 still precedes address validation. `ManejadorDeErrores` maps the three CHECKs to 400.
- Infrastructure: migration `20261101104443_DireccionDeSucursal` (8 columns, 3 CHECKs), `SucursalConfiguration`.
- Web: `direccionDeSucursal.ts` (+ test), "Dirección" section in `ModalDeSucursal`, `Sucursales.tsx`, `api/tipos.ts`.
- Docs: `docs/10-modelo-de-datos/01-padrones-auxiliares.md` (table, CHECKs, behaviour); the index row needed no change.
- Tests: `DireccionDeSucursalTests` (domain), `SucursalesDireccionTests` (round trip, typed 400s, 404-first, SQLSTATE 23514),
  `SucursalesDireccionMigracionTests` (UpDownUp), web helper and modal tests.
- Verification: build 0 errors; Domain 2266 passed; Application 1637 passed (structural guards included); integration filter
  (Sucursal|UpDownUp|Cobertura|AislamientoDeTenant) 112 passed; web `tsc -b` clean, `oxlint` no new findings, vitest
  (Sucursales.test.tsx + paginas/organizacion) 73 passed.
- Mutation evidence: `if (latitud is null != longitud is null)` replaced by `if (false)` -> 2 domain tests failed (reverted);
  migration CHECKs `ck_sucursales_latitud` and `ck_sucursales_coordenadas_completas` replaced by `true` -> 4 of 6 raw-SQL
  CHECK cases failed, the 2 longitud cases still passed as expected (reverted).
- Deviation: tests for the RED step were written with the Domain code in the same pass, so RED was shown by mutation, not by a
  missing type. `SucursalListado` fields are optional (`Partial`) in the web type to avoid touching unrelated fixtures.

## T3 design (2026-11-01)

Branch `pnader/mercadopago-e2-t3`, stacked on T2 (#592). Backend only; the web is T6.

- Table `locales_mercadopago` (operativa: id_tenant + id_empresa, RLS like `conexiones_mercadopago`): `id_local_mercadopago`
  PK, `id_tenant`, `id_empresa`, `id_sucursal` (FK compuesta con empresa y tenant), `id_usuario_mercadopago bigint` (the
  seller the store belongs to), `id_store_mercadopago bigint`, `external_id varchar(60)`, `fecha_registro`,
  `fecha_sincronizacion`, `created_at`/`updated_at`. UNIQUE (`id_sucursal`, `id_usuario_mercadopago`): one store per sucursal
  per Mercado Pago account (a reconnection to another account registers again; the old row stays as history).
- `external_id` = `WAYSS` + id_sucursal (alphanumeric, stable, unique per seller).
- `POST /api/sucursales/{id}/mercadopago` (Admin; the sucursal's empresa must have an active connection):
  1. 404 first for a missing/other-tenant/soft-deleted sucursal.
  2. Address completeness for Mercado Pago: calle, altura, ciudad, provincia, latitud, longitud; otherwise 400
     `direccion_incompleta_para_mercadopago` naming the missing fields. `referencia` optional.
  3. Per-sucursal advisory lock (serializes two clicks), then: row for (sucursal, current seller) exists -> `PUT
     /users/{user_id}/stores/{id}` with the current name and location; no row -> first `GET
     /users/{user_id}/stores/search?external_id=` (adopt a store a lost commit left behind), else `POST
     /users/{user_id}/stores`. Then upsert the row and an audit entry. Always an idempotency key on non-GET calls.
  4. Text sent to Mercado Pago: accents stripped, characters outside letters/digits/space/`.,-` removed, trimmed to its
     limits; Mercado Pago's rejection surfaces as the existing `mercadopago_solicitud_rechazada` (502) with its sanitized
     message.
- `GET /api/sucursales/{id}/mercadopago`: link state for the current seller: `sin_conexion`, `no_registrada`,
  `registrada` (with ids and dates) or `desactualizada` (sucursal `updated_at` after `fecha_sincronizacion`).
- The fake Mercado Pago handler in the integration tests covers create, search-adopt, update and a 4xx rejection.

## T3 progress (2026-11-01)

Commit `198123a3` on `pnader/mercadopago-e2-t3` (route: delegated writer).

- Domain: `LocalMercadoPago`, `LocalParaMercadoPago` (completeness rule, `external_id` = `WAYSS`+id, limits), `TextoParaMercadoPago`
  (sanitizer), action `sucursal.mercadopago` and `PayloadDeAuditoria.LocalDeMercadoPago`.
- Application: `ServicioDeLocalesMercadoPago` (`ObtenerAsync`, `RegistrarAsync`), `ContratosDeLocales`, DbSet, DI. Infrastructure:
  `LocalMercadoPagoConfiguration`, migration `20261101111426_LocalesMercadoPago` (RLS via `HabilitarRlsDeTenant`). API: routes in
  `MercadoPagoEndpoints` (group `GestionDeCatalogo`, so `SuperficieDeAutorizacion` needed no allow-list entry), 23505 mapping
  `mercadopago_local_duplicado`. Docs: index row + section in docs/10 part 07. Golden inventory: 4 lines added.
- Tests: `LocalParaMercadoPagoTests` (28), audit tests, `MercadoPagoLocalesTests` (22, fake handler, real Postgres).
- Verification: build 0 errors; Domain 2295; Application 1637 (guards green); integration filter 245 passed.
- Mutation evidence (mutate, run, revert): `Latitud is null` check -> `false`: 3 domain tests fail; search-adopt branch -> `false`: 3
  integration tests fail; lock call removed: concurrency test fails; commit-on-401 catch -> `false`: 401 test fails; `HabilitarRlsDeTenant`
  removed: RLS test, `LaCoberturaDePoliciesEsCompleta` and one more fail.
- Decisions: the lock transaction runs on the request db, and a 401 mark made inside it is committed before rethrowing (otherwise the
  rollback undoes the reconnection mark). The PUT after an adoption refreshes the data. The idempotency key is `ways-local-{id}-alta` for
  the create and `ways-local-{id}-{hash of body}` for the update. `sync date` = time the sucursal was read, not the end of the call.
- Deviations: no dedicated migration test (the `UpDownUp` chain covers Up/Down); the web `tipos.ts` action union was not touched.
- Open: a registered sucursal now counts as in use (`sucursal_en_uso` on baja; same for empresa/tenant); a PUT to a store deleted in
  Mercado Pago surfaces as 502 instead of re-creating; MP DTO shapes, accepted characters and the state name for CABA are unverified live.

## Judgment-day T3 round 1

- 2026-11-01: three findings confirmed by both blind judges on 198123a3; fixed in cdc6701f.
- T3-1: the create key was fixed per sucursal (`ways-local-{id}-alta`) while the body can change; Mercado Pago answers 409 for the
  same key with another body within 24 h. Now `ways-local-{id}-alta-{body hash}`. The update key adds the sucursal's `updated_at`
  ticks (`ways-local-{id}-{hash}-{ticks}`), so A -> B -> A yields three distinct keys and a retry of the same state reuses its key.
- T3-2: with a link row a PUT to a store deleted in Mercado Pago (404) left the sucursal stuck. A 404 from the PUT now falls back
  to search by `external_id` (adopt + PUT) or create, and the link row's store id is updated in the same transaction. Other 4xx
  still surface as 502.
- T3-3: the sanitizer replaces any non-admitted character with a space (S/N -> S N, 1234 1/2 -> 1234 1 2, O'Higgins -> O Higgins,
  9º -> 9) and an altura of S/N / SN / S N counts as missing for Mercado Pago.
- Tests: 8 new domain cases (sanitizer, S/N rule); 5 new integration tests (alta key changes with the body, A-B-A-B update keys all
  distinct, PUT 404 -> adopt, PUT 404 -> create, PUT 400 still 502 without search/create). `AuditoriaAsync` now orders by
  `id_auditoria DESC` (it ordered by text, which picked the wrong row).
- Verification: build 0 errors; Domain 2304; Application 1637; integration filter MercadoPagoLocales 27 passed.
- Mutation evidence (mutate, run, revert): create key back to fixed `-alta` + update key without ticks + `when (false)` on the 404
  catch, together -> 4 integration tests failed (alta key, update keys, adopt, create); sanitizer non-admitted branch removed + S/N
  rule removed, together -> 8 domain tests failed. All reverted, green again.
- Verdict pending: scoped re-judgment of the fix delta.

## Judgment-day T3 verdict

- 2026-11-01: **APPROVED** after two rounds. Round 1: both judges confirmed three warnings (create key fixed per sucursal,
  no recovery from a store deleted in Mercado Pago, sanitizer glued words); fixed in cdc6701f. Scoped re-judgment: both
  judges confirmed the adopt PUT reused the failed PUT's key, and the sin-número rule was too narrow; fixed in 224e9df5
  (update key = sucursal + hash of store id, body hash and sucursal state; sin-número forms compared on letters/digits).
  Mutation: dropping the store id from the key fails the adopt test; shrinking the sin-número set fails 4 domain cases.
  Final scoped re-judgment: both clean (suggestions only). RDD is off.
- Open (one judge, not fixed): a registered sucursal counts as in use for bajas (OD4 strict, same as the E1 connection for
  the empresa) and fusion leaves the origin's link as history; there is no unregister path; a reconnection to another
  Mercado Pago account leaves the old link as history. Suggestion: `desactualizada` fires on any sucursal edit, even of
  fields not sent to Mercado Pago.

## T4 design (2026-11-01)

Branch `pnader/mercadopago-e2-t4`, stacked on T3. Backend only; the web is T6.

- Table `cajas_mercadopago` (operativa: id_tenant + id_empresa, RLS): `id_caja_mercadopago` PK, `id_tenant`, `id_empresa`,
  `id_punto_venta` (FK compuesta con empresa y tenant), `id_local_mercadopago` (FK to `locales_mercadopago`: the store it
  hangs from), `id_usuario_mercadopago bigint`, `id_pos_mercadopago bigint`, `external_id varchar(40)`, `url_qr` (the
  `qr_response` static QR image link, nullable), `fecha_registro`, `fecha_sincronizacion`, `created_at`/`updated_at`. UNIQUE
  (`id_punto_venta`, `id_usuario_mercadopago`).
- `external_id` = `WAYSPV` + id_punto_venta (letters+digits, <= 40, unique per seller).
- `POST /api/puntos-venta/{id}/mercadopago` (Admin): 404 first; the PV's sucursal must be registered as a local for the
  current seller, else 409 `mercadopago_local_no_registrado`; per-PV advisory lock; row exists -> `PUT /pos/{id}` with
  name and `store_id` of the PV's current sucursal's local (this is what re-homes a PV moved to another sucursal), with the
  same 404 -> search -> adopt/create recovery as T3; no row -> `GET /pos?external_id=` adopt, else `POST /pos` with
  `{name, external_id, store_id, fixed_amount: false, category}` (category optional, omitted). Keys follow T3's scheme
  (content + target + state hashes, <= 64).
- `GET /api/puntos-venta/{id}/mercadopago`: `sin_conexion`, `local_no_registrado`, `no_registrada`, `registrada` (ids, QR
  url) or `desactualizada` (PV `updated_at` after `fecha_sincronizacion`, or its sucursal's local differs from the row's).
- Name sent: PV name through `TextoParaMercadoPago`, <= 45 chars.
- Same fake-handler integration coverage as T3, plus re-homing after moving the PV to another registered sucursal.

## T4 progress (2026-11-01)

Note: the Mercado Pago shape assumptions below (paths `/pos`, `PUT`, `results`, `qr`, `fixed_amount`) were superseded by the verified facts in judgment-day round 1.

Commit `09ac04a5` on `pnader/mercadopago-e2-t4` (route: delegated writer).

- Domain: `CajaMercadoPago`, `CajaParaMercadoPago` (`WAYSPV`+id, name <= 45 via the T3 sanitizer), action `pv.mercadopago` (entity
  `punto_venta`) and `PayloadDeAuditoria.CajaDeMercadoPago`. Application: `ServicioDeCajasMercadoPago` (`ObtenerAsync`,
  `RegistrarAsync`), `ContratosDeCajas`, code `mercadopago_local_no_registrado`, DbSet, DI. Infrastructure: `CajaMercadoPagoConfiguration`,
  migration `20261101121553_CajasMercadoPago` (RLS via `HabilitarRlsDeTenant`). API: routes in `MercadoPagoEndpoints` (group
  `GestionDeCatalogo`, as T3), 23505 mapping `mercadopago_caja_duplicada`. Docs: index row + section in docs/10 part 07. Golden
  inventory: 5 lines.
- Model summary: `cajas_mercadopago(id_caja_mercadopago, id_tenant, id_empresa, id_punto_venta, id_local_mercadopago,
  id_usuario_mercadopago bigint, id_pos_mercadopago bigint, external_id varchar(40), url_qr varchar(500) NULL, fecha_registro,
  fecha_sincronizacion, created_at, updated_at, deleted_at)`; UNIQUE (`id_punto_venta`, `id_usuario_mercadopago`); FKs tenant,
  empresa (id_empresa, id_tenant), punto_venta (id_punto_venta, id_tenant) and local (id_local_mercadopago, id_empresa, id_tenant), all
  RESTRICT; CHECK external_id not blank. New alternate key `ak_locales_mercadopago_id_local_id_empresa_id_tenant` on the T3 table.
- Mercado Pago shape assumptions (reference, not exercised live): `POST /pos` and `PUT /pos/{id}` take `{name, external_id, store_id
  (number), fixed_amount: false}`; response `{id, qr:{image,...}}` (also `qr_response`, both accepted); search `GET /pos?external_id=`
  -> `{paging, results:[{id, external_id, store_id}]}`; path is `/pos` per the brief, the T1 facts say `/v2/pos`.
- Moving a PV: the new FK hangs the caja from the PV by (id, tenant) and from the local, never from the sucursal, so the
  `UPDATE puntos_venta SET id_sucursal` is untouched. DEFECT FOUND AND FIXED: moving the LAST caja of a sucursal that is registered as a
  local answered 409 `sucursal_en_uso`, because T3's `locales_mercadopago` row counts as use of the origin sucursal in
  `InspectorDeUso` (the caja cascade-drops the empty origin). The move path now excludes `locales_mercadopago` from that check, as it
  already did `parametros` (`excluirParametros` renamed `excluirAcompanantes`); stock/lots/movements still block. The plain
  `DELETE /api/sucursales/{id}` still treats the local as use (T3 open item). Side effect to know: a PV with a caja now counts as in
  use for its baja (`punto_venta_en_uso`) and for empresa/tenant, like every FK.
- Deviations: the audit action is `pv.mercadopago` (repo convention `pv.*`), not `puntoventa.mercadopago`. No alternate key was added to
  `puntos_venta` (the first try, `(id, id_empresa, id_tenant)`, broke 73 Application tests that create a PV with its sucursal in one
  `SaveChanges`: `PuntoVenta.IdEmpresa is unknown`), so the DB does not compare the caja's empresa with the PV's; the service takes it
  from the PV. No 409 `pos_already_exists` recovery on create (the prior search avoids it; it would surface as 502). RED for the move
  fix was observed (test failed with 409 `sucursal_en_uso` before the change); the rest was written with the code and proven by mutation.
- Verification: `dotnet build Ways.slnx -c Release` 0 errors; Domain 2321 passed; Application 1637 passed (guards included);
  integration filter (MercadoPago|UpDownUp|Cobertura|AislamientoDeTenant|SuperficieDeAutorizacion|Sucursal) 283 passed.
- Mutation evidence (mutate, run `MercadoPagoCajas`, restore from backup copy): local lookup without the sucursal predicate -> 3 tests
  fail (move-last-caja, re-homing, no-local-after-move); local-not-registered answer 409 -> 500 -> 2 fail; search-adopt `false` +
  PUT-404 catch `when (false)` together -> 5 fail (adopt, borrada-adopta, borrada-crea, alta, 503); lock call removed + `store_id` forced
  to a constant + local not re-pointed, together -> 4 fail (lock test, re-homing, alta, adopt); create key without body hash + update
  key without store/state, plus `HabilitarRlsDeTenant` removed, together -> 4 fail (alta key, A-B-A keys, RLS test,
  `LaCoberturaDePoliciesEsCompleta`).
- Open: baja of a sucursal/PV with Mercado Pago links stays blocked (OD4); no unregister path; a moved PV leaves the origin's store in
  Mercado Pago untouched; `desactualizada` fires on any PV edit; the local is read once under the PV lock but a concurrent re-registration
  of its sucursal (other lock) can change its store id mid-call.

## Judgment-day T4 round 1 (2026-11-01)

Findings (both blind judges unless noted) and fixes, commit `d8ac17c1`:

- F1: the POS calls used `/pos`, `PUT /pos/{id}`, `results`, `qr`, `fixed_amount` and no `operating_mode`. Verified facts say
  `/v2/pos`, `PATCH /v2/pos/{id}`, search key `data`, QR in `qr_response.image`, no `fixed_amount`, `config.qr.operating_mode =
  "pdv"` (needed for the E3 dynamic QR) and `store_id` as a digit string. Fixed in `ServicioDeCajasMercadoPago` and the DTOs of
  `ContratosDeCajas`; the PV name now goes through `TextoParaMercadoPago.SanearNombreDeCaja` (letters, digits, space, hyphen,
  underscore; dots and commas become spaces; <= 45). A create that answers 409 (`pos_already_exists`) now searches by
  `external_id` and adopts (PATCH) instead of surfacing a 502; with no match the 409 still surfaces as 502. A PATCH 404 keeps its
  recovery. The fake-handler tests follow the new routes, methods and shapes.
- F2 (T3 bug exposed by the facts): `BuscarPorExternalIdAsync` of the store service treated the store-search 404 as an error;
  a 404 now means "not found" and the store is created. T3 tests: "no match" is a 404 (`BusquedaVacia`), and one test keeps the
  tolerant empty `results` list. The store update stays `PUT` (verified).
- F3: the caja registration did not exclude a concurrent re-registration of its sucursal's local (T3 takes the sucursal lock).
  Fixed: fixed lock order sucursal → PV everywhere (T3 takes only the sucursal one); the PV is read once to choose the sucursal
  lock and again under both locks, with a 409 `mercadopago_punto_venta_movido` if it changed sucursal in between. New column
  `cajas_mercadopago.id_store_mercadopago bigint NOT NULL` (the store the POS was registered under), set on create, adopt and
  patch; `GET` answers `desactualizada` also when the local's current store id differs from the row's, and a later POST re-homes it.
  The unshipped migration `20261101121553_CajasMercadoPago`, its designer and the snapshot were edited by hand (not regenerated);
  docs/10 part 07 is in sync.
- Accepted, not fixed: moving the last PV soft-deletes the origin sucursal while its `locales_mercadopago` row (and the Mercado Pago
  store) stays as history; no cross-empresa alternate key on `puntos_venta` (the service takes the empresa from the PV).

Mutation evidence (mutate, run `MercadoPagoCajas|MercadoPagoLocales`, restore from backup copies): create-409 catch `when (false)` ->
`UnAltaQueContesta409PosAlreadyExistsBuscaPorExternalIdYLaAdopta` and `...SigueSiendo502` fail; store-search 404 catch `when
(false)` -> `UnaBusquedaSinCoincidenciasQueContesta404CreaElLocal` fails (plus every test whose setup relies on the 404, 50 in
all); sucursal lock call removed -> `UnaRegistracionDelLocalDeLaSucursalExcluyeLaRegistracionDeSusCajas` fails; `id_store`
comparison removed from `ObtenerAsync` -> `SiElLocalSeRegistraDeNuevoConOtroStoreIdLaCajaQuedaDesactualizadaYRegistrarlaLaReapunta`
fails. The first attempt left two of the mutations unapplied (multi-line `sd` pattern); they were reapplied with the editor and the
file contents verified before the run.

Verification: `dotnet build Ways.slnx -c Release` 0 errors; Domain 2323 passed; Application 1637 passed; integration filter
(MercadoPago|UpDownUp|Cobertura|AislamientoDeTenant|Sucursal) 284 passed, 0 failed.

## Judgment-day T4 verdict

- 2026-11-01: **APPROVED** after two rounds. Round 1 (both judges): wrong POS route `/pos` (verified `/v2/pos`, PATCH,
  `data`, `qr_response`), the caja lock did not exclude a concurrent re-registration of its local, and the T3 store search
  treated MP's 404-on-no-match as an error; fixed in d8ac17c1 (plus `id_store_mercadopago` on the caja). Re-judgment (both):
  POS search 404 must mean no match and PATCH 404 `store_not_found` is not a deleted POS; judge B: take the sync date before
  reading the PV; fixed in 31c9e834 with two integration tests and mutation evidence (each guard removed fails its test).
  Final re-judgment: both clean (suggestions: any 404 on search reads as no match; the `store_not_found` literal is
  case-sensitive). `has-pending-model-changes`: none. RDD is off.
- Accepted (documented): moving the last PV soft-deletes the origin sucursal while its local stays as history; no
  cross-empresa AK on `puntos_venta`.

## T5 design (2026-11-01)

Branch `pnader/mercadopago-e2-t5`, stacked on T4. Backend only; the web is T6. Facts: binding a Point terminal to a store +
POS happens only in the Mercado Pago mobile app; one terminal per POS; the API lists terminals and switches their mode.

- New nullable columns on `cajas_mercadopago`: `id_terminal_point varchar(80)` (`TYPE__SERIAL`) and `fecha_terminal`.
- `GET /api/puntos-venta/{id}/mercadopago/terminales` (Admin): 404 first; needs a registered caja for the current seller
  (else 409 `mercadopago_caja_no_registrada`); calls `GET /terminals/v1/list?pos_id={id_pos}` and returns the terminals
  (`id`, `operatingMode`, `asignada`), plus `instrucciones` (how to bind in the app) when the list is empty.
- `POST /api/puntos-venta/{id}/mercadopago/terminal` `{idTerminal}`: same preconditions; the terminal must be in the list for
  that POS (else 409 `mercadopago_terminal_no_vinculada`); if its mode is not `PDV`, `PATCH /terminals/v1/setup` with
  `{terminals:[{id, operating_mode:"PDV"}]}` (403 `store_pos_not_found` / 412 -> typed 409s with clear messages); then store
  `id_terminal_point` + `fecha_terminal` and audit. Per-PV lock (same order as T4: sucursal -> PV).
- `DELETE /api/puntos-venta/{id}/mercadopago/terminal`: clears the assignment in Ways (does not touch Mercado Pago).
- The caja GET (T4) also returns `idTerminalPoint`.

## T5 progress (2026-11-01)

Commit `1f9f626d` on `pnader/mercadopago-e2-t5` (route: delegated writer).

- Domain: `TerminalPointMercadoPago` (id rule, `PDV` check), `CajaMercadoPago.IdTerminalPoint` / `FechaTerminal`, action `pv.terminalmercadopago`
  (entity `punto_venta`) and `PayloadDeAuditoria.TerminalDeMercadoPago` (previous and new terminal, mode changed; no token).
  Application: `ServicioDeTerminalesMercadoPago` (`ListarAsync`, `AsignarAsync`, `QuitarAsync`), `ContratosDeTerminales`, four codes in
  `CodigosDeErrorDeMercadoPago` (`mercadopago_caja_no_registrada`, `mercadopago_terminal_no_vinculada`,
  `mercadopago_terminal_modelo_no_admitido`, `mercadopago_caja_con_terminal_en_pdv`), DI; the lock helper of the caja service became
  `internal static` and is shared. `EstadoDeCajaMercadoPago` gained `IdTerminalPoint`. Infrastructure: columns + CHECK
  `ck_cajas_mercadopago_terminal` in `CajaMercadoPagoConfiguration`, migration `20261101141301_TerminalDeCajaMercadoPago` (no RLS change:
  same table). API: `GET .../mercadopago/terminales`, `POST|DELETE .../mercadopago/terminal` in the same `GestionDeCatalogo` group (Admin).
  Docs: docs/10 part 07 (columns, CHECK, endpoints, Point facts); the index row needed no change.
- Model summary: `cajas_mercadopago` + `id_terminal_point varchar(80) NULL`, `fecha_terminal timestamptz NULL`; CHECK both-or-neither and
  not blank. No new table, no FK, no unique index (the terminal belongs to its POS in Mercado Pago; one column per caja = one terminal).
- Client check: `ClienteApiMercadoPago.ListarTerminalesAsync` already matched the verified shape (`{data:{terminals}, paging}`, numeric
  `pos_id`, `store_id` as number or string); unchanged. The service filters by `pos_id` again on its side.
- Verification: build 0 errors; `has-pending-model-changes`: none; Domain 2378; Application 1640 (guards and the `ClienteApiMercadoPago`
  unit tests live here); integration filter (MercadoPago|UpDownUp|Cobertura|AislamientoDeTenant|SuperficieDeAutorizacion) 236 passed, 0 failed.
- Mutation evidence (mutate, run `MercadoPagoCajasTests` terminal tests, restore from backup): pos_id filter removed -> 2 fail (listing,
  other-POS terminal); `if (!EstaEnPdv)` -> `if (true)` -> 3 fail (already PDV, repeat, retry); 412 mapping `== 4120` -> 2 fail (both 412
  cases); `terminal_not_allowed_action` literal changed -> the 403 model case fails; regex check removed from `Normalizar` -> 4 DTO cases
  fail; DELETE made to call Mercado Pago -> the delete test fails. All restored, suite green again.
- Deviations: (1) the brief's regex `^[A-Z0-9]+__[A-Za-z0-9]+$` rejects real ids (`PAX_A910__8701` has a single underscore inside the
  type), so the rule is `^[A-Z0-9]+(_[A-Z0-9]+)*__[A-Za-z0-9]+$`. (2) Validation of the body happens in the service (after the 404), the
  endpoint takes a nullable body so a missing body is a 400 `terminal_point_invalida` and not a framework binding error before the 404.
  (3) The POST/DELETE answer `{idTerminalPoint, fechaTerminal, modoCambiado}` (not the whole caja state); DELETE is idempotent (no
  assignment = 200 with nulls). (4) Audit action name `pv.terminalmercadopago`. (5) Tests were written with the code; RED shown by mutation.
- Open: the PATCH idempotency key is terminal + mode only (as designed): if an operator reverts the terminal to STANDALONE in the app
  within 24 h and Ways switches it again, Mercado Pago may answer the stored echo without applying it (unverified that `/terminals/v1/setup`
  honors the header); a terminal stored in Ways stays stored if someone unbinds it in the app (the listing shows it gone, `asignada` is
  false for all); two PVs of Ways cannot hold the same terminal only by Mercado Pago's one-POS rule, there is no unique index; GET lists
  at most one page (no `limit`/`offset` sent; filtering by `pos_id` yields at most one terminal); `PATCH /terminals/v1/setup` response is not
  read; web is T6.

## Judgment-day T5 round 1

- Fixed in `3f3d4134` (confirmed by both judges): T5-1 the setup PATCH uses a fresh idempotency key per attempt (`ways-terminal-{pv}-{Guid:N}`)
  and the echoed terminal must be in PDV mode, else 502 `mercadopago_respuesta_invalida` and nothing is stored (an echo without the
  terminal is tolerated: the real response shape is unverified). T5-2 re-registering a caja whose `id_pos` changed clears the terminal
  (audited as `desasignacion`); the listing now returns `idTerminalAsignada` and `asignadaVigente`. T5-3 DELETE clears every caja row
  of the PV (any seller), needs no active connection and never calls Mercado Pago (DELETE without connection or caja is now a 200 no-op).
- Mutation evidence: constant key -> 2 fail; echo check disabled -> 1 fail; clearing on pos change disabled -> 1 fail;
  `asignadaVigente` ignoring the listing -> 1 fail; DELETE reading the connection -> 2 fail. All restored.
- Verification: build 0 errors; pending model changes: none; Domain 2378; Application 1640; integration `MercadoPagoCajas` 78 passed.

## Judgment-day T5 verdict

- 2026-11-01: **APPROVED** after one fix round. Both judges confirmed: the setup PATCH key repeated across attempts, and a
  stale terminal assignment was silent (terminal unbound in the app, or caja re-registered under a new POS id); judge B also
  suggested clearing without an active connection. Fixed in 3f3d4134 (fresh key per attempt + echo check, clear on POS id
  change with audit, `idTerminalAsignada`/`asignadaVigente` in the listing, DELETE clears every row of the PV without reading
  the connection); mutation evidence per fix. Scoped re-judgment: both clean (suggestion: a 2xx without the terminal is
  accepted; the class comment now says so). RDD is off.

## T6 design (2026-11-01)

Branch `pnader/mercadopago-e2-t6`, stacked on T5. Web only (`src/Ways.Web`), Admin only.

- `api/mercadoPago.ts`: typed calls for the sucursal local (GET/POST `/sucursales/{id}/mercadopago`), the caja (GET/POST
  `/puntos-venta/{id}/mercadopago`) and the terminal (GET `.../mercadopago/terminales`, POST/DELETE `.../mercadopago/terminal`),
  plus Spanish messages for every new error code (`direccion_incompleta_para_mercadopago`, `mercadopago_local_no_registrado`,
  `mercadopago_local_duplicado`, `mercadopago_caja_duplicada`, `mercadopago_punto_venta_movido`, `mercadopago_caja_no_registrada`,
  `mercadopago_terminal_no_vinculada`, `mercadopago_terminal_modelo_no_admitido`, `mercadopago_caja_con_terminal_en_pdv`,
  `terminal_point_invalida`, `mercadopago_solicitud_rechazada`, `mercadopago_no_disponible`).
- Sucursales screen: a "Mercado Pago" row action (Admin) opens `ModalDeMercadoPagoDeSucursal`: state badge (`sin_conexion` with a
  pointer to Empresas, `no_registrada`, `registrada` with the store id and dates, `desactualizada`), the missing address fields
  by label, and "Registrar en Mercado Pago" / "Actualizar en Mercado Pago".
- Puntos de venta screen: a "Mercado Pago" row action (Admin) opens `ModalDeMercadoPagoDePuntoVenta`: caja state (same badges
  plus `local_no_registrado`), register/update, the static QR image (`urlQr`) with a link to open it for printing, and a
  "Terminal Point" section: list of terminals of the POS with mode, assign / remove, the binding instructions when empty, and a
  warning when the stored assignment is no longer bound (`asignadaVigente` false).
- No per-row status fetch in the grids (one request per row); the state lives in the modal.
- react-async-state for every read (generation token) and write (full-window disabled, re-entrancy ref); colocated Vitest tests
  for the pure mapping helpers (labels of states and missing fields); component tests wait for data (web-test-data-gates).

## T6 progress (2026-11-01)

Commit `b7a885b8` on `pnader/mercadopago-e2-t6` (route: delegated writer). Web only.

- `api/mercadoPago.ts`: `clienteDeLocalesMercadoPago` and `clienteDeCajasMercadoPago` (state, register, terminals list, assign, remove),
  their types, Spanish copies for every new code (plus `mercadopago_no_conectada`, `requiere_reconexion`, `tokens_ilegibles`,
  `respuesta_invalida`) and per-action 404 copies (sucursal vs punto de venta).
- `organizacion/`: `ModalDeMercadoPagoDeSucursal`, `ModalDeMercadoPagoDePuntoVenta`, `estadosDeMercadoPago.ts` (labels, missing-field
  labels, stale-assignment rule) and `useEscrituraUnica.ts` (synchronous re-entrancy ref + `ocupado`). Both screens got a "Mercado Pago"
  row button visible only to Admin (`puedeGestionarCatalogos`); Root enters the screens but does not see it.
- QR: image from `urlQr` plus "Abrir para imprimir" (`target="_blank" rel="noopener noreferrer"`); a `urlQr` that is not http(s) is not rendered.
- Mutation evidence: re-entrancy guard removed -> 3 fail (sucursal register, caja register, terminal assign); generation checks removed
  -> 2 fail (sucursal read + write), 3 fail (caja read, terminals, write); Admin gate forced true -> 2 fail (both screens); stale rule
  forced false -> 2 fail (helper + modal warning). All restored.
- Verification (src/Ways.Web): `tsc -b --noEmit` clean; oxlint no findings in touched files; vitest api/mercadoPago + Sucursales +
  PuntosVenta + organizacion 11 files, 307 passed; `npm run build` ok.
- Deviations: the register button is hidden (not only disabled) while the address has missing fields; "Registrada" offers no re-register
  button (the server would accept it, nothing to sync). The address-incomplete 400 shows a copy and re-reads the state, whose
  `camposFaltantes` provide the labels (the 400 itself only carries the machine text).
- Open: no per-row status in the grids (by design); the QR host is whatever Mercado Pago returns (the back-office web has no CSP; the
  desktop POS does not host these screens); POS bundle unaffected, no release needed.

## Next step

E2 complete; next E3 (dynamic QR charge).

## Judgment-day T1 verdict

- 2026-10-31: **APPROVED** after round one. Both judges confirmed E2T1-1 (no renewal backoff) and E2T1-2 (unguarded
  error-body read); fixed in afa5bf5a; the scoped re-judgment of the delta came back clean from both. RDD is off.
- Suspect (judge A only, not auto-fixed): a stale 401 whose guarded wipe touches nothing (a renewal already rotated the
  token) still answers `mercadopago_requiere_reconexion` on a healthy connection; it should retry once with the fresh
  token or answer `mercadopago_no_disponible`. Pick it up in T3, where the first write calls appear.
- Info: a non-GET call without an idempotency key is allowed; T3/T4 must always pass one.

## Judgment-day T2 verdict

- 2026-11-01: **APPROVED** in round zero: neither blind judge reported a CRITICAL finding on 5516a04d. RDD is off.
- Info (both judges): the web range check runs on the raw value while the server rounds to 6 decimals first; the input
  pattern already limits to 6 decimals, so it is unreachable in practice.
- Info (both judges): the PUT replaces the whole address; the only caller (Sucursales.tsx) sends the full form, but a stale
  modal can overwrite an address saved meanwhile by another admin (no concurrency token, same as the name).
- Info (one judge each): an over-long provincia answers `provincia_muy_largo` instead of `provincia_invalida`; the web and
  server provincia lists are duplicated by hand with no test tying them.

## Judgment-day T6 verdict

- 2026-11-01: **APPROVED** in round zero: no CRITICAL finding from either judge on b7a885b8. RDD is off.
- Applied after the verdict (judge B warning, trivial): copies for `mercadopago_renovacion_rechazada` and
  `mercadopago_credenciales_rechazadas`, with their cases in `mercadoPago.test.ts`.
- Info (one judge each, not fixed): the caja modal does not re-read after a register failure caused by a server-side state
  change; an `http:` QR url is accepted (mixed content, no `onError` fallback); a `registrada` caja without QR has no re-sync
  action; the `desactualizada` copy omits the local re-registration cause; assign/remove stay enabled during a terminals refresh.

E2 is complete with T6. Next: E3 (dynamic QR charge), doc `odd/tasks/mercadopago-cobros.md`.
