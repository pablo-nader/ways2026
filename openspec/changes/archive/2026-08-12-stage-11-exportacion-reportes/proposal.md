# Proposal: Stage 11 — Infraestructura de exportación + reportes descargables

## Intent

Stage 10 answered "¿cómo va el negocio?" on screen. The next question the owner asks in
front of that screen is "¿y esto lo puedo bajar?", and today the answer is no: **no
`.csproj` references any Excel, CSV or PDF library, no endpoint returns a file, no
response carries `Content-Disposition`, and the web has no `xlsx`/`jspdf` dependency.**
There is no export infrastructure at all.

This is a **pattern-setting stage**, not a feature stage. Its real deliverable is one
decided, tested, licence-clean way to turn a report into a downloadable file, chosen once
so etapas 12, 13, 14 and 18 consume it without reopening the question (doc-11:121-140,
doc-11:328-347). The reports it ships are the proof that the pattern works, and the two
that the business is actually missing: **Ver Cajas (G2)** and **Caja General / Caja Z
(G3)** — the only two legacy screens from sección G still owed (doc-01:418-427), both
deferred here by stage 10's own out-of-scope list.

## Scope

### In Scope

- **Export seam**: `TablaExportable` (neutral, typed) + `IExportadorDeTabla` in
  Application, one XLSX implementation in Infrastructure, library imported in exactly one
  file.
- **Route convention**: `GET {ruta-del-reporte}/export?formato=xlsx`, declared inside the
  same `MapGroup` as its source route, inheriting its authorization by co-location.
- **Exports for the nine existing `/api/reportes/*` reports** (stage 10), plus the ventas
  listing, the compras listing and the estado de cuenta.
- **G2 — Ver Cajas**: `GET /api/reportes/cajas` (histórico de turnos **cerrados** with
  totals) + `GET /api/reportes/cajas/{id}` (existing `ResumenDeTurno` + the turno's ticket
  and gasto listings), both exportable, plus their web screen.
- **G3 — Caja General / Z**: read surface over the existing `movimientos_tesoreria`
  ledger, exportable, plus its web screen.
- **Print views** (`@media print` + a print layout) for estado de cuenta and detalle de
  caja — the "imprimible" half of the mandate.
- **`api.descargar()`** in `cliente.ts` + a reusable download button, wired into the
  Tablero panels and the new caja screens.
- **`/api/reportes/stock/existencias`** + export + a modest screen (last slice, droppable).

### Out of Scope

- **Any database change whatsoever** (see *Modelo de datos propuesto*).
- **Server-side PDF generation** and any PDF library — decision 9.
- **CSV** as a shipped format — decision 2.
- **Branding beyond a text header block**: no logo, no per-empresa template, no colors —
  decision 7, flagged for the owner.
- **Asynchronous jobs, generated-file storage, scheduled or emailed reports.**
- **Any write path.** In particular the legacy G3 "Ingresar FC" modal (manual tesorería
  entry) stays excluded — the `tesoreria` spec's standing "Manual Tesorería Entries Are
  Out Of Scope" requirement is not reopened.
- **Open turnos in G2.** It is a *histórico de cierres* (doc-01:419); the live turno is
  already on `/caja`.
- Report builder, saved exports, per-user column selection, cross-empresa consolidation,
  labels/carteles (Etapa 18), auditoría export (Etapa 14).

## Capabilities

### New Capabilities

- `exportacion-de-reportes`: the export contract end to end — the `/export` sibling-route
  convention, the `formato` enum, policy inheritance by co-location, the row cap and its
  refusal, file naming and the in-sheet header block, `Content-Disposition` encoding, the
  full inventory of exportable surfaces, and the binding invariant that an export's
  figures equal its source endpoint's figures for the same parameters.
- `historico-de-cajas`: the G2/G3 read surface — closed-turno listing with totals derived
  from **already persisted** `arqueos_turno` rows, turno detail as the existing
  `ResumenDeTurno` plus the turno's ticket and gasto listings, the tesorería book, and the
  role split between the cajero's own close and management's cross-turno views.

### Modified Capabilities

- `reportes-de-gestion`: each report gains an `/export` sibling under its own policy; adds
  the `/stock/existencias` report.
