# Apply Progress: Stage 20 — Organization Relationships and Usage-Guarded Logical Deletion

This document did not exist during apply — each slice recorded its progress directly into
`state.yaml`'s `phases.apply.notes` instead of a separate file. This is a summary compiled at
archive time from that record, for readers who want the five-slice shape without the full
`state.yaml` narrative. `state.yaml` remains the primary source for verbatim detail; this file
does not supersede it.

## Summary table

| Slice | Branch | PR | Merge commit | Scope |
|---|---|---|---|---|
| 1 | `feat/stage20-slice1-proyeccion-api` | #165 | `5f0018f` | Backend projection API — owner names and tenant identity added to the four listing DTOs |
| 2 | `feat/stage20-slice2-web-relaciones` (planning name `feat/stage20-slice2-proyeccion-web`) | #167 | `d616362` | Web — names, filters and the tenant column on the four root screens |
| 3 | `feat/stage20-slice3-inspector-de-uso` | #169 | `c3f62d7` | `InspectorDeUso` / `InventarioDeDependientes` — the usage guard, shipped INERT (zero production callers) |
| 4 | `feat/stage20-slice4-bajas-api` | #171 | `99b5abe` | Backend deletion API — three DELETE routes, structural minimums, cascade, usuario guard wiring |
| 5 | `feat/stage20-slice5-bajas-web` | #173 | `858e958` | Web — delete buttons, the `ConfirmacionDeBaja` modal gate, 409 copy mapping |

All five merge commits are present on `main` in the planned order 1→2→3→4→5 (re-confirmed
independently by `sdd-verify` against `git log`).

## Slice 1 — Projection API

Branch `feat/stage20-slice1-proyeccion-api` off `main`. Round 1 measured 1285 changed lines (1252
insertions + 33 deletions) against the 800-line budget; after judgment-day round 2 (final), 1420
changed lines (1386 + 34), of which production is 217 across four files and tests are 1203
(`ProyeccionDeOrganizacionTests.cs`, 1194 lines / 12 `[Fact]`). The orchestrator accepted the
single-PR overflow (OD1 of this slice): production is only ~176 lines and the ~1085 test lines are
the evidence round 1's review demanded for exactly those 176.

Six mutations (M1–M6) run for real and killed. Judgment-day round 1 corrected six confirmed
findings (three with new mutation evidence M7–M9): an `IgnoreQueryFilters` leak into a correlated
subquery, three untested non-listing response paths, a `FirstAsync`→`FirstOrDefaultAsync`
correction on three re-projections, Reconciliación 8 recorded for V9/usuarios pagination, a
pairwise-distinctness fixture repair, and a false DTO doc-comment contract corrected.

Suites (final): `dotnet build` clean (2 pre-existing `NU1903` SSH.NET advisories),
`Ways.Domain.Tests` 545/545, `Ways.Application.Tests` 373/373, `Ways.IntegrationTests` 1732/1732
in 18 m 08 s, run once and alone against Docker.

## Slice 2 — Web projection + filters

Branch `feat/stage20-slice2-web-relaciones` off `main` (the launch prompt's branch name; `tasks.md`
still records the planning name `feat/stage20-slice2-proyeccion-web`). Measured 34 files, 1909
insertions + 289 deletions = 2198 changed lines (1862 ignoring pure re-indentation) against an
estimate of ~430 — reported, not absorbed; production ~1054, tests ~1144.

