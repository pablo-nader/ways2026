# Design: Stage 9 — Costo congelado en la línea de venta

## Technical Approach

Stage 9 is the smallest stage in the program and its design goal is to *stay* that way. The sale
line already freezes seven fields; this stage adds an eighth. There is **no new service, no new
calculator, no new rule, no new query, no new lock, no new failure mode and no new API surface** —
only a column pair, one assignment that rides data already in hand, and one migration whose only
genuinely hard part is the RLS trap of its backfill.

The capture rides the *non-retryable* half of `ServicioDeVentas.EmitirAsync`. `articuloPorId`
(`ServicioDeVentas.cs:96-98`) already materializes the whole `Articulo`, `CostoNominal` included,
two statements before `MaterializarItems` runs. The cost therefore enters the immutable
`PlanDeVenta` exactly like `momento` does: **pinned once, before the retryable lambda**, so a retry
can never re-read a `costo_nominal` that a concurrent compra confirm moved mid-flight. The
transactional half only copies the value it was handed.

Everything else is stage-5/8 posture reused verbatim: snapshot fields live on the entity next to
their siblings, CHECKs back the invariant at the schema layer, `db-error-backstops` maps every new
constraint, and `ServicioDeVentas` is *extended*, never restructured.

## Architecture Decisions

| # | Decision | Alternatives considered | Rationale |
|---|---|---|---|
| 1 | **Capture inside `MaterializarItems` (`:786-806`), carried by `LineaDelPlan` (`:971-974`), written in `EjecutarTransaccionAsync` step 3 (`:600-620`).** Three edited lines plus one record field | (a) Read `costo_nominal` inside the transaction, next to the stock upsert; (b) resolve the cost in `ServicioDeOfertas` alongside the price; (c) a `CalculadorDeCosto` in Domain | (a) adds a query **inside** the transaction, extends the lock window, and re-reads a value the retry contract wants frozen — the cost of a retried sale could differ from the cost of its first attempt. (b) conflates the pricing authority with a purely informational snapshot and would widen `ResultadoDeResolucion` for no reader. (c) invents a Domain class with zero arithmetic to own: the value is copied, not computed. The chosen point is where `articulo.Nombre`, `articulo.IdArea` and `articulo.IdAlicuotaIva` are already copied (`:801-805`) — the cost is the same kind of value and belongs in the same statement |
| 2 | **The pinned query budget stays at exactly `17`** — `VentasCheckoutTests.ElCheckoutEmiteUnaCantidadConstanteDeConsultasIndependienteDeLaCantidadDeLineas` (`:918`, `Assert.Equal(17, …)`) is **not edited by this stage** | Relax the assertion to `<=` while touching the file | The test asserts equality on purpose (`:911-917`: "una baja tiene que notarse acá"). Since the capture reads no new row, the constant is the machine-checkable proof of "zero extra queries". **Any diff that touches line 918 is a design violation, not a test update** |
| 3 | **NCX needs zero dedicated code.** An NCX travels the same `EmitirAsync` path with `signoTipoComprobante = -1`, and `MaterializarItems` applies the sign **only** to `Cantidad` (`:773`). `CostoNominal` is copied unsigned, so `costo_unitario × cantidad` is negative on an NCX with no branch | (a) Copy the cost from the associated comprobante's items; (b) negate `costo_unitario` when `signo < 0` | (a) is not computable: `id_comprobante_asociado` is optional even for an NCX and links **header to header** (`ReglaDeComprobantes.ValidarComprobanteAsociado`, `:77-82`) — there is no `id_item_original`, and article-matching heuristics would guess silently on partial returns. (b) would double-negate against the already-signed `cantidad`. Proposal decision 3's unsigned invariant is thus a *property of the existing code*, not a new rule to enforce — and that is exactly what makes it cheap |
| 4 | **The migration's backfill carries its own `SET LOCAL app.acceso = 'plataforma'` in the same `Sql()` block as the `UPDATE`** | (a) Rely on the applying context already being in platform mode; (b) grant the migration role `BYPASSRLS`; (c) ship the columns with no backfill | (a) is **verified false for the deploy path**: `WaysDbContextFactory` (`:30-52`, what `dotnet ef database update` uses) builds the context with **no** `InterceptorDeContextoDeTenant` — that interceptor is only registered in DI (`DependencyInjection.cs:52`, `:65`). With no interceptor the GUC is never set, `app_modo()` returns `'ninguno'`, and under `FORCE ROW LEVEL SECURITY` + `USING (app_es_plataforma() OR id_tenant = app_tenant_actual())` the `UPDATE` matches **zero rows and reports success**. (b) is forbidden by `InicializadorDeBaseDeDatos.VerificarRolSinBypassAsync`. (c) is the honest fallback if the owner rejects estimated data, but discards the only cost we will ever know for historical lines. `SET LOCAL` expires at COMMIT by construction, so there is nothing to reset and nothing leaks into later migrations — **the block must not be emitted with `suppressTransaction: true`** |
| 5 | **Both CHECKs get an exact-name mapping in the existing `ClasificarCheckDeVentas` (`ManejadorDeErrores.cs:531-555`), even though both are unreachable from every verified write path** | Document the omission per the `db-error-backstops` "platform seed only" gate | Unreachability was verified, not assumed: `ServicioDeArticulos.ExigirCostoValido` (`:447-459`) rejects a negative `costo_nominal` at the ABM, and `CalculadorDeCompra` only ever writes a `>= 0` effective cost. Nothing outside the backfill writes `costo_es_estimado = true`. But an unmapped 23514 on the **checkout** path is a 500 on the widest-reach endpoint in the system, and the mapping is two switch arms with no ordering trap (the switch matches full names, unlike the `_numero` family that bit stage 8). Cost: near zero. Benefit: the worst reachable outcome degrades from 500 to 400 |
| 6 | **`costo_es_estimado` is mapped with `HasDefaultValue(false)`, mirroring `Descuento` (`ItemComprobanteVentaConfiguration.cs:52-56`)** | `IsRequired()` with no default, writing `false` explicitly on every insert | The DB default is what makes the column addable to a populated table without rewriting rows, and what keeps the emission path from having to *say* anything about estimation. EF omitting the column when the CLR value equals the default is harmless here: both sides are `false`. The entity property stays a plain `bool` — the emission path never assigns it |