- `rentabilidad-y-comisiones`: margin and commission exports stack `LecturaDeRentabilidad`
  exactly like their sources, and the exported file MUST carry the stage-10 coverage block
  in its header.
- `tesoreria`: a read/listing surface over `movimientos_tesoreria` now exists; the
  manual-write exclusion stands unchanged.

## Approach

One neutral table model, one writer, many mappings. Each report maps its **existing typed
response record** into a `TablaExportable`; the exporter turns that into a workbook. No
export re-queries the database, so an export cannot drift from its screen. The route lives
next to its source inside the same `MapGroup`, so it cannot drift from its policy either.
Everything is synchronous and buffered, bounded by a counted-before-generated row cap that
**refuses** rather than truncates.

## Autonomous decisions

The owner delegated technical decisions to the orchestrator with recorded rationale
(doc-11:136-140 are the input questions). Each below is a founded recommendation with a
conservative, reversible bias.

**1 — XLSX library: ClosedXML (MIT), conditional on a transitive-licence audit; MiniExcel
(Apache 2.0) is the pre-approved fallback.**
`EPPlus` is **disqualified**, not merely questioned: since v5 it ships under PolyForm
Noncommercial, which forbids commercial use, and Ways is a commercial SaaS — the only
permissive EPPlus is an abandoned pre-v5 branch. Raw `DocumentFormat.OpenXml` (MIT) is
rejected as the primary API: its verbosity would make "add an export" cost pages of code,
defeating the entire point of this stage (it stays in the graph anyway as ClosedXML's own
dependency). `NPOI` (Apache 2.0) is a viable second fallback with worse ergonomics.
**Binding condition**: slice 1 must enumerate the full transitive package graph and record
each package's declared licence in the PR description. Any licence outside
MIT / Apache-2.0 / BSD / MS-PL reopens the choice and falls back to MiniExcel. This is a
verifiable step, not an assurance — the licence graph is exactly the kind of claim that
must not be asserted from memory.

**2 — CSV is not shipped in v1.**
XLSX carries typed numbers and dates natively, so it has *no* locale ambiguity. A CSV
opened in an es-AR Excel does: decimal comma versus field comma, accents without a UTF-8
BOM, and date parsing by regional setting. Shipping XLSX only is both simpler and more
correct. Adding CSV later is one more `formato` enum value plus one more writer behind the
same seam — no route, policy or contract changes.

**3 — Synchronous, in-request, buffered, with a hard row cap that refuses.**
A job queue costs a table, a background service, generated-file storage, a polling
endpoint and a retention policy — an entire stage of infrastructure serving zero current
users at store-scale volumes, and it would force the database change this stage is trying
to avoid. **Cap: 25 000 data rows per file, counted with a `COUNT(*)` under the same
filters *before* generating anything.** Over the cap → `400 exportacion_demasiado_grande`
naming the actual row count and the offending range. **Never a truncated file**: a
spreadsheet someone forwards to their accountant with a silent cut is a lie with a footer,
and a footnote does not survive copy-paste. Refusing forces a correct query. The escape
hatch to async is *evidence*: the first real user who legitimately needs a range past the
cap is the justification, not speculation.

**4 — Endpoint pattern: `GET {ruta-del-reporte}/export?formato=xlsx`, same `MapGroup`,
declared immediately after its source route.**
Rejected `?formato=` on the source route itself: one route with two response types is an
OpenAPI lie, breaks the generated client's typing, and silently redefines every existing
test's contract. Rejected a separate `/api/exportaciones/*` namespace: it has no
structural link to its source, so parameters and policy drift by construction and every
export must re-declare its gate by hand. Co-location makes both inheritances
*structural*. `formato` is **required**, with a single legal member (`xlsx`) in v1;
anything else → `400 formato_no_soportado`. This is in mild tension with
`dto-contract-honesty` (a one-value enum), accepted deliberately: the value is validated
and load-bearing, it makes the URL self-describing in a support ticket, and it makes
adding `csv` an addition rather than a change of default behaviour.

