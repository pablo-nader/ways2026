# Archive Report: stage-20-organizacion-relaciones-y-bajas

**Change**: `stage-20-organizacion-relaciones-y-bajas` · **Mode**: hybrid (openspec files written by
this phase; Engram persistence owned by the orchestrator per this session's preflight) ·
**Verified HEAD**: `858e9589ed657ce2a7e533bb00d6db75e99552db` (`main`) — the exact tree `sdd-verify`
graded PASS WITH WARNINGS. **HEAD at archive time**: `8ed8042` (`docs(skills): incorpora las
lecciones recurrentes de la etapa 20`), one commit ahead of the verified HEAD — this is the
skills-loop commit CLAUDE.md mandates, committed separately by the orchestrator and explicitly
**out of scope** for this change; it is not part of the diff this archive reports on and required
no code review under this change's judgment-day cycle. **Fecha de archivado**: 2026-09-05. **Target
de archivo**: `openspec/changes/archive/2026-09-05-stage-20-organizacion-relaciones-y-bajas/`.

## Traceability of sources read

- `openspec/changes/stage-20-organizacion-relaciones-y-bajas/proposal.md`
- `openspec/changes/stage-20-organizacion-relaciones-y-bajas/explore.md`
- `openspec/changes/stage-20-organizacion-relaciones-y-bajas/design.md`
- `openspec/changes/stage-20-organizacion-relaciones-y-bajas/state.yaml` (full, 1440 lines —
  `phases.propose/spec/design/tasks/apply/verify` notes and the nine `orchestrator_decisions`
  OD1–OD6)
- `openspec/changes/stage-20-organizacion-relaciones-y-bajas/tasks.md` (116 task lines, 116/116
  `[x]`; the 11 Reconciliaciones; the Binding Verify Criteria table; the mutation-evidence tables of
  all five slices)
- `openspec/changes/stage-20-organizacion-relaciones-y-bajas/verify-report.md` (verdict
  `pass_with_warnings`, 0 CRITICAL / 3 WARNING / 9 SUGGESTION, admitted by
  `gentle-ai sdd-verify-validate --requirements 19 --scenarios 73`,
  `evidence_revision sha256:dd355fd0...5c9fd2`)
- `openspec/changes/stage-20-organizacion-relaciones-y-bajas/specs/{bajas-de-organizacion,
  tenant-organization,usuarios-tenant-scoping}/spec.md` (three delta specs, merged in this phase)
- Pre-merge main specs: `openspec/specs/tenant-organization/spec.md`,
  `openspec/specs/usuarios-tenant-scoping/spec.md` (no prior `openspec/specs/bajas-de-organizacion/`
  existed)
- `git log` on `main` — independently re-confirmed the five merge commits and their order

**Final-state note on the two unticked task boxes.** `verify-report.md` (§2, W1) found `tasks.md`
still showing `5.11` and `5.12` as `[ ]` at verify time, provably complete from evidence outside the
file (two judgment-day rounds recorded in `tasks.md` itself, PR #173 merged as the verified HEAD).
Re-reading `tasks.md` in this phase (grep for `^- \[ \]`) finds **zero** unchecked boxes: both were
ticked in a later commit, and `state.yaml`'s `phases.apply.status` already reads `done` rather than
the `in_progress` W1 reported at verify time. This report states the current, corrected state — not
the stale verify-time snapshot — per the Final-State Authority rule; the correction is confirmed by
direct inspection of the file, not merely asserted.

---

## 1. What shipped

Two independent parts, five slices, stacked-to-main.

**Part A — organization relationships projected in the root UI (slices 1–2).** The four root
listing DTOs (`TenantListado`, `EmpresaListado`, `PuntoVentaListado`, `UsuarioListado`) gained owner
names and live child counts, each within the existing single-round-trip query (`GET /api/usuarios`
costs 2 because it is the only paginated listing — see Reconciliación 8, not a regression this stage
introduced). The four root screens (`Tenants.tsx`, `Empresas.tsx`, `PuntosVenta.tsx`,
`Usuarios.tsx`) render those names instead of raw integers, gained owner filters over the
already-loaded list, and gained Vitest coverage where none existed before. A mid-slice
owner-reported bug (Admin-role user creation missing a tenant selector) was fixed on the same
branch (task 2.17).

**Part B — usage-guarded logical deletion for tenant, empresa, punto de venta, and Usuario alignment
(slices 3–5).** `InspectorDeUso` / `InventarioDeDependientes` discover the complete dependent set of
each of the four principals from EF metadata (`IEntityType.GetReferencingForeignKeys()`), never from
a hand-maintained list, classified into exactly one of three buckets (timestamped / untimestamped /
carve-out) with a checked-in golden inventory as the actual completeness trip-wire. Three new DELETE
routes (`DELETE /api/plataforma/tenants/{id}`, `DELETE /api/empresas/{id}`,
`DELETE /api/puntos-venta/{id}`) apply structural minimums first, then the usage guard, then a
bounded same-instant cascade; `DELETE /api/usuarios/{id}` gained the same usage guard positioned
strictly after `PoliticaDeRoles`. All deletion is logical (`DeletedAt`) — zero physical deletes
anywhere in the four organization tables, asserted as code
(`BajasEstructuralesTests`) and proven by mutation. The four root screens gained delete buttons
behind a shared modal confirmation gate (`ConfirmacionDeBaja`) mapping six named 409 codes to
distinct Spanish copy.

## 2. Zero-schema result

The db_gate was **ZERO-SCHEMA-RATIFIED** (`state.yaml:9–55`) before slice 1, and it held across
every slice and was re-proved, not trusted, at both apply and verify:

- Zero files under `src/Ways.Infrastructure/Persistencia/Migraciones/` in the whole stage diff
  (`22af91a..858e958`); the last migration is still `20260822002214_FiscalArcaEtapa19a.cs`.
- `dotnet ef migrations has-pending-model-changes` clean (the working invocation needs
  `--project src/Ways.Infrastructure --startup-project src/Ways.Infrastructure`, since
  `Ways.Api` does not reference `Microsoft.EntityFrameworkCore.Design` — a flag set `tasks.md` never
  wrote down, recorded by `sdd-verify`).
- `InicializadorDeBaseDeDatos.cs`, `Politicas.cs`, `ManejadorDeErrores.cs` appear nowhere in the
  66-file, 13 721-insertion, 495-deletion non-`openspec/` stage diff.
- Zero physical deletes: an independent repository scan (`ExecuteDelete` 0, `.Remove(` 0,
  `DELETE FROM` outside `Migraciones/` 0, `RemoveRange(` exactly the six pre-existing detail-set
  receivers, none an organization table) plus `BajasEstructuralesTests` as executable proof.

`db-error-backstops` is **structurally N/A** for this whole change: every deletion is an
`UPDATE ... SET deleted_at`, against which `DeleteBehavior.Restrict` contributes zero protection, so
no Postgres SQLSTATE can ever fire — the application guard and its tests are the sole line of
defence, and this was named the stage's top risk from the proposal onward.

## 3. Pull Requests

| PR | Branch | Slice | Merge commit | Content |
|---|---|---|---|---|
| #165 | `feat/stage20-slice1-proyeccion-api` | 1 | `5f0018f` | Owner names + child counts projected into the four listing DTOs, single round trip preserved |
| #167 | `feat/stage20-slice2-web-relaciones` | 2 | `d616362` | The four root screens render names, offer owner filters, gain Vitest coverage; mid-slice fix for the Admin tenant-selector bug |
| #169 | `feat/stage20-slice3-inspector-de-uso` | 3 | `c3f62d7` | `InspectorDeUso` / `InventarioDeDependientes`, the usage guard, shipped INERT (zero production callers, verified) |
| #171 | `feat/stage20-slice4-bajas-api` | 4 | `99b5abe` | Three DELETE routes, structural minimums, bounded cascade, usuario guard wiring, audit trail for every baja |
| #173 | `feat/stage20-slice5-bajas-web` | 5 | `858e958` | Delete buttons, the shared `ConfirmacionDeBaja` modal gate, 409 copy mapped by code |

All five merge commits are present on `main` in the planned order 1→2→3→4→5, independently
re-confirmed against `git log` by `sdd-verify` and again in this phase.

## 4. The eleven Reconciliaciones

Registered in `tasks.md` to resolve tensions between what earlier phases wrote and what later
phases discovered or arbitrated, without editing delta-spec text mid-flight:

1. **`tenant-organization`'s "…gets a clean 403" scenario is superseded by OD6.** A cascade-deleted
   admin gets `401 credenciales_invalidas`, not `403 tenant_suspendido` — the login lookup runs
   under the `"BajaLogica"` filter with no `IgnoreQueryFilters`, so the user is simply not found.
   The property (cannot log in, cleanly, no crash) holds; only the code differs.
2. **Design's T3 is closed by OD4, not left open.** A soft-deleted dependent still blocks — no task
   emits an `AND d.deleted_at IS NULL` variant.
3. **Design's T1 is closed by OD5 as an accepted latency, not a scope expansion.** Empresa/PV
   deletion ships latent — correct, tested below the API, unreachable through the API until
   creation endpoints exist.
4. **Design's T4 (the completeness test cannot exist as written) is adopted as the four nets
   N1–N4.** The literal "fails the build on an unclassified type" is a tautology against a total
   classifier; delivered instead by N1 (mechanical-impossibility throws naming the type), N2 (bucket
   read off the table), **N3 (the checked-in sorted golden inventory — the actual trip-wire)** and N5
   (generic per-anchor scope-column coverage, added in slice 3 round 2).
5. **Test relocation is budgeted, not discovered.** The guard's raw SQL cannot run on the InMemory
   provider, so `EliminarAsync` cases relocate to `Ways.IntegrationTests` (the
   `ServicioDeOfertas.ActualizarAsync` precedent).
6. **U1–U8 (design's guarded-write conjunct enumeration) are transcribed to slice 4** up front, per
   `mutation-proof-tests` rule 3, each paired with its own task-level kill.
7. **The `sdd-tasks` 530-word size budget is overridden**, matching the archived stage-17/18/19a
   `tasks.md` shape, registered rather than silently exceeded.
8. **V9 means "the projection adds no round trip"**, and for `GET /api/usuarios` the literal reading
   is false: it costs 2 because it is the only paginated listing and its `CountAsync` predates this
   change. The projection itself adds zero commands on all four endpoints.
9. **S1's `iff` holds for the platform-vs-tenant distinction and is FALSE for the D13 orphan** (a
   soft-deleted tenant's account: non-null `IdTenant`, null `NombreTenant`). `IdTenant`, never the
   name, is the discriminator a consumer must read.
10. **The orphan option's id suffix (`— (tenant 7)`) is not a violation of "no raw owner id."** It is
    the D13 anomaly's handle — there is no name to display — not an owner identity; no **cell**
    presents an id as the owner's identity.
11. **"One article blocks" is true for the tenant and the empresa, and false for the punto de
    venta.** No article-shaped row hangs off a punto de venta (`articulos` is tenant-wide); the PV
    blocks on its own smallest customer datum, one `stock` row — usage propagates UP the hierarchy,
    never down (the slice-3 design amendment).

Six scenarios across the delta specs pass *through* one of these Reconciliaciones (1, 4 twice, 8, 9,
11 per `verify-report.md`'s own count) rather than being scored a miss; Reconciliación 10 is
additionally referenced by a scenario whose literal verdict was already a plain PASS. Every one of
these seven scenario locations is annotated in this phase's spec merge with a one-line
`> Superseded by Reconciliación N (stage 20): ...` note directly beneath the scenario, so the merged
main specs no longer carry a false MUST — the scenario text itself is left byte-identical, per this
change's own no-mid-flight-edit discipline.

## 5. OD5 latency and the accepted residuals / carry-forwards

**OD5 — empresa and punto de venta deletion ships LATENT.** Ways has no endpoint that creates a
second empresa or punto de venta (`AprovisionamientoEndpoints` exposes only
`POST /api/plataforma/tenants`; `OrganizacionEndpoints` has no `POST`), so the structural minimum
fires on every empresa/PV delete attempt through the API today, and `empresa_en_uso` /
`punto_venta_en_uso` are reachable only below the API layer. The code is correct and tested there
(hand-seeded second empresa/PV in the service/integration suite). Same shape as `EstadoTenant.Baja`,
which shipped in stage 1 and waited until this stage for its writer. No creation endpoint was added
— the owner asked to delete things created by mistake, not to create more, and widening that scope
is the owner's call.

**Accepted known limitations** (`verify-report.md` §6, none a defect, none blocking):

1. **OD5 latency** — as above.
2. **R1 — a sale between the guard's read and the deletion's commit.** Under READ COMMITTED, a sale,
   shift or comprobante can commit between the guard's `EXISTS` and the deletion's commit. Closing
   it would put an administration lock on the POS hot path (the stage-19a D1 lesson). Recovery is a
   one-line `UPDATE ... SET deleted_at = NULL`, because B1 destroys nothing.
3. **T6 — FK index coverage is reported, not guaranteed.** A trip-wire reads `pg_indexes` and
   freezes an empty expected-uncovered set; adding a missing index would be DDL, forbidden by the
   zero-schema gate.
4. **Ambiguous commit under no-retry.** A lost ACK after a successful commit surfaces as a generic
   `500`, mitigated at the copy layer ("verificá el listado antes de reintentar"); the accepted
   `AnularAsync`/`AjustarAsync` profile.
5. **Slice-5 modality scope is screen-inert, not document-modal.** Every control inside the four
   root screens is behind `bloqueado`, but the `Layout.tsx` navbar (~25 `NavLink`s + "Salir") is
   ungated, the 409 banner renders outside the `aria-modal` dialog, and focus can be lost during the
   write. See §6 below — carried forward as its own follow-up rather than closed here.
6. **The stage-2 backfill over-block on pre-existing tenants** — fail-safe (over-block), the
   discriminator deliberately unchanged, `InicializadorDeBaseDeDatos.cs` untouched.

**Bookkeeping WARNINGs from verify, resolved or explicitly out of scope by archive time:**

- **W1** (tasks.md unticked boxes / `state.yaml` `phases.apply.status`) — resolved, per §"Final-state
  note" above.
- **W2** (production doc-comments carry judgment-day round/finding ids) — documentation-only, no
  code change; carried forward, listed in §6 below.
- **W3** (`.claude/skills/` changes uncommitted at verify time) — resolved by the orchestrator
  committing them separately (`8ed8042`, one commit ahead of the verified HEAD); those skill changes
  are **not part of this change's diff or this archive**, per this phase's explicit instructions.

**Nine SUGGESTIONs from verify, carried forward as accepted residuals (none blocks archive):**

- S1 — the physical-delete scan's `RemoveRange(` anchor and `DELETE FROM` file-glob are narrower
  than the property they claim; zero matching call sites exist today. **Carried to §6 as a follow-up
  item ("the `Remove(` scan widening").**
- S2 — the bridged-409 location phrase is plan-dependent when the same leaf matches both its direct
  and its bridged branch; the code and the 409 remain unaffected.
- S3 — `estadoAnterior` is read from the identity map, not refreshed under the lock; a narrow
  concurrent-suspend race can misreport the audit row's "previous state" field only.
- S4 — the R2-2 concurrency test forces its rendezvous sequentially; it proves the re-read and the
  404, not advisory-lock contention (asserted structurally only).
- S5 — **a platform-readable audit surface does not exist**: the `tenant.baja` audit row persists
  and is readable at the database, but `GET /api/auditoria` is Admin-only and the cascade has just
  deleted every admin of that tenant. **Carried to §6 as its own follow-up.**
- S6 — **six screens keep the native `confirm()`** (`Articulos`, `Clientes`, `Categorias`,
  `Ofertas`, `PaginaCatalogo`, `Proveedores`), out of stage scope. **Carried to §6.**
- S7 — slice-5 coverage gaps that are not defects (per-screen wiring has no own kill on three of the
  four screens; a couple of minor state-clearing asymmetries).
- S8 — the retry double-`Add` class pre-exists outside this stage, in
  `ServicioDePrecios.AbrirNuevoPrecioAsync` and `ServicioDeUsuarios.CrearAsync`. **Chip
  `task_29095520`.**
- S9 — `SSH.NET 2024.1.0` carries `NU1903` (`GHSA-q939-rpr3-3284`, high severity), test-only,
  pre-existing, unchanged by this stage. **Chip `task_869978db`.**

## 6. Follow-up items recommended as their own future change

These are not defects in what shipped; each is a deliberately deferred boundary named by this
change's own design or judgment-day record, and each is large enough or independent enough to merit
its own SDD cycle rather than a patch onto this one:

1. **A platform-readable audit surface.** The `tenant.baja` row persists in tenant X for forensics
   and export and is readable at the database, but `GET /api/auditoria` is Admin-only
   (`Politicas.LecturaDeAuditoria`) and the cascade has just deleted every admin of that tenant, so
   nobody reads it through the API today. `Politicas.cs` stays untouched by this change's own
   decision. Reopen condition: the first time platform needs a deleted tenant's trail without going
   to the database (named in judgment-day slice 4, round 2, R2-7, and verify §7 S5).
2. **Document-level modality for the deletion confirmation gate.** The slice-5 gate is proven
   screen-inert (every control inside each of the four root screens is behind `bloqueado`,
   control-by-control, both judges, two judgment-day rounds) but is **not** modal at the document
   level: the `Layout.tsx` navbar (~25 `NavLink`s + "Salir") is ungated, so an operator can navigate
   away or log out with a DELETE undecided; the 409 banner renders as a sibling of the `aria-modal`
   dialog with no `role="alert"`, hiding it from exactly the users `aria-modal` was added for; and
   focus can be lost during the write, unobservable by jsdom. Plus the six screens that still use
   the native `confirm()` (`Articulos`, `Clientes`, `Categorias`, `Ofertas`, `PaginaCatalogo`,
   `Proveedores`) rather than the shared `ConfirmacionDeBaja` gate. A real fix needs a cross-cutting
   `inert`/portal change at the application-shell level plus the rule-10 sweep that adopts the
   shared gate on the six remaining screens — outside this stage's scope by design.
3. **Creation endpoints for a second empresa or punto de venta.** This is the OD5 latency's root
   cause: Ways has no endpoint that creates a second empresa or punto de venta today, so
   `empresa_en_uso` and `punto_venta_en_uso` are unreachable through the API and provable only below
   it. Adding such endpoints is explicitly what would **un-latch** empresa/PV deletion for real
   operator use — it was deliberately not added here because the owner asked to delete
   mistakenly-created entities, not to create more, and widening that scope silently is not this
   phase's call.
4. **The `Remove(` / `DELETE FROM` physical-delete scan is narrower than its own record claims**
   (verify §7 S1): `db\.(\w+)\.RemoveRange\(` would miss a differently-named receiver
   (`context.X.Remove(`, `dbPlataforma.Usuarios.RemoveRange(`), and the "all files" `DELETE FROM`
   claim is scoped to `*.cs` only, excluding a future `.sql` resource. Zero matching call sites and
   zero `.sql` files exist today, so the property currently holds — only the trip-wire's future
   coverage is narrow. Widen the anchors the next time the scan file is touched, rather than as a
   standalone change if it can ride along with #1–#3 above.

## 7. Chips spawned by this change

- **`task_869978db`** — `SSH.NET 2024.1.0` carries `NU1903` (`GHSA-q939-rpr3-3284`, high severity)
  on `tests/Ways.IntegrationTests.csproj`. Pre-existing, test-only, unchanged by this stage; the
  build is otherwise clean (verify §7 S9).
- **`task_29095520`** — the retry double-`Add` class pre-exists outside this stage, in
  `ServicioDePrecios.AbrirNuevoPrecioAsync` (`:123, :226, :229, :240`) and
  `ServicioDeUsuarios.CrearAsync` (`:201, :207, :215, :219`). Not touched by stage 20 (verify §7 S8).

## 8. Specs merged to `openspec/specs/`

| Domain | Action | Details |
|---|---|---|
| `bajas-de-organizacion` | Created (NEW, full spec) | 12 requirements / 41 scenarios, mechanically copied byte-for-byte from the delta (`diff -r` empty, verbatim below), then annotated with 3 `> Superseded by Reconciliación 4/11` notes beneath the affected scenarios |
| `tenant-organization` | Updated (ADDED-only delta) | 5 ADDED requirements / 21 scenarios appended after the pre-existing content, which stays byte-identical (`git diff --unified=0` shows a single `@@ -193,0 +194,176 @@` hunk — zero removed lines); 2 `> Superseded by Reconciliación 1/10` annotations added within the newly appended text |
| `usuarios-tenant-scoping` | Updated (ADDED-only delta) | 2 ADDED requirements / 11 scenarios appended after the pre-existing content, which stays byte-identical (`git diff --unified=0` shows a single `@@ -102,0 +103,105 @@` hunk — zero removed lines); 2 `> Superseded by Reconciliación 8/9` annotations added within the newly appended text |

No requirement was MODIFIED, REMOVED or RENAMED in either delta — every pre-existing requirement of
both `tenant-organization` and `usuarios-tenant-scoping` is preserved verbatim.

### Mechanical copy evidence — `bajas-de-organizacion` (new spec)

```
$ cp openspec/changes/stage-20-organizacion-relaciones-y-bajas/specs/bajas-de-organizacion/spec.md \
     openspec/specs/bajas-de-organizacion/.spec.md.tmp
$ diff -r openspec/changes/stage-20-organizacion-relaciones-y-bajas/specs/bajas-de-organizacion/spec.md \
          openspec/specs/bajas-de-organizacion/.spec.md.tmp
(empty — byte-identical)
$ mv openspec/specs/bajas-de-organizacion/.spec.md.tmp openspec/specs/bajas-de-organizacion/spec.md
```

The three `> Superseded by Reconciliación N` annotation lines were added to this file **after** the
verified mechanical copy above, as a deliberate, recorded content edit — not part of the copy
operation itself.

### Merge evidence — `tenant-organization` and `usuarios-tenant-scoping` (ADDED-only deltas)

```
$ git diff --unified=0 -- openspec/specs/tenant-organization/spec.md
@@ -193,0 +194,176 @@ fiscal and non-fiscal simultaneously.
(176 lines added after line 193; zero lines removed before it)

$ git diff --unified=0 -- openspec/specs/usuarios-tenant-scoping/spec.md
@@ -102,0 +103,105 @@ directly.
(105 lines added after line 102; zero lines removed before it)
```

## 9. Archive move evidence

See the phase result for the verbatim `diff -r` readback of the archive folder move (source snapshot
vs. archived destination, `archive-report.md` excluded as additive per the Mechanical Copy Contract)
— an empty diff is the only passing evidence and is included there.

## 10. Task ledger

`tasks.md` carries 116 numbered task lines. **116/116 are `[x]`** at archive time (re-confirmed by
`grep -c "^- \[ \]"` returning `0` in this phase) — the two boxes verify found unticked (`5.11`,
`5.12`) are now ticked. No stale-checkbox reconciliation was needed.

## 11. SDD Cycle Complete

The change has been fully explored, proposed, specified, designed, tasked, implemented across five
stacked-to-main PRs (each closed by a clean judgment-day round), verified (PASS WITH WARNINGS, 0
CRITICAL), and archived. Zero schema changes were made across the whole cycle. Ready for the
follow-up changes named in §6, whenever the owner chooses to open them.
