# Tasks: Stage 7 — Cuenta corriente y reliquidación a precio del día

## Orchestrator Decisions Recorded This Phase

1. **Web split into two PR slices, not one.** The proposal's indicative
   order lists "5. Web" as a single, explicitly "splittable" slice. Following
   the stage-6 precedent (apertura/movimientos/resumen vs. cierre), this
   tasks.md splits it into Slice 5 (estado de cuenta screen + pago modal,
   `OperacionDePos` — every role) and Slice 6 (ajuste + reliquidación modals,
   `SupervisionDeCuentaCorriente` — Supervisor+Admin only, irreversibility +
   rule-10 sibling-replication obligations concentrated here).
2. **Policy-name conflict between design and spec, flagged for correction.**
   `design.md`'s API Surface table names the new policy
   `Politicas.SupervisionDeOperacion` ("named generically on purpose so the
   deferred cierre tightening can stack on it"); every spec file
   (`operacion-de-pos`, `ajustes-de-cuenta-corriente`) names it
   `Politicas.SupervisionDeCuentaCorriente`. Tasks bind to the **spec** name
   (`SupervisionDeCuentaCorriente`) because specs carry the testable
   acceptance scenarios; `sdd-verify` should flag `design.md` for a wording
   correction, same posture as the stage-6 `turno_ya_cerrado` conflict.
3. **Domain-code naming conflict, same treatment.** `design.md`'s Backstop
   Map lists `medio_no_admite_pago_a_cuenta` / `detalle_requerido`;
   `specs/pagos-a-cuenta` and `specs/ajustes-de-cuenta-corriente` use
   `pago_a_cuenta_sin_medios_fisicos` / `ajuste_detalle_requerido`. Tasks bind
   to the spec codes; flag `design.md` for correction at verify.
4. **Slice 2 (pago a cuenta write path) touches `ServicioDeVentas.AnularAsync`**
   — per the caller's instruction, this slice gets its own full judgment-day
   round in addition to the stacked-PR default, mirroring stage-6 Slice 5.
5. **Baselines re-checked at branch time.** Cached baselines (Domain 306 /
   Application 209 / Integration 481 / vitest 219) predate the in-flight D6
   resumen-parcial follow-up PR; Slice 1's branch-cut task re-reads the actual
   counts before recording deltas in later slices.
6. **Doc-10 update split across two slices**, per the DB gate's own condition
   (`state.yaml`): §1's RC catalog note ships in Slice 1 (same slice as the
   seed, so the doc never drifts from the schema); §8's etapa-7 status note +
   marker + financed-fraction deviation ships as a close-out in Slice 6 (last
   slice, once every deviation is actually shipped).

## Review Workload Forecast

| Field | Value |
|-------|-------|
| Estimated changed lines | ~6,000–7,700 total (incl. EF migration boilerplate) |
| 400-line budget risk | High |
| Chained PRs recommended | Yes |
| Suggested split | Slice 1 → Slice 2 → Slice 3 → Slice 4 → Slice 5 → Slice 6 |
| Delivery strategy | auto-chain (cached decision) |
| Chain strategy | stacked-to-main |

Decision needed before apply: No — resolved: chained PRs, stacked-to-main,
`judgment-day` before every PR, per `auto-chain`. Six slices forecast. Slice 2
(pago a cuenta write path) is the highest-risk backend slice — it widens
`ServicioDeVentas.AnularAsync`, the project's most-guarded transaction, and
gets a **dedicated full judgment-day round** on top of the default. Slice 3
(reliquidación engine) is the largest slice by line count and carries the
heaviest pure-Domain test mass — the centerpiece the proposal and design both
name explicitly. Slice 1 (schema gate: one column, one self-FK, one AK, one
partial index, one idempotent seed row) is small relative to stage 6's
five-table gate, but still opens as `size:exception`-adjacent because it is
the DB CHANGE GATE surface, already approved in autonomous mode.

Decision needed before apply: No
Chained PRs recommended: Yes
Chain strategy: stacked-to-main
400-line budget risk: High

### Suggested Work Units