**5 — Role gating is inherited structurally, with one explicit split for caja.**
`/rentabilidad/export` and `/comisiones/export` stack `LecturaDeRentabilidad` exactly like
their sources — **an Excel of margin data is as sensitive as the screen, and worse,
because it leaves the building.** For caja the split is: the **turno detail / Z-report**
sits under `OperacionDePos`, the same policy as `GET /api/caja/turnos/{id}/resumen` which
the cajero already reads on screen (refusing them a printable copy of the close they just
performed would be absurd, and the data is already theirs); the **cross-turno histórico
(G2)** and the **tesorería book (G3)** sit under `LecturaDeReportes` (Supervisor + Admin),
because they are management views over other people's shifts and over the fondo de caja.
**Verify criterion**: every export route ships a 403 test for the role one step below its
gate.

**6 — G2/G3: minimal new aggregation. Verified against the code.**
- **G3 = zero new aggregation.** `movimientos_tesoreria` already stores
  `inicio / ingreso / egreso / final / concepto / id_empleado / fecha` — literally the
  legacy Caja Z columns (`MovimientoTesoreria.cs:14-41`, whose own doc-comment calls it
  "ex `cajaz` del legacy"). One paginated read endpoint, one screen, one export.
- **G2 detail = zero new derivation.** `GET /api/caja/turnos/{id}/resumen` already returns
  medios, cantidad de tickets, primer/último ticket, ingresos por área, egresos por
  categoría y por área, and retiros (`ResumenDeTurno`, Contratos.cs:103-111). The only
  additions are the two line listings the legacy detail shows — the turno's tickets and
  gastos — both plain `WHERE id_turno_caja = @id` reads, because
  `ComprobanteVenta.IdTurnoCaja` and `Gasto.IdTurnoCaja` both exist and both are indexed.
- **G2 listing is the only genuinely new aggregation, and it is cheap**: for a closed
  turno the totals come from the **already persisted** `arqueos_turno` rows
  (Σ `importe_esperado`, Σ `importe_declarado`, Σ `diferencia`), never by re-running the
  live derivation N times. Egresos reuse the existing `EgresosDeTurno` definition (gastos
  del turno + retiros de `movimientos_caja`), never a second definition of the same word.
  Open turnos are excluded, which removes the partial-totals problem entirely.

**7 — File naming and branding: deterministic and textual. Branding beyond that is flagged
for the owner.**
Pattern `{reporte}_{alcance}_{desde}_{hasta}.xlsx` — e.g.
`ventas_resumen_pv3_2026-08-01_2026-08-12.xlsx`, `caja_turno_412_2026-08-12.xlsx`. No
timestamp and no random suffix, so two identical requests produce the same name and a
re-download overwrites instead of littering `Descargas` with `(1)`, `(2)`. **ASCII only**
in the file name (empresa and PV appear as ids, not names) even though `filename*`
(RFC 5987) is emitted for correctness — file names travel through mail, Windows shares and
phones. **Header block inside the sheet** (rows 1-4, blank row, table header at row 6):
empresa, punto de venta or "Todos", the date range, and the generation instant with its
zone plus the user who generated it — a file outlives the screen, so it must say what it
is and when it was taken. Any export carrying an estimated-cost figure repeats stage 10's
**coverage block** in that header. **Logo, colours, letterhead and per-empresa templates
are out of scope and flagged**: they need a stored asset, an upload path and an
image-handling dependency — a feature with its own storage and UI, not a formatting
detail.

**8 — Web downloads use `fetch` + blob via a new `api.descargar()`, not a plain anchor.**
Verified: the session is an HttpOnly cookie `ways.sesion`, `SameSite=Lax`, over a
same-origin `/api` (`Program.cs:55-60`, `cliente.ts:4-5,42-44`) — so a plain
`<a href>` *would* authenticate. **Authentication is not the problem; failure is.** A 403
(a Supervisor clicking a rentabilidad export), a 400 (row cap) or a 401 (expired session)
would navigate the SPA away to a raw ProblemDetails JSON page and never fire the existing
`alPerderLaSesion` observer (`cliente.ts:30-39,52-55`). `api.descargar()` reuses `pedir`'s
exact error path, reads the file name from `Content-Disposition`, and revokes its object
URL. Cost: the file is materialised in browser memory — bounded by decision 3's cap. Every
download button must disable while in flight and surface `ErrorApi.message` in the page's
existing error surface; a download that silently does nothing is this pattern's worst
failure mode.

