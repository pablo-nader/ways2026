## Verification Report

**Change**: stage-9-costo-congelado
**Version**: N/A (single delta, no versioned spec)
**Mode**: Standard (no Strict TDD signal found for this change)
**Verified against**: `main` @ `eff30a2` (PR #75 merged; slice range `3894173..eff30a2`)

### Completeness

| Metric | Value |
|--------|-------|
| Tasks total | 18 |
| Tasks complete (checked `[x]`) | 16 (1.1-1.16) |
| Tasks incomplete (checked `[ ]`) | 2 (1.17 judgment-day, 1.18 branch/PR) |

Tasks 1.17 and 1.18 are unchecked in `tasks.md` but the work they describe is
independently proven done: `state.yaml`'s apply-phase notes record a clean
judgment-day round (2/2 APPROVE, one MINOR doc fix applied, one MINOR
informational with no action) and PR #75 is merged into `main` at `eff30a2`
(`git log` confirms the merge commit and the slice commits
5b037c8..3d80367). This is a documentation/state drift, not a missing
deliverable — see WARNING-1.

### Build & Tests Execution

**Build**: Passed (implicit — `dotnet test` restored/built all three suites with no errors)

**Tests**: 1294 passed / 0 failed / 0 skipped, across three suites, run directly against `main`

```text
dotnet test tests/Ways.Domain.Tests --no-restore
  Correctas! - Con error: 0, Superado: 378, Omitido: 0, Total: 378 — Ways.Domain.Tests.dll

dotnet test tests/Ways.Application.Tests --no-restore
  Correctas! - Con error: 0, Superado: 212, Omitido: 0, Total: 212 — Ways.Application.Tests.dll

dotnet test tests/Ways.IntegrationTests --no-restore   (Docker-backed, ~4m20s)
  Correctas! - Con error: 0, Superado: 704, Omitido: 0, Total: 704 — Ways.IntegrationTests.dll
```

All three counts match the stated baselines exactly: Domain 378/378 (proposal
and design both state "nothing new" for Domain — confirmed, no drift),
Application 212/212 (same — confirmed), Integration 704/704 (baseline 691 +
13 new tests in `CostoCongeladoTests.cs`/backstop files = 704, matches
`state.yaml`'s apply-phase notes exactly).

`src/Ways.Web` has zero changes in the slice diff (`git diff --stat
3894173 eff30a2` shows no `Ways.Web` path), so `vitest` was not re-run — this
matches the design's Testing Strategy row ("No change. No file under
`src/Ways.Web` is touched").

**Coverage**: Not measured (no coverage tool wired into this repo's test commands; not available)

### Spec Compliance Matrix

Only the scenarios stage-9 adds or modifies are new evidence obligations;
pre-existing scenarios in the same requirements (turno/numeración/reprint/no-edit)
are unaffected by this stage's diff and are covered by pre-existing tests.

| Requirement | Scenario | Test | Result |
|-------------|----------|------|--------|
| Comprobante Schema At Rest | A negative `costo_unitario` is unrepresentable | `VentasStockBackstopTests.UnItemConCostoUnitarioNegativoViolaLaCheckDeCostoNoNegativo` | ✅ COMPLIANT |
| Comprobante Schema At Rest | An estimated row with no cost is unrepresentable | `VentasStockBackstopTests.UnItemMarcadoEstimadoSinCostoViolaLaCheckDeEstimadoConCosto` | ✅ COMPLIANT |
| Snapshot Immutability of Items | Emission freezes the live `costo_nominal` onto the line | `CostoCongeladoTests.EmisionCongelaElCostoNominalVigenteEnLaLinea` | ✅ COMPLIANT |
| Snapshot Immutability of Items | An articulo with no cost produces an honest gap, never zero | `CostoCongeladoTests.UnArticuloSinCostoNominalProduceUnaLineaConCostoNuloNuncaCero` + `UnArticuloConCostoNominalCeroPersisteCeroDistinguibleDeNulo` | ✅ COMPLIANT |
| Cost Snapshot Semantics, NCX Freeze, And No-Exposure | NCX freezes its own current cost, sign reverses on its own | `CostoCongeladoTests.UnaNcxCongelaSuPropioCostoDeEmisionYElProductoConCantidadDaNegativo` | ✅ COMPLIANT |
| Cost Snapshot Semantics, NCX Freeze, And No-Exposure | The emit response never carries cost | `CostoCongeladoTests.ItemEmitidoYComprobanteEmitidoNoTienenNingunMiembroDeCosto` + `ElCuerpoJsonDeLaRespuestaDeCheckoutNoContieneNingunaClaveDeCosto` | ✅ COMPLIANT |
| One-Shot Backfill Marks Pre-Existing Rows As Estimated | Platform mode reaches every tenant's rows | `CostoCongeladoTests.LosCatalogosPreexistentesDeDosTenantsGananCostoEstimadoTrasElBackfillYLosGapsQuedanIntactos` (naive, `ways_owner`) + `ElBackfillSoloAlcanzaFilasEnModoPlataformaYEsIdempotente` (honest, `ways_app` statement-level, proves the RLS trap by first asserting 0 rows without `SET LOCAL`) | ✅ COMPLIANT |
| One-Shot Backfill Marks Pre-Existing Rows As Estimated | Re-running the backfill is a no-op | `CostoCongeladoTests.ElBackfillSoloAlcanzaFilasEnModoPlataformaYEsIdempotente` step (c) | ✅ COMPLIANT |

**Compliance summary**: 8/8 new-scenario obligations compliant (100%). No UNTESTED, FAILING, or PARTIAL scenarios found.

### Correctness (Static Evidence)

| Requirement | Status | Notes |
|------------|--------|-------|
| Migration matches the DB CHANGE GATE model exactly | ✅ Implemented | `20260811033540_CostoCongeladoEnVentaEtapa9.cs` — column names/types/nullability, both CHECK names and predicates, backfill `WHERE`/`FROM` clause, and `Down` are identical to `proposal.md`'s "Modelo de datos propuesto" and `state.yaml`'s gate text. Zero deviations found. |
| Zero new queries in the sale transaction | ✅ Implemented | `CostoUnitario` is threaded through the already-materialized `articuloPorId` dictionary (`ServicioDeVentas.cs:96-98` → `:806`); `VentasCheckoutTests.cs:918` (`Assert.Equal(17, …)`) is byte-unchanged in the slice diff. |
| Capture happens in the non-retryable half | ✅ Implemented | `MaterializarItems` runs at `ServicioDeVentas.cs:105`, before `plan` is built (`:145`) and before `estrategia.ExecuteAsync` opens the retryable lambda (`:178-180`) — confirmed by direct read of the method body. |
| NCX needs zero dedicated code | ✅ Implemented | `MaterializarItems` copies `articulo.CostoNominal` unsigned into every `LineaDelPlan` regardless of `signoTipoComprobante`; the sign is applied only to `Cantidad` (`:774`), so no NCX-specific branch exists — matches design decision/finding 3. |
| Both CHECKs get exact-name mapping | ✅ Implemented | `ManejadorDeErrores.cs:554-562`, two arms in `ClasificarCheckDeVentas`, exact string match (no `Contains`/prefix ambiguity), consistent with the rest of the switch. |
| No-exposure (decision 5) | ✅ Implemented | `git diff --stat` across the whole slice range shows zero changes to `src/Ways.Application/Ventas/Contratos.cs` and zero changes anywhere under `src/Ways.Web` — confirmed at the file level, not just by test. |
| doc-10 §4 note | ✅ Implemented | Accurate: states both columns, both CHECK names/predicates, the platform-mode backfill rationale, idempotency, and the no-exposure rule; uses the same trailing-blockquote convention as the stage 1/5/6/7/8 notes already in the file. |

### Coherence (Design)

| Decision | Followed? | Notes |
|----------|-----------|-------|
| 1 — Capture inside `MaterializarItems`, carried by `LineaDelPlan`, written in `EjecutarTransaccionAsync` step 3 | ✅ Yes | Verified by direct code read (see Correctness table). |
| 2 — Query-budget constant stays `17`, line 918 untouched | ✅ Yes | Confirmed via `git diff` on `VentasCheckoutTests.cs` across the full slice range — empty diff. |
| 3 — NCX needs zero dedicated code | ✅ Yes | Confirmed by code read; also proven behaviorally by the NCX sign test. |
| 4 — `SET LOCAL` lives inside the same `Sql()` block as the `UPDATE`, no `suppressTransaction` | ✅ Yes | Migration `Up()` shows exactly one `Sql()` call containing both statements; no `suppressTransaction: true` argument used anywhere in the file. |
| 5 — Both CHECKs mapped even though unreachable from a verified write path | ✅ Yes | Both arms present; backstop tests directly exercise the CHECKs via raw SQL insert, independent of any app-layer guard. |
| 6 — `costo_es_estimado` mapped with `HasDefaultValue(false)`, mirroring `Descuento` | ✅ Yes | `ItemComprobanteVentaConfiguration.cs` — `Property(i => i.CostoEsEstimado).HasColumnName("costo_es_estimado").HasDefaultValue(false).IsRequired();` and the emission path (`EjecutarTransaccionAsync`) never assigns `CostoEsEstimado`, letting the CLR/DB default apply. |

### Issues Found

**CRITICAL**: None.

**WARNING**:
1. `tasks.md` leaves boxes 1.17 (judgment-day) and 1.18 (branch/PR) unchecked
   even though both are independently confirmed complete: `state.yaml`'s
   apply-phase notes record a clean judgment-day round (2/2 APPROVE, first
   pass) and PR #75 is merged to `main` at `eff30a2`. The tasks artifact was
   not updated to reflect the orchestrator-executed steps — documentation
   drift with no functional impact, but it means `tasks.md` alone
   understates completion. Recommend checking both boxes (or adding a note
   referencing the PR/judgment-day evidence) before or during archive.

**SUGGESTION**: None — no additional style, coverage, or documentation gaps
found beyond the WARNING above. The implementation stays unusually tight to
its own design/proposal: every binding decision (1 through 6, plus the six
proposal decisions) was checked against source and found followed with no
deviation.

### Verdict
PASS WITH WARNINGS
Full spec/design/correctness compliance with all three .NET suites green at
their exact expected counts (378/212/704) and zero migration deviation from
the owner-approved gate model; the single WARNING is a cosmetic tasks.md
checkbox drift on two orchestrator-executed steps that are otherwise
independently proven complete.