## Data Flow

```
  POST /api/ventas
    │
    ├─ db.Articulos → articuloPorId              (ServicioDeVentas.cs:96-98, YA existía)
    │        └── Articulo.CostoNominal ─────────┐   sin consulta adicional
    │                                            │
    ├─ MaterializarItems (:786-806) ─────────────┴─→ LineaDelPlan.CostoUnitario
    │        (fuera de la lambda reintentable: el costo queda pineado como `momento`)
    │
    └─ PlanDeVenta ──→ EjecutarTransaccionAsync paso 3 (:600-620)
                          └─→ ItemComprobanteVenta.CostoUnitario  (+ CostoEsEstimado = false por default)

  ComprobanteEmitido / ItemEmitido ──✗── el costo NO cruza (decisión 5 del proposal)
```

## File Changes

| File | Action | Description |
|---|---|---|
| `src/Ways.Domain/Ventas/ItemComprobanteVenta.cs` | Modify | `CostoUnitario` (`decimal?`) + `CostoEsEstimado` (`bool`), placed after `Total`; the class doc-comment's snapshot list (`:14-19`) gains both, and each property gets its own `<summary>` in the style of `IdListaPrecio`/`Cantidad` — unsigned per-unit, IVA included, `NULL` = unknown, `0` = a stated cost of zero |
| `src/Ways.Infrastructure/Persistencia/Configuraciones/ItemComprobanteVentaConfiguration.cs` | Modify | Two `Property` mappings (`numeric(14,2)` nullable; `boolean` `HasDefaultValue(false)` required) + two `HasCheckConstraint` |
| `src/Ways.Infrastructure/Persistencia/Migraciones/*_CostoCongeladoEnVentaEtapa9.cs` | Create | 2 × `AddColumn`, 2 × `AddCheckConstraint`, 1 × `Sql()` with the platform-mode backfill; `Down` drops all four |
| `src/Ways.Application/Ventas/ServicioDeVentas.cs` | Modify | `LineaDelPlan` gains `decimal? CostoUnitario`; set at `:801-805`; written at `:600-620` |
| `src/Ways.Api/Seguridad/ManejadorDeErrores.cs` | Modify | Two arms in `ClasificarCheckDeVentas` (decision 5) |
| `openspec/specs/comprobantes-venta/spec.md` | Modify | Delta per the proposal's *Modified Capabilities* |
| `docs/10-modelo-de-datos.md` §4 | Modify | Schema note, trailing-blockquote convention of stages 5-8 |
| `tests/Ways.IntegrationTests/*` | Create/Modify | See *Testing Strategy* |
| `src/Ways.Web/**`, `src/Ways.Application/Ventas/Contratos.cs` | **Untouched** | Decision 5 of the proposal |