**9 — No server-side PDF library. Browser print view instead.**
The two "imprimible" deliverables (estado de cuenta, detalle de caja) get a dedicated
print layout and `@media print` CSS; the browser's own "Guardar como PDF" covers the PDF
need at zero dependency and zero licensing risk — the same shape the legacy used
(`ticket.php` is a `window.print()`, doc-11:307-308). QuestPDF was evaluated honestly: its
Community licence is free only below a declared annual-gross-revenue threshold, above
which a paid Professional/Enterprise licence is required. Adopting it now plants a
**revenue-triggered licensing cliff** in the dependency graph of a commercial SaaS, for a
use case that does not exist yet. Server-side PDF becomes genuinely necessary when a
document must be emailed, archived or legally attached — that is **Etapa 19** (comprobante
fiscal con QR, where doc-11:304 already reserves the decision) and **Etapa 18** (etiquetas
y carteles, which have physical layout requirements). Deciding it here would decide it
blind. Adding a PDF library later changes no route, no policy and no data.

**10 — Stock exportable ships minimal and last, droppable to Etapa 13.**
There is no stock listing today: `GET /api/stock` requires *both* `idPuntoVenta` and
`idArticulo` (`StockEndpoints.cs:14`), so "stock exportable" needs a new report, not just
an export. It ships as `/api/reportes/stock/existencias` (stock joined to articulos,
covered by `ix_stock_punto_venta`) with a modest screen, as the **last slice**. If the
budget tightens it is dropped to **Etapa 13**, where a stock report belongs anyway —
mínimos, punto de pedido and reposición give it the context it lacks here. Recorded so
that dropping it is a decision, not an oversight.

**11 — One seam, one containment folder.**
Every report maps its existing typed response into `TablaExportable`; **no export
re-queries**, and no export contains a number the screen cannot show. **Verify criterion**:
for every export, an integration test asserts the exported figures equal the JSON
endpoint's figures for the same parameters. The Excel library is referenced in exactly one
Infrastructure file — the same containment discipline stage 10 applied to recharts — with
an architecture test asserting no other file references it.

## Modelo de datos propuesto

**THIS STAGE PROPOSES NO DATABASE CHANGE.** No new table, view, materialized view, enum,
column, constraint, index, foreign key or RLS policy; no change to any existing schema
object; no migration; no data statement.

Index coverage was reviewed against the actual EF configurations, and every access pattern
this stage introduces is already served:

| Access pattern | Existing index |
|---|---|
| G2 histórico de turnos por PV y fecha | `ix_turnos_caja_punto_venta_fecha` (`id_punto_venta, id_tenant, fecha_apertura`) |
| G2 totals from persisted arqueos | `ix_arqueos_turno_turno` (`id_turno_caja, id_tenant`) |
| G2 detail — tickets del turno | `ix_comprobantes_venta_turno` (`id_turno_caja, id_tenant`) |
| G2 detail — gastos del turno | `ix_gastos_turno` (`id_turno_caja, id_tenant`) |
| G2 detail — retiros del turno | `ix_movimientos_caja_turno` (`id_turno_caja, id_tenant`) |
| G3 tesorería book, chain order | `ix_movimientos_tesoreria_punto_venta_id` (`id_punto_venta, id_tenant, id`) |
| Existencias por punto de venta | `ix_stock_punto_venta` (`id_punto_venta, id_tenant`) |
| Every stage-10 report export | unchanged — reuses the stage-10 services verbatim |

**The gate reopens automatically if `sdd-apply` finds itself writing a migration.** Any
migration in this change is a scope violation, not an implementation detail, and must stop
and return to the owner.

## Affected Areas