Thirteen mutations (M1–M13) run for real: twelve killed, one honest survivor (M10, closed by
react-async-state rule 9's generation guard already covering it). Task 2.17 was added mid-slice on
an owner-reported bug (Admin-role user creation missing the tenant selector) — five further
mutations (M14–M18) all killed, followed by a page-size gap closure (M19 killed).

Judgment-day round 1 fixed eight confirmed items (C1–C8/S1–S8); twelve mutations (M20–M30b) run,
ten killed, three honest survivors kept as defence-in-depth traps for slice 5. Judgment-day round 2
(FINAL) fixed ten confirmed items (R2-1–R2-10); seven mutations (M31–M37) run, all killed, three
honest survivors carried forward.

Suites (final): `npm run test` 63 files / 1021 tests green, `npm run build` clean, `npm run lint`
exit 0 (five pre-existing warnings). Zero backend files touched, zero schema.

## Slice 3 — InspectorDeUso (inert usage guard)

Branch `feat/stage20-slice3-inspector-de-uso` off `main`. Measured 8 files, 1658 insertions, 0
deletions: production 585 across four files, tests 1073. The guard shipped INERT and this was
verified, not assumed (`rg -n "PrimeraDependenciaEnUsoAsync" src/` returns exactly one line, its
own declaration).

Judgment-day round 1 fixed six confirmed items, the headline being C1 (CRITICAL, both judges): the
FK walk alone missed `puntos_venta` as a Tenant dependent because `PuntoVentaConfiguration` has no
`HasOne<Tenant>()`, fixed by unioning the FK walk with a tenant-scope-column walk (closing the
CLASS, not the instance). Seven mutations run, killed; two honest survivors (equivalent mutants of
a pre-existing class). Two design deviations declared explicitly: the `Marcado` predicate weakened
correctly (C3) and N1 downgraded from "the" completeness test to one of four nets after being named
a tautology.

Judgment-day round 2 (FINAL) fixed five confirmed items, the headline being R2-1 (CRITICAL, single
judge, orchestrator-verified): the Empresa anchor read pristine with a full operating history
because no operational table carries `id_empresa` directly — fixed by making usage propagate UP
the hierarchy via a new `puntos_venta` bridge (`AgregarRamasPuenteadasPorPuntoDeVenta`), the golden
gaining exactly 17 lines. Four mutations killed plus one re-run.

Suites (final): `dotnet build` clean, `Ways.Domain.Tests` 545/545, `Ways.Application.Tests`
427/427, `Ways.IntegrationTests` 1743/1743 in 12 m 37 s.

## Slice 4 — Deletion API

Branch `feat/stage20-slice4-bajas-api` off `main`. Measured 14 files, 2682 insertions, 219
deletions: production 681/-122 across six files, tests 2001/-97. The slice overflowed the 800-line
budget (~2150 authored lines against a ~530 forecast) and this was reported, not hidden — the
forecast predated three slice-1 judgment-day inputs that all landed here.

Sixteen mutations (U1–U8, N4, P1–P6) run for real and all killed, including the six SLICE-1
surviving mutants, closed by a different route than predicted (re-routing below the confound, not
through the deletion writers). Zero physical deletes asserted as code
(`BajasEstructuralesTests`), proven by mutation.

Judgment-day round 1 fixed seven confirmed items (C1–C7): missing audit trail for the three
organization bajas and the cascade (C1), the usuario guard running outside any transaction (C2),
the bridged-409 label degrading unnecessarily (C3), a fourth authorization-surface walker gap (C4),
name-based anchor resolution for the usuario guard (C5), a physical-delete scan widening (C6), and
a mislabeled table (C7). Six mutations killed, one recorded honestly as without an independent
kill (C5).

Judgment-day round 2 (FINAL) fixed ten confirmed items (R2-1–R2-10), the headline being R2-1
(CRITICAL, judge B): the four bajas now run under `FabricaDeEstrategiaSinReintento` instead of a
retryable strategy, because a retry would re-read an already-deleted row and answer 404 to a baja
that actually succeeded. Twelve mutations run, eleven killed, one recorded without an independent
kill (transaction atomicity is not expressible as a single-clause mutation).

Suites (final): `dotnet build` clean, `Ways.Domain.Tests` 545/545, `Ways.Application.Tests`
434/434, `Ways.IntegrationTests` 1780/1780 in 12 m 49 s, web 63 files / 1022 tests.

## Slice 5 — Deletion web (buttons + modal gate)

Branch `feat/stage20-slice5-bajas-web` off `main`, web only. Measured 13 files, 2035 insertions,
192 deletions: production 901 (722/-179), tests 1240, docs 86. The ~340-line estimate was overrun
by roughly 6×, reported as structural (test-per-branch replication across four screens, the four
slice-2 carry-forward items, and one real defect found by a test).

A real defect was found by a test, not reasoned about: the re-entrancy guard read from React
state, so two same-tick clicks both passed and issued two DELETEs; fixed with a synchronous ref
mirror (`ocupadoRef`) replicated to all four screens. Thirteen mutations (MS1–MS12 + MS7b) run: ten
killed, two honest survivors (later corrected in round 1), one discarded no-op mutant replaced by
one that kills.

Judgment-day round 1 fixed eight confirmed findings, the headline being C1 (CRITICAL, both judges):
a superseded confirmation gate still fired the DELETE and discarded both outcomes — fixed with one
derived `bloqueado` state driving every control plus a token minted at Confirm. Twenty mutations
run: eighteen killed, two honest survivors reflecting a true construction reason.

Judgment-day round 2 (FINAL, no third round) fixed five confirmed findings plus one records entry
(R2-1–R2-6), the headline being R2-1: five form fields on `Usuarios` had escaped the modal gate
entirely. Seven mutations run, all killed, zero survivors.

Suites (final, this is the verified HEAD `858e958`): `npm run test` 65 files / 1102 tests green,
`npm run build` clean, `npm run lint` exit 0 (five pre-existing warnings). `dotnet build Ways.slnx`
clean (2 pre-existing `NU1903` advisories).

## Cross-slice invariants held throughout

- **Zero schema, every slice, re-verified at each judgment-day round and at verify**: no file under
  `src/Ways.Infrastructure/Persistencia/Migraciones/`, the last migration stays
  `20260822002214_FiscalArcaEtapa19a.cs`, `dotnet ef migrations has-pending-model-changes` clean.
- **Zero physical deletes** over `tenants`, `empresas`, `puntos_venta`, `usuarios` — asserted as
  code (`BajasEstructuralesTests`) from slice 4 onward, and by direct repository scan at verify.
- **`InicializadorDeBaseDeDatos.cs`, `Politicas.cs`, `ManejadorDeErrores.cs` untouched** across the
  whole stage diff (`22af91a..858e958`).

## Delivery report owed to the owner (OD5, task 5.12)

Empresa and punto de venta deletion ships **latent**: correct and tested below the API
(`empresa_en_uso` / `punto_venta_en_uso` proven with a hand-seeded second empresa/PV), but
unreachable through the API because Ways has no endpoint that creates a second empresa or punto de
venta. Also reported: **R1** (a sale committed between the guard's read and the deletion's commit —
accepted, recovery is a one-line `UPDATE` because nothing is destroyed) and **T6** (FK index
coverage is reported, not guaranteed, since adding a missing index would be DDL).