| Unit | Goal | Likely PR | Est. lines | Notes |
|------|------|-----------|-----------|-------|
| 1 | Schema gate: marker column + self-FK + AK + partial index + idempotent RC seed + doc-10 §1 note | PR 1 | ~400–600 | Base: `main`. DB CHANGE GATE already approved (autonomous mode, `state.yaml`) — no STOP task, only the recorded condition (doc-10 §1 in-slice). |
| 2 | Pago a cuenta write path: `EscriturasDeCuentaCorriente` extraction, `ValidadorDePagoACuenta`, new lean `ServicioDeCuentaCorriente.RegistrarPagoAsync`, `AnularAsync` widening | PR 2 | ~1,400–1,800 | Base: PR 1. **Own dedicated judgment-day round** — touches `AnularAsync`. |
| 3 | Reliquidación engine (centerpiece): pure `ReliquidadorDeConsumos` first, then `LectorDeConsumosReliquidables` + `ServicioDeReliquidacion` transaction | PR 3 | ~1,700–2,100 | Base: PR 2 (reuses `EscriturasDeCuentaCorriente`). Largest slice, heaviest unit-test mass. |
| 4 | Ajuste manual + estado de cuenta read API + `SupervisionDeCuentaCorriente` policy | PR 4 | ~1,100–1,300 | Base: PR 3. Closes the mixed-sequence saldo-invariant Success Criterion. |
| 5 | Web: estado de cuenta screen + pago modal | PR 5 | ~850–1,050 | Base: PR 4. `OperacionDePos` surface, every role. |
| 6 | Web: ajuste + reliquidación modals + doc-10 §8 close-out | PR 6 | ~500–700 | Base: PR 5 (same files). `SupervisionDeCuentaCorriente` surface, heavier obligation count. |

---

## Slice 1: Schema Gate — Marker + Seed (PR 1)

**Start**: `main`. **Finish**: `id_movimiento_actualizacion` column + self-FK
+ AK + partial index live; `RC` resolves on both a fresh and a
stage-6-migrated database; doc-10 §1 carries the RC note. **Rollback**:
down-migration (drop the column, its FK, its index; set `RC.activo = false`).

- [x] 1.1 Re-read the four cached baselines (`dotnet test` Domain/Application/
  Integration counts, `npx vitest run` count) at branch-cut time and record
  the actual numbers in this task's checkbox note — the D6 resumen-parcial
  follow-up may have shifted them. *(Orchestrator Decision 5)*
- [x] 1.2 Add `CuentaCorrienteEtapa7` migration: `movimientos_cuenta_corriente
  .id_movimiento_actualizacion integer NULL`; `ak_movimientos_cuenta_corriente
  _id_movimiento_id_tenant (id_movimiento, id_tenant)`;
  `fk_movimientos_cuenta_corriente_actualizacion (id_movimiento_actualizacion,
  id_tenant) → movimientos_cuenta_corriente (id_movimiento, id_tenant) ON
  DELETE RESTRICT`; partial index
  `ix_movimientos_cuenta_corriente_consumos_pendientes (id_cliente, id_tenant)
  WHERE tipo = 'consumo' AND id_movimiento_actualizacion IS NULL`. *(design:
  Table Shapes A — pinned decision "self-FK marker")*
- [x] 1.3 Same migration: idempotent `INSERT INTO tipos_comprobante (…)
  SELECT … WHERE NOT EXISTS (SELECT 1 FROM tipos_comprobante WHERE codigo =
  'RC')` — `clase = venta`, `nombre = 'Recibo de cobranza'`, `letra NULL`,
  `signo = +1`, `discrimina_iva = false`, `es_fiscal = false`,
  `afecta_stock = false`, `activo = true`. *(design: Table Shapes B; spec:
  pagos-a-cuenta / RC Tipo Comprobante Ships As An Idempotent Seed)*
- [x] 1.4 [P] Append `RC` to `TiposComprobanteBase` in
  `InicializadorDeBaseDeDatos.cs` (fresh-database seed list). *(spec:
  pagos-a-cuenta / A fresh database seeds RC from the seed list)*
- [x] 1.5 [P] Modify `src/Ways.Domain/CuentaCorriente/MovimientoCuentaCorriente.cs`:
  add `IdMovimientoActualizacion`; `TipoMovimientoCc.cs` loses its "reserved
  for stage 7" doc-comment on `Pago`/`ActualizacionPrecios`. *(design: File
  Changes)*
- [x] 1.6 Update `MovimientoCuentaCorrienteConfiguration.cs`: the new column,
  self-FK, AK, and partial index. Confirm `has-pending-model-changes` clean.
  *(design: Table Shapes A)*
- [x] 1.7 Update `docs/10-modelo-de-datos.md` §1: add the `RC` row to the
  `tipos_comprobante` catalog note (letra NULL, signo +1, non-fiscal — `PRE`
  precedent). *(gate condition, `state.yaml`; Orchestrator Decision 6)*