| Area | Impact | Description |
|---|---|---|
| `src/Ways.Application/Exportacion/` | New | `TablaExportable`, `ColumnaExportable`, `IExportadorDeTabla`, the row-cap guard |
| `src/Ways.Infrastructure/Exportacion/` | New | `ExportadorXlsx` — the only file referencing the Excel library |
| `src/Ways.Infrastructure/Ways.Infrastructure.csproj` | Modified | One `PackageReference` (the only dependency added by this stage) |
| `src/Ways.Api/Endpoints/ReportesEndpoints.cs` | Modified | Nine `/export` siblings + `/stock/existencias` + `/cajas` + `/tesoreria` |
| `src/Ways.Api/Endpoints/{Ventas,Compras,CuentaCorriente}Endpoints.cs` | Modified | One `/export` sibling each |
| `src/Ways.Application/Caja/` | Modified | G2 listing service + the turno's ticket/gasto listings |
| `src/Ways.Application/Reportes/` | Modified | Existencias report; `TablaExportable` mappings per report |
| `src/Ways.Web/src/api/cliente.ts` | Modified | `api.descargar()` reusing `pedir`'s error path |
| `src/Ways.Web/src/componentes/` | New | `BotonDeDescarga`, print layout |
| `src/Ways.Web/src/paginas/` | New/Modified | `HistoricoDeCajas`, `Tesoreria`, `Existencias`; download buttons on `Tablero`, `CuentaCorriente`, `Compras` |
| `src/Ways.Web/src/App.tsx`, `componentes/Layout.tsx` | Modified | Three routes + nav entries under Caja |
| Database | **None** | See *Modelo de datos propuesto* |

## Risks

| Risk | Likelihood | Mitigation |
|---|---|---|
| **An export's numbers differ from the screen's** — a second query path drifting silently. The worst failure of this stage: a wrong file that looks right and gets forwarded. | Med | No export re-queries; single mapping from the same service response; an exported-equals-endpoint integration test per export |
| Transitive licence contamination in ClosedXML's graph | Med | Slice-1 audit recorded in the PR; MiniExcel pre-approved fallback |
| A margin/cost workbook reaching someone who cannot see the screen | Low | Policy inheritance by co-location + a 403 test per export at the role one step below its gate |
| Silent truncation of a large export | Low | Count before generating; hard `400`; never a truncated file |
| Memory pressure from buffered workbooks | Low | Row cap + count-before-generate |
| G2 listing totals diverging from the turno detail | Med | Both read the **same persisted** `arqueos_turno` rows; the live derivation is never used for a closed turno; open turnos excluded |
| Scope creep into Etapa 18 (etiquetas) or into a report builder | Med | Explicit out-of-scope list; `formato` enum has one member |
| Print view drifting from the screen it prints | Low | Print layout consumes the same component data, not a second fetch |
| Web slices overflowing the 400-line budget | Med | Slices 5 and 6 pre-identified as the split candidates |

## Rollback Plan

Nothing persists and no schema changes, so **every slice reverts cleanly with no data to
unwind**. Reverting slice 1 removes one `PackageReference` and two files; every later
slice is a route plus a mapping plus a screen. Reverting the whole stage returns the
system to stage 10 exactly. If the licence audit fails, only slice 1's exporter
implementation is swapped — the seam, the routes and every mapping stay.

## Dependencies

- **Stage 10** (archived, `2026-08-12-stage-10-agregacion-dashboard`): the nine
  `/api/reportes/*` endpoints this stage exports, and the `LecturaDeReportes` /
  `LecturaDeRentabilidad` split it must preserve.
- **Stage 6** (turnos de caja): `turnos_caja`, `arqueos_turno`, `movimientos_caja`,
  `movimientos_tesoreria` and `ServicioDeResumenDeTurno` — all G2/G3 data already exists.
- One new NuGet package, pending the slice-1 licence audit. No new web dependency.

## Success Criteria

- [ ] Every one of the nine stage-10 reports has a working `/export?formato=xlsx` under
      its own policy, and each has a test proving the exported figures equal the endpoint's.
- [ ] Adding an export to a *new* report costs one mapping and one route line — demonstrated
      by `/stock/existencias`, the last report added.
- [ ] A Supervisor gets `403` on `/rentabilidad/export`, proven by test.
- [ ] An over-cap request returns `400` with the actual row count; **no truncated file is
      ever produced**, proven by test.
- [ ] Ver Cajas (G2) and Caja General/Z (G3) are reachable, correct and downloadable —
      sección G of doc-01 is fully closed except the deliberately excluded G4.
- [ ] Estado de cuenta and detalle de caja print correctly to PDF from the browser.
- [ ] The Excel library is referenced in exactly one file, proven by an architecture test.
- [ ] `dotnet ef migrations list` is unchanged from stage 10's closing state.
- [ ] Domain / Application / Integration / vitest suites all green.

## Plan de slices

