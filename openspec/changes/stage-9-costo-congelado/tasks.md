# Tasks: Stage 9 — Costo congelado en la línea de venta

## Orchestrator Decisions Recorded This Phase

1. **Single slice, single PR** — matches the proposal's own estimate
   (~250-400 changed lines) and the design's "no new service, no new query,
   no new lock" posture. No chaining candidate exists: the change is one
   column pair, one capture point, one migration.
2. **DB CHANGE GATE is already approved** (`state.yaml`, 2026-08-11,
   explicit owner `ok`). No STOP task is emitted. The migration task instead
   carries a hard constraint: the DDL and the backfill statement must match
   the approved model in `state.yaml` / proposal.md *Modelo de datos
   propuesto* **exactly** — any deviation (a renamed CHECK, a reordered
   column, a changed `WHERE` clause) reopens the gate and requires the
   owner's re-approval before `sdd-apply` continues.
3. **judgment-day applies once**, on the whole slice diff, before the PR —
   per `protocolo-pr-solo-dev`. No dedicated extra round: unlike stage-8
   Slice 2, this change touches no new state machine.

## Review Workload Forecast

| Field | Value |
|-------|-------|
| Estimated changed lines | ~250–400 (migration + config + 2 entity props + ~5 lines in `ServicioDeVentas` + 2 error-mapping arms + spec/doc note + tests) |
| 400-line budget risk | Low |
| Chained PRs recommended | No |
| Suggested split | Single PR |
| Delivery strategy | ask-on-risk (default; no risk trigger fired) |
| Chain strategy | stacked-to-main |

Decision needed before apply: No
Chained PRs recommended: No
Chain strategy: stacked-to-main
400-line budget risk: Low

### Suggested Work Units

| Unit | Goal | Likely PR | Est. lines | Notes |
|------|------|-----------|-----------|-------|
| 1 | Entity + config + migration (gate-approved model) + `ServicioDeVentas` capture + error mapping + doc-10 note + full test coverage | PR 1 | ~250–400 | Base: `main`. Branch `feat/stage9-slice1-costo-congelado`. Single PR, no chaining. |

---

## Slice 1: Costo Congelado (PR 1)

**Start**: `main`. **Finish**: `CostoCongeladoEnVentaEtapa9` migration live
(two additive columns, two CHECKs, platform-mode backfill), `ServicioDeVentas`
freezes `costo_unitario` on every TX/NCX line with zero new queries, both
CHECKs mapped to 400s, doc-10 §4 updated, full suite green. **Rollback**:
down-migration drops both CHECKs and both columns; every other object is
untouched (proposal Rollback Plan step 3).

- [ ] 1.1 Modify `src/Ways.Domain/Ventas/ItemComprobanteVenta.cs`: add
  `CostoUnitario` (`decimal?`) and `CostoEsEstimado` (`bool`) properties
  after `Total`; extend the class doc-comment's frozen-snapshot list
  (`:14-19`) with both; give each property its own `<summary>` stating
  unsigned-per-unit, IVA-included, `NULL` = unknown, `0` = stated zero cost.
  *(design: File Changes; spec: Snapshot Immutability of Items)*
- [ ] 1.2 Modify `src/Ways.Infrastructure/.../ItemComprobanteVentaConfiguration.cs`:
  add `Property(x => x.CostoUnitario).HasColumnType("numeric(14,2)")`
  (nullable); add `Property(x => x.CostoEsEstimado).IsRequired()
  .HasDefaultValue(false)`; add two `HasCheckConstraint` calls —
  `ck_items_comprobante_venta_costo_no_negativo`
  (`costo_unitario IS NULL OR costo_unitario >= 0`) and
  `ck_items_comprobante_venta_estimado_con_costo`
  (`NOT costo_es_estimado OR costo_unitario IS NOT NULL`).
  *(design decision 6; proposal: Modelo de datos propuesto)*