- [x] 1.8 Backstop: confirm (comment only, no code change) the generic `fk_`
  prefix branch in `ManejadorDeErrores` covers
  `fk_movimientos_cuenta_corriente_actualizacion` → `400
  referencia_invalida`; confirm `ux_tipos_comprobante_codigo` is already
  mapped (stage 1). *(design: Backstop Map)*
- [x] 1.9 [P] Integration: `RC` resolves on a database migrated from stage 6
  (idempotent insert proven, re-run safe, no duplicate row). *(spec:
  pagos-a-cuenta / RC resolves on an already-migrated database)*
- [x] 1.10 [P] Integration: raw-SQL 23503 backstop test for the new self-FK
  (unreachable in normal flow — the id comes from the same-transaction
  `RETURNING`, tested via a forced raw insert); confirm RLS still applies to
  `movimientos_cuenta_corriente` with the new column (no policy change
  needed). *(design: Backstop Map; Table Shapes A — RLS note)*

**Verify**: `dotnet test --filter FullyQualifiedName~MovimientoCuentaCorriente|FullyQualifiedName~TiposComprobante`

---

## Slice 2: Pago a Cuenta Write Path (PR 2) — own full judgment-day round

**Depends on**: Slice 1 (marker column not required here, but the migration
must be live). **Start**: PR 1 merged/branch. **Finish**: `RC` payments emit
end-to-end through a new lean service, cash lands in the arqueo with no new
derivation term, anulación reverses the `Pago` movement, the entire stage-5/6
integration suite stays green. **Rollback**: new files + the 3-line
`AnularAsync` widening only — revert restores stage-6 `AnularAsync` behaviour
bit-for-bit for `TX`/`NCX`.

- [ ] 2.1 [P] Create `src/Ways.Application/CuentaCorriente/EscriturasDeCuentaCorriente.cs`:
  extract `ActualizarSaldoClienteAsync` (`ServicioDeVentas.cs:811-828`) and
  `InsertarMovimientoCcAsync` (`:830-855`) **verbatim**, widening
  `id_comprobante_venta`/`id_pago_comprobante` to nullable-per-tipo (a
  `Consumo` requires `id_pago_comprobante`, a `Pago` must not carry one).
  *(design decision 1 — pinned: "shared EscriturasDeCuentaCorriente")*
- [ ] 2.2 Modify `ServicioDeVentas.cs`: delegate the two extracted statements
  to `EscriturasDeCuentaCorriente` — no behavior change to the existing
  `Consumo` write path. *(design decision 1; File Changes)*
- [ ] 2.3 [P] Create `src/Ways.Domain/CuentaCorriente/ValidadorDePagoACuenta.cs`:
  a sibling pure validator (not a `ValidadorDePagos` branch) — 7 rules,
  observable rejection order, CC medio forbidden, no importe field
  (`importeAplicado = Σ importe − Σ vuelto`). *(design decision 6 — pinned:
  "ValidadorDePagoACuenta sibling class"; spec: pagos-a-cuenta / RC Forbids
  Cuenta Corriente Medios And Consumidor Final)*
- [ ] 2.4 Promote `AsignarNumeroComprobante`/`AsignarComprometidoAsync` from a
  private method of `ServicioDeVentas` to
  `AsignadorDeNumeroComprobante.AsignarComprometidoAsync` (pure move, no new
  mechanism). *(design decision 7 — pinned: "numeración untouched"; spec:
  comprobantes-venta / RC and TX numerar independently)*
- [ ] 2.5 Create `src/Ways.Application/CuentaCorriente/ServicioDeCuentaCorriente.cs`
  with `RegistrarPagoAsync`: resolve cliente (404 / CF → 400
  `cliente_sin_cuenta_corriente`), punto de venta (404), turno abierto (409
  `turno_no_abierto`, before all else), `ValidadorDePagoACuenta.Validar` →
  `importeAplicado`, `AsignarComprometidoAsync` (own transaction), then the
  5-step transaction: `ExigirTurnoAbiertoBajoLockAsync` (`FOR SHARE`, first
  statement) → INSERT `comprobantes_venta` → INSERT `pagos_comprobante` →
  `EscriturasDeCuentaCorriente.ActualizarSaldo(−importeAplicado)` → INSERT
  movimiento `pago`. *(design: Transactions — PAGO A CUENTA, binding
  statement order; design decision 1 — pinned: "new lean services")*