## Migration Shape

Name: **`CostoCongeladoEnVentaEtapa9`**. One migration, four DDL operations plus one data statement,
in this order:

1. `AddColumn<decimal>("costo_unitario", "items_comprobante_venta", "numeric(14,2)", nullable: true)`
2. `AddColumn<bool>("costo_es_estimado", …, nullable: false, defaultValue: false)`
3. `AddCheckConstraint("ck_items_comprobante_venta_costo_no_negativo", …)`
4. `AddCheckConstraint("ck_items_comprobante_venta_estimado_con_costo", …)`
5. `migrationBuilder.Sql(…)` — **one block**, `SET LOCAL app.acceso = 'plataforma';` immediately
   followed by the proposal's `UPDATE … FROM articulos …`, both inside the migration transaction.

The CHECKs are added **before** the backfill on purpose: they then validate the backfill itself
instead of being asserted over data they never saw. No RLS call is needed — `items_comprobante_venta`
already carries its policy from `VentasStockYCuentaCorrienteEtapa5`, and a new column inherits it;
`HabilitarRlsDeTenant` must **not** be re-invoked (it would fail on the duplicate policy).

`Down`: drop both CHECKs, then both columns. No other object is touched.

## Backstop Map (db-error-backstops)

| Constraint | Reachable from a client write path? | Mapping | Test |
|---|---|---|---|
| `ck_items_comprobante_venta_costo_no_negativo` | No — `ExigirCostoValido` (`ServicioDeArticulos.cs:447-459`) and `CalculadorDeCompra` both keep `costo_nominal >= 0` | 23514 → 400 `costo_de_item_invalido`, exact name in `ClasificarCheckDeVentas` | Raw-SQL insert asserting `SqlState == "23514"` **and** `ConstraintName`, plus a `ManejadorDeErroresVentasTests` arm asserting the translated code |
| `ck_items_comprobante_venta_estimado_con_costo` | No — nothing but the backfill writes `true` | 23514 → 400 `costo_estimado_sin_costo` | Idem |

No new unique index and no new FK ⇒ **no 23505/23503 surface and no race test**: a CHECK has no
race to lose. That exemption is stated here rather than left implicit.

## Testing Strategy