- [ ] 1.3 Create migration `CostoCongeladoEnVentaEtapa9`: **must reproduce
  the gate-approved model exactly — any deviation reopens the DB CHANGE
  GATE.** Order: (a) `AddColumn<decimal>("costo_unitario", …, nullable:
  true)`; (b) `AddColumn<bool>("costo_es_estimado", …, nullable: false,
  defaultValue: false)`; (c) both `AddCheckConstraint`s from 1.2, **before**
  the backfill so they validate it; (d) one `Sql()` block —
  `SET LOCAL app.acceso = 'plataforma';` immediately followed by the
  proposal's `UPDATE items_comprobante_venta i SET costo_unitario =
  a.costo_nominal, costo_es_estimado = true FROM articulos a WHERE
  a.id_articulo = i.id_articulo AND a.id_tenant = i.id_tenant AND
  i.id_articulo IS NOT NULL AND a.costo_nominal IS NOT NULL AND
  i.costo_unitario IS NULL;`, in the **same** `Sql()` call, no
  `suppressTransaction: true` (design finding 1 — `WaysDbContextFactory` has
  no tenant interceptor on the deploy path, so `SET LOCAL` outside this
  block never applies). `Down`: drop both CHECKs, then both columns.
  *(design: Migration Shape; state.yaml gate)*
- [ ] 1.4 Update `docs/10-modelo-de-datos.md` §4: schema note for
  `costo_unitario`/`costo_es_estimado` on `items_comprobante_venta`, same
  trailing-blockquote convention as stages 5–8.
  *(proposal: Affected Areas)*
- [ ] 1.5 Modify `src/Ways.Application/Ventas/ServicioDeVentas.cs`:
  `LineaDelPlan` record (`:971-974`) gains `decimal? CostoUnitario`.
  *(design decision 1)*
- [ ] 1.6 Same file, `MaterializarItems` (`:786-806`): assign
  `CostoUnitario = articulo.CostoNominal` in the same statement that already
  copies `articulo.Nombre`/`IdArea`/`IdAlicuotaIva` (`:801-805`) — **before**
  the retryable lambda, so a retry never re-reads a `costo_nominal` moved by
  a concurrent compra confirm. No new query: `articuloPorId` (`:96-98`)
  already materializes `CostoNominal`. *(design decision 1; Data Flow)*
- [ ] 1.7 Same file, `EjecutarTransaccionAsync` step 3 (`:600-620`): set
  `ItemComprobanteVenta.CostoUnitario` from `LineaDelPlan.CostoUnitario`.
  Do **not** assign `CostoEsEstimado` explicitly — it stays at its EF
  `HasDefaultValue(false)`. *(design decision 6)*
- [ ] 1.8 Modify `src/Ways.Api/Seguridad/ManejadorDeErrores.cs`: add two
  arms to `ClasificarCheckDeVentas` (`:531-555`), exact constraint name —
  `ck_items_comprobante_venta_costo_no_negativo` → 400
  `costo_de_item_invalido`; `ck_items_comprobante_venta_estimado_con_costo`
  → 400 `costo_estimado_sin_costo`. *(design decision 5; Backstop Map)*
- [ ] 1.9 [P] Integration snapshot tests, new
  `tests/Ways.IntegrationTests/CostoCongeladoTests.cs`: emission with
  `costo_nominal = 121.00` ⇒ line `(121.00, false)`; `costo_nominal = NULL`
  ⇒ `(NULL, false)`, never `0`; `costo_nominal = 0` ⇒ `(0, false)`,
  distinguishable from the NULL case; reprint via `GET /api/ventas/{id}`
  shows the frozen value unchanged after the live cost moves.
  *(spec: Snapshot Immutability of Items — both new scenarios)*
- [ ] 1.10 [P] Integration NCX-sign test, same file: an NCX freezes its own
  current cost (not the original TX's), and `costo_unitario × cantidad`
  comes out negative because `cantidad` is negative on the NCX; a cost that
  moved between the TX and the NCX is asserted as the accepted residual, not
  a bug. *(spec: Cost Snapshot Semantics, NCX Freeze, And No-Exposure)*
- [ ] 1.11 Integration query-budget regression:
  `VentasCheckoutTests.ElCheckoutEmiteUnaCantidadConstanteDeConsultasIndependienteDeLaCantidadDeLineas`
  passes at `Assert.Equal(17, …)` (`:918`) **with the line unedited** — any
  diff touching it is a design violation, not a test update.
  *(design decision 2)*
- [ ] 1.12 [P] Integration backfill test, multi-tenant (the
  `ComprasTipoSeedTests` harness precedent): fresh database → migrate to
  `ComprasYTransferenciasEtapa8` → seed two tenants, each with a comprobante
  + item + a priced articulo, plus one line with `id_articulo NULL` and one
  whose articulo has no cost → `MigrateAsync()` → assert both tenants' rows
  are `(costo_nominal, true)` and the two gap rows stay `(NULL, false)`.
  *(design: Testing Strategy; spec: One-Shot Backfill — multi-tenant scenario)*
- [ ] 1.13 Integration backfill test, **statement-level over `ways_app`**
  (design finding 2 — 1.12 alone is a false green: `WaysApiFixture` migrates
  as `ways_owner`, the container superuser, so RLS never applies there).
  Raw `NpgsqlConnection` over `fixture.AppConnectionString` (`ways_app`,
  `NOSUPERUSER NOBYPASSRLS`): (a) run the `UPDATE` **without** the
  `SET LOCAL` prefix ⇒ assert `ExecuteNonQueryAsync` returns `0`; (b) run
  the shipped statement **with** the prefix ⇒ assert rows of every tenant
  affected; (c) re-run ⇒ assert `0` (idempotent). No exception expected at
  any step — an RLS-blocked `UPDATE` yields `0` rows, not a throw.
  *(design decision 4; Testing Strategy — "false green"; spec: One-Shot
  Backfill — idempotent re-run scenario)*
- [ ] 1.14 [P] Integration CHECK backstops: one raw-SQL insert per
  constraint asserting `SqlState == "23514"` and `ConstraintName`, plus a
  `ManejadorDeErroresVentasTests` arm per constraint asserting the
  translated domain code from 1.8. *(design: Backstop Map)*
- [ ] 1.15 [P] Integration no-leakage proof: reflection over
  `ItemEmitido`/`ComprobanteEmitido` asserting no member name contains
  `costo`; a raw-JSON assertion on the `POST /api/ventas` response body
  confirming no `costo` key at any level. *(spec: Cost Snapshot Semantics —
  "the emit response never carries cost"; proposal decision 5)*
- [ ] 1.16 Regression: full Domain/Application/Integration/vitest suite
  green — no existing assertion altered other than the two doc/spec files
  from 1.4 and the pre-existing spec delta; `src/Ways.Web` untouched.
  *(proposal: Success Criteria)*
- [ ] 1.17 Run `judgment-day` (two independent blind review agents) on the
  slice diff; fix confirmed issues and re-judge until a clean round.
  *(protocolo-pr-solo-dev)*
- [ ] 1.18 Branch `feat/stage9-slice1-costo-congelado` off `main`; open the
  PR per `branch-pr` convention; merge stacked-to-main (single PR — no
  parent PR to stack on for this slice).

**Verify**: `dotnet test --filter FullyQualifiedName~CostoCongelado|FullyQualifiedName~VentasCheckoutTests`

---

## Dependency Summary

```
Slice 1 (only slice — entity/config → migration (gate-approved model,
         platform-mode backfill) → ServicioDeVentas capture → error
         mapping → doc-10 note → full test coverage → judgment-day → PR)
```

All tasks within the slice are sequential except the `[P]`-tagged test
tasks (1.9, 1.10, 1.12, 1.14, 1.15), which are independent of each other
once 1.1–1.8 land. No stage-10 work starts from this tasks file.
