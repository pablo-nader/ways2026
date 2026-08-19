---
name: mutation-proof-tests
description: "Trigger: writing a test whose PURPOSE is to prove one specific clause — an RLS/tenant predicate, a WHERE filter, a timezone/bucketing expression, a validation guard, a fallback branch. The test ships only with recorded mutation evidence: mutate the clause, watch the test fail, revert."
license: Apache-2.0
metadata:
  author: ways-project
  version: "1.0"
---

## Activation Contract

Load when a test exists to prove that a SPECIFIC line or clause does its job — not
general behavior coverage. Typical markers: a tenant/RLS predicate (`id_tenant = $n`),
a soft-delete or estado filter, a timezone/`date_trunc` expression, a platform-mode
`SET LOCAL`, a hardening guard (null check, `HasIanaId`), an error-classifier arm.

Born from three same-class incidents in one program:
1. Stage 9: the naive multi-tenant backfill test passed with `SET LOCAL app.acceso`
   deleted (fixture migrates as the testcontainer superuser — RLS never applied).
2. Stage 10 slice 2: the cross-tenant test passed with `AND cv.id_tenant = $2`
   deleted (real RLS + an empresa-scoped PV list masked the predicate).
3. Stage 10 slice 2: the timezone test passed with `timezone($1, ...)` deleted
   (single-day range → the C# WHERE boundary alone determined the result).

The defect class: **the asserted outcome is overdetermined** — other layers
(RLS, scoping, C# pre-filters, closed UI paths) produce the same observable result,
so deleting the clause under test changes nothing the test can see.

## Hard Rules

1. **Name the clause.** Before writing the test, state (in the test name or a one-line
   comment) WHICH exact clause it exists to prove. If you cannot name one, this skill
   does not apply — it is ordinary behavior coverage.

2. **Run the mutation, don't reason it.** Temporarily delete/neuter the named clause,
   run the test, confirm it FAILS, revert, confirm it passes. "It would obviously fail"
   has been wrong three times in this repo. Record the evidence in the PR body or the
   review notes (mutation applied → exact failing test → reverted → green).

3. **Kill the confounds, not the layers.** If the test passes under mutation, do not
   weaken the other layers (never disable RLS, never bypass scoping) — instead route
   the test BELOW the confound: call the component directly with hand-built inputs the
   upper layers would never produce (e.g. a PV list containing another tenant's id),
   or widen the data so the clause alone decides the outcome (multi-day range so the
   bucket LABEL, not row admission, carries the assertion). The most common confound
   of this class is a PRE-CHECK that mirrors a transactional guard: the sequential
   test path always dies at the pre-check, so every WHERE conjunct of the guarded
   UPDATE survives deletion (stage 17 slice 3: all three conjuncts of the conversion
   guard survived 17/17 — the guard exists for the RACES the pre-check cannot see).
   A guard mirrored by an earlier check needs BOTH a direct below-the-confound test
   (0 rows on each conjunct) AND its real TOCTOU race test (pre-check reads stale →
   the other transaction commits → the guard alone must refuse).

4. **Assert the discriminating value.** Prefer asserting the value only the clause can
   produce (the bucket label, the affected-row count, the SQLSTATE) over asserting
   absence/presence that a confound can also explain.

5. **Superuser fixtures lie.** Any test that "proves" an RLS-dependent behavior while
   running on `ways_owner` (testcontainer superuser) proves nothing — use the
   `ways_app` connection (NOSUPERUSER NOBYPASSRLS) at statement level, per the
   stage-9 precedent.

6. **An equality test asserts EVERY column of EVERY row — or it is not an equality
   test.** Five occurrences in one stage (stage 11: bucket rows unasserted, coverage
   counts as digit substrings, date columns skipped ×2, per-ticket totals rotatable
   with count+sum preserved, a whole section droppable) prove the pattern: partial
   equality tests pass while the file lies. When a test's purpose is "export equals
   endpoint" or "serialized equals source": loop over ALL rows, assert ALL cells,
   with per-row discriminating values (two rows with DIFFERENT values per column, so
   swaps and rotations are detectable — identical fixture values hide misassignment).
   Aggregate assertions (count, sum) NEVER substitute for per-row assertions.

7. **Async assertions have their own confound: a retrying matcher can pass on its
   first tick, BEFORE the stale microtask lands.** A `waitFor` asserting "old value
   still there, new value absent" exits green immediately if the mutation's effect
   hasn't flushed yet — proving nothing. Resolve the stale promise INSIDE `act`
   (awaiting it) and assert synchronously after the flush. (Occurrence: CajaZ stale
   test, stage 11 — survived its own strengthening until the flush was forced.)

8. **A workbook equality test also asserts the HEADER row — the header is what
   binds a cell to its column.** Reading data cells by position from
   `primeraFilaDeDatos` leaves the header texts unguarded: swapping two column
   titles ships a workbook whose `Mínimo` column is labeled `Reposición` — a lying
   file an operator orders stock from — while every data-cell equality stays green.
   Assert ALL header texts of the export's column set, in exact order, read from the
   real header row (`ExportadorXlsx.FilaDeTituloDeTabla`), once per export equality
   test. (Two same-day occurrences, stage 13 slices 2 and 4 — both caught as
   surviving mutants by judgment-day, both fixed with a six/seven-header assert that
   also kills the adjacent hard-coded-label mutant. Pre-existing exports share the
   gap; close it whenever an export equality test is touched.)

9. **The three shared gaps of every `/export` route test, and the settled question
   about `IsEmpty()`.** An export route needs all three, per route, or a mutant lives:
   (a) the tope-rejection test seeds `tope + 2` and asserts the REAL count — on a
   LISTADO route (two `Exigir`, `.Take(tope + 1)`) seeding `tope + 1` makes the count
   and the truncated read agree, so deleting the first `Exigir` survives; AGREGADO
   routes have one `Exigir` over a materialized `Filas.Count` and need no change;
   (b) a `formato=pdf` test PER ROUTE — one test does not cover its siblings;
   (c) an exactly-tope success test asserting 200 and a complete workbook, or
   `Exigir(count, tope - 1)` survives. Assert the count as `"tiene N filas"`, never a
   bare `"N"` — the title also carries the tope, so a lone digit can match the wrong
   number. `hoja.Row(n).IsEmpty()` IS load-bearing as the end-of-data assertion:
   `ExportadorXlsx.AplicarFormatoDeColumna` styles whole columns, but a styled-yet-
   unwritten row still reads empty while a row holding data reads non-empty (proven
   by forcing a fourth data row into a three-row expectation and watching the assert
   fail). Both judges raised this independently — it is settled, do not re-litigate.
   (Stage 14 slice 6 found the three gaps; swept across all 18 routes and 11 files.)

10. **A date-boundary test sends the offset the CLIENT sends, never `Z`.** A
   `desde`/`hasta` built as `...T00:00:00Z`/`...T23:59:59Z` is a confound: with
   offset zero, `limite.DateTime` and `limite.UtcDateTime` are the same value, so
   every timezone defect in the request path is invisible. The web builds both
   limits with the browser's own offset (`fechaIsoConOffset`, duplicated in
   `compras.ts`/`cuentaCorriente.ts`/`reportes.ts`/`auditoria.ts`), so a test that
   sends `Z` is not testing the production payload. Use a real negative offset
   (`-03:00`, the default zone) so the end-of-day límite lands on the NEXT UTC day,
   and assert the displayed date — the `Período` header and both ends of
   `NombreDeArchivo` — not only the rows returned. (Two occurrences of this class:
   the `hoy` UTC filename bug of stage 11, and the `desde`/`hasta` display bug plus
   the Npgsql `only offset 0 (UTC) is supported` 500 found in stage 14 slice 6 —
   both survived because every export test in the repo sent offset zero. Residual:
   only `AuditoriaExportTests` sends a real offset; the other five export test files
   still send `Z`. Close each one whenever it is touched.)

11. **A ledger assert needs prior state that discriminates — a fresh seed proves
   nothing about provenance.** With a fresh entity (saldo 0) and ONE operation,
   `saldo_resultante == importe == total` by arithmetic coincidence, so a mutant that
   sources the stored snapshot from ANY in-scope value (the request total, a local
   recomputation) passes green. Every assert over `saldo_resultante`, a cached
   `saldo`, or a derived estado seeds REAL prior debt: ≠ 0, ≠ the operation's own
   importe, ≠ their trivial sum — and reverse paths (anulación) assert the resulting
   snapshot too, not only the reversal's importe. (Stage 15 slice 2: judge B's
   CRITICAL — the only `SaldoResultante` assert lived in the fresh-provider test and
   the value-substitution mutant survived 9/9; killed with prior debt 800+1500⇒2300.)

12. **A read layer has its own three mutant classes — write-side coverage kills none
   of them.** Proven the hard way (stage 15 slice 4: three CRITICALs in one round,
   all surviving 100% of the slice's tests):
   (a) **Source-of-truth**: a field documented as "read from the cache/stored column,
   never re-derived" needs a test that DELIBERATELY DESYNCS cache from derivation
   (raw `UPDATE` to a sentinel) and asserts the endpoint returns the sentinel —
   in-sync fixtures make both sources indistinguishable by construction.
   (b) **Projection**: EVERY projected money/date field of a returned item gets
   asserted with per-row discriminating values — a `SaldoResultante = 0m` hardcode
   survived 8/8 tests because no test read the field back. Third occurrence
   (stage 16 slice 5) sharpened the rule: EVERY positional field of a response
   record gets read back at least once with values pairwise-distinct across
   fields — a 17-parameter positional constructor with two adjacent ints is one
   swap away from shipping proveedor and punto de venta exchanged with 197/197
   green; rich fixtures on the "interesting" derived fields do not cover the
   identity/aggregate fields (Pendiente asserted only where it equals 0, totals
   never read back). One integral "the detail returns every field with its
   truth" test per response DTO closes the whole class.
   (c) **Identity predicate**: seeding ONE entity per tenant makes `Where(Id == x)`
   undeletable-undetectable; every listing/estado test seeds a SECOND sibling of the
   same tenant with its own rows and asserts exact counts + row identity, so
   cross-entity leaks fail loudly. This applies to WRITE paths too, not just reads:
   a replace-set `DELETE`/`RemoveRange` whose scope predicate is widened to the whole
   table survives every single-entity fixture (stage 16 slice 2: the unscoped item
   delete passed 11/11 — second occurrence of the class after stage 15 slice 4's
   read-side `Where(IdProveedor)`). Every scoped destructive write gets a sibling
   seed whose rows must remain intact, asserted by exact count and identity.

## Decision Gate

| Situation | Action |
|---|---|
| Test exists to prove a predicate/filter/guard/expression | Mutation evidence recorded before commit |
| Test passes with the clause deleted | Re-route below the confound (rule 3) — never call it done |
| RLS-related assertion | ways_app connection, statement-level, row counts |
| Test sends a date boundary as `...Z` | Confound (rule 10) — resend with a real negative offset |
| Cannot name the clause under test | Ordinary coverage — this skill does not apply |