| Layer | What to test | Approach |
|---|---|---|
| Domain (378) | **Nothing new.** Honest statement, not an omission: this stage adds no arithmetic and no rule — `CalculadorDeTotales` and `ReglaDeComprobantes` are untouched, and `ItemComprobanteVenta` is a POCO. A Domain test here would assert the C# compiler | — |
| Application (212) | **Nothing new.** The mapping is proven end-to-end by the integration snapshot test; a `ModeloDeVentasTests` mirror would restate the configuration file | — |
| Integration — snapshot | Emission with `costo_nominal = 121.00` ⇒ line `(121.00, false)`; article with `costo_nominal NULL` ⇒ `(NULL, false)`, **never** `0`; article with `costo_nominal = 0` ⇒ `(0, false)`, distinguishable from the previous case; reprint via `GET /api/ventas/{id}` unchanged | New `CostoCongeladoTests.cs`, `VentasCheckoutTests` seeding helpers |
| Integration — NCX sign | An NCX line stores an **unsigned** `costo_unitario` and `costo_unitario × cantidad` comes out negative; a cost moved between the TX and the NCX is asserted as the *accepted* residual, not as a bug | Idem |
| Integration — query budget | `ElCheckoutEmiteUnaCantidadConstanteDeConsultasIndependienteDeLaCantidadDeLineas` still passes at `Assert.Equal(17, …)` **with the line unedited** | Existing test, run as the slice gate |
| Integration — backfill, multi-tenant (**risk #1**) | Fresh database → migrate to `20260805181153_ComprasYTransferenciasEtapa8` → seed **two** tenants, each with a comprobante + item and an articulo with `costo_nominal`, plus one line with `id_articulo NULL` and one whose articulo has no cost → `MigrateAsync()` → assert **both** tenants' rows are `(costo_nominal, true)` and the two gap rows stayed `(NULL, false)` | `ComprasTipoSeedTests.LosTiposDeCompraAterrizanEnUnaBaseYaMigradaDesdeStage7…` (`:76-142`) verbatim as the harness |
| Integration — backfill, **RLS proven for real** | ⚠ The test above alone is a **false green**: `WaysApiFixture` migrates as `ways_owner`, which is the container **superuser**, so RLS never applies and the backfill would pass without `SET LOCAL`. A second test must run the backfill body over `fixture.AppConnectionString` (`ways_app`, `NOSUPERUSER NOBYPASSRLS`, `:108`): (1) statement **without** the `SET LOCAL` prefix ⇒ **0 rows affected**, the trap made visible; (2) the shipped statement **with** it ⇒ rows of **every** tenant affected; (3) re-run ⇒ 0 rows, idempotency | Raw `NpgsqlConnection`, `ExecuteNonQueryAsync` return value asserted — not an exception, per `db-error-backstops` ("RLS-blocked UPDATE yields 0 rows, NOT an exception") |
| Integration — CHECK backstops | One raw-SQL insert per CHECK asserting `SqlState` + `ConstraintName`; the two translated domain codes | `VentasStockBackstopTests` / `ManejadorDeErroresVentasTests` pattern |
| Integration — no leakage | Reflection over `ItemEmitido`/`ComprobanteEmitido` asserting **no** member whose name contains `costo`, **plus** a raw-JSON assertion on the `POST /api/ventas` response body | New arm in `CostoCongeladoTests.cs` |
| Web (vitest) | **No change.** No file under `src/Ways.Web` is touched, so `web-descriptor-tests` has no new surface to cover | — |

The backfill SQL literal is duplicated between the migration and its test **on purpose** (the
`ComprasTipoSeedTests` precedent, `:164-169`): a migration is a frozen snapshot and must not depend
on a shared constant that a later edit could silently re-point. The two copies are pinned to each
other by a doc-comment on both sides.

## What Does NOT Change (asserted, not assumed)

- **No API contract change.** `ItemEmitido` / `ComprobanteEmitido` (`Ways.Application/Ventas/Contratos.cs:41-62`)
  are byte-unchanged; no endpoint, policy, request DTO or route is added or edited.
- **No web change.** `src/Ways.Web` is not in the diff at all.
- **The cost is never serialized on any sale surface** — not on emission, not on reprint, not in a
  list. The only readers of the two columns in this stage are the migration and the tests.
- **No change to the sale transaction's shape**: same statement order, same lock order, same
  `EstrategiaSinReintento`/retry contract, same numbering, same stock, same cuenta corriente, same
  anulación (which writes no items, so a frozen cost is never rewritten).
- **No change to how compras writes `costo_nominal`**, and no revert of it on anulación
  (stage-8 decision 10c stands).
- **No new RLS policy, no new index, no new FK, no new enum**, and no edit to any existing column.

## Open Questions

- [ ] **The backfill uses today's cost.** Under inflation that is typically *higher* than the cost
      at sale time, so an estimated margin is a pessimistic lower bound. Pinned in the spec; stage 10
      must exclude estimated lines by default.
- [ ] **`ways_owner` being a superuser in the test container** makes the migration-level backfill
      test structurally weaker than production, where ADR-5 says the application role owns the
      tables without `BYPASSRLS`. The `ways_app` statement-level test is the compensation, not an
      equivalent — a future fixture that migrates as a non-superuser owner would close the gap for
      every migration, not just this one.
- [ ] **An estimated row is never re-estimated.** The deferred temporal reconstruction over
      `items_comprobante_compra` can re-run over exactly the rows marked `costo_es_estimado = true`,
      but nothing in this stage records *when* or *from what* the estimate was taken.