Stacked-to-main, one judgment-day round per slice (`protocolo-pr-solo-dev`).

| # | Branch | Content | ~lines |
|---|---|---|---|
| 1 | `feat/stage11-slice1-exportador` | Licence audit + package, `TablaExportable`/`IExportadorDeTabla`, `ExportadorXlsx`, row-cap guard, `Content-Disposition` helper (RFC 5987), **first export** (`/reportes/ventas/resumen/export`), containment + 403 + cap tests | ~400 |
| 2 | `feat/stage11-slice2-exports-reportes` | The remaining eight stage-10 report exports — one mapping each + exported-equals-endpoint tests | ~380 |
| 3 | `feat/stage11-slice3-exports-listados` | Ventas listing, compras listing, estado de cuenta exports (pagination bypassed under the cap) | ~350 |
| 4 | `feat/stage11-slice4-descarga-web` | `api.descargar()`, `BotonDeDescarga`, Tablero wiring, vitest | ~330 |
| 5 | `feat/stage11-slice5-cajas-api` | G2 listing (totals from persisted arqueos) + detail (resumen + ticket/gasto listings) + both exports | ~400 |
| 6 | `feat/stage11-slice6-cajas-web` | `/caja/historico` listing + detail screens, downloads, descriptor tests | ~380 |
| 7 | `feat/stage11-slice7-tesoreria` | G3 read endpoint + export + `/caja/tesoreria` screen | ~330 |
| 8 | `feat/stage11-slice8-vistas-impresion` | Print layout + `@media print` for estado de cuenta and detalle de caja | ~280 |
| 9 | `feat/stage11-slice9-existencias` | `/reportes/stock/existencias` + export + screen — **droppable to Etapa 13** | ~300 |

**Review Workload Forecast for `sdd-tasks`**: ~3 150 lines total. Chained PRs **required**,
`chain_strategy` stacked-to-main. **Slices 1, 5 and 6 are the ones at real risk of
overflowing 400** — slice 1 splits at the seam/first-export boundary, slices 5 and 6 at the
listing/detail boundary.

## Deferred / adjacent (recorded, not in scope)

- **CSV format** — one enum value plus one writer, behind the same seam (decision 2).
- **Server-side PDF** — reopens in Etapa 18 (etiquetas, physical layout) and Etapa 19
  (comprobante fiscal con QR, doc-11:304). Purely additive (decision 9).
- **Async job + deferred download** — justified by the first real over-cap request, not by
  speculation (decision 3).
- **Per-empresa branding** (logo, colours, letterhead) — owner flag (decision 7).
- **Manual tesorería entry** (legacy "Ingresar FC") — a write path; the `tesoreria` spec's
  exclusion stands.
- **`articulos_empresas` replace-set concurrency gap** and the **importe CHECK
  micro-gate** — carried over from stage 8, still open, untouched here.
- **`ways_owner` as a testcontainer superuser** — repo-wide migration-test weakness;
  irrelevant here (no migration), still open.
- **Recharts containment has no CI lint rule** (stage-10 WARNING-1) — this stage adds a
  second containment boundary with the same weakness; a shared import-boundary rule is a
  natural future fix.

## Proposal question round

The owner delegated these; each records the assumption taken so a correction is cheap.

1. **Branding on generated files** — *OWNER FLAG.* Assumed: a plain text header block
   (empresa, PV, rango, generado por/cuándo) and nothing else. Logo, colours and
   per-empresa templates are deferred. **This is the one genuinely product-weight call in
   the stage.**
2. **May a Vendedor download the Z-report of the turno they just closed?** Assumed **yes**
   — `OperacionDePos`, the same policy under which they already read that resumen on
   screen. Management's cross-turno G2/G3 views stay at `LecturaDeReportes`.
3. **Over-cap behaviour: refuse or truncate-with-notice?** Assumed **refuse** (400 with the
   row count). A truncated spreadsheet that gets forwarded is a lie with a footer.
4. **XLSX only, no CSV?** Assumed **yes** — XLSX has no es-AR locale ambiguity; CSV in
   Excel does. CSV remains one enum value away.
5. **Is "stock exportable" worth a screen in this stage, or does it belong to Etapa 13?**
   Assumed: ship it minimal and **last**, droppable to Etapa 13 without loss.