- [ ] 2.6 Modify `ServicioDeVentas.AnularAsync` — the pinned 3-line widening:
  contramovimiento filter becomes `Tipo == Consumo || Tipo == Pago`;
  `id_pago_comprobante` nullable per tipo; a reliquidated consumo raises
  `409 consumo_reliquidado`. *(design decision 5 — pinned: "AnularAsync
  3-line widening + 409 consumo_reliquidado"; spec: pagos-a-cuenta /
  Anulación Reverses The Pago Movement; consumo-cuenta-corriente / Anulación
  Produces A Contramovimiento)*
- [ ] 2.7 Add `CuentaCorrienteEndpoints.cs`: `POST
  /api/clientes/{id}/cuenta-corriente/pagos` under `OperacionDePos`. Update
  `SuperficieDeAutorizacionTests` allowlist with this new non-GET route.
  *(design: API Surface)*
- [ ] 2.8 [P] Unit: `ValidadorDePagoACuenta` — all 7 rules, observable
  rejection order, CC medio rejected, CF rejected, importeAplicado
  derivation. *(design decision 6; spec: pagos-a-cuenta, all validation
  scenarios)*
- [ ] 2.9 Integration: RC emission persists zero items and zero
  `movimientos_stock`; RC with no open turno rejected `409
  turno_no_abierto` before any write; RC attaches the resolved open turno;
  RC with a CC medio rejected `pago_a_cuenta_sin_medios_fisicos`; RC
  targeting Consumidor Final rejected `cliente_sin_cuenta_corriente`; RC
  accepted with mixed physical medios. *(spec: pagos-a-cuenta, all six
  scenarios under "RC Comprobante…", "RC Requires An Open Turno", "RC
  Forbids…")*
- [ ] 2.10 Integration: RC emission writes one `Pago` movement and drops
  `Cliente.Saldo`; a failure after the comprobante insert rolls back
  everything (comprobante, movement, saldo); overpayment produces saldo a
  favor, never rejected. *(spec: pagos-a-cuenta / RC Writes One Negative
  Pago Movement Atomically, Overpayment Produces Saldo A Favor)*
- [ ] 2.11 Integration: anulando an RC restores saldo with a `+`
  contramovimiento; anulando an RC is rejected `409 turno_cerrado` when its
  turno is closed. *(spec: pagos-a-cuenta / Anulación Reverses The Pago
  Movement, both scenarios)*
- [ ] 2.12 [P] Integration: RC and TX numerar independently at the same
  punto de venta. *(spec: comprobantes-venta / RC and TX numerar
  independently; pagos-a-cuenta / RC Gets Its Own Numeración Series)*
- [ ] 2.13 Integration (arqueo participation): a turno with a TX sale
  (efectivo) and an RC pago a cuenta (efectivo) — both contribute to the
  same `SUM(pagos_comprobante.importe)` term, no separate RC line, no code
  change to `CalculadorDeArqueo`. *(spec: arqueo-de-cierre / An RC pago
  counts toward efectivo esperado like any other pago)*
- [ ] 2.14 Integration (concurrency, racy surface): pago a cuenta racing a
  cierre de turno — the pago is either counted in the arqueo or rejected
  `409 turno_no_abierto`, never neither. *(design: Backstop Map — "three
  racy surfaces"; Concurrency guarantees)*
- [ ] 2.15 [P] Integration (budget): pago a cuenta issues a **constant** ≤ 7
  queries regardless of medios count. `DbCommand` interceptor test. *(design:
  Transactions — "Read budget")*
- [ ] 2.16 Run a **dedicated full judgment-day round** on this slice's diff
  alone before opening the PR — `AnularAsync` is the project's most-guarded
  transaction. *(Orchestrator Decision 4)*
- [ ] 2.17 Regression: entire stage-5/6 integration suite green, no
  assertion changed beyond the new turno-precondition-adjacent fixtures.

**Verify**: `dotnet test --filter FullyQualifiedName~ServicioDeCuentaCorriente|FullyQualifiedName~ServicioDeVentas.AnularAsync`

---

## Slice 3: Reliquidación Engine (PR 3) — the centerpiece

**Depends on**: Slice 2 (`EscriturasDeCuentaCorriente`). **Start**: PR 2
merged/branch. **Finish**: the pure re-pricer is exhaustively tested, the
commit transaction and the preview share one formula, all three reliquidación
racy surfaces are proven. **Rollback**: new files only — reliquidación simply
cannot run again if reverted; no stage-5/6 behaviour depends on it.

- [ ] 3.1 Create `src/Ways.Domain/CuentaCorriente/ReliquidadorDeConsumos.cs` +
  records `LineaAReliquidar`, `ConsumoAReliquidar`, `ResultadoDeReliquidacion`,
  `DetalleDeConsumo`: `totalDelDia = round(cantidad × precioActual, 2,
  AwayFromZero)` (never `id_oferta IS NOT NULL` as the "offer applied"
  signal — `descuento > 0` is), `delta(c) = round(Σ delta(i) × factor(c), 2,
  AwayFromZero)` with `factor = min(1, importeFinanciado / totalComprobante)`,
  unpriceable lines (`IdArticulo NULL` or no vigente price) skipped with a
  motivo, never fatal. *(design: The Re-Pricing Derivation; pinned decisions
  "financed-fraction proration", "skip-unpriceable"; Interfaces/Contracts)*
- [ ] 3.2 [P] Unit: `ReliquidadorDeConsumos` exhaustive — plain re-price
  up/down; offer reversion in both directions with the worked example
  (sold 900, current 1500, delta 600 = 500 re-pricing + 100 annulled
  discount) asserted numerically; `factor = 1` collapsing to the legacy
  formula; partial financing proration; missing price / `IdArticulo NULL`
  skipped with motivo; all-lines-skipped ⇒ delta 0; empty input; rounding at
  `AwayFromZero`; the 500-consumo cap (`HayMas: true`). *(design: Testing
  Strategy — Unit (Domain); spec: reliquidacion-a-precio-del-dia / Offer-Line
  Discounts Are Reverted, Never Excluded, both scenarios; Re-Pricing Uses The
  Client's Current id_lista_precio)*
- [ ] 3.3 Create `src/Ways.Application/CuentaCorriente/LectorDeConsumosReliquidables.cs`:
  eligibility query (`tipo = 'consumo'`, `id_movimiento_actualizacion IS
  NULL`, `importe > 0`, comprobante `estado = 'emitido'`,
  `comprobante.total > 0`), ordered `fecha ASC`, `LIMIT 500`; items query;
  `ServicioDePrecios.PreciosVigentesEnLoteAsync` call — **never**
  `ServicioDeOfertas.ResolverAsync`. *(design decision 3 — pinned: "re-pricer
  via PreciosVigentesEnLoteAsync never ResolverAsync"; Eligibility
  paragraph; pinned deviation "anulados-excluded eligibility"; spec:
  reliquidacion-a-precio-del-dia / Eligibility Scan Covers Not-Yet-
  Reliquidated Consumos Only)*
- [ ] 3.4 Create `src/Ways.Application/CuentaCorriente/ServicioDeReliquidacion.cs`:
  preview (no lock, calls the same `ReliquidadorDeConsumos`) + commit — the
  8-step transaction: `SELECT saldo, id_lista_precio FROM clientes … FOR
  UPDATE` (lock #1, **no turno**) → eligible-consumos scan → items query →
  `PreciosVigentesEnLoteAsync` → `Calcular` (delta = 0 ⇒ COMMIT, no write,
  200 no-op) → `UPDATE clientes SET saldo += delta … RETURNING` → INSERT
  movimiento `actualizacion_precios RETURNING id` → `UPDATE
  movimientos_cuenta_corriente SET id_movimiento_actualizacion = $id WHERE
  id_movimiento = ANY($ids) AND id_movimiento_actualizacion IS NULL`
  (rowcount mismatch ⇒ throw, defense in depth). *(design decision 4 —
  pinned: "cliente FOR UPDATE first, no turno"; Transactions — RELIQUIDACIÓN,
  binding statement order)*
- [ ] 3.5 Add `Politicas.SupervisionDeCuentaCorriente` constant
  (Supervisor + Admin). Add `CuentaCorrienteEndpoints`: `GET
  /api/clientes/{id}/cuenta-corriente/reliquidacion` (preview) and `POST`
  (commit), both under `SupervisionDeCuentaCorriente`. Update
  `SuperficieDeAutorizacionTests` allowlist with the new POST route.
  *(Orchestrator Decision 2; spec: operacion-de-pos /
  SupervisionDeCuentaCorriente Policy Gates Reliquidación And Ajuste Manual)*
- [ ] 3.6 Integration (derivation identity): `GET …/reliquidacion`
  immediately before the `POST` returns a delta **byte-identical** to the
  committed movement's `importe`. *(design: Testing Strategy — Integration
  (derivation identity); the "never two formulas" contract)*
- [ ] 3.7 Integration (atomicity): force a failure at each of the 8
  reliquidación steps ⇒ saldo, marker, and ledger all untouched. *(spec:
  reliquidacion-a-precio-del-dia / A fault-point failure rolls back the
  marker together with the movement)*
- [ ] 3.8 Integration (concurrency): reliquidación × sale race for the same
  cliente — rendezvous test, no lost consumo, no double count; two
  concurrent reliquidaciones — the loser re-scans, finds an empty set,
  returns a clean no-op with exactly one movement written total. *(spec:
  reliquidacion-a-precio-del-dia / Concurrent Reliquidación And Sale…; design:
  Concurrency guarantees — "Two reliquidaciones")*
- [ ] 3.9 Integration: running reliquidación twice writes exactly one
  movement, the second run a clean no-op with `Cliente.Saldo` unchanged; a
  previously reliquidated consumo is skipped; re-pricing reads the client's
  current lista, not the sale-time lista. *(spec: reliquidacion-a-precio-
  del-dia / A Run With No Eligible Consumos Is A No-Op, Eligibility Scan…,
  Re-Pricing Uses The Client's Current id_lista_precio)*
- [ ] 3.10 [P] Integration: two comprobantes / three lines write exactly one
  `ActualizacionPrecios` movement with `importe` equal to the summed deltas;
  no reversal endpoint exists for `ActualizacionPrecios` (404). *(spec:
  reliquidacion-a-precio-del-dia / One ActualizacionPrecios Movement Per
  Run…, Reliquidación Is Irreversible…)*
- [ ] 3.11 [P] Integration (budget): constant ≤ 8 queries over 2 / 50 / 200
  eligible consumos. `DbCommand` interceptor test. *(design: Transactions —
  "Read budget")*
- [ ] 3.12 [P] Integration (authorization): Vendedor rejected `403` from
  reliquidación; Supervisor and Admin both succeed (authorization-wise).
  *(spec: operacion-de-pos / Supervisor can run reliquidación…, Vendedor is
  rejected…)*
- [ ] 3.13 Close the anulación×reliquidación TOCTOU (judgment-day slice-2
  finding, judge A): `AnularAsync`'s `consumo_reliquidado` guard reads the
  movements via a plain unlocked SELECT before any row lock — a concurrent
  reliquidación committing its marker between that read and the reversal
  commit produces an unrepresentable "reversed and reliquidated" state.
  Fix per the judge's recommendation: lock the `clientes` row (or re-check
  `id_movimiento_actualizacion` for each movement immediately after
  acquiring its cliente-row lock, failing closed with `409
  consumo_reliquidado` if it flipped). Rendezvous race test: anulación ×
  reliquidación of the same cliente ⇒ exactly one wins; never both.
  *(design: Concurrency guarantees — extends the enumerated racy surfaces)*
- [ ] 3.14 Regression: Slices 1–2 suites unedited and green.

**Verify**: `dotnet test --filter FullyQualifiedName~ReliquidadorDeConsumos|FullyQualifiedName~ServicioDeReliquidacion`

---

## Slice 4: Ajuste Manual + Estado de Cuenta API (PR 4)

**Depends on**: Slice 3 (`Politicas.SupervisionDeCuentaCorriente` already
exists). **Start**: PR 3 merged/branch. **Finish**: manual ajustes post
against saldo with a required detalle, estado de cuenta reads header + page
in one call, the mixed-sequence saldo-invariant Success Criterion is
provable end-to-end. **Rollback**: new files + one new endpoint group only.

- [ ] 4.1 [P] Create `src/Ways.Domain/CuentaCorriente/ReglaDeAjusteDeCuenta.cs`:
  `importe ≠ 0`, `detalle` required with `length(btrim(detalle)) >= 5`.
  *(design decision 8 — pinned: "ajuste structural distinction"; Transactions
  — AJUSTE MANUAL; spec: ajustes-de-cuenta-corriente / Ajuste Requires A
  Detalle)*
- [ ] 4.2 [P] Create `src/Ways.Domain/CuentaCorriente/CalculadorDeEstadoDeCuenta.cs`:
  `disponibilidad` as `decimal?` (`credito_ilimitado` ⇒ `null`, never a
  fabricated number); movement labelling derived structurally
  (`id_comprobante_venta IS NULL` ⇒ manual ajuste, `IS NOT NULL` ⇒ anulación
  contramovimiento — no new column). *(design decision 8, decision 9;
  Interfaces/Contracts)*
- [ ] 4.3 Extend `ServicioDeCuentaCorriente.cs` with `RegistrarAjusteAsync`
  (single-statement transaction: `UPDATE clientes SET saldo = saldo +
  importe … RETURNING` → INSERT movimiento `ajuste`, `id_comprobante_venta
  NULL`, `detalle`) and `ObtenerEstadoDeCuentaAsync` (header + page in one
  `GET`; running balance is the stored `saldo_resultante`, never
  re-derived; default last-month window, `desde`/`hasta`, `historico`).
  *(design decision 9; Transactions — AJUSTE MANUAL)*
- [ ] 4.4 Add endpoints: `POST /api/clientes/{id}/cuenta-corriente/ajustes`
  under `SupervisionDeCuentaCorriente`; `GET
  /api/clientes/{id}/cuenta-corriente?desde=&hasta=&historico=` under
  `OperacionDePos`. Update `SuperficieDeAutorizacionTests` allowlist with the
  new POST route. *(design: API Surface)*
- [ ] 4.5 [P] Unit: `ReglaDeAjusteDeCuenta` — empty/short detalle rejected,
  the 5-char boundary, `importe = 0` rejected; `CalculadorDeEstadoDeCuenta`
  — `credito_ilimitado` ⇒ `null` disponibilidad, movement-label derivation
  both directions. *(spec: ajustes-de-cuenta-corriente / Ajuste Requires A
  Detalle, Ajuste Is Distinct From The Anulación Contramovimiento)*
- [ ] 4.6 Integration: ajuste with no detalle rejected
  `ajuste_detalle_requerido` before any write; a negative ajuste reduces
  saldo; a manual ajuste carries `id_comprobante_venta NULL` and stays
  distinguishable from an anulación contramovimiento; ajuste snapshots
  `saldo_resultante` atomically. *(spec: ajustes-de-cuenta-corriente, all
  four "Ajuste Requires…"/"Is Distinct…"/"Updates Saldo…" scenarios)*
- [ ] 4.7 [P] Integration (authorization): Supervisor posts an ajuste
  successfully; Vendedor rejected `403`. *(spec: ajustes-de-cuenta-corriente
  / Ajuste Authorization Under Supervisor + Admin, both scenarios)*
- [ ] 4.8 Integration: disponibilidad for a limited-credit cliente; ilimitado
  when `credito_ilimitado`; the movement list's `saldo_resultante` matches
  the ledger at every row; no filter returns the last month; `historico`
  returns the full ledger; RLS blocks a cross-tenant read; a brand-new
  cliente's estado de cuenta returns `saldo = 0` and an empty list with
  `200`, never a 404. *(spec: estado-de-cuenta, all seven scenarios)*
- [ ] 4.9 Integration (invariant, Success Criterion): `Cliente.Saldo` equals
  the sum of that cliente's `movimientos_cuenta_corriente.importe` over a
  scenario mixing consumo, pago, ajuste, reliquidación and anulación.
  *(spec: consumo-cuenta-corriente / Saldo Is The Maintained Cache Of The
  Ledger — mixed sequence; proposal: Success Criteria)*
- [ ] 4.10 Regression: Slices 1–3 suites unedited and green.

**Verify**: `dotnet test --filter FullyQualifiedName~ServicioDeCuentaCorriente|FullyQualifiedName~CalculadorDeEstadoDeCuenta`

---

## Slice 5: Web — Estado de Cuenta Screen + Pago Modal (PR 5)

**Depends on**: Slice 4 (estado de cuenta + pago endpoints). **Start**: PR 4
merged/branch. **Finish**: the screen renders header + filtered movement
list + a working pago modal for every role; `CuentaCorriente.tsx` compiles
and is tested. **Rollback**: new route + entry point only.

- [ ] 5.1 [P] Add `src/Ways.Web/src/api/cuentaCorriente.ts`: pure
  request/response mappers for header + page + pagos; a
  **non-authoritative** disponibilidad mirror (same posture as `arqueo.ts`).
  *(design: Web Composition)*
- [ ] 5.2 Add `src/Ways.Web/src/paginas/CuentaCorriente.tsx`: header (saldo /
  acuerdo — `"ilimitado"` when applicable / disponibilidad), desde–hasta +
  "ver histórico" filters, movement table reading `saldoResultante` per row,
  pago modal. `react-async-state` rules 8 (`key={idCliente}` on the
  subtree), 9 (first-line re-entrancy guard + full-window disable on the
  pago), 3 (ledger generation bumped before the write), 7 (medios de pago
  load failure ⇒ visible aviso + actually-disabled "Registrar pago").
  *(design: Web Composition; react-async-state obligations 3, 7, 8, 9)*
- [ ] 5.3 Modify `Clientes.tsx`: per-row entry point to
  `/clientes/:id/cuenta-corriente`; modify `App.tsx`: wire the route.
  *(design: File Changes)*
- [ ] 5.4 [P] Unit: `cuentaCorriente.ts` mappers, disponibilidad mirror.
  *(web-descriptor-tests)*
- [ ] 5.5 Component: pago flow succeeds and refetches the ledger; empty
  ledger renders an empty state, never a re-query; medios/estado-de-cuenta
  failing to load shows an aviso and an actually-disabled "Registrar pago".
  RTL + `user-event`, `vi.mock('../api/cliente')`. *(design: Testing
  Strategy — Component (Web))*

**Verify**: `npx vitest run src/paginas/CuentaCorriente.test.tsx src/api/cuentaCorriente.test.ts`

---

## Slice 6: Web — Ajuste + Reliquidación Modals (PR 6)

**Depends on**: Slice 5 (same screen file) + Slice 3/4 (real ajuste/
reliquidación endpoints). **Start**: PR 5 branch. **Finish**: both
Supervisor+Admin actions functional, reliquidación is double-submit-proof,
the `turno_no_abierto` recovery path is replicated across every sibling
modal, doc-10 §8 records the stage as implemented. **Rollback**: two modals +
role-gated buttons + the doc note only.

- [ ] 6.1 Extend `CuentaCorriente.tsx`: ajuste modal (importe + detalle,
  role-gated) and reliquidación modal (preview + commit, role-gated,
  irreversible-by-design confirmation). Role gating via the existing
  `usuario?.rolId` claim (`ROL.Supervisor | ROL.Admin`) — cosmetic; server
  `SupervisionDeCuentaCorriente` is the enforcement. *(design: Web
  Composition; spec: operacion-de-pos / SupervisionDeCuentaCorriente Policy…)*
- [ ] 6.2 Implement rule 9 on **both** new modals: first-line re-entrancy
  guard + full-window disable — a double-submit on reliquidación would
  charge/re-price the client twice. Implement rule 6: a 2xx reliquidación is
  never reported as failure (the post-write ledger refetch has its own
  try/catch and its own copy). *(design: Web Composition — react-async-state
  obligations 6, 9)*
- [ ] 6.3 **Rule 10 — sibling-surface replication.** Grep the pago modal's
  `turno_no_abierto` recovery path (Slice 5) and replicate it across the
  ajuste and reliquidación modals in this same commit — all three are
  sibling surfaces that can raise the same 409. *(design: Web Composition —
  react-async-state obligation 10)*
- [ ] 6.4 Update `docs/10-modelo-de-datos.md` §8: status note — etapa 7
  implemented; the marker as a self-FK (design decision 2); the
  financed-fraction deviation from strict legacy parity. *(design: Migration
  / Rollout; Orchestrator Decision 6)*
- [ ] 6.5 [P] Component: double-click on "Reliquidar" issues exactly one
  POST (rule 9); `turno_no_abierto` recovery present in every sibling modal
  (pago, ajuste, reliquidación). RTL + `user-event`. *(design: Testing
  Strategy — Component (Web))*
- [ ] 6.6 Smoke-verify (`tsc -b` / `oxlint` / `vite build` clean).
- [ ] 6.7 Regression: full `npx vitest run` green (Slice 1's re-checked
  baseline + this stage's new tests, no unrelated assertion changed).

**Verify**: `npx vitest run` (full suite) && `npx tsc -b` && `npx vite build`

---

## Dependency Summary

```
Slice 1 (schema gate — marker + idempotent RC seed, DB CHANGE GATE pre-approved)
        │
        ▼
Slice 2 (pago a cuenta write path — EscriturasDeCuentaCorriente extraction,
         AnularAsync widening — own dedicated judgment-day round)
        │
        ▼
Slice 3 (reliquidación engine — the centerpiece: pure re-pricer first,
         then the 8-step transaction)
        │
        ▼
Slice 4 (ajuste manual + estado de cuenta API — closes the mixed-sequence
         saldo invariant)
        │
        ▼
Slice 5 (web: estado de cuenta screen + pago modal, OperacionDePos)
        │
        ▼
Slice 6 (web: ajuste + reliquidación modals, SupervisionDeCuentaCorriente —
         heavier obligation count, doc-10 §8 close-out)
```

Within each slice, `[P]`-tagged tasks are parallelizable; all others are
sequential (schema → domain → application → API → tests). Every slice after
Slice 1 shares either `EscriturasDeCuentaCorriente`, the cliente row lock, or
the `SupervisionDeCuentaCorriente` policy, per design's lock-order and
one-derivation invariants — no two slices are fully independent. Chained
PRs, stacked-to-main, `judgment-day` before every PR (per
`protocolo-pr-solo-dev`); Slice 2 gets a dedicated full judgment-day round.
