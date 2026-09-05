# Tasks: Stage 20 — Organization relationships and usage-guarded logical deletion

## Review Workload Forecast

| Field | Value |
|-------|-------|
| Estimated changed lines | ~2 160 across 5 slices (slice 1 ~390 · slice 2 ~430 · slice 3 ~470 · slice 4 ~530 · slice 5 ~340) |
| 800-line budget risk | **Low** — every slice sits at roughly half the 800-line cap; three split points are pre-authorized |
| Chained PRs recommended | **Yes** |
| Suggested split | PR 1 (projection API) → PR 2 (projection web) → PR 3 (the inert guard) → PR 4 (deletion API) → PR 5 (deletion web) |
| Delivery strategy | auto-chain |
| Chain strategy | stacked-to-main |

Decision needed before apply: No
Chained PRs recommended: Yes
Chain strategy: stacked-to-main
400-line budget risk: High
800-line budget risk: Low

**Both budget lines are stated on purpose, and they do not contradict each other.** The literal
`400-line budget risk` line is the guard contract's default budget: against 400, three of the five
slices (430, 470, 530) overflow, so the honest value is **High** — which is precisely why OD1 raised
the budget for this change. Against the configured `review_budget_lines: 800` (`state.yaml:8`,
OD1), every slice sits at roughly half the cap and the risk is **Low**. The operative budget for
this change is **800**.

The DB gate is **ZERO-SCHEMA-RATIFIED** (`state.yaml:9-55`), so no owner decision blocks slice 1.
The three arbitrations the orchestrator took after design (OD4, OD5, OD6) are transcribed below and
are binding on every slice. Slice 4 carries the only inflators: its test matrix and the **budgeted**
relocation of the `EliminarAsync` cases out of the InMemory-backed Application suite (design fact 9,
D12) — the estimate above already includes both, which is why slice 4 is ~530 and not design's ~490.

**Skill applicability, stated up front.** `mutation-proof-tests` (every test whose purpose is proving
one specific clause ships with recorded mutation evidence: mutate, watch it fail, revert) ·
`dto-contract-honesty` (every field added to a DTO has exactly one fate) · `web-descriptor-tests`
(colocated Vitest tests for descriptors and mapping helpers) · `react-async-state` (token/generation
gating and a full-window disabled state) · `work-unit-commits` · `judgment-day` before every PR.
**`db-error-backstops` is STRUCTURALLY N/A for this change**: B1 forbids physical deletion, every FK
is `DeleteBehavior.Restrict`, and `Restrict` contributes exactly zero protection against
`UPDATE … SET deleted_at`, so no Postgres SQLSTATE can ever fire on any path this stage adds. There
is no branch to add to `ManejadorDeErrores.cs`, and its untouched state is verify criterion V6. The
consequence must be said out loud: **the application guard is the sole line of defence and its tests
carry the entire safety argument.**

## Orchestrator arbitrations carried into these tasks (binding, do not reopen)

| # | Arbitration | Effect on the task list |
|---|---|---|
| **OD4** | **A soft-deleted dependent STILL BLOCKS.** Design's T3 recorded a pending owner ruling; it is now ruled in favour of the proposal and the spec (`bajas-de-organizacion` → *A Soft-Deleted Dependent Still Blocks*) | The guard emits **no** `AND d.deleted_at IS NULL` conjunct on any branch. Task 3.10 asserts its absence and task 4.11 proves the behaviour end to end. Recorded as a **one-line reversible knob** in `InspectorDeUso`'s doc-comment: reversing it means adding that conjunct per branch, flipping task 4.11's test, and regenerating N3's golden. **Not in tension with S2** — the usage guard counts history (deleted dependents count), the structural minimum counts live siblings (deleted siblings do not) |
| **OD5** | **Ways has no endpoint that creates a second empresa or punto de venta**, so the structural minimum fires on **every** empresa/PV delete through the API. `empresa_en_uso` and `punto_venta_en_uso` are provable **only below the API layer** | **No API-level integration test may be written for those two codes** — it would pass for the wrong reason (`mutation-proof-tests` rule 3). Task 4.13 and task 4.9's empresa/PV halves are written at the service/integration layer with a hand-seeded second empresa/PV. **No creation endpoint is added**: scope is deliberately not expanded. The latency **must be reported to the owner at delivery** (task 5.12) |
| **OD6** | **A cascade-deleted admin gets `401 credenciales_invalidas`, not `403 tenant_suspendido`** | Task 4.17 implements design's version. The `tenant-organization` spec scenario *"A deleted tenant's user cannot log in and gets a clean 403"* is **superseded**: the property it protects (cannot log in, cleanly, no crash) is preserved, only the code differs. The 403 branch stays reachable for a **suspended** tenant and is asserted unchanged as a regression |

## Reconciliaciones

1. **`tenant-organization` spec scenario "…gets a clean 403" is superseded by OD6.** The scenario is
   left byte-identical in the spec (deltas of this change must not be edited mid-flight without a
   record); the deviation is registered here and re-asserted in task 4.17, which ships **two** tests:
   `401 credenciales_invalidas` for a cascade-deleted admin, `403 tenant_suspendido` for a suspended
   tenant. Verify must read the spec scenario through this reconciliation.
2. **Design's T3 is closed by OD4, not left open.** Every occurrence of *"needs an owner ruling
   before slice 3 is written"* in `design.md:506-512` is resolved: implement the proposal's direction
   (blocks). No task may emit the `AND d.deleted_at IS NULL` variant.
3. **Design's T1 is closed by OD5 as an accepted latency, not a scope expansion.** Empresa/PV
   deletion ships **latent** — correct, tested below the API, and unreachable through the API until
   creation endpoints exist. Same shape as `EstadoTenant.Baja`, which shipped in stage 1 and waited
   until this stage for its writer.
4. **Design's T4 (the completeness test cannot exist as written) is adopted as the four nets N1-N4.**
   The spec's requirement *"Every Referencing Type Is Classified Into Exactly One Bucket Or The Build
   Fails"* is delivered by N1 (totality — `Construir` throws **naming** the type and the FK on the
   three mechanical impossibilities), N2 (the bucket is read off the **table**, not restated from the
   code), **N3 (the checked-in sorted inventory golden — the actual trip-wire)** and N4 (pristine
   regression). A `Desconocido` bucket that throws at request time is explicitly **rejected**: it
   converts a future stage's omission into a production 500 instead of a red test. This is a
   **substitution recorded, not silently performed**.
5. **Test relocation is budgeted, not discovered.** The guard's raw SQL cannot run on the InMemory
   provider, so the `EliminarAsync` cases of `ServicioDeOrganizacionTests` and `ServicioDeUsuariosTests`
   move to `tests/Ways.IntegrationTests` (the exact `ServicioDeOfertas.ActualizarAsync` precedent).
   Accounted for in slice 4's ~530-line estimate; task 4.7 owns it.
6. **U1-U8 (design's guarded-write conjunct enumeration, `design.md:383-398`) are transcribed to
   slice 4**, the slice that owns every one of those statements, per `mutation-proof-tests` rule 3
   ("up front, before any test is written"). Each conjunct is paired with its own task-level kill.
7. **The `sdd-tasks` 530-word size budget is overridden, registered rather than silently exceeded.**
   The binding project precedent is the archived stage-17/18/19a `tasks.md` shape (per-slice headers,
   binding verify criteria, the U-row conjunct table, per-task requirement links), which the launch
   prompt named as the structure to match and which `design.md:66-69` already recorded overriding for
   the same reason. A 530-word checklist cannot carry OD4/OD5/OD6, N1-N4, U1-U8 and the ZERO-SCHEMA
   criteria without dropping load-bearing content, and dropping it is what verify would then have to
   reconstruct by archaeology.
8. **V9 means "the projection adds no round trip", and for `GET /api/usuarios` the literal reading
   is false.** Measured on the merged slice-1 code: the three organization listings cost exactly
   **1** database command each; `GET /api/usuarios` costs **2**. The second one is the pagination
   `CountAsync` (`ServicioDeUsuarios.cs:70`) — usuarios is the **only paginated** listing — and it
   **predates this change**: it is emitted by code slice 1 never touched. The projection itself adds
   **zero** commands on all four endpoints; if it added one, the usuarios count would be 3. Task 1.7
   asserts exactly that (`1` for each organization listing, `2` for usuarios) and
   `CadaListadoCuestaExactamenteUnaIdaALaBase`'s doc-comment records the reason at the assertion.
   **The pagination is NOT changed to make a sentence true.** The sentence *"`GET /api/usuarios`
   MUST still execute a single database round trip"* (`specs/usuarios-tenant-scoping/spec.md:14-15`)
   and its scenario *"The tenant column costs no extra round trip"* (`:29-32`) are left
   **byte-identical** — deltas of this change are not edited mid-flight — and are **superseded by
   this reconciliation**, the same handling Reconciliación 1 gives OD6. Verify must read V9
   (`tasks.md`, `design.md:469`) through it: the property the criterion protects (no N+1, no second
   query per row, no extra round trip **caused by the projection**) is preserved and proven; only
   the absolute number for the one paginated listing differs.
9. **S1's `iff` holds for the platform-vs-tenant distinction and is FALSE for the D13 orphan.**
   The sentence *"`NombreTenant` MUST be `null` if and only if `IdTenant` is `null`"*
   (`specs/usuarios-tenant-scoping/spec.md:8-9`) reads as a biconditional over every row. The
   forward direction is what the API actually owes and is implemented and asserted: an account with
   no tenant (`IdTenant is null`) never carries a name, and the literal `"Plataforma"` is never
   fabricated server-side. The converse is **deliberately not true**: when the owning tenant is
   soft-deleted, `IdTenant` is **non-null** and `NombreTenant` is **null**, because D13 chooses to
   render the orphan as a visible anomaly instead of hiding the row — that is exactly what makes
   slice 1 correct without slice 4's cascade and therefore independently mergeable. A consumer may
   **not** read a null name as "this is platform staff"; `IdTenant` is the discriminator, which is
   why the web's `"Plataforma"` copy (task 2.7) keys off `IdTenant`, never off the name. The spec
   sentence and the *"Platform staff render as Plataforma"* scenario are left **byte-identical** —
   deltas of this change are not edited mid-flight — and are **superseded by this reconciliation**,
   the same handling Reconciliación 1 gives OD6 and Reconciliación 8 gives V9. Recorded in the code
   at the two places that state the contract (`Usuarios/Contratos.cs`,
   `ServicioDeUsuarios.NombreDeTenantAsync`), both of which name the two null cases instead of the
   `iff`, and asserted by `ElListadoDeUsuariosLlevaElTenantDeCadaCuentaYNuncaFabricaLaEtiquetaPlataforma`
   plus `UnaCuentaCuyoTenantFueDadoDeBajaNoTraeNombreDeTenantEnNingunoDeLosTresCaminos`.
10. **The orphan option's id suffix (`— (tenant 7)`) is NOT a violation of the "no raw owner id"
    sentence, and the label is deliberately left alone.** `tenant-organization`'s
    *"owner ids MUST NOT be displayed as the identity of the owner"* targets rows that HAVE an
    owner: the requirement is that a real owner is presented by its name, never by its number, and
    that is what every non-orphan row and every non-orphan option does. An orphan is the D13
    anomaly — the owning tenant is soft-deleted, so there is **no name to display** — and its
    filter option still needs a key the operator can tell apart from another orphan's. The id
    suffix is that handle, and it is the *anomaly's* rendering, not an owner identity: presenting
    it as an owner would require claiming the number identifies something the row can name, which
    is exactly what it cannot do. **The supporting argument is corrected in round 2**: the original
    wording said a bare `—` "would collapse two distinct orphan tenants into one indistinguishable
    option", and that is no longer true — `desempatarHomonimos` now resolves exactly that collision
    on its own, so a bare `—` would come out as `— (tenant 7)` / `— (tenant 8)` anyway. The reason
    the suffix is built into `etiquetaDeOpcionDeTenant` instead of being left to the generic
    tie-break is that the anomaly must be legible **even when there is only one orphan**: with a
    single orphan there is no collision to break, and a bare `—` would render an option the
    operator cannot tell from the empty/placeholder state. The conclusion is unchanged: the label
    stays as it is. The spec sentence is left **byte-identical** — deltas of this
    change are not edited mid-flight — and is read through this reconciliation, the same handling
    Reconciliación 1 gives OD6 and Reconciliación 9 gives S1. Task 2.13's assertion still holds
    unchanged: no **cell** presents an id as the owner's identity. *(judgment-day round 1, judge A
    SUGGESTION; deferred by the orchestrator with no code change.)*

## Binding Verify Criteria (all slices)

Carried from `design.md:451-473` and `state.yaml`'s ratified gate. **None may be relaxed by any
slice**, and every slice re-asserts V1-V6 (they are invariants of the whole stage, not of one PR).

1. **Zero new files** under `src/Ways.Infrastructure/Persistencia/Migraciones/` — the last migration
   is and stays `20260822002214_FiscalArcaEtapa19a.cs`.
2. `dotnet ef migrations has-pending-model-changes` **clean**.
3. `src/Ways.Infrastructure/Persistencia/InicializadorDeBaseDeDatos.cs` **untouched**
   (`git diff --exit-code`).
4. **Zero physical deletes**: repository scan for `ExecuteDelete`, `ExecuteDeleteAsync`, `Remove(`,
   `RemoveRange(` and `DELETE FROM` over `tenants`, `empresas`, `puntos_venta`, `usuarios`.
5. `src/Ways.Api/Seguridad/Politicas.cs` **untouched** — zero new policies; all four DELETEs reuse
   the policy of the group they already belong to.
6. `src/Ways.Api/Seguridad/ManejadorDeErrores.cs` **untouched** — no SQLSTATE branch, because none
   can fire (`db-error-backstops` N/A).
7. `IWaysDbContext.cs` gains **exactly one** member (`IModel Model { get; }`) and **zero**
   implementations change (`rg ": IWaysDbContext"` over `src/` and `tests/` returns zero matches).
8. **N3's golden is checked in and green**; any regeneration inside a PR is accompanied by a written
   classification decision for each changed line.
9. Each of the four list endpoints performs **exactly one** database round trip (`ContadorDeComandos`)
   — read through **Reconciliación 8**: the criterion is that the **projection** adds no round trip.
   `GET /api/usuarios` costs 2 because it is the only paginated listing and its `CountAsync`
   predates this change; the other three cost 1.
10. `InspectorDeUso` has **zero callers** in the slice-3 diff (`rg` over `src/`).
11. Mutation evidence recorded in the PR body for **every** U-row belonging to that slice; structural
    rows record the file/state/definition assertion instead of a runtime failure, **and say so**.
12. Domain / Application / Integration / Vitest suites green; `npm run build` (typecheck), `npm run
    lint` and `dotnet build Ways.slnx` clean.
13. **Zero** `CREATE`/`ALTER`/`DROP`/`INSERT`/`UPDATE`-DDL statements anywhere in the diff outside the
    guard's generated read-only `SELECT`.

## Test commands (a task is not done until its tests pass)

| Command | When |
|---|---|
| `dotnet build Ways.slnx` | after every production edit |
| `dotnet test tests/Ways.Domain.Tests/Ways.Domain.Tests.csproj` | slices 1, 3, 4 (non-regression) |
| `dotnet test tests/Ways.Application.Tests/Ways.Application.Tests.csproj` | slices 1, 3, 4 |
| `dotnet test tests/Ways.IntegrationTests/Ways.IntegrationTests.csproj` | slices 1, 4 |
| `npm --prefix src/Ways.Web run test` | slices 2, 5 |
| `npm --prefix src/Ways.Web run build` | slices 2, 5 — **this is also the typecheck** |
| `npm --prefix src/Ways.Web run lint` | slices 2, 5 |

> **Integration suite discipline (binding).** `Ways.IntegrationTests` requires Docker. **NEVER run
> two integration suites concurrently against the same Docker daemon** — the fixture sets a
> process-level environment variable and every class shares
> `[Collection("Ways.IntegrationTests secuencial")]`. Capture the `.trx` on any suspected flake.

## Per-slice delivery ritual (apply MUST follow this, per slice, in order)

1. Implement the slice's tasks on its own branch, cut from `main` (**stacked-to-main**).
2. Run every relevant test command above until **all green**.
3. Run the **`judgment-day`** protocol: two independent blind review agents judge the diff, verdicts
   are compared, confirmed issues are fixed, the diff is re-judged. **Iterate until a clean round**
   (no confirmed issues).
4. Only then create the PR (`branch-pr` skill, conventional commits, **no AI attribution, no
   `Co-Authored-By`**), with the slice's mutation evidence in the PR body (V11).
5. Merge. Then, and only then, start the next slice.

## Suggested Work Units

Merge order `1 → 2 → 3 → 4 → 5`. **Slices 1-2 (Part A) and 3-5 (Part B) are independent by design**:
Part A ships standalone value if Part B stalls, and slice 3 depends on nothing because it is inert.

| Unit | Goal | Likely PR | Depends on | Focused test command | Runtime harness | Rollback boundary |
|---|---|---|---|---|---|---|
| 1 | Projection API: four DTOs, correlated-subquery projections, one-round-trip proof | PR 1 | — | `dotnet test tests/Ways.IntegrationTests --filter "FullyQualifiedName~ProyeccionDeOrganizacionTests"` | Testcontainers Postgres 17, `ways_app` role | Revert. DTO fields disappear; the web slice is not merged yet |
| 2 | Projection web: mirrors, five pure helpers, name columns, counts, four filters, first Vitest files | PR 2 | 1 | `npm --prefix src/Ways.Web run test` | jsdom + RTL + `user-event`, `vi.mock('../api/cliente')` | Revert. The screens return to rendering ids |
| 3 | The guard, **inert**: `IModel`, `InventarioDeDependientes`, `InspectorDeUso`, N1-N3, rendering suite | PR 3 | — | `dotnet test tests/Ways.Application.Tests --filter "FullyQualifiedName~InventarioDeDependientesTests\|FullyQualifiedName~InspectorDeUsoTests"` | **No container** — the real Npgsql model over an unopened connection (`Modelo*Tests.cs` pattern) | Revert. Nothing calls it; the guard cannot have run |
| 4 | Deletion API: three routes, three `EliminarAsync`, cascade, minimums, `Usuario` guard, six 409s, N4, U1-U8, RLS, relocations | PR 4 | 1, 3 | `dotnet test tests/Ways.IntegrationTests --filter "FullyQualifiedName~BajasDeOrganizacionTests"` | Real Postgres 17 Testcontainer, `ways_app` (non-superuser, RLS-scoped), `RelojFijo` for the boundary pair | Revert removes three routes and one guard call. Rows already soft-deleted stay soft-deleted and hidden — a **pre-existing, supported state** |
| 5 | Deletion web: buttons, confirmation, `codigo`→copy, `react-async-state` discipline ×4, docs 09/10 | PR 5 | 2, 4 | `npm --prefix src/Ways.Web run test` | jsdom + RTL | Revert removes buttons. The API still works; nobody can press it |

**Pre-approved degradation**, in priority order: (1) slice 4 splits into `4a` (tenant + empresa +
cascade + minimums, U1-U6) and `4b` (punto de venta + the `Usuario` guard + relocations, U7-U8);
(2) slice 3 splits into `3a` (`IModel` + `InventarioDeDependientes` + N1-N3) and `3b`
(`InspectorDeUso` + rendering); (3) slice 2 splits into `2a` (names and counts) and `2b` (filters).
**Never degraded** (`state.yaml:409-415`): N1-N4, the two carve-out tests, the "one article blocks"
test, and the zero-physical-delete scan.

**Requirement tags used below.** `BO-Rn` = `specs/bajas-de-organizacion/spec.md` requirement *n* in
document order · `TO-Rn` = `specs/tenant-organization/spec.md` ADDED requirement *n* · `UT-Rn` =
`specs/usuarios-tenant-scoping/spec.md` ADDED requirement *n*.

| Tag | Requirement |
|---|---|
| BO-R1 | Deletion Is Always Logical, Never Physical |
| BO-R2 | The Pristine Discriminator Is A Strict Timestamp Comparison Against The Entity's Own CreatedAt |
| BO-R3 | In Use Means Anything The Customer Created Beyond The Provisioning Baseline |
| BO-R4 | The Dependent Set Is Discovered From EF Metadata, Never From A Hand-Maintained List |
| BO-R5 | Every Referencing Type Is Classified Into Exactly One Bucket Or The Build Fails |
| BO-R6 | There Are Exactly Two Carve-Outs, Each With A Written Reason And Its Own Test |
| BO-R7 | A Soft-Deleted Dependent Still Blocks |
| BO-R8 | A Shared-Catalog Row With A NULL Owner Does Not Block |
| BO-R9 | Cascade Is Bounded To The Organization Projection And Shares One Instant |
| BO-R10 | Structural Minimums Are Checked Before The Usage Guard, With Their Own Named Codes |
| BO-R11 | The Complete 409 Code Set Is Exactly Six Codes |
| BO-R12 | Deletion Never Becomes A Cross-Tenant Existence Oracle |
| TO-R1 | Organization Listings Project Owner Names, Never Raw Ids |
| TO-R2 | The Tenants Listing Carries Live Child Counts |
| TO-R3 | The Root Screens Filter By Owner Over The Already-Loaded List |
| TO-R4 | Platform Logical Deletion Surface For Tenant, Empresa And Punto De Venta |
| TO-R5 | Tenant Deletion Is The Only Writer Of EstadoTenant.Baja |
| UT-R1 | The Usuarios Listing Carries The Account's Tenant Identity |
| UT-R2 | Usuario Deletion Gains The Usage Guard After PoliticaDeRoles, Never Instead Of It |

`[P]` marks tasks that may run in parallel with the other `[P]` tasks of the same block. Everything
not marked `[P]` is sequential and blocks what follows it in its slice.

---

## Slice 1: Projection API — owner names and child counts (PR 1)

**Branch**: `feat/stage20-slice1-proyeccion-api`. **Start**: `main`. **Finish**: the four listing
DTOs carry owner names and counts, projected as correlated subqueries inside the existing `Select`,
each list endpoint still costing exactly one round trip. No web consumer yet (slice 2). **Depends
on**: nothing. **Estimate**: ~390 lines. **Rollback**: revert — the DTO fields disappear and the web
slice is not merged yet. **Skills**: `dto-contract-honesty` (every added field has exactly one fate),
`mutation-proof-tests` (rules 12b/12c on the DTO readback), `work-unit-commits`.
**`db-error-backstops`: N/A** (no SQLSTATE can fire on a read projection).

**Binding note (D13).** `Empresa` and `PuntoVenta` carry **no navigation properties**
(`Empresa.cs:9-26`), so owner names can only be **correlated scalar subqueries** — never
`e.Tenant!.Nombre`. This also removes the INNER-JOIN-drops-the-row trap for free. The projected name
is `string?` **on purpose**: an orphan renders as an anomaly instead of vanishing, which is what
decouples slice 1 from slice 4's cascade and keeps Part A independently mergeable.

- [x] 1.1 Modify `src/Ways.Application/Organizacion/Contratos.cs` — `TenantListado` gains
  `int CantidadEmpresas, int CantidadPuntosVenta, int CantidadUsuarios`; `EmpresaListado` gains
  `string? NombreTenant`; `PuntoVentaListado` gains `string? NombreTenant, string? RazonSocialEmpresa`.
  **`dto-contract-honesty`**: each field's single consumer is a column in slice 2; the pre-existing
  `EmpresaListado.IdTenant` and `PuntoVentaListado.IdTenant`/`.IdEmpresa` are **not** deleted — they
  stop being rendered and become the **filter keys**, named here so a reviewer does not read them as
  newly dead. *(TO-R1, TO-R2; design D13, Interfaces table)*
- [x] 1.2 Modify `src/Ways.Application/Usuarios/Contratos.cs` — `UsuarioListado` gains
  `int? IdTenant` and `string? NombreTenant`. **S1 is binding as read through Reconciliación 9**,
  not as a literal `iff`: what the API owes is the forward direction (`IdTenant is null` ⇒
  `NombreTenant is null`, and the literal `"Plataforma"` **MUST NOT** be fabricated server-side —
  that copy is the web's job, slice 2 task 2.7). The converse does **not** hold: the D13 orphan has
  a non-null `IdTenant` and a null `NombreTenant`. *(UT-R1; design D14; Reconciliación 9)*
- [x] 1.3 Modify `ServicioDeOrganizacion.ListarTenantsAsync` — three correlated `Count()` subqueries
  in the same `Select`. The `"BajaLogica"` filter applies inside the LINQ tree, so deleted children
  are excluded **for free** (assert it, do not assume it, task 1.9). `CantidadUsuarios` counts only
  usuarios of that tenant; platform staff (`IdTenant is null`) are counted under no tenant.
  *(TO-R2; design D13)*
- [x] 1.4 Modify `ServicioDeOrganizacion.ListarEmpresasAsync` — correlated scalar subquery for
  `NombreTenant` (no navigation exists; fact 1). *(TO-R1)*
- [x] 1.5 Modify `ServicioDeOrganizacion.ListarPuntosVentaAsync` — two correlated scalar subqueries
  (`NombreTenant`, `RazonSocialEmpresa`). *(TO-R1)*
- [x] 1.6 Modify `ServicioDeUsuarios`'s two listing projections (`:76-79`, `:90-92`) — `IdTenant`
  passthrough plus the correlated `NombreTenant` subquery, both nullable. *(UT-R1)*
- [x] 1.7 [P] Integration test: **exactly one** database round trip for each of
  `GET /api/plataforma/tenants`, `GET /api/empresas`, `GET /api/puntos-venta`, `GET /api/usuarios`,
  via `ContadorDeComandos` (the `VentasCheckoutTests:930` precedent). Four assertions, one per
  endpoint. **Mutation**: split one projection into a second query, watch the count go to 2, revert.
  *(TO-R1, TO-R2, UT-R1; verify criterion V9)*
- [x] 1.8 [P] Integration test: the empresas listing carries the owning tenant's name and the puntos
  de venta listing carries **both** owner names, with a **sibling row of another tenant** present in
  every fixture (`mutation-proof-tests` rule 12c) so a projection that ignores the correlation is
  killed. *(TO-R1)*
- [x] 1.9 [P] Integration test: counts reflect surviving children only — logically delete one empresa
  and one usuario and assert the counts drop; assert a platform account with `id_tenant IS NULL` is
  counted under **no** tenant. *(TO-R2)*
- [x] 1.10 [P] Integration test — **the orphan case**: an empresa whose tenant row is soft-deleted
  still appears in the listing with `nombreTenant = null`. This is what makes slice 1 correct
  **without** slice 4 and must not be weakened into an assumption. *(TO-R1; design D13)*
- [x] 1.11 [P] Integration test: every **positional** field of the four listing DTOs read back with
  **pairwise-distinct** values (`mutation-proof-tests` rule 12b), so a swapped record positional
  argument is killed rather than surviving on equal values. *(TO-R1, TO-R2, UT-R1)*
- [x] 1.12 [P] Integration test: a tenant usuario carries `idTenant = <its tenant>` and its tenant's
  name; the seeded `root` account carries `idTenant = null` **and** `nombreTenant = null` — the API
  never sends the literal `"Plataforma"`. *(UT-R1, S1)*
- [x] 1.13 [P] Integration test (regression, unchanged behaviour): a tenant admin calling
  `GET /api/usuarios` receives only their own tenant's rows and never another tenant's name.
  *(UT-R1)*
- [x] 1.14 GATE GUARD + non-regression — re-assert V1-V6 and V13 on this slice's diff (no migration
  file, `has-pending-model-changes` clean, `InicializadorDeBaseDeDatos.cs` / `Politicas.cs` /
  `ManejadorDeErrores.cs` untouched, zero physical deletes, zero DDL). Full Domain + Application +
  Integration suites green; `dotnet build Ways.slnx` clean.
- [x] 1.15 `judgment-day` round: two blind review agents, fix confirmed findings, re-judge to a clean
  round.
- [x] 1.16 Open PR 1 `feat/stage20-slice1-proyeccion-api` (`branch-pr`, conventional commits, no AI
  attribution), record the mutation evidence for tasks 1.7-1.12 in the PR body (V11), merge to `main`
  after the clean round. **PR #165 merged to `main` as commit `5f0018f`.**


### Mutation evidence — slice 1 (`mutation-proof-tests` rule 2, produced, not reasoned)

Every mutation below was applied to the tree, the named test was run and observed RED, and the
mutation was then reverted (`git checkout --`) and the test observed GREEN again. Commands:
`dotnet test tests/Ways.IntegrationTests/Ways.IntegrationTests.csproj --filter "FullyQualifiedName~<test>"`.

| # | Task | Clause under test | Mutation applied | Observed failure |
|---|---|---|---|---|
| M1 | 1.7 | The owner name travels inside the same `Select` | `ListarEmpresasAsync` resolves `NombreTenant` from a second `db.Tenants.ToDictionaryAsync` query | `CadaListadoCuestaExactamenteUnaIdaALaBase` — `Assert.Equal() Failure: Values differ` (1 → 2 commands) |
| M2 | 1.8 | The correlation `t.Id == e.IdTenant` | `db.Tenants.Select(t => t.Nombre).FirstOrDefault()` — correlation dropped | `LosListadosDeEmpresasYPuntosDeVentaLlevanLosNombresDeSusDuenios` — `Assert.Equal() Failure: Strings differ` (both empresas report the same tenant) |
| M3 | 1.9 | The `"BajaLogica"` filter runs inside the correlated `Count` | `db.Usuarios.IgnoreQueryFilters(new[] { "BajaLogica" }).Count(...)` | `LosContadoresDelTenantCuentanSoloHijosVivosYNuncaAlPersonalDePlataforma` — `Expected: 1 / Actual: 2`. Note for the reviewer: EF applies `IgnoreQueryFilters` at **query** level, so the deleted *empresa* is the first count that stops dropping — the test still dies on exactly the clause it names (deleted children must not count), just on the first of the three |
| M3b | 1.9 | The tenant correlation of `CantidadUsuarios` | `db.Usuarios.Count()` — correlation dropped, so platform staff would be counted | same test — `Assert.Equal() Failure: Values differ` |
| M4 | 1.10 | The owner name is a correlated **subquery**, not a JOIN | `ListarEmpresasAsync` rewritten as `db.Empresas.Join(db.Tenants, …)` | `UnaEmpresaCuyoTenantFueDadoDeBajaSigueApareciendoConNombreDeTenantNulo` — `Assert.Single() Failure: The collection did not contain any matching items`; the orphan empresa vanished from the listing, which is precisely the trap D13 exists to avoid |
| M5 | 1.11 | Positional argument order of `TenantListado` | `CantidadEmpresas` and `CantidadPuntosVenta` swapped in `ProyeccionDeTenant` | `CadaCampoPosicionalDeLosCuatroListadosSeLeeDeVueltaConValoresDistintos` — `Expected: 2 / Actual: 3` |
| M6 | 1.12 | The API never fabricates the `"Plataforma"` literal (S1/D14) | `… .FirstOrDefault() ?? "Plataforma"` in `ServicioDeUsuarios.ListarAsync` | `ElListadoDeUsuariosLlevaElTenantDeCadaCuentaYNuncaFabricaLaEtiquetaPlataforma` — `Assert.Null() Failure: Value is not null / Actual: "Plataforma"` |
| M7 | 1.15 (ronda 1, hallazgo 1) | The **explicit** `t.DeletedAt == null` of the usuarios subquery — `IgnoreQueryFilters` is query-level, so `incluirEliminados=true` also stripped the filter from the correlated subquery | `.Where(t => t.Id == u.IdTenant && t.DeletedAt == null)` → `.Where(t => t.Id == u.IdTenant)` | `UnaCuentaCuyoTenantFueDadoDeBajaNoTraeNombreDeTenantEnNingunoDeLosTresCaminos` — `Assert.Null() Failure: Value is not null / Actual: "Huerfano-A-252cafab"`, on the `incluirEliminados=true` path only (the default listing and the detail stayed green — that is the discrepancy the finding named) |
| M8 | 1.15 (ronda 1, hallazgo 2) | `ServicioDeUsuarios.NombreDeTenantAsync` — the source of `NombreTenant` on **every** non-listing path (`ObtenerAsync`, the `POST` 201 body, the `PUT` body) | body replaced by `return null;` (keeping one instance read so CA1822 does not mask the mutant) | `ElAltaElDetalleYLaEdicionDeUsuarioDevuelvenLosDiezCamposProyectados` — `Assert.Equal() Failure: Strings differ / Expected: "Detalle-usuario-8dc6ccae" / Actual: null`, on the **201 body** of `POST /api/usuarios`. Before this round the same mutation left the entire suite green |
| M9 | 1.15 (ronda 1, hallazgo 5) | The `e.Id == p.IdEmpresa` correlation of `RazonSocialEmpresa` — needs a sibling of the **same owner** (`mutation-proof-tests` rule 12c) | `.Where(e => e.Id == p.IdEmpresa)` → `.Where(e => e.IdTenant == p.IdTenant)` | `LosListadosDeEmpresasYPuntosDeVentaLlevanLosNombresDeSusDuenios` — `Assert.Equal() Failure: Strings differ / Expected: "Norte Anexo SRL" / Actual: "Norte SRL"`. Observed RED when run; the kill is NOT claimed as universal — an unordered `FirstOrDefault` owes nobody the same row on every correlated evaluation, so in theory the mutant could hit both empresas correctly. Softened here to match the test doc-comment (R2-7) |

**Task 1.13 carries NO mutation evidence, and that is stated rather than papered over.** It is a
**regression** test over behaviour this slice does not add: tenant scoping on `GET /api/usuarios`
comes from the EF `"Tenant"` query filter plus RLS, both pre-existing. Mutating them would be
mutating infrastructure this slice never touches, which `mutation-proof-tests` rule 1 explicitly
excludes ("if you cannot name a clause this change introduces, it is ordinary coverage").

**Two test defects were found by running the mutations and were fixed, not rationalised:**

1. `Assert.True(tenant.Id > 4)` in task 1.11 was **order-dependent**: on a container where only
   that test ran, the tenant id was 3 and the assertion failed for a reason unrelated to any
   mutation. Fixed by seeding four **deliberately unbalanced** filler tenants first (`TenantsDeRelleno`, defined as the largest seeded counter) (different
   numbers of empresas, puntos de venta and usuarios each), which desynchronises the four identity
   sequences. That is what makes `id`, `idTenant` and `idEmpresa` pairwise distinct **by
   construction** instead of by luck — the exact condition rule 12b needs to kill a swap.
2. The first draft of task 1.12 asserted S1's `iff` (`NombreTenant is null` ⟺ `IdTenant is null`)
   over **every** row, and went red against the orphan row seeded by task 1.10. The `iff` is true
   of the platform/tenant distinction but **not** of the orphan, which D13 deliberately renders
   with a null name. The assertion now states the direction the API actually owes (no account
   without a tenant carries a name, and no row carries the literal) and the doc-comment records
   why the converse is not claimed.

### Slice 1 delivery notes

- **BUDGET OVERFLOW, REPORTED RATHER THAN ABSORBED — AND THE OWNER RULED IT SHIPS AS ONE PR.**
  The figures below are measured with `git diff main --stat -- src tests`; the `openspec/` artifacts
  are excluded from the threshold. The estimate was **~390** and the operative budget **800** (OD1).
  **The estimate was wrong, not the implementation.**

  **Orchestrator decision, recorded verbatim:** *slice 1 ships as ONE PR at 1285 changed lines
  against the 800-line budget (OD1), because production is only ~176 lines and the ~1085 test lines
  are the evidence round 1's review demanded for exactly those 176 — splitting would separate the
  proof from the claim and buy two more review cycles for no gain in review quality. The owner was
  told the corrected number and offered the split.*

  Measurement history, so no number in this file is stale:
  - **After round 1** (the figures the decision above was taken on): **1285 changed lines**
    (1252 insertions + 33 deletions), `ProyeccionDeOrganizacionTests.cs` at **1076 lines** with
    **11 `[Fact]` methods**.
  - **After round 2** (final): **1420 changed lines** (1386 insertions + 34 deletions), of which
    production is **217** (185 + 32) across four files and tests are **1203**;
    `ProyeccionDeOrganizacionTests.cs` is **1194 lines** with **12 `[Fact]` methods**, zero
    deletions. The delta is round 2's own corrections: the fourth explicit `DeletedAt == null`
    predicate set, the organization orphan readback test, and the reconciliation/doc-comment
    honesty fixes. The decision does not change — the ratio it rests on (small production surface,
    large demanded evidence) only got more pronounced.

  The task list asks for tests one of which (1.11) must read back **35 positional fields** across
  four DTOs, and each of which must carry its named clause and its mutation rationale in the file.
  **No split point was pre-authorized for slice 1**, so this was an orchestrator decision, not an
  apply-phase one: the natural cut, had one been wanted, was production + `ServicioDeOrganizacion`
  tests (1.7-1.10) in PR 1a and the `UsuarioListado` projection + tests (1.11-1.13) in PR 1b.
  Trimming the doc-comments to fit was rejected: they are exactly the content a reviewer needs to
  check the mutation argument.

- **Detail/edit endpoints re-project after writing.** `Obtener*`/`Actualizar*`/`Suspender`/
  `Reactivar` return the same listing records, and the counters and owner names do not live on the
  entity, so the only place they exist is the query. `ObtenerTenantAsync`/`ObtenerEmpresaAsync`/
  `ObtenerPuntoVentaAsync` still cost **one** round trip (they project instead of loading the
  entity, and validate scope on the projected `IdTenant`, preserving `BuscarX`'s 404-then-ADR-8
  order); the four write paths cost one extra read. The single-round-trip budget of TO-R1/UT-R1 is
  about the **listings**, which are what scale with row count. Alternative rejected: leaving the
  new fields at `0`/`null` on the detail paths — that is exactly the accepted-and-dropped shape
  `dto-contract-honesty` forbids.
- **`WaysApiFixture.CrearContextoDeAplicacion` gained an optional `params IInterceptor[]`.** Zero
  call sites changed; it is what lets task 1.7 count commands on a service call.
- **No web file was touched** — `tipos.ts` mirrors land in slice 2, and adding fields to the JSON
  payload is backward compatible for the screens as they stand.

- **Judgment-day, ronda 1 (task 1.15): six confirmed findings, six fixes, three new mutations.**
  (1) `IgnoreQueryFilters(["BajaLogica"])` is applied at **query** level, so `incluirEliminados=true`
  also stripped the soft-delete filter from the correlated tenant-name subquery: the same account
  showed a soft-deleted tenant's name on that path and `null` on the other two. Fixed by making the
  subquery's predicate explicit (`t.DeletedAt == null`) instead of leaning on the ambient filter;
  the `IgnoreQueryFilters` call and its ADR-6 comment are untouched. Killed by M7 on all three
  paths. (2) Every non-listing response path shipped untested — `return null;` in
  `NombreDeTenantAsync` left the whole suite green. Three new readback tests now cover
  `Obtener/Actualizar/Suspender/Reactivar` of tenant, `Obtener/Actualizar` of empresa and punto de
  venta, and the `POST` 201 / `GET {id}` / `PUT` bodies of usuario, with pairwise-distinct values
  (rule 12b). Killed by M8. (3) The three re-projections after `SaveChangesAsync` used `FirstAsync`,
  which turns a row that became invisible between write and re-read into a 500 while the sibling
  read paths return a domain 404; all three now use `FirstOrDefaultAsync` + `ErrorDominio.NoEncontrado`
  — this matters for slice 4, which adds concurrent deleters on exactly those rows. (4) V9 is not
  literally satisfied for usuarios: recorded as **Reconciliación 8**, no code change, the spec file
  left byte-identical. (5) `RazonSocialEmpresa`'s correlation had no deterministic kill (one empresa
  per tenant); task 1.8's fixture now seeds a second empresa **of the same tenant** with its own
  punto de venta. Killed by M9. (6) The `NombreTenant` doc-comments claimed null **iff** `IdTenant`
  is null, which is false for the D13 orphan; both `Contratos.cs` and `NombreDeTenantAsync` now
  state the two real cases. The "never fabricate `Plataforma`" rule is unchanged.

- **Judgment-day, ronda 2 (task 1.15, FINAL round): seven items, seven fixes, four mutations RUN
  and all four SURVIVED — recorded as survivors, not dressed up as kills.** (R2-1) The false `iff`
  was still binding in the artifacts even though round 1 fixed the
  two code doc-comments: registered as **Reconciliación 9** and task 1.2's wording now points at it;
  `specs/usuarios-tenant-scoping/spec.md` is left **byte-identical**, the same handling
  Reconciliación 1 and 8 give their superseded sentences. (R2-2) Round 1's `t.DeletedAt == null`
  hardening was applied only to `ServicioDeUsuarios.ListarAsync`; the three owner-name subqueries of
  `ServicioDeOrganizacion` and `ServicioDeUsuarios.NombreDeTenantAsync` still leaned on the ambient
  filter. All four now carry the explicit predicate, with the rationale written once at
  `ProyeccionDeEmpresa`. **NO mutation evidence is claimed for any of the four predicates, and the
  new test says so in its own doc-comment.** The four mutations were nevertheless RUN
  rather than reasoned about, per `mutation-proof-tests` rule 2 — each predicate deleted in turn,
  rebuilt, `ProyeccionDeOrganizacionTests` run alone, **12/12 green every time**, then reverted:
  `t.Id == e.IdTenant && t.DeletedAt == null` → `t.Id == e.IdTenant` **SURVIVED**;
  `t.Id == p.IdTenant && t.DeletedAt == null` → `t.Id == p.IdTenant` **SURVIVED**;
  `e.Id == p.IdEmpresa && e.DeletedAt == null` → `e.Id == p.IdEmpresa` **SURVIVED**; and in
  `NombreDeTenantAsync`, `t.Id == id && t.DeletedAt == null` → `t.Id == id` **SURVIVED** (the
  `GET /api/usuarios/{id}` arm of
  `UnaCuentaCuyoTenantFueDadoDeBajaNoTraeNombreDeTenantEnNingunoDeLosTresCaminos` is the path that
  clause serves, and it stayed green). That is the
  expected outcome and the reason the finding was defence-in-depth, not a live defect: no
  organization path strips `"BajaLogica"` today, so the ambient filter produces the identical
  result and the clause has nothing of its own to prove yet. They are defence-in-depth for slice 4's
  deletion writers, and the new
  `LosHuerfanosDeOrganizacionSeRindenIgualPorElListadoYPorElDetalle` pins the expected D13 behaviour
  (orphan visible with a null owner name, by listing AND by detail, for all three subqueries) so
  slice 4 has something to mutate against. (R2-3) `ProyectarTenantAsync` had become byte-identical to
  the public `ObtenerTenantAsync` after the C3 fix; the helper is deleted and the two write paths
  call the public method, behaviour identical. (R2-4) The stale line counts in `state.yaml` and in
  the budget note are replaced by measured figures and the orchestrator's verbatim one-PR decision.
  (R2-5) `Assert.True(tenant.Id > 4)` passed by a margin of one that came from the production seed
  the test never establishes: both occurrences now assert `Assert.DoesNotContain(tenant.Id,
  ContadoresSembrados)`, and the bound is guaranteed by the test's own seeding — `TenantsDeRelleno`
  is defined as the largest seeded counter, so the tenant under test lands strictly above all three
  by monotonicity of the identity sequence. (R2-6) The organization readback test's doc-comment
  claimed a mutation ("replacing the projection body with constants survived the whole suite") that
  is false — `ProyeccionDeTenant` is the same expression object task 1.11 already reads back through
  the listing. Corrected to its real added value: **endpoint wiring**, proving the four non-listing
  routes return that projected record and not one built differently. No mutation is claimed and the
  M table still lists none for it. (R2-7) M9's "cualquiera sea la fila que elija la base" was
  stronger than SQL semantics guarantee for an unordered `FirstOrDefault`; softened to what was
  actually observed (RED on this engine), with the theoretical gap named.

- **CARRIED FORWARD TO SLICE 4, deliberately not patched in round 2.** Round 1's fix (3) made the
  re-projection after `SaveChangesAsync` return a domain **404** instead of a 500 when the row went
  invisible between write and re-read (`ServicioDeOrganizacion.ObtenerTenantAsync` as called by
  `ActualizarTenantAsync`/`CambiarEstadoTenantAsync`, and the empresa/PV equivalents). That means a
  caller can receive `404` for a write that **did** persist. It is **unreachable today** — no
  soft-delete writer exists for tenants, empresas or puntos de venta — and the correct fix is a
  **transaction boundary around write + re-projection**, not a patch to the re-read. **Slice 4 owns
  it**: its design must decide whether the write and its re-projection share one transaction (so the
  response can never contradict what was committed) or whether the write paths return the entity
  they already hold instead of re-reading. Task 4.x must close this explicitly and say which of the
  two it chose; leaving it as-is is not a valid outcome once the deleters exist.

---

## Slice 2: Projection web — names, counts and filters (PR 2)

**Branch**: `feat/stage20-slice2-proyeccion-web`. **Start**: PR 1 merged. **Finish**: the four root
screens render owner **names** and counts, never raw owner ids; three tenant filters and one empresa
filter operate over the already-loaded list; the **first** Vitest files for these four screens exist.
**Depends on**: slice 1 (the DTO fields). **Estimate**: ~430 lines. **Rollback**: revert — the
screens return to rendering ids. **Skills**: `web-descriptor-tests` (colocated tests for the pure
helpers), `dto-contract-honesty` (each mirrored field is consumed by exactly one column or filter),
`work-unit-commits`.

**Binding note (D15).** Filter option sets are derived from the **already-loaded rows**, never from a
second fetch: `GET /api/plataforma/tenants` is `Politicas.SoloPlataforma` while `Empresas.tsx` and
`PuntosVenta.tsx` are reachable by a tenant admin under `GestionDeOrganizacion`, so a fetch would 403
for exactly the users the screen was built for. Deriving from the rows also makes an empty option set
impossible by construction, and satisfies S5 (a filter can never disclose an out-of-scope tenant).

- [x] 2.1 Modify `src/Ways.Web/src/api/tipos.ts` — mirror the four DTO shapes from slice 1, with
  `nombreTenant`/`razonSocialEmpresa`/`idTenant` nullable exactly as the server declares them.
  *(TO-R1, TO-R2, UT-R1)*
- [x] 2.2 Modify `src/Ways.Web/src/api/organizacion.ts` — five **pure** helpers, no React, no fetch:
  `opcionesDeTenant`, `opcionesDeEmpresa` (narrowed by the selected tenant), `filtrarPorTenant`,
  `filtrarPorEmpresa`, `etiquetaDeTenant` (`null` → the literal `"Plataforma"`). *(TO-R3, UT-R1;
  design D14, D15)*
- [x] 2.3 Modify `src/Ways.Web/src/api/usuarios.ts` — mirror `UsuarioListado`'s two new fields. The
  `eliminar` call already exists and is **not** touched in this slice. *(UT-R1)*
- [x] 2.4 Modify `src/Ways.Web/src/paginas/Tenants.tsx` — three count columns (empresas, puntos de
  venta, usuarios). *(TO-R2)*
- [x] 2.5 Modify `src/Ways.Web/src/paginas/Empresas.tsx` — tenant **name** column replacing the raw
  integer at `:156`; tenant filter over the loaded list. *(TO-R1, TO-R3)*
- [x] 2.6 Modify `src/Ways.Web/src/paginas/PuntosVenta.tsx` — tenant name and empresa razón social
  columns replacing the two integers at `:216-217`; tenant filter **and** empresa filter, where
  selecting a tenant **narrows** the empresa options and **clears** an empresa selection that no
  longer belongs to it. *(TO-R1, TO-R3; design D15)*
- [x] 2.7 Modify `src/Ways.Web/src/paginas/Usuarios.tsx` — tenant column rendering the tenant name,
  or the literal **"Plataforma"** when `idTenant === null` (never an empty cell); tenant filter.
  *(UT-R1, TO-R3; design D14)*
- [x] 2.8 [P] Create `src/Ways.Web/src/api/organizacion.test.ts` — **`web-descriptor-tests`**: one
  case per helper branch — `opcionesDeTenant` dedup + ordering + the `null` option,
  `opcionesDeEmpresa` narrowing to the selected tenant, `filtrarPorTenant`/`filtrarPorEmpresa`
  including the "no selection" identity case, `etiquetaDeTenant`'s null branch. *(TO-R3, UT-R1)*
- [x] 2.9 [P] Create `src/Ways.Web/src/paginas/Tenants.test.tsx` — the three counts render from the
  DTO, with pairwise-distinct values so a column swap is killed. *(TO-R2)*
- [x] 2.10 [P] Create `src/Ways.Web/src/paginas/Empresas.test.tsx` — the tenant **name** renders (not
  the id); selecting a tenant narrows the rendered rows **with no additional network request**
  (assert the mocked client's call count); clearing the filter restores the full loaded list.
  *(TO-R1, TO-R3)*
- [x] 2.11 [P] Create `src/Ways.Web/src/paginas/PuntosVenta.test.tsx` — both owner names render;
  selecting a tenant narrows the empresa select **and** clears an empresa that no longer belongs to
  it; the empresa filter narrows the rows. *(TO-R1, TO-R3)*
- [x] 2.12 [P] Create `src/Ways.Web/src/paginas/Usuarios.test.tsx` — `"Plataforma"` renders for
  `idTenant === null` and the tenant name renders otherwise; the tenant filter narrows the rows; a
  single-tenant dataset offers exactly one tenant option (S5). **The usuarios filter operates on an
  unpaginated 25-row window** (`GET /api/usuarios` default `tamanio` 25, no pager rendered,
  `pagina.total` unused): it filters what is on screen, not the tenant's users, so a tenant with
  users past row 25 shows fewer rows than it has. Pre-existing truncation, made more visible by the
  filter, **left in the code deliberately** — server-side pagination is already deferred with a
  reopen condition in `state.yaml` — and named here rather than hidden (judgment-day round 1,
  judge A WARNING, deferred by the orchestrator). *(UT-R1, TO-R3)*
- [x] 2.13 [P] Assertion across the four screen tests: **no cell presents `idTenant` or `idEmpresa`
  as the owner's identity** — the raw ids survive only as `<select value>` filter keys.
  *(TO-R1; Success Criterion "no raw owner id is displayed")*
- [x] 2.14 GATE GUARD + non-regression — `npm --prefix src/Ways.Web run test`,
  `npm --prefix src/Ways.Web run build` (typecheck) and `npm --prefix src/Ways.Web run lint` all
  clean; re-assert V1-V6 on this slice's diff (a web-only slice must still touch zero migrations and
  zero backend guard files).
- [x] 2.17 **Tenant selector on user creation — owner-reported mid-slice, bounded addition.** The
  owner hit a real defect while testing: as a platform actor, creating a user with rol Admin from
  `Usuarios.tsx` always failed with the backend 400 `tenant_requerido` because the create form had
  no way to choose a tenant. The server already supported it (`CrearUsuario` accepts
  `int? IdTenant`, `Contratos.cs:43-49`) and the web mirror had dropped the field — a
  **`dto-contract-honesty`** violation on the web side. Delivered: (a) `tipos.ts` mirrors
  `idTenant: number | null` on `CrearUsuario`; `ActualizarUsuario` is left alone because the server
  record does **not** accept a tenant (`Contratos.cs:51-55`) and inventing one would be the same
  violation in reverse; (b) a tenant `<select>` in the create form, rendered **only** for a platform
  actor (`esNuevo && ofreceTenant`) — a tenant admin never sees a tenant list (S5) and sends
  `idTenant: null`, which `ServicioDeUsuarios.CrearAsync` ignores in favour of `Actor.IdTenant`;
  (c) options derived from the **already-loaded rows** via the existing `opcionesDeTenant(filas)`,
  minus the platform token, so **no second fetch** was added and D15 still holds; (d) a client-side
  mirror of `PoliticaDeRoles.ValidarConsistenciaDeRolYAlcance` for guidance only — rol Root disables
  the selector and clears `idTenant` **in state**, any other rol makes it `required`; the server
  stays the authority and its 400 still surfaces through the existing error path, untouched;
  (e) **`react-async-state`** rule 5 — the selector is inert while the list is loading and while a
  save is in flight. Five mutations run for real (M14-M18 below). *(UT-R1; design D15, spec S5)*
  **Page-size gap closed as a narrow web-only follow-up on the same branch.** (c) above left a known
  gap, stated in tasks.md and state.yaml: deriving the create selector's options from the
  already-loaded rows meant a platform actor could only assign a tenant with a user on the current
  page (`tamanio` 25). Closed by fetching the full tenant universe via
  `clienteDeOrganizacion.listarTenants()` (`GET /plataforma/tenants`, `Politicas.SoloPlataforma`) —
  gated **strictly** on `esPlataforma`, so a tenant admin never calls it (S5) — while the **filter**
  option set (D15) is left untouched, still derived from the loaded rows with zero second fetch.
  No estado filtering is applied to the fetched list — the server is the authority on business
  rules — and results are sorted by name. `react-async-state` rule 5 still gates the selector, now
  on the tenant fetch's own loading flag (`tenantsDePlataformaCargando`), not the row list's.
  One mutation run for real, M19 below (dropping the `esPlataforma` guard on the new fetch),
  observed RED on the tenant-admin test, then reverted and observed GREEN again; M14-M18 stay
  valid unmodified. *(UT-R1; design D15, spec S5)*
- [x] 2.15 `judgment-day` round to a clean round.
- [x] 2.16 Open PR 2 `feat/stage20-slice2-proyeccion-web`, merge to `main` after the clean round.

### Mutation evidence — slice 2 (`mutation-proof-tests` rule 2, produced, not reasoned)

Every mutation below was applied to the working tree, the named suite was run, the result was
observed, and the mutation was then reverted and the suite observed GREEN again. Command:
`npx vitest run <file>` from `src/Ways.Web`. **Branch as delivered**:
`feat/stage20-slice2-web-relaciones` (the launch prompt's name; `tasks.md` above still records the
planning name `feat/stage20-slice2-proyeccion-web` — same slice, different branch label).

| # | Task | Clause under test | Mutation applied | Observed result |
|---|---|---|---|---|
| M1 | 2.2, 2.5, 2.6, 2.7 | `etiquetaDeTenant` discriminates on `idTenant`, never on the name (Reconciliación 9) | body replaced by `return fila.nombreTenant ?? ETIQUETA_PLATAFORMA` | **KILLED, 4 tests across 4 files** — `un huérfano … NO se rinde como plataforma`: `expected 'Plataforma' to be '—'`; plus the orphan test of `Empresas`, `PuntosVenta` and `Usuarios` |
| M2 | 2.2, 2.7 | `claveDeTenant` gives platform staff a token no `String(idTenant)` can produce | `return String(idTenant)` | **KILLED, 4 tests** — `expected [ '9', 'null' ] to deeply equal [ 'sin-tenant', '9' ]`, and the "Plataforma" homonym filter test returned the tenant row instead of the staff row |
| M3 | 2.2, 2.6 | `opcionesDeEmpresa` narrows to the selected tenant (D15) | `for (const fila of filas)` — the `filtrarPorTenant` call dropped | **KILLED, 3 tests** — `expected [ '11', '10', '20' ] to deeply equal [ '11', '10' ]` |
| M4 | 2.6 | `cambiarFiltroDeTenant` CLEARS the empresa state, not just its rendering | body replaced by `setFiltroEmpresa((prev) => prev)` | **SURVIVED at first**, then KILLED after the test was re-routed below the confound. The confound is real and named in the test: the derived `empresaVigente` fallback already blanks the `<select>`, so asserting the blank proves nothing. The discriminating observation is that returning the tenant filter to "Todos" must NOT resurrect the foreign empresa — `expected [ 'PV Este' ] to deeply equal [ 'PV Centro', 'PV Anexo', 'PV Este' ]` |
| M5 | 2.4, 2.9 | The three count columns are not interchangeable | `cantidadEmpresas` and `cantidadPuntosVenta` swapped in the `<td>`s | **KILLED** — `rinde los tres contadores de cada tenant en su propia columna` |
| M6 | 2.5 | `Empresas` actually applies the tenant filter | `const visibles = items` | **KILLED, 2 tests** — `expected [ 'Sur SRL', 'Sur Anexo SA', …(1) ] to deeply equal [ 'Este SRL' ]` |
| M7 | 2.5, 2.13 | The empresas tenant column renders the NAME, not the id | `<td>{e.idTenant}</td>` | **KILLED, 3 tests** including the task-2.13 no-raw-id assertion |
| M8 | 2.7 | `Usuarios` actually applies the tenant filter | `const visibles = filas` | **KILLED, 3 tests** — `expected [ 'vendedor.sur', …(2) ] to deeply equal [ 'vendedor.este' ]` |
| M9 | 2.6 | The empresa column marks a missing razón social as an anomaly, not as an id | `?? String(p.idEmpresa)` | **KILLED** — the orphan PV test |
| M10 | — | ALL generation guards of `Empresas.tsx` deleted at once | every `generacion.current` check removed (0 left) | **SURVIVED, 6/6 green — recorded as a survivor, not dressed up as a kill.** See the note below: on the three organization screens the stale-read window is closed by `react-async-state` rule 9 (nothing can supersede an in-flight operation), so the gate is defence-in-depth there and has nothing of its own to prove |
| M11 | 2.7 | The generation guard of `Usuarios.cargar` — the ONE reachable stale-read window in this slice | `if (generacion.current !== token) return` deleted before `setPagina` | **KILLED** — `una respuesta de búsqueda vieja que aterriza tarde no pisa a la nueva`: `expected [ 'vendedor.sur', 'vendedor.este' ] to deeply equal [ 'staff' ]` |
| M12 | 2.5 | Rule 9: an in-flight write blocks every action that could supersede it, not just Submit | `disabled={ocupado !== null}` removed from the row's "Editar" | **KILLED** — `Received element is not disabled` |
| M13 | 2.5 | Rule 6: the post-write refresh sits OUTSIDE the write's try/catch, so a committed write is never reported as a failure | the success aviso moved back inside the write path | **KILLED** — `Unable to find an element with the text: Se actualizó "Sur SRL".` |
| M14 | 2.17 | `idTenant: datos.idTenant` in the create payload builder — the exact field whose absence produced the owner's 400 `tenant_requerido` | the line deleted from the `CrearUsuario` literal (`as CrearUsuario` to keep it compiling) | **KILLED, 3 tests** — `expected { usuario: 'nuevo.admin', …(4) } to match object { usuario: 'nuevo.admin', …(3) }`, plus `… to match object { rolId: 1, idTenant: null }` and `… to match object { rolId: 4, idTenant: null }` |
| M15 | 2.17 | The rol `onChange` clears `idTenant` **in state**, not only in the rendering | `onCambio({ ...valor, rolId })` — the `rolId === ROL.Root ? null : …` clear dropped | **KILLED** — `el rol Root deshabilita el selector y limpia el tenant ya elegido`: `expect(element).toHaveValue()` / `Expected the element to have value: ` / `Received: 2`. **DOWNGRADED in round 1 to "defence-in-depth, unreachable through `/roles`"** — see the note below the table; the kill is real but it kills a branch a real actor cannot enter |
| M16 | 2.17 | `ofreceTenant` gates the selector — S5 anti-oracle: a tenant admin never enumerates tenants | `{esNuevo && (` — the `ofreceTenant` conjunct dropped | **KILLED, 2 tests** — `expected document not to contain element, found <select`, and `expected "vi.fn()" to be called at least once` (the now-visible `required` select blocks submission, so the POST never fires — incidental proof that the `required` copy matches real enforcement, `react-async-state` rule 7) |
| M17 | 2.17 | ~~The create selector offers only ASSIGNABLE tenants — the platform token is not an id~~ | ~~`const tenantsAsignables = opcionesTenant` — the `VALOR_SIN_TENANT` filter dropped~~ | **SUPERSEDED in round 1 — the clause no longer exists.** The `VALOR_SIN_TENANT` filter lived on the `!esPlataforma` arm of `tenantsAsignables`, which round 1 proved unreachable (the selector renders only under `ofreceTenant={esPlataforma}`) and removed as dead code. The kill recorded here was real when it ran, against code that is gone; it is **not** re-targeted to a different clause and **not** left standing as if it still guarded something. The surviving property of that selector — its option set — is now guarded by M22 and M26b |
| M18 | 2.17 | `react-async-state` rule 5: the selector is part of the full-window disabled state while the list (its own option source) is still loading | `disabled={guardando \|\| esRolDePlataforma}` — `tenantsCargando` dropped | **KILLED** — `el selector queda inerte mientras la lista está cargando`: `expect(element).toBeDisabled()` / `Received element is not disabled` |
| M19 | 2.17 (page-size gap closure) | S5 anti-oracle: a tenant admin must never call `listarTenants()`, not even in the background | `if (!esPlataforma) return` deleted from the tenant-universe fetch effect | **KILLED** — `un admin de tenant nunca pide el universo de tenants (listarTenants)`: `expected "vi.fn()" to not be called with arguments: [ '/plataforma/tenants' ]`, received 3 calls including it |
| M20 | R1-C1 | `react-async-state` rule 7: the tenant-universe `.catch` SURFACES the failure instead of silently setting `[]` | `setError(ERROR_TENANTS)` deleted from the catch | **KILLED, 2 tests** — `un fallo del universo de tenants se rinde en pantalla y abrir "Nuevo" lo reintenta` and `Guardar queda inerte cuando el universo de tenants falló`: `Unable to find an element with the text: /No se pudo cargar la lista de tenants/` |
| M20b | R1-C1 | The retry hangs off opening "Nuevo" — the effect's `[esPlataforma]` deps never re-fire on their own | `if (esPlataforma && tenantsDePlataformaFallo) cargarTenantsDePlataforma()` deleted from the "Nuevo" handler | **KILLED** — `expected 1 to be 2` (the second `GET /plataforma/tenants` never happens) |
| M21 | R1-C2 | `disabled={guardando \|\| sinTenantAsignable}` on Guardar — a `disabled` `<select>` is EXEMPT from HTML constraint validation, so `required` does not block the POST during the load/failure window | `disabled={guardando}` | **KILLED, 2 tests** — `expect(element).toBeDisabled()` / `Received element is not disabled`, on both the loading and the failed window |
| M21b | R1-C2 | The same guard re-checked inside `guardar()` (rule 9: a same-tick double click beats the `disabled` attribute) | `if (formulario.id === null && universoDeTenantsIndisponible) return` deleted | **SURVIVED, 24/24 green — recorded as a survivor.** `userEvent.click` honours the `disabled` attribute, so the handler is never entered; the guard exists for the same-tick race the DOM cannot be made to produce here. Same class as the re-entrancy guards already on this screen |
| M22 | R1-C4 | The assignable-tenant label MARKS a non-`Activo` estado — the server never inspects the destination tenant's estado, so a user created inside a suspended tenant silently cannot log in | `return nombre` — the `estado === 'Activo' ? … : \`${nombre} (${estado.toLowerCase()})\`` marker dropped | **KILLED, 2 tests across 2 files** — `expected [ 'Elegí un tenant', …(3) ] to deeply equal [ 'Elegí un tenant', …(3) ]` (the helper unit test and the screen test). **Scope corrected in round 2**: only the `(suspendido)` half of this clause is a live path. A tenant in `Baja` is soft-deleted and `GET /plataforma/tenants` does not list it, so the `(baja)` arm is defence-in-depth, not a real listing row — the `Baja` fixture exists to exercise the branch and the doc-comments of `etiquetaDeTenantAsignable` and of both tests now say so instead of presenting it as something an operator can see. The code is kept in case the listing stops filtering them |
| M23 | R1-S2 | The filter reconciliation is WRITTEN to state in `cargar`, not only derived at render | `setFiltroTenant((prev) => seleccionVigente(…))` deleted | **KILLED** — `un filtro invalidado por una búsqueda no resucita cuando las filas vuelven`: `Expected the element to have value: ` / `Received: 3`. The derived fallback is the confound and it is named in the test: while the option is missing it blanks the `<select>` on its own, so the discriminating observation is that the filter must NOT reapply itself when the rows come back |
| M24 | R1-S3 | The post-write refresh reloads with the APPLIED search term, not the input draft | `await cargar(token, busqueda, true)` | **KILLED** — `expected '/usuarios?busqueda=juan' to be '/usuarios'` (text typed and never searched narrowed the table after a Baja) |
| M25 | R1-S4 | The password POST has its own `try`: a committed PUT is never reported as a failure (rule 6) | the POST moved back inside the PUT's `try` | **KILLED** — `Unable to find an element with the text: /no se pudo cambiar la contraseña/` (the screen said "No se pudo guardar." for a profile that had already committed) |
| M26 | R1-S5 | `desempatarHomonimos` in `opcionesDeTenant` — two DISTINCT tenants sharing a free-text name render byte-identical options | the `desempatarHomonimos(…, 'tenant')` call dropped | **KILLED** — `desempata con el id a dos tenants distintos que comparten nombre` |
| M26b | R1-S5 | The same disambiguation on the create selector (`opcionesDeTenantAsignable`) — same class, and the surface where picking the wrong twin assigns the user to the wrong tenant | the `desempatarHomonimos(opciones, 'tenant')` call dropped | **KILLED** — `expected [ 'Comercio Sur', 'Comercio Sur' ] to deeply equal [ 'Comercio Sur (tenant 2)', …(1) ]` |
| M27 | R1-S5 | The same disambiguation on `opcionesDeEmpresa`, over the equally free-text razón social | the `desempatarHomonimos(…, 'empresa')` call dropped | **KILLED** — `desempata con el id a dos empresas distintas que comparten razón social` |
| M28 | R1-C3 | `esPlataforma &&` gates the tenant FILTER of `Empresas` with the same criterion that already gated the tenant COLUMN | `{true && (` | **KILLED** — `un admin de tenant no ve el filtro por tenant, igual que no ve la columna`: `expect(element).not.toBeInTheDocument()` |
| M29 | R1-C3 | The same gate on `PuntosVenta` — and only on the TENANT filter; the empresa filter stays for every actor | `{true && (` | **KILLED** — `un admin de tenant no ve el filtro por tenant, pero sí el de empresa`: `expect(element).not.toBeInTheDocument()` |
| M30 | R1-S7 | `refrescarTrasEscribir` clears `ocupado` in a `finally` REGARDLESS of the generation — a token mismatch used to `return` with the flag still on, freezing every action | the `if (generacion.current === token)` gate put back on the `finally` (`Tenants.tsx`) | **SURVIVED, 4/4 green — recorded as a survivor.** Stated by the finding itself: unreachable today, because rule 9 disables every action that could bump the generation while `ocupado` is set. Fixed as a trap for slice 5's delete buttons, which add exactly such actions |
| M30b | R1-S7 | The same, on `Usuarios.tsx` | the `if (generacion.current === token)` gate put back on the `finally` | **SURVIVED, 24/24 green — same reason as M30, recorded not dressed up** |

**No survivors among M14-M18.** One inaccuracy in a test's own doc-comment was found by running M15 and
corrected rather than left standing: the first draft claimed the M4 confound applied (a derived
fallback masking a missing state clear), but here the `<select>`'s `value` is derived from the SAME
`valor.idTenant` that travels in the POST, so there is no independent fallback and both observations
discriminate. M15 in fact dies on the rendered value, and the comment now says so.

**M10 is the honest survivor of this slice, and it is stated rather than papered over.** The
generation gate on `Tenants.tsx`, `Empresas.tsx` and `PuntosVenta.tsx` guards a window that
`react-async-state` rule 9 already closes: while a write is outstanding every action that could
supersede it is disabled, and while a read is outstanding the table is replaced by the loading
indicator, so no second operation can start. Deleting all four guards from `Empresas.tsx` therefore
changes nothing observable and the suite stays green. They are kept as defence-in-depth for slice 5,
which adds delete buttons to exactly these screens. `Usuarios.tsx` is different — its search box is
reachable while a load is in flight, which is a genuine two-reads-in-flight window — and that is
where M11 kills.

**Round 1 — M15 is DOWNGRADED to defence-in-depth, and the comment that oversold it is rewritten.**
Judge B asked whether the create selector's Root branch guards anything reachable. It does not:
`PoliticaDeRoles.RolesAsignablesPor` (`PoliticaDeRoles.cs:30-35`) returns
`[Admin, Supervisor, Vendedor]` for a Root actor and `[Supervisor, Vendedor]` for an Admin —
**Root is in no actor's list**, so `GET /roles` (`ServicioDeUsuarios.RolesAsignablesAsync`, which
projects exactly that list) can never put a Root `<option>` in the rol `<select>`. The rol-root
branch is therefore unreachable from this screen, and M15 kills a branch a real actor cannot enter.
The code is KEPT as defence-in-depth against a future change to the rol catalogue, but its record is
corrected on both counts: the table row now says so, and the doc-comment of `FormularioUsuario`
plus the inline comment on the rol `onChange` no longer claim the clear prevents a live 403 — the
live clause there is the tenant `required` for every other rol, whose absence is the real
400 `tenant_requerido`. The test fixture keeps its Root rol row on purpose: it is what makes the
defence-in-depth branch exercisable at all, and the doc-comment now says that is what it is.

**Round 1 — three honest survivors, stated rather than papered over.** M21b (the `guardar()`
re-entrancy guard), M30 and M30b (the ungated `finally` that clears `ocupado`) all survived their
mutations with the suite fully green, and are recorded as survivors in the table. M21b guards a
same-tick double click that `userEvent` cannot produce, because it honours the `disabled`
attribute. M30/M30b guard a token mismatch that rule 9 makes unreachable **today** — the finding
that produced them said exactly that ("unreachable today, a trap for slice 5's delete buttons"),
and the fix is taken for that reason, not because a test proves it. Same handling M10 already got.

**Round 1 — the filter write-back is REPLICATED to `Empresas`/`PuntosVenta` without a reachable
repro, and that is stated.** The state write-back that M23 kills on `Usuarios.tsx` is applied to the
other two screens under `react-async-state` rule 10 (any correctness pattern established on one
surface is replicated across every sibling with the same interaction). Those two screens have **no
search box**: their only row-set change is the post-write refresh, which reloads the full list, so
the resurrection sequence judge A demonstrated (narrow → the option disappears → widen → the option
returns) has no reachable trigger there. Mutating their write-back therefore SURVIVES by
construction and no test is invented to pretend otherwise; the shared pure helper
`seleccionVigente` carries its own unit tests, and slice 5 — which adds deletions to those screens
— is where the trigger becomes reachable.

**One test defect was found by running M4 and was fixed, not rationalised.** The first draft of the
empresa-clearing test asserted only `toHaveValue('')` after switching tenants, which the derived
`empresaVigente` fallback satisfies on its own. The assertion now returns the tenant filter to
"Todos" and requires the full list back: only a genuine `setFiltroEmpresa(SIN_FILTRO)` produces
that, because a stale `'30'` would become a valid option again and silently reapply itself.

### Mutation evidence — slice 2, judgment-day round 2 (FINAL round)

Same protocol as above: every mutation was applied to the working tree, `npx vitest run <file>` was
run from `src/Ways.Web`, the result was OBSERVED, then reverted and observed GREEN again.

| # | Finding | Clause under test | Mutation applied | Observed result |
|---|---|---|---|---|
| M31 | R2-1 | The tenant-universe failure has its OWN rendered banner, derived from `tenantsDePlataformaFallo`, and never travels through the shared `error` slot | `setError(ERROR_TENANTS)` put back in the `.catch` and the dedicated banner removed | **KILLED, 2 tests** — `el fallo del universo de tenants sobrevive a una carga de usuarios que resuelve después`: `Unable to find an element with the text: /No se pudo cargar la lista de tenants/`; and the reverse direction, `el fallo del universo y el del listado de usuarios se rinden los dos`: `Unable to find an element with the text: Se cayó el listado de usuarios.` — the shared slot clobbers in BOTH directions and each test pins one |
| M32 | R2-2 | The password-POST failure goes to the RED slot while the committed PUT's confirmation stays in the green one | the `setErrorPassword(…)` call replaced by an assignment to `mensajeOk` ("Usuario … actualizado, pero no se pudo cambiar la contraseña. …"), routing the failure back into `aviso` | **KILLED** — `el fallo de la contraseña va en rojo y la confirmación del perfil se queda en verde`: `expect(element).toHaveClass("alert-danger")`. The assertion is on the alert VARIANT, not the text: the text alone cannot tell a failure announced in `alert-success` from one announced in `alert-danger` |
| M33 | R2-4 | `!tenantsDePlataformaCargando` guards the "Nuevo" retry against a duplicate in-flight `GET /plataforma/tenants` | the conjunct dropped | **KILLED** — `dos clicks seguidos en "Nuevo" disparan un solo reintento`: `expected 3 to be 2` |
| M34 | R2-7 | `esPlataforma &&` gates the tenant FILTER of `Usuarios`, parity with C3 on the sibling screens | `{true && (` on the filter, and both tenant-cell gates dropped | **KILLED** — `un admin de tenant no ve el filtro ni la columna de tenant`: `expected document not to contain element, found <select` |
| M34b | R2-7 | The same gate on the tenant COLUMN, mutated ALONE so the column assertion is not overdetermined by the filter one | only `{esPlataforma && <th>Tenant</th>}` and `{esPlataforma && <td>…</td>}` dropped | **KILLED** — same test, `expected document not to contain element, found <th>`. Run separately on purpose: killing the pair together would not have proven the column half |
| M35 | R2-5 | The `guardar()` re-check announces itself instead of exiting silently | `setError(ERROR_ALTA_SIN_TENANTS)` removed, back to the bare `return` | **SURVIVED, 28/28 green — recorded as a survivor, not dressed up.** Exactly the M21b class and the finding said so: `userEvent.click` honours the `disabled` attribute, so the handler is never entered and the DOM cannot produce the same-tick double click the guard exists for. The silent exit is removed because a guard that can fire must be able to say why, not because a test proves it |
| M36 | R2-6 | The platform option enters `desempatarHomonimos`'s collision map — a tenant named literally `Plataforma (sin tenant)` produced two byte-identical labels | the pre-round-2 shape restored: `porClave.get(VALOR_SIN_TENANT)` taken out before the tie-break, which is then run over the filtered rest | **KILLED** — `un tenant llamado exactamente como la opción de plataforma no queda con etiqueta idéntica`: `expected 1 to be 2` (the two labels collapse into one). The pre-existing `"Plataforma"`-alone case does NOT cover this: there the fixed `(sin tenant)` suffix already separated them |
| M37 | R2-8 | The empresa write-back reconciles against the tenant-NARROWED option set, the same one the render uses | `seleccionVigente(opcionesDeEmpresa(filas), prev)` — the `tenanteReconciliado` argument dropped | **KILLED** — `la empresa elegida no resucita cuando un refresco la saca del tenant vigente`: `expected [ 'PV Anexo' ] to deeply equal [ 'PV Centro', 'PV Anexo', 'PV Este' ]`. Same confound and same discriminating observation as M4/M23: while the tenant filter is still on, both versions paint identically |
| M38 | R2-3 | `Empresas.tsx` clears `ocupado` in an UNGATED `finally`, and its token check moved inside the try so the mismatch path also reaches it | the `if (generacion.current === token)` gate put back on the `finally` | **SURVIVED, 15/15 green — recorded as a survivor.** Identical to M30/M30b and for the identical reason: rule 9 disables every action that could bump the generation while `ocupado` is set, so the frozen-flag window is unreachable today. Taken as the trap slice 5's delete buttons will need |
| M38b | R2-3 | The same, on `PuntosVenta.tsx` | the same gate put back | **SURVIVED, 15/15 green — same reason, recorded not dressed up** |

**Round 2 — R2-1 needed its confound named before it could be killed.** The obvious test (mount,
let `/plataforma/tenants` fail, assert the banner) passes with the failure routed through `error`
too, because on the default mock ordering the users list resolves FIRST and its `setError('')` has
already run. The kill only happens once the ordering is inverted by hand — tenants rejects, the
banner is asserted, and only THEN the users promise resolves — which is why both promises are
controlled explicitly in that test. Its sibling pins the opposite direction: `ERROR_TENANTS` must
not bury a genuine users-list failure.

**Round 2 — two honest survivors, M35 and M38/M38b.** Neither is presented as a kill. M35 is the
M21b class (the DOM cannot produce the race the guard covers); M38/M38b are the M30/M30b class
(rule 9 closes the window today). All three fixes are taken for replication and honesty reasons
stated by the findings themselves, and slice 5 is where they become reachable.

**Round 2 — one item recorded with NO code change, by decision.** Crafted tenant names that
pre-embed a generated suffix (`"Foo (suspendido)"`, `"Bar (tenant 3)"`) can make a label read as if
the web had marked it. This is misleading COPY only: the option keys stay true, the filtering and
the create payload are driven by those keys, and the text is authored by a platform actor about
their own tenants. No defence is added and none is planned.

### Slice 2 delivery notes

- **BUDGET OVERFLOW, MEASURED AND REPORTED, NOT ABSORBED — AND NOT SPLIT UNILATERALLY.**
  `git diff main --stat -- src`: **34 files, 1 909 insertions + 289 deletions = 2 198 changed
  lines** against an estimate of ~430 and an operative budget of 800 (OD1). Ignoring
  pure re-indentation (`git diff -w`): **1 741 + 121 = 1 862**. The split is production **~1 054**
  / tests **~1 144**, and the production figure is itself inflated: the four screens account for
  443 insertions and 120 deletions with `-w`, the rest is the mechanical `tipos.ts` mirror plus
  58 lines of fixture fields across 23 pre-existing test files.

  The shape is the same one the orchestrator already ruled on for slice 1: a small production
  surface carrying a large body of demanded evidence. The five new test files are 1 078 lines,
  required by `web-descriptor-tests` (a colocated test per helper branch, smoke-only is not done)
  and `mutation-proof-tests` (a named clause plus recorded evidence per test).

  **The pre-approved degradation for this slice (`2a` names and counts / `2b` filters) is no
  longer a clean cut** and this is stated rather than forced: the filter derivation, the column
  rendering and the `react-async-state` retrofit live in the same four screen bodies, so cutting
  along names-vs-filters would split single functions across two PRs. The cut that IS clean is
  per screen, and the six commits are already shaped for it:
  `tipos.ts` mirror → helpers + their tests → Tenants → Empresas → PuntosVenta → Usuarios.
  **This is an orchestrator decision, not an apply-phase one**, and no PR was opened.

- **Task 2.3 has no file to modify: `src/Ways.Web/src/api/usuarios.ts` does not exist.**
  `UsuarioListado` is declared in `tipos.ts` and `Usuarios.tsx` calls `api.get` directly — there is
  no usuarios client module, and the `eliminar` call the task mentions is an inline
  `api.delete` in the screen. The task's substance (mirror the two new fields) is delivered by
  task 2.1. **No module was created just to satisfy the task's wording**: an empty indirection
  layer would be dead code. Marked done on that basis, recorded here rather than silently.

- **`react-async-state` was applied to all four screens, which is most of the production delta
  beyond the columns and filters.** The screens combine React state with async fetch/save, so the
  skill's activation contract fires. What landed, per screen: a generation ref with its
  invalidation contract documented on the ref; a token check before every state application after
  every await, including the `finally` that clears the flags and the opt-in rethrow of the loader;
  a per-operation `ocupado` flag that disables the form AND every row action for the whole window
  from click until the post-write refresh lands (rule 9, not token reconciliation); and the
  post-write refresh isolated from the write's try/catch with a distinguishable message
  (rule 6). `Usuarios.tsx` also gained `key={formulario.id ?? 'nuevo'}` on the form subtree
  (rule 8) and a re-entrancy guard on every handler. M10-M13 are the evidence, including the
  survivor.

- **The filter selection is DERIVED, not synchronised by an effect.** `tenantVigente` /
  `empresaVigente` fall back to "no filter" when the stored selection is not among the options
  derived from the currently loaded rows, so a refresh that removes a row can never leave a
  `<select>` pointing at an option that no longer exists — without a `useEffect` that would fire a
  second render pass. The one place state IS mutated is `cambiarFiltroDeTenant`, and it uses a
  functional updater built from `prev` (rule 1), never from closure state.

- **A tenant literally named "Plataforma" stays distinguishable in the filter.** The column renders
  the literal `"Plataforma"` for `idTenant === null` (D14), so a homonym tenant renders the same
  text — `nombre` is free text and that is unavoidable. The filter is where it must not collapse:
  the platform option's key is the token `sin-tenant` (never any `String(idTenant)`) and its label
  carries the suffix `(sin tenant)`, so the two options are distinct by key AND by label. M2 kills
  the key collapse; the `Usuarios` homonym test kills the label collapse.

- **Pre-existing flake observed, NOT introduced by this slice.**
  `Reposicion.test.tsx > arranca con el primer punto de venta cargado, sin ?dias= en la consulta`
  fails intermittently under the full-suite run (`TypeError: Cannot read properties of undefined
  (reading '0')` at line 152 — `apiGetMock.mock.calls.find(...)` returns `undefined`). Measured:
  **1 failure in 3 full runs on this branch, and 1 failure in 4 full runs on `main` with the branch
  stashed**. Alone, the file passes 12/12. The file's only change in this slice is two fixture
  fields. Not fixed here — it is outside slice 2's scope — but recorded so nobody reads a red
  suite as slice 2's doing.

- **Task 2.17's launch brief carried two factual premises that did NOT hold, and the corrections are
  recorded rather than silently absorbed.** (1) It said slice 2 already had "a tenant fetch for its
  filter" to reuse. There is no tenant fetch anywhere in slice 2 and there must not be: **D15**
  forbids it, and `tasks.md`'s own binding note under slice 2 says so. The state actually reused is
  the derived `opcionesDeTenant(filas)`, which is what the brief's real instruction ("do not add a
  second fetch") demanded. (2) It said `Usuarios.tsx` already had the `esPlataforma` notion — it did
  not; `Empresas.tsx:30` and `PuntosVenta.tsx:41` do, and `Usuarios.tsx` only had the inline
  `actual?.rolId === ROL.Root` inside `puedeEditar`. `esPlataforma` was introduced here with the
  identical `rolId === ROL.Root` definition its two sibling screens use (`react-async-state` rule 10).

- **KNOWN GAP OF THE DERIVED OPTION SET, STATED NOT HIDDEN.** Because the create selector derives
  its options from the loaded rows (D15, no second fetch), a platform actor can only assign a tenant
  that has at least one user **on the current page**. `GET /api/usuarios` is the one paginated
  listing and its default `tamanio` is 25 (`UsuariosEndpoints.cs:21`), and the screen never sends a
  page parameter. In practice every tenant is provisioned WITH its admin user
  (`ServicioDeAprovisionamiento`), so a tenant with zero users does not normally exist; the reachable
  case is the 26th-and-beyond tenant. Closing it needs either the platform-only
  `listarTenants()` fetch (which D15 rules out for the shared screens, though `Usuarios.tsx`'s
  selector is platform-only and would not 403) or the server-side pagination already deferred with a
  reopen condition in `state.yaml`. **Not decided here** — this was a bounded bug fix, and widening
  it to a fetch/pagination decision is the orchestrator's call.

  **CLOSED as a narrow web-only follow-up (still on `feat/stage20-slice2-web-relaciones`).** The
  orchestrator chose the platform-only `listarTenants()` fetch over the pagination alternative:
  `Usuarios.tsx`'s create selector is reachable **only** by a platform actor, and
  `GET /plataforma/tenants` is `Politicas.SoloPlataforma`, so it never 403s for that actor and adds
  no scope creep. The **filter** option set is untouched — still `opcionesDeTenant(filas)`, D15
  still holds there, zero second fetch on that path. The 26th-tenant-onward case is now reachable
  regardless of which page the loaded users happen to fall on.

- **Verify criteria re-asserted on this slice's diff (V1-V6, V13).** Zero files under
  `Migraciones/`; `dotnet ef migrations has-pending-model-changes` → *"No changes have been made to
  the model since the last migration"*; `InicializadorDeBaseDeDatos.cs`, `Politicas.cs` and
  `ManejadorDeErrores.cs` all pass `git diff main --exit-code`; zero `ExecuteDelete`/`RemoveRange`/
  `DELETE FROM` additions; zero DDL. `git diff main --name-only` outside `src/Ways.Web/` is
  **empty** — this slice touches no backend file at all, which is why the three dotnet test suites
  were not run (`dotnet build Ways.slnx` was, and is clean).

---

## Slice 3: `InspectorDeUso` — the guard, deliberately INERT (PR 3)

**Branch**: `feat/stage20-slice3-inspector-de-uso`. **Start**: `main` (this slice depends on
**nothing** — it is inert by construction). **Finish**: `InventarioDeDependientes` (pure metadata
walk, three buckets, exactly two carve-outs) and `InspectorDeUso` (statement rendering + raw-ADO
execution) exist and are registered in DI, with **no caller anywhere in `src/`**; N1, N2 and N3 are
green and N3's golden is checked in. **Depends on**: nothing. **Estimate**: ~470 lines. **Rollback**:
revert — nothing calls it, so the guard cannot have run. **Skills**: `mutation-proof-tests` (rule 1:
never assert a tautology — see Reconciliación 4), `work-unit-commits`. **`db-error-backstops`: N/A.**

**Why this slice is inert.** There is no database backstop behind this guard (`db-error-backstops`
structurally N/A), so it is shipped with no caller **on purpose**: it can be reviewed on its own
merits before anything can invoke it. Verify criterion V10 asserts the zero-caller property.

**OD4 is binding here.** No branch may emit `AND d.deleted_at IS NULL`. Record the reversal cost as a
one-line knob in `InspectorDeUso`'s doc-comment (add the conjunct per branch, flip task 4.11's test,
regenerate N3's golden) — record it, do not implement it.

- [x] 3.1 Modify `src/Ways.Application/Abstracciones/IWaysDbContext.cs` — add **exactly one** member,
  `IModel Model { get; }`, with the doc-comment argument the interface itself supplies (`:150-152`:
  `DatabaseFacade` is the same EF Core abstraction any `DbContext` already exposes). **Zero
  implementation lines change** — `DbContext.Model` satisfies it implicitly and `rg ": IWaysDbContext"`
  over `src/` and `tests/` returns zero matches. *(design D1, C; verify criterion V7)*
- [x] 3.2 Create `src/Ways.Application/Organizacion/InventarioDeDependientes.cs` — **pure**: no
  database, no clock, no DI (D2), so N3's golden can be regenerated without a container.
  `ClasificacionDeDependiente { Excluido, Marcado, SinMarca }`, `RamaDeUso(Tabla, Columnas,
  PropiedadesDelPrincipal, Clasificacion)` with `UsaAncla => Clasificacion is Marcado`, and
  `Construir(IModel, Type ancla)`. Classification is **per dependent entity type**, evaluated in the
  fixed order carve-out → timestamped → untimestamped, and is **total by construction** (no runtime
  `else` can throw). Branch predicates are built by zipping `fk.Properties` with
  `fk.PrincipalKey.Properties`, so composite `(id, id_tenant)` and alternate-key FKs need no special
  case, and `MovimientoStock` contributes **two** independent branches. *(BO-R4, BO-R5; design D3, A)*
- [x] 3.3 Same file — `Excluidos` as a `FrozenSet<Type>` with **exactly two** members,
  `Ways.Domain.Auditoria.Auditoria` and `NumeracionCliente`, **each carrying its written reason in
  code** (B5: the audit trail is a record *about* the entity and the referenced row survives logical
  deletion; the provisioning counter is inserted by raw SQL in
  `AsignadorDeNumeroCliente.AsegurarContadorAsync`, is not an `EntidadBase`, and is not customer
  data). A carve-out emits **no branch at all**. *(BO-R6)*
- [x] 3.4 Same file — `Construir` throws `InvalidOperationException` **naming the CLR type and the
  FK** for the three *mechanical* impossibilities: an entity type with no mapped table, a `Marcado`
  type whose `created_at` column cannot be resolved, and an FK whose principal properties are not all
  readable from the anchor. These are build-time failures via N1, **never** production 500s.
  *(BO-R5; design A)*
- [x] 3.5 Create `src/Ways.Application/Organizacion/InspectorDeUso.cs` —
  `PrimeraDependenciaEnUsoAsync(Type tipoAncla, IReadOnlyList<object> valoresDeClave,
  DateTimeOffset ancla, CancellationToken)` returning the **name of the first blocking table** or
  `null`. One statement: `UNION ALL` of `SELECT '<tabla>' AS tabla WHERE EXISTS (SELECT 1 FROM
  <tabla> d WHERE <fk> = $n [AND d."created_at" > $m])` with an **outer `LIMIT 1`**. Raw ADO on the
  **caller's** connection/transaction, opened through `Database.OpenConnectionAsync` so
  `InterceptorDeContextoDeTenant` sets the RLS GUCs — **never** `Database.SqlQuery<T>` /
  `FromSqlRaw` against this model (the stage-1 slice-2 trap). *(BO-R4, BO-R7; design D5, D6, D)*
- [x] 3.6 Same file — **injection surface closed**: identifiers come from `IEntityType`/`IProperty`
  metadata only, are schema-qualified and double-quoted, and are **rejected by the generator** unless
  they match `^[a-z_][a-z0-9_]*$`; every anchor key value and the anchor's `CreatedAt` is a bound
  **parameter** (`ParametrosDeComando.Agregar`, the `AsignadorDeNumeroCliente` idiom). No
  user-supplied string ever reaches the statement. *(design D, Threat Matrix)*
- [x] 3.7 Same file — doc-comment records **OD4 as a one-line reversible knob**: no
  `AND d.deleted_at IS NULL` conjunct is emitted, a soft-deleted dependent still blocks, and
  reversing it means adding that conjunct per branch, flipping task 4.11's test and regenerating
  N3's golden. Also record **side effect B**: RLS lives on the connection, so it still applies, and
  **no `id_tenant` conjunct is added** — an extra conjunct can only ever *narrow* the result, and a
  narrowing bug under-blocks, the one direction this stage refuses. *(BO-R7; design D6, E, OD4)*
- [x] 3.8 Modify the DI module (`src/Ways.Api/Programa.cs` or its registration file) — register
  `InspectorDeUso` **scoped**. **No caller until slice 4.** *(design File Changes)*
- [x] 3.9 **N1 — totality.** Create
  `tests/Ways.Application.Tests/Persistencia/InventarioDeDependientesTests.cs`: `Construir(db.Model,
  T)` succeeds for **all four** anchors (`Tenant`, `Empresa`, `PuntoVenta`, `Usuario`), and the
  emitted branch count equals `GetReferencingForeignKeys().Count()` **minus** the carved-out FKs — no
  FK is silently dropped. Built over the real Npgsql model on an **unopened** connection, the
  existing `ModeloDeOrganizacionTests.cs:17-30` pattern: **no container**. *(BO-R5; never degradable)*
- [x] 3.10 **N2 — the rule is read off the TABLE, not restated from the code.** Same file: for every
  branch, `rama.UsaAncla == entityType.GetProperties().Any(p => p.GetColumnName() == "created_at")`,
  computed **independently in the test**. **Mutation**: change the classifier to key on
  `EntidadTenant`, or invert it, or hardcode a type list — the test must go red each time. *(BO-R5;
  never degradable)*
- [x] 3.11 **N3 — the inventory golden (THE TRIP-WIRE).** Same file plus
  `tests/Ways.Application.Tests/Persistencia/Fixtures/inventario-de-dependientes.txt`: a **sorted,
  checked-in** line per branch, `<ancla> | <tabla> | <columnas> | <bucket>`, including one `excluido`
  line per carve-out so the file also pins the two-member carve-out set. Any FK a future stage adds,
  removes, retargets or reclassifies produces a **diff naming the exact table and column**.
  Regeneration is a deliberate edit that must be justified line by line in the PR body (V8). *(BO-R5,
  BO-R6; never degradable — this is the executable form of the spec's completeness requirement)*
- [x] 3.12 [P] Assert the carve-out list contains **exactly** `Auditoria` and `NumeracionCliente` and
  nothing else, and that neither contributes a branch for any of the four anchors. *(BO-R6)*
- [x] 3.13 [P] Create `tests/Ways.Application.Tests/Organizacion/InspectorDeUsoTests.cs` — statement
  **rendering** (pure string assertions over `Construir` + the renderer): a `Marcado` branch carries
  the `created_at > @ancla` conjunct with a **strict** `>`; a `SinMarca` branch carries **only**
  `<fk> = @id`; a composite FK renders **two conjuncts**; an alternate-key principal reads its values
  off the anchor; identifiers are quoted and schema-qualified; a non-conforming identifier is
  **rejected**; the parameter count and binding order match the branch order; the outer `LIMIT 1` is
  present. *(BO-R2, BO-R4, BO-R5)*
- [x] 3.14 [P] **OD4 rendering assertion**: no rendered branch contains `deleted_at`. This is the
  cheap half of OD4 (the behavioural half is task 4.11). *(BO-R7; OD4)*
- [x] 3.15 [P] Rendering assertion for **BO-R8**: a nullable FK renders the plain `<fk> = @id`
  predicate with no `IS NULL` special case — `fk = @id` simply does not match `NULL`, so a shared
  catalogue row (`id_empresa IS NULL` on `Cliente`/`Proveedor`/`Oferta`/`ConfiguracionDeCatalogo<T>`)
  cannot block an empresa. The behavioural proof is task 4.21. *(BO-R8)*
- [x] 3.16 **[S]** Structural: `rg` over `src/` proves `InspectorDeUso` has **zero callers** in this
  slice's tree. Recorded as a file/state assertion, **not** a runtime kill. *(verify criterion V10)*
- [x] 3.17 **[S]** Structural: `IWaysDbContext.cs` gained **exactly one** member and
  `rg ": IWaysDbContext"` over `src/` and `tests/` still returns zero hand-written implementations.
  *(verify criterion V7)*
- [x] 3.18 GATE GUARD + non-regression — re-assert V1-V6 and V13 (the guard's generated statement is
  the **only** SQL in the diff and it is read-only `SELECT`/`EXISTS`); Domain + Application suites
  green; `dotnet build Ways.slnx` clean.
- [ ] 3.19 `judgment-day` round to a clean round.
- [ ] 3.20 Open PR 3 `feat/stage20-slice3-inspector-de-uso`, record N1/N2/N3 mutation evidence in the
  PR body (V11), merge to `main` after the clean round.

### Mutation evidence — slice 3 (`mutation-proof-tests` rule 2, produced, not reasoned)

Every mutation below was applied to the working tree, the named suite was run, the result was
observed, and the mutation was then reverted and the suite observed GREEN again. Command:
`dotnet test tests/Ways.Application.Tests/Ways.Application.Tests.csproj --filter <suite>`.
Baseline before and after every mutation: **417/417 green**.

| # | Task | Clause under test | Mutation applied | Observed result |
|---|---|---|---|---|
| M1 | 3.2, 3.10 | The bucket is keyed on the dependent TABLE (the `created_at` column), never on a CLR base class | `llevaMarca` replaced by `typeof(EntidadTenant).IsAssignableFrom(dependiente.ClrType)` | **KILLED, 5 tests** — `N1_ConstruirNoTiraParaNingunaDeLasCuatroAnclas(Tenant)` (the mechanical-impossibility throw fired, naming `Usuario`: an `EntidadBase` with no resolvable `created_at`), `N1_LaCuentaDeRamasEsLaDeLasFksMenosLosCarveOuts(Tenant)`, `N2_UsaAnclaEquivaleATenerColumnaCreatedAt(Tenant)`, `N3`, `NingunCarveOutAportaRamaParaNingunaAncla` |
| M2 | 3.2, 3.10 | The classifier is not inverted | `llevaMarca ? Marcado : SinMarca` -> `llevaMarca ? SinMarca : Marcado` | **KILLED, 5 tests** — `N2` on **all four** anchors + `N3` |
| M3 | 3.2, 3.10, 3.11 | The bucket is derived from metadata, never a hand-written table list | classifier body replaced by a hardcoded `is not "arqueos_turno" and not ...` list that **omits `lotes`** | **KILLED, 5 tests** — `N1` x2 (the throw named `Ways.Domain.Stock.Lote`), `N2(Tenant)`, `N3`, `NingunCarveOutAportaRamaParaNingunaAncla` |
| M3b | 3.10, 3.11 | *(honesty row)* the same hardcode, **exactly correct for today's model** | classifier body replaced by the complete, correct hardcoded list | **SURVIVED, 44/44 green.** Recorded as a survivor, not dressed up: a hardcode that agrees with the model on every current row is an **equivalent mutant**, and no test over this model can see it. It is precisely what **N3 exists to catch tomorrow** — the day a stage adds a table, the derived classifier follows the model and the hardcode does not, and N3 goes red naming it |
| M4 | 3.9, 3.11 | `GetReferencingForeignKeys()` is the only source — no FK is silently dropped | `.Where(fk => fk.DeclaringEntityType.GetTableName() != "stock")` inserted into the walk | **SUPERSEDED RECORD, RE-RUN IN ROUND 2 (R2-3).** The original row named `N1_LaCuentaDeRamasEsLaDeLasFksMenosLosCarveOuts`, which round 1 DELETED as a tautology (item 3.22) — the row cited a test that no longer exists. The same mutation was re-applied on the round-2 tree and observed: **KILLED, 7 tests** — `N1_NingunaFkSeCaeEnSilencioYLosCarveOutsNoEjecutan` on `Tenant` (*"Estas FKs hacia Tenant no aportaron ninguna rama al inventario: stock\|id_tenant"*) and on `PuntoVenta` (*"...: stock\|id_punto_venta,id_tenant"*), `N5` on `Tenant` (*"...: stock\|id_tenant"*) and on `PuntoVenta` (*"...: stock\|id_punto_venta"*), `N3` (QUITADAS: `Empresa \| stock via puntos_venta \| id_punto_venta,id_tenant \| sinmarca`, `PuntoVenta \| stock \| id_punto_venta,id_tenant \| sinmarca`, `Tenant \| stock \| id_tenant \| sinmarca`), `UnaRamaSinMarcaLlevaSoloElPredicadoDeLaFk` and `UnaRamaPuenteadaUneLaHojaConPuntosVentaYLigaElAnclaSobreElPuente` |
| M5 | 3.3, 3.12 | The carve-out set has exactly two members | `typeof(NumeracionCliente)` removed from `Excluidos` | **KILLED, 3 tests** — `LosCarveOutsSonExactamenteAuditoriaYNumeracionCliente`, `NingunCarveOutAportaRamaParaNingunaAncla`, `N3` |
| M6 | 3.5, 3.13 | The `>` of the anchor conjunct is **strict** (a `>=` makes every freshly provisioned tenant undeletable) | `d."created_at" > $n` -> `>= $n` | **KILLED, 7 tests** — `UnaRamaMarcadaLlevaElConjuntoDeAnclaConMayorEstricto`, `LaCuentaYElOrdenDeParametros...` x4, `UnaFkCompuestaDeClaveAlternativa...`, `UnaFkNullableRindeElPredicadoLlano...` |
| M7 | 3.5, 3.13 | A `Marcado` branch actually carries the anchor conjunct | `if (rama.UsaAncla)` -> `if (false)` | **KILLED, 7 tests** — same set as M6 |
| M8 | 3.7, 3.14 | **OD4** — no branch emits `deleted_at`, so a soft-deleted dependent still blocks | `AND d."deleted_at" IS NULL` appended to every branch | **KILLED, 13 tests** — `NingunaRamaMencionaDeletedAt` on **all four** anchors, plus 9 rendering equalities |
| M9 | 3.5, 3.13 | The **outer** `LIMIT 1` — what makes the `Append` node stop at the first blocking branch | `") AS ramas LIMIT 1"` -> `") AS ramas"` | **KILLED, 4 tests** — `ElStatementAbreConLaProyeccionYCierraConElLimitExterno` on all four anchors |
| M10 | 3.6, 3.13 | The identifier rejection — the closure of the injection surface. **The pattern recorded here, `^[a-z_][a-z0-9_]*$`, is the PRE-C5 one**: round 1 item 3.26 replaced it with `\A[a-z_][a-z0-9_]*\z`, because in .NET `$` also matches before a trailing line break. The mutation and its kills are unaffected (it neuters the whole check, not its anchoring); the anchor itself is what M22 isolates | `IdentificadorValido.IsMatch(valor)` -> `true` | **KILLED, 5 tests** — `UnIdentificadorNoConformeSeRechaza` x4 (including the `comprobantes_venta"; DROP TABLE usuarios; --` case) and `UnaColumnaOUnEsquemaNoConformeTambienSeRechazan` |
| M11 | 3.2, 3.13 | The zip of `fk.Properties` with `fk.PrincipalKey.Properties` (composite and alternate-key FKs) | every conjunct bound to `$1` instead of the zipped index | **KILLED, 7 tests** — `UnaFkCompuestaDeClaveAlternativaRindeDosConjuntosLeidosDelAncla`, `UnTipoConDosFksAlAnclaAportaDosRamasIndependientes`, `UnaRamaSinMarcaLlevaSoloElPredicadoDeLaFk`, `LaCuentaYElOrden...` x2, +2 |
| M12 | 3.6, 3.13 | Identifiers are schema-qualified and double-quoted | `FROM "{esquema}"."{tabla}" d` -> `FROM {tabla} d` | **KILLED, 13 tests** — `TodaTablaSeEmiteCalificadaPorEsquemaYEntreComillas` on all four anchors + 9 rendering equalities |
| M13 | 3.3, 3.12 | The renderer filters carve-outs even when handed the complete inventory | the `Where(... is not Excluido)` of `Renderizar` dropped | **KILLED, 3 tests** — `ElRenderizadorNuncaEmiteUnaTablaExcluida` on `Tenant`, `PuntoVenta` and `Usuario` |
| M14 | 3.11 | **N3 is a real trip-wire and its diff NAMES the exact table and column** | the golden edited the way a future stage would break it: one line deleted (`PuntoVenta / movimientos_stock / id_punto_venta_destino,id_tenant`) and one reclassified (`Tenant / stock / id_tenant`, `sinmarca` -> `marcado`) | **KILLED, 1 test**, and the message printed the exact lines — `AGREGADAS: PuntoVenta / movimientos_stock / id_punto_venta_destino,id_tenant / sinmarca` and `Tenant / stock / id_tenant / sinmarca`; `QUITADAS: Tenant / stock / id_tenant / marcado`. The first run used a plain `Assert.Equal` over the two collections, whose diff truncates each line at ~50 characters and cut `id_punto_venta_destino` down to `id_punto_venta_de`; the explicit added/removed message was written **because of** that observation, not before it |

**Structural rows, stated as structural and never dressed up as runtime kills**
(`mutation-proof-tests` rule 13):

| # | Task | Assertion | Evidence |
|---|---|---|---|
| S1 | 3.16 | `InspectorDeUso` has **zero callers** in `src/` (V10) | `rg -n "PrimeraDependenciaEnUsoAsync" src/` returns exactly **one** line — its own declaration. The line number DRIFTS with every doc-comment edit, so it is re-observed each round instead of copied: `:72` when this row was first written, `:77` at the round-1 commit `55fe4ac` (the row was stale, R2-3), `:86` after round 2. `rg -n "InspectorDeUso" src/` returns 5 lines: the class declaration, two doc-comment mentions, one `<see cref>` in `IWaysDbContext.cs` and the `AddScoped<InspectorDeUso>()` registration. A DI registration is not a call site |
| S2 | 3.17 | `IWaysDbContext.cs` gained **exactly one** member and zero implementations changed (V7) | `git diff --stat` on that file = `11 insertions(+)`: 2 `using`, 8 doc-comment lines and 1 member (`IModel Model { get; }`). `rg ": IWaysDbContext" src/ tests/` -> **zero** matches, unchanged |
| S3 | 3.18 | V1 — zero new migrations | `Migraciones/` still ends at `20260822002214_FiscalArcaEtapa19a.cs`; `dotnet ef migrations has-pending-model-changes` -> *"No changes have been made to the model since the last migration."* |
| S4 | 3.18 | V2/V3/V5/V6 — `InicializadorDeBaseDeDatos.cs`, `src/Ways.Infrastructure/Persistencia/**`, `Politicas.cs` and `ManejadorDeErrores.cs` untouched | `git status --short` lists only two modified files, both under `src/Ways.Application/` |
| S5 | 3.18 | V4 — zero physical deletes | a scan for `ExecuteDelete`, `RemoveRange(`, `.Remove(` and `DELETE FROM` over both new files returns zero matches |
| S6 | 3.18 | V13 — the only SQL in the diff is the guard's generated statement, and it is read-only | the diff's sole SQL producer is `InspectorDeUso.Renderizar`, which emits `SELECT`/`EXISTS`/`UNION ALL`/`LIMIT` and nothing else; the rendering suite asserts the full text of every branch of all four anchors |

### Judgment-day slice 3, round 1 — confirmed items corrected

Six confirmed items fixed and two recorded with no code change. **ZERO schema, zero physical
deletes, the guard stays INERT** (`rg -n "PrimeraDependenciaEnUsoAsync" src/` still returns only
its own declaration): no configuration, no migration and no `Politicas.cs` /
`InicializadorDeBaseDeDatos.cs` / `ManejadorDeErrores.cs` line was touched.

- [x] 3.21 **C1 (CRITICAL, both judges) — `puntos_venta` was missing from the `Tenant` dependent
  set.** *(Round 2, R2-2: this row originally claimed the fix "closes the CLASS, not the
  instance". That overstates it and is corrected here. The union closes the class **for
  `EntidadTenant` subclasses** — every one of them is reached by the scope-column source
  whether or not it declares an FK against `tenants`. A tenant-scoped table that is NOT an
  `EntidadTenant` subclass is covered today only by its declared FKs, and by **N5 as the CI
  trip-wire** that names it the day one appears without one.)* `PuntoVentaConfiguration.cs:64-69`
  declares only `HasOne<Empresa>().HasForeignKey(p => new { p.IdEmpresa, p.IdTenant })` and **no**
  `HasOne<Tenant>()`, so `Tenant.GetReferencingForeignKeys()` never yielded `puntos_venta`: a
  tenant whose customer opened a second local read **PRISTINE** — fail-OPEN, in the data-loss
  direction, the one this stage refuses. `InventarioDeDependientes.InventarioCompleto` now builds
  the `Tenant` dependent set as the **UNION** of (a) the FK walk and (b) every entity type
  assignable to `EntidadTenant` mapped to the `id_tenant` scope column — the same reflection idiom
  `WaysDbContext.AplicarFiltroDeTenant` already uses for the query filter — deduplicated by
  `(tabla, columnas)` and classified by the **same** bucket rule. Adding the FK to the model was
  rejected on sight: that is a schema change and reopens the gate. The N3 golden gained
  `Tenant | puntos_venta | id_tenant | marcado` **and only that line** (`git diff --stat` on the
  fixture = `1 insertion(+)`). *(design D2, A; BO-R5)*
- [x] 3.22 **N5 — the dependent-SET completeness net**, judge B's manual audit written as code
  (`InventarioDeDependientesTests.N5_TodaTablaConIdTenantAparecenEnElInventarioDelAncla`,
  container-free against the real Npgsql model): for the `Tenant` anchor, **every** entity type in
  the model mapped to an `id_tenant` column must appear in its inventory (`excluido` lines count as
  present, because a carve-out is a written decision and not an omission), and the assertion NAMES
  the missing tables. **N1's count assertion is a tautology and is now recorded as one**: it
  compared `InventarioCompleto().Count` against the very walk that produces it, so a dependent that
  walk cannot see is invisible on both sides of the equality — that is exactly why C1 survived N1,
  N2 and N3. N1's second half was rewritten to what it can honestly assert (no FK is silently
  dropped: every FK's `(tabla, columnas)` is present; and no carve-out reaches the executable set),
  and **N5 is the set-level trip-wire N1 cannot be**, because its universe comes from an
  INDEPENDENT source — the model's column mapping. *(design B; never degradable)*
- [x] 3.23 **C2 (judge A) — the raw-ADO execution half had ZERO tests.**
  `rg PrimeraDependenciaEnUsoAsync tests/` returned nothing: every existing test went through the
  static `Renderizar`, so the bind ORDER (`valoresDeClave` first, the anchor instant last), the
  `ramas.Any(rama => rama.UsaAncla)` gate, the caller-transaction attachment and the
  `ExecuteScalarAsync as string` had no net at all — deleting the gate leaves the SQL referencing
  `$n` with `n-1` parameters bound, which is a Postgres bind error, which is a 500, and the whole
  suite stayed green. New `tests/Ways.IntegrationTests/InspectorDeUsoEjecucionTests.cs` (Docker,
  real Postgres) drives `PrimeraDependenciaEnUsoAsync` for **all four** anchors, including the two
  composite-key anchors (`Empresa` and `PuntoVenta`, `Id` + `IdTenant` + the instant = **three**
  parameters, which is what makes the order observable). Sibling seeds per `mutation-proof-tests`
  rule 12c: a second empresa of the same tenant, a second punto de venta and a second tenant, so a
  predicate that ignores either position of the composite key dies. *(BO-R4, BO-R7; design D5, D6)*
- [x] 3.24 **C3 (judge A) — the `Marcado` predicate deviated from design section A, undeclared, in
  the under-blocking direction.** `Clasificar` decided the bucket from the presence of a
  `created_at` COLUMN alone; the design's membership test is
  `typeof(EntidadBase).IsAssignableFrom(t.ClrType)` **and** the column. Both conditions are now
  required, so a future type carrying `created_at` without inheriting `EntidadBase` — which does
  not share the project's stamping convention, so its mark is not comparable against the anchor
  instant — falls to `SinMarca` (existence only), which OVER-blocks: the safe side. N2 recomputes
  with the SAME two-condition rule. **The golden did not change**, verified and not assumed: all 13
  `sinmarca` lines lack the column and every `marcado` line inherits `EntidadBase`. See M17/M18
  below for the honest consequence — on today's model the deviation is unobservable.
- [x] 3.25 **C4 (judge B) — an empty executable set returned `null` (fail-OPEN) while `Renderizar`
  threw for the same state.** `PrimeraDependenciaEnUsoAsync` now THROWS
  `InvalidOperationException`, matching `Renderizar`: an empty branch set means the inventory knows
  nothing about that anchor, not that the entity is pristine, and returning `null` asserted the
  second without having asked anything. Unit test
  `UnAnclaSinRamasEjecutablesTiraEnVezDeDevolverNull`.
- [x] 3.26 **C5 (both judges) — the `$` anchor accepted a trailing newline.** In .NET `$` also
  matches BEFORE a final `\n`, so `"stock\n"` passed `^[a-z_][a-z0-9_]*$` and everything after the
  line break would have been concatenated into the statement. The pattern is now
  `\A[a-z_][a-z0-9_]*\z` (absolute end of string) and the `"stock\n"` rejection case was added to
  the theory.
- [x] 3.27 **C6 (judge A, small).** (a) A `null` element of `valoresDeClave` reached
  `ParametrosDeComando.Agregar` unnormalized and produced an opaque Npgsql failure; it is now an
  `ArgumentException` NAMING the index and the property, the same shape as the count mismatch
  beside it. (b) `ElRenderizadorNuncaEmiteUnaTablaExcluida` asserted `numeraciones_clientes` for
  `PuntoVenta`/`Usuario`, where **no such FK exists** — a vacuous assertion — and omitted `Empresa`
  entirely. Each row now declares the carve-outs that ACTUALLY reference that anchor, the test pins
  that set before asserting the absence, and `Empresa` is in the theory with an EMPTY set, which is
  the row that states "no carve-out references an empresa today" and goes red the day one does.

**Recorded, no code change (by decision).**

| # | Finding | Record |
|---|---|---|
| R1 | Judge A — `InicializadorDeBaseDeDatos.cs:584` (`var ahora = reloj.Ahora`) stamps the stage-2 backfill's `listas_precio`/`clientes` rows for PRE-EXISTING tenants with a LATER startup instant than those tenants' own `created_at` | **Known OVER-BLOCK, fail-SAFE, discriminator deliberately unchanged.** Operator-facing consequence: such a tenant is permanently blocked by `clientes` even with zero customer data, and no retry or waiting clears it. Carried as a **slice-4 input**: the 409 names the blocking table, so the operator sees `clientes` and can tell this apart from real customer data. Fixing it means re-stamping backfilled rows to each tenant's own instant — a data change behind the ZERO-SCHEMA gate, out of scope here |
| R2 | Judge A — `ObtenerConexionAbiertaAsync` opens the connection and never closes it | **Replicated PRE-EXISTING pattern, not a new defect.** Byte-identical to the idiom already in `AsignadorDeNumeroCliente`, `ServicioDeVentas`, `ServicioDeStock` and `ServicioDeLotes`: the connection belongs to the caller's `DbContext` and its lifetime is the scope's, so closing it here would break the caller. Changing it is a five-call-site sweep of untouched code, not a slice-3 correction |

### Mutation evidence — slice 3, judgment-day round 1 (run, not reasoned)

Application suite baseline before and after every mutation: **422/422 green** (417 before this
round; +5 from N5, the two new `InspectorDeUso` unit tests, the `"stock\n"` theory case and the
`Empresa` theory row). New integration class baseline: **4/4 green**.

| # | Item | Clause under test | Mutation applied | Observed result |
|---|---|---|---|---|
| M15 | 3.21, 3.22 | The `Tenant` dependent set is the UNION, not the FK walk alone | `AgregarRamasDeAlcanceDeTenant(ancla, tipoAncla, ramas);` deleted from `InventarioCompleto` | **KILLED, 2 tests.** N5 named the exact table — *"Estas tablas llevan id_tenant y NO están en el inventario del ancla Tenant, así que un tenant que las usó lee PRÍSTINO (falla abierta): puntos_venta"* — and N3 printed `QUITADAS: Tenant \| puntos_venta \| id_tenant \| marcado` |
| M16 | 3.21, 3.23 | The same union, observed through EXECUTION against real Postgres | idem | **KILLED, 1 test** — `UnSegundoPuntoDeVentaBloqueaLaBajaDelTenant`: `Expected: "puntos_venta" / Actual: null`. The failure IS the fail-open: the guard reported a tenant with two puntos de venta as pristine |
| M17 | 3.24 | *(honesty row)* the `created_at`-column half of the two-condition `Marcado` rule | `llevaMarca && heredaDeEntidadBase` -> `llevaMarca` (the pre-fix predicate) | **SURVIVED, 422/422 green.** Recorded as a survivor, not dressed up |
| M18 | 3.24 | *(honesty row)* the `EntidadBase` half of the same rule | `llevaMarca && heredaDeEntidadBase` -> `heredaDeEntidadBase` | **SURVIVED, 422/422 green.** The two survivors together say the honest thing: on TODAY'S model the three formulations are the same function, because the mechanical-impossibility throw already guarantees `heredaDeEntidadBase => llevaMarca` and no type carries `created_at` without inheriting `EntidadBase`. There is no witness in the model, so no test over this model can see the difference — an equivalent mutant of the M3b class. The fix is design conformance for the day a witness appears, and N3's golden is what will name that day's table |
| M19 | 3.23 | The `ramas.Any(rama => rama.UsaAncla)` gate — the anchor instant is bound whenever any branch references `$n` | the whole `if (ramas.Any(...)) { Agregar(comando, ancla); }` block deleted | **KILLED, 4/4 integration tests**, with the real Npgsql message: `Npgsql.PostgresException : 08P01: bind message supplies 2 parameters, but prepared statement "" requires 3`. That is the 500 the whole suite used to survive |
| M20 | 3.23 | The bind ORDER — `valoresDeClave` first, the anchor instant LAST | the two `Agregar` blocks swapped | **KILLED, 4/4 integration tests**: `Npgsql.PostgresException : 42883: operator does not exist: integer = timestamp with time zone` (POSITION 111/112/144) — the `timestamptz` landed in `$1` against `id_empresa`/`id_punto_venta` |
| M21 | 3.25 | The execution path fails CLOSED on an empty executable set | the `throw` restored to `return null;` | **KILLED, 1 test** — `UnAnclaSinRamasEjecutablesTiraEnVezDeDevolverNull`: *"Assert.Throws() Failure: No exception was thrown"* |
| M22 | 3.26 | The `\z` ANCHOR specifically, isolated from the message text | only the `Regex` construction reverted to `"^[a-z_][a-z0-9_]*$"`, leaving `PatronDeIdentificador` (which the error message prints) untouched, so the theory's other four cases stay green and only the anchoring is under test | **KILLED, exactly 1 case** — `UnIdentificadorNoConformeSeRechaza(tabla: "stock\n")`: *"Assert.Throws() Failure: No exception was thrown"*. The first attempt reverted the shared constant and killed all five cases on the message assertion instead of the anchor; that run proved nothing about `\z` and was redone |
| M23 | 3.27 | The positional `null` rejection of `valoresDeClave` | the validation loop deleted | **KILLED, 1 test** — `UnValorDeClaveNuloSeRechazaNombrandoSuIndice` got `InvalidOperationException` / `Npgsql.NpgsqlException : Failed to connect to 127.0.0.1:5432` instead of `ArgumentException`: without the guard the container-free unit test reaches the connection, which is precisely the opaque failure the guard replaces |


### Judgment-day slice 3, round 2 — confirmed items corrected (FINAL round, no third)

Five confirmed items fixed. **ZERO schema, zero physical deletes, the guard stays INERT**
(`rg -n "PrimeraDependenciaEnUsoAsync" src/` still returns only its own declaration, now at
`InspectorDeUso.cs:86`): no configuration, no migration and no line under
`src/Ways.Infrastructure/` was touched. `dotnet ef migrations has-pending-model-changes` →
*"No changes have been made to the model since the last migration."*

- [x] 3.28 **R2-1 (CRITICAL, orchestrator-verified) — the `Empresa` anchor read PRISTINE with a
  full operating history: fail-OPEN, same class as round 0's C1 by a different mechanism.**
  NO operational table carries `id_empresa`: `comprobantes_venta`, `comprobantes_compra`, their
  items, `pagos_comprobante`, `movimientos_stock`, `stock`, `stock_lotes`, `turnos_caja`,
  `movimientos_caja`, `movimientos_tesoreria`, `movimientos_cuenta_corriente`, `presupuestos`,
  `remitos`, `ordenes_compra` and `gastos` all key on `id_punto_venta`. An empresa's DIRECT
  referencers are structure and catalogue only — the 13 lines the golden already had. The scenario:
  provision, load tenant-wide articles (`id_empresa` NULL, so no `articulos_empresas` row), sell at
  the provisioned punto de venta. All 13 branches fail — the 11 `marcado` ones need
  `created_at > T0` and the provisioned rows are exactly `T0`, the 2 `sinmarca` ones have no row —
  so the guard reports the empresa as PRISTINE while slice 4 soft-deletes it and its punto de
  venta.
  **The fix makes usage propagate UP the structural hierarchy**: a punto de venta in use means its
  empresa is in use. `InventarioDeDependientes` gained a THIRD source
  (`AgregarRamasPuenteadasPorPuntoDeVenta`): for the `Empresa` anchor, every branch of the
  `PuntoVenta` anchor's EXECUTABLE inventory is re-emitted BRIDGED by `puntos_venta` — same buckets,
  same carve-outs, nothing hand-written. The new `PuenteDeUso` record carries the bridge table and
  its two column vectors, and the renderer emits
  `... FROM "public"."<hoja>" d JOIN "public"."puntos_venta" pv ON d."<fk>" = pv."id_punto_venta"
  AND d."id_tenant" = pv."id_tenant" WHERE pv."id_empresa" = $1 AND pv."id_tenant" = $2
  [AND d."created_at" > $3]`. ONE statement, not N queries per punto de venta; identifiers still
  come only from EF metadata and still go through the `\A[a-z_][a-z0-9_]*\z` validation; parameters
  stay positional and in the existing bind order (`PropiedadesDeAncla(Empresa)` is still
  `Id, IdTenant`, so the anchor instant is still `$3`). The label returned to the operator is the
  **leaf** table (`turnos_caja`, `comprobantes_venta`, …), which is what the 409 has to name.
  **`Tenant` does NOT need this** — every table carries `id_tenant` and the scope-column source
  already brings it, verified by N5 on the `Tenant` anchor — and `PuntoVenta` and `Usuario` are
  leaves of the hierarchy. `ElConjuntoPuenteadoDeEmpresaEsElInventarioEjecutableDePuntoVenta`
  asserts exactly that: the bridged set equals `Construir(PuntoVenta)`, and the other three anchors
  emit zero bridged branches. *(design D2, D3, A; BO-R5)*
- [x] 3.29 **R2-2 — N5 is now GENERIC over the four anchors; "closes the CLASS" was an
  overstatement and is corrected in item 3.21.**
  `N5_TodaTablaDeAlcanceDelAnclaApareceEnSuInventario` is a theory over
  `(ancla, columnas de alcance)`: `id_tenant`; `id_empresa`;
  `id_punto_venta` + `id_punto_venta_destino`; `id_empleado` + `id_empleado_apertura` +
  `id_empleado_cierre` + `id_actor`. For each anchor the universe is every entity type in the model
  mapping one of those columns, minus the anchor's own table, and every `(tabla, columna)` PAIR of
  that universe must appear in that anchor's inventory (`excluido` lines count as present). The
  assertion NAMES the missing pair. Pair granularity, not table granularity, is what makes the net
  see a second FK from a table that already contributes one — `movimientos_stock` reaches
  `PuntoVenta` twice, and `ordenes_compra`/`turnos_caja` reach `Usuario` twice.
- [x] 3.30 **R2-3 — three stale records corrected in place** (judge A). (a) M4 cited
  `N1_LaCuentaDeRamasEsLaDeLasFksMenosLosCarveOuts`, which round 1 deleted as a tautology; the
  mutation was RE-RUN on this tree and the row now carries the observed result and messages.
  (b) M10 recorded the pre-C5 `^…$` pattern; the row now says so and points at M22 as the
  isolation of the anchor. (c) S1's `InspectorDeUso.cs:72` is now re-observed per round
  (`:72` → `:77` at `55fe4ac` → `:86` here) instead of copied forward.
- [x] 3.31 **R2-4 — production doc-comments state present INTENT, not review history** (judge B).
  `InventarioDeDependientes.ColumnaDeAlcanceDeTenant` no longer narrates what "quedaba FUERA" of
  the inventory; it says WHY the `Tenant` anchor needs a second source. `InspectorDeUso`'s pattern
  comment no longer narrates what "pasaba la validación"; it says why `\A`/`\z` and never `^`/`$`.
  Test-file comments keep their narrative — a test's reason to exist IS the defect it kills.
- [x] 3.32 **R2-5 — arity guard on the synthesized tenant-scope branch** (judge B, suggestion).
  `AgregarRamasDeAlcanceDeTenant` zips ONE hardcoded column (`id_tenant`) against
  `clavePrincipal.Properties`. A composite `Tenant` PK would make `Zip` truncate in silence and the
  branch would bind only the first property: silent UNDER-blocking. It now throws
  `InvalidOperationException` naming the key, the same shape as the other mechanical
  impossibilities, executed by N1 in CI. No witness exists on today's model (see M26 for the honest
  consequence).

**Golden N3 — every added line justified (V8).** `git diff --stat` on
`tests/Ways.Application.Tests/Persistencia/Fixtures/inventario-de-dependientes.txt` =
**17 insertions(+), 0 deletions(−)**. The rendering gained a `via` segment in the table field
(`Empresa | comprobantes_venta via puntos_venta | id_punto_venta,id_tenant | marcado`), still four
`" | "`-separated fields, so N3's comparison is unchanged. The 17 lines are exactly the 17
EXECUTABLE branches of the `PuntoVenta` anchor (its golden lines minus the `auditoria` carve-out),
each re-emitted for `Empresa` through `puntos_venta` with its bucket unchanged:

| Added line | Why it must be there |
|---|---|
| `comprobantes_venta via puntos_venta` (marcado) | a SALE is the canonical proof that a customer operated on this empresa |
| `comprobantes_compra via puntos_venta` (marcado) | purchases key on the PV, same argument |
| `gastos via puntos_venta` (marcado) | expenses key on the PV |
| `ordenes_compra via puntos_venta` (marcado) | purchase orders key on the PV |
| `presupuestos via puntos_venta` (marcado) | quotes key on the PV |
| `remitos via puntos_venta` (marcado) | delivery notes key on the PV |
| `turnos_caja via puntos_venta` (marcado) | a cash shift opened is operating history |
| `parametros via puntos_venta` (marcado) | PV-scoped parameters the customer edited; the empresa-scoped `parametros` row is a SEPARATE line and stays |
| `movimientos_stock via puntos_venta` ×2 (sinmarca) | outgoing AND incoming transfers — two FKs, two branches, per M11's rule |
| `movimientos_tesoreria via puntos_venta` (sinmarca) | treasury movements key on the PV |
| `movimientos_cuenta_corriente via puntos_venta` (sinmarca) | customer account ledger keys on the PV |
| `movimientos_cuenta_corriente_proveedor via puntos_venta` (sinmarca) | supplier account ledger keys on the PV |
| `stock via puntos_venta` (sinmarca) | a stock row exists only where the customer loaded stock |
| `stock_lotes via puntos_venta` (sinmarca) | lot-level stock, same argument |
| `numeraciones_comprobante via puntos_venta` (sinmarca) | a numbering counter for the PV means comprobantes were issued or configured there |
| `numeraciones_fiscales via puntos_venta` (sinmarca) | idem for the fiscal series |
| `movimientos_caja` — **absent on purpose** | it keys on `id_turno`/`id_empleado`, never on `id_punto_venta`, so it is not in the `PuntoVenta` inventory and is not bridged; `turnos_caja` covers the same history |
| `auditoria` — **absent on purpose** | it is a carve-out, so it is not in the EXECUTABLE inventory of `PuntoVenta` and is never bridged; `ElConjuntoPuenteadoDeEmpresaEsElInventarioEjecutableDePuntoVenta` asserts that |

The two `sinmarca` families deserve the honest note: `numeraciones_*` and `stock` block on
EXISTENCE, so the day a provisioning path seeds them for a new punto de venta, the empresa becomes
permanently undeletable. That is the OVER-block direction (fail-safe), it is what
`UnTenantReciennAprovisionadoEstaPristinoEnLasCuatroAnclas` and the round-2 baseline assertion
watch, and it is the direction this stage accepts.

### Mutation evidence — slice 3, judgment-day round 2 (run, not reasoned)

Application suite baseline before and after every mutation: **427/427 green** (422 before this
round; +3 from N5 becoming a four-case theory, +2 from the two new bridge rendering tests).
Domain: **545/545**. Inspector integration class baseline: **6/6** (4 before, +2 from the bridge).

| # | Item | Clause under test | Mutation applied | Observed result |
|---|---|---|---|---|
| M24a | 3.28 | The `Empresa` dependent set includes the family BRIDGED by `puntos_venta` | `AgregarRamasPuenteadasPorPuntoDeVenta(ancla, tipoAncla, ramas);` deleted from `InventarioCompleto` | **KILLED, 3 Application tests** — `N3` printed all 17 `QUITADAS` lines, starting at `Empresa \| comprobantes_compra via puntos_venta \| id_punto_venta,id_tenant \| marcado`, plus `UnaRamaPuenteadaUneLaHojaConPuntosVentaYLigaElAnclaSobreElPuente` and `ElConjuntoPuenteadoDeEmpresaEsElInventarioEjecutableDePuntoVenta` |
| M24b | 3.28 | The same family, observed through EXECUTION against real Postgres | idem | **KILLED, 2/6 integration tests** — `UnTurnoDeCajaEnSuPuntoDeVentaBloqueaLaBajaDeLaEmpresa`: *"Assert.Equal() Failure: Strings differ / Expected: \"turnos_caja\" / Actual: null"*, and `ElTurnoDeOtroTenantNoBloqueaLaBajaDeLaEmpresaPorElPuente` with the same message. The failure IS the fail-open: the guard reported an empresa with an open cash shift as pristine |
| M25 | 3.29 | N5 is generic and its universe is INDEPENDENT of the walk that produces the inventory | `.Where(fk => !(tipoAncla == typeof(Usuario) && fk.DeclaringEntityType.GetTableName() == "turnos_caja"))` inserted into the FK walk | **KILLED, 3 tests.** `N5(Usuario)` NAMED the exact pairs — *"Estos pares (tabla, columna) están scopeados por el ancla Usuario y NO están en su inventario, así que una entidad que los usó lee PRÍSTINA (falla abierta): turnos_caja\|id_empleado_apertura, turnos_caja\|id_empleado_cierre"*. Said honestly: `N1(Usuario)` and `N3` also went red, because for the `Usuario` anchor EVERY branch comes from the FK walk, so no mutation of that walk can be invisible to N1. N5's independence is what covers the anchors whose set is NOT only the walk |
| M26 | 3.32 | *(reachability row)* the arity guard of the synthesized tenant-scope branch | `clavePrincipal.Properties.Count != 1` → `!= 2`, so today's single-property key trips it | **KILLED, 6 tests**, with the message the guard exists to print: *"El ancla Ways.Domain.Organizacion.Tenant tiene una clave primaria de 1 propiedades (Id) y la rama de alcance de tenant solo puede zipear la columna id_tenant: el inventario necesita una columna de alcance por propiedad de la clave."* What it proves and what it does NOT: it proves the guard is REACHED on every `Construir(Tenant)` and that its message names the key. It does NOT prove a composite `Tenant` PK would be caught — no such witness exists in this model, and none was fabricated. Same honesty class as M3b/M17/M18 |
| M4 (re-run) | 3.9, 3.11, 3.30 | `GetReferencingForeignKeys()` is the only source of the DIRECT set | see the M4 row above, re-applied on this tree | **KILLED, 7 tests** — the full message set is recorded in the M4 row |

**Structural rows for round 2**

| # | Item | Assertion | Evidence |
|---|---|---|---|
| S7 | 3.28 | The guard is still INERT (V10) | `rg -n "PrimeraDependenciaEnUsoAsync" src/` → exactly **one** line, its own declaration at `InspectorDeUso.cs:86`. `rg -n "InspectorDeUso" src/` → 5 lines, unchanged |
| S8 | 3.28 | ZERO schema (V1) and zero Infrastructure files touched (V2/V3/V5/V6) | `git diff --stat` touches 6 files: 2 under `src/Ways.Application/Organizacion/`, 3 test files and the golden fixture. `dotnet ef migrations has-pending-model-changes` → *"No changes have been made to the model since the last migration."* |
| S9 | 3.28 | ZERO physical deletes (V4), and the only SQL is still read-only (V13) | the diff adds one `JOIN` inside the existing `SELECT … WHERE EXISTS` and nothing else; a scan for `ExecuteDelete`, `RemoveRange(`, `.Remove(` and `DELETE FROM` over the changed production files returns zero matches |

---

## Slice 4: Deletion API — routes, cascade, minimums and the `Usuario` guard (PR 4)

**Branch**: `feat/stage20-slice4-bajas-api`. **Start**: PRs 1 and 3 merged. **Finish**: three DELETE
routes exist under existing policies, the guard has its first callers, the cascade shares one
instant, the two structural minimums fire, `Usuario` deletion is guarded after `PoliticaDeRoles`, all
six 409 codes are live, N4 is green and U1-U8 each have their kill. **Depends on**: slice 1 (nothing
functional, but it is the merge order) and **slice 3** (the guard). **Estimate**: ~530 lines,
including the budgeted test relocation. **Rollback**: revert removes three routes and one guard call;
rows already soft-deleted stay soft-deleted and hidden — a **pre-existing, supported state**
(`Usuario` has produced it since stage 1). **Skills**: `mutation-proof-tests` (rules 3, 5, 12c, 13,
14), `work-unit-commits`. **`db-error-backstops`: N/A** — no constraint can fire, so there is no
SQLSTATE to classify and `ManejadorDeErrores.cs` stays untouched (V6).

**BINDING — OD5.** `empresa_en_uso` and `punto_venta_en_uso` are **unreachable through the API
today** because no endpoint creates a second empresa or punto de venta, so the structural minimum
fires first on every attempt. **Do NOT write API-level integration tests for those two codes.** They
are proven at the service/integration layer with a hand-seeded second empresa/PV (below the
confound). Do **not** add creation endpoints. This latency is reported to the owner at delivery.

**BINDING — OD6.** A cascade-deleted admin gets **401 `credenciales_invalidas`**, not 403. The
`tenant-organization` scenario claiming 403 is superseded (Reconciliación 1).

**BINDING — INPUTS CARRIED FROM SLICE 1 (judgment-day FINAL re-judgment).** Three items were
confirmed by both judges after the two permitted correction rounds were spent. None has user impact
today; all three become reachable exactly when this slice adds the deletion writers, so this slice
closes them deliberately rather than inheriting them silently.

1. **The three `Count` subqueries of `ProyeccionDeTenant` are not hardened.**
   `ServicioDeOrganizacion.cs:40-42` (`db.Empresas.Count`, `db.PuntosVenta.Count`,
   `db.Usuarios.Count`) still lean on the ambient `"BajaLogica"` filter, while the four owner-name
   subqueries carry an explicit `DeletedAt == null` after R2-2. Worse, the file now states two
   contradictory rules for the identical risk: the doc-comment at `:29-34` says no explicit
   predicate is needed, and the one at `:116-120` says the opposite. Round 1's M3 already observed
   this exact mechanism firing on a counter. **This slice must harden the three counters and
   reconcile the two doc-comments into one rule.**
2. **Four production predicates ship with four SURVIVING mutants.** `mutation-proof-tests`'s
   decision gate says a clause whose deletion the suite survives must be **re-routed below the
   confound, never called done**. Today no path strips the ambient filter, so no RED is reachable
   and the surviving mutants were honestly recorded rather than dressed up — but the debt is real.
   **This slice makes them provable**: the deletion writers are the reachable path, so each of the
   four predicates gets a real kill here, or the predicate is removed as unprovable. Do not carry a
   third unproven round.
3. **`TenantsDeRelleno = UsuariosDelTenantLeido` is an unenforced coincidence.**
   `ProyeccionDeOrganizacionTests.cs:45-50` guarantees the tenant under test outranks every seeded
   counter only because `4` happens to be the maximum of `{2, 3, 4}`. Lowering that const or raising
   a sibling silently breaks the bound with no compile-time or runtime check. **Tie it to the actual
   maximum** when this slice next touches that fixture.

**BINDING — INPUT CARRIED FROM SLICE 1 (judgment-day ronda 2).** Slice 1's write paths
(`ActualizarTenantAsync`, `CambiarEstadoTenantAsync`, `ActualizarEmpresaAsync`,
`ActualizarPuntoVentaAsync`) **re-project after `SaveChangesAsync`** and return a domain `404` when
the row went invisible between the write and the re-read — so a caller can get `404` for a write that
persisted. That is unreachable until **this slice** adds the deleters, which is why it was not
patched in slice 1's round 2. **This slice must close it deliberately** and record which of the two
options it took: (a) put the write and its re-projection inside **one transaction**, or (b) have the
write paths return the record they already hold instead of re-reading. Also add the **four**
explicit `DeletedAt == null` predicates slice 1 shipped as defence-in-depth
(`ServicioDeOrganizacion.ProyeccionDeEmpresa` ×1 and `ProyeccionDePuntoVenta` ×2,
`ServicioDeUsuarios.NombreDeTenantAsync` ×1) to the U-row conjunct enumeration: each one **survived
its mutation in round 2** because nothing strips `"BajaLogica"` on those paths today, and a
deletion writer is exactly what makes them reachable — each one then needs its own kill. Leaving
either item as-is is not a valid outcome for this slice.

**BINDING — OD4.** A soft-deleted dependent still blocks (task 4.11).

**Guarded-write conjunct enumeration (`mutation-proof-tests` rule 3, up front, before any test is
written)** — transcribed from `design.md:383-398`:

| # | Statement | Conjuncts | The test that kills each |
|---|---|---|---|
| **U1** | `usuarios WHERE IdTenant == @id` (tenant cascade) | (a) `IdTenant == id` | A **sibling tenant** with its own admin: its usuario stays live, asserted by identity **and** by exact count (rule 12c) |
| **U2** | `puntos_venta WHERE IdTenant == @id` | (a) `IdTenant == id` | Same sibling-tenant pair |
| **U3** | `empresas WHERE IdTenant == @id` | (a) `IdTenant == id` | Same sibling-tenant pair |
| **U4** | `puntos_venta WHERE IdEmpresa == @id` (empresa cascade) | (a) `IdEmpresa == id` | A **second empresa of the SAME tenant**, hand-seeded: its PV stays live |
| **U5** | `COUNT(empresas WHERE IdTenant == @id)` (minimum) | (a) `IdTenant == id` | A sibling tenant's empresa must not be counted, or the minimum never fires |
| **U6** | `COUNT(puntos_venta WHERE IdEmpresa == @id)` | (a) `IdEmpresa == id` | A sibling empresa's PV must not be counted |
| **U7** | Guard branch, `Marcado` | (a) `<fk> = @id` · (b) `created_at > @ancla` **strict** | (a) a dependent of a **sibling** entity must not block. (b) **two kills**: a row created **exactly at** the anchor must **not** block (rule 14 boundary fixture under `RelojFijo`), and a row one tick later **must** block |
| **U8** | Guard branch, `SinMarca` | (a) `<fk> = @id` only | Adding `AND created_at > @ancla` must fail (no such column); a `Stock` row for the PV blocks with no timestamp involved |

### Implementation

- [ ] 4.1 Create `src/Ways.Application/Organizacion/EtiquetasDeTablas.cs` — the label dictionary
  (`comprobantes_venta` → *"ventas"*, `articulos` → *"artículos"*, …) with the fallback *"datos
  cargados"* for an unmapped table. **This is not the hand list B4 forbids**: it decides only **how
  to word** an already-decided block, so a missing entry costs a vaguer sentence, never a wrong
  verdict. *(BO-R11)*
- [ ] 4.2 Modify `ServicioDeOrganizacion` — `EliminarTenantAsync`, inside
  `db.Database.CreateExecutionStrategy().ExecuteAsync` (**never** `BeginTransaction` outside it —
  the ADR-16 trap), in this exact order: `pg_advisory_xact_lock(idTenant, -20)` → **re-read the
  anchor under the lock** (404 if a concurrent delete won) → usage guard **evaluated ONCE, with no
  pre-check** → `var momento = reloj.Ahora` → cascade writes + anchor write → **one**
  `SaveChangesAsync` → COMMIT. **No pre-check**: `mutation-proof-tests` rule 3 names the
  pre-check-mirroring-a-guard shape as this repository's most common confound; running the guard once
  removes the confound instead of writing tests to defeat it. *(BO-R9, TO-R4; design D7, D11, F)*
- [ ] 4.3 Same method — the tenant write sets `DeletedAt` **and** `Estado = EstadoTenant.Baja` in the
  **same** `SaveChangesAsync` (two statements would admit an interleaving where the row is deleted but
  still `activo`). This is the enum value's **first and only writer**; suspension and reactivation
  keep refusing to touch it. *(TO-R5; design D10)*
- [ ] 4.4 Same method — the cascade is `Where(hijo.IdTenant == id)` over **live rows only**, covering
  `usuarios`, `puntos_venta` and `empresas`, all sharing the single `momento` on **both** `DeletedAt`
  and `UpdatedAt`. **Write order is NOT claimed** — EF chooses statement order inside one
  `SaveChanges`; atomicity is the property and one transaction delivers it. **S3**: an
  already-deleted child keeps its **original** `deleted_at`, otherwise the restore-by-instant rule is
  destroyed for the earlier deletion. The cascade **MUST NOT** extend to areas, medios de pago,
  listas de precio, the Consumidor Final cliente or numeraciones. *(BO-R9; design D9, S3)*
- [ ] 4.5 Same service — `EliminarEmpresaAsync`: same transaction/lock shape, with the **structural
  minimum first** (`ultima_empresa_del_tenant` when the tenant has exactly one surviving empresa),
  then the usage guard (`empresa_en_uso`), then the cascade to `puntos_venta WHERE IdEmpresa == @id`.
  **S2**: the minimum's `COUNT` excludes logically deleted siblings. **S6**: when both a minimum and
  usage apply, the **structural code wins**. *(BO-R10, BO-R9, TO-R4)*
- [ ] 4.6 Same service — `EliminarPuntoVentaAsync`: structural minimum first
  (`ultimo_punto_venta_de_la_empresa`), then the usage guard (`punto_venta_en_uso`). No children, no
  cascade. *(BO-R10, TO-R4)*
- [ ] 4.7 Same service — the class doc-comment's *"este servicio no crea ni elimina nada"* is
  corrected; every refusal is raised as `ErrorDominio.Conflicto("<codigo_snake_case>", "<mensaje>")`
  with the message built through `EtiquetasDeTablas` (Spanish copy, snake_case codes — OD2).
  *(BO-R11)*
- [ ] 4.8 Modify `src/Ways.Application/Usuarios/ServicioDeUsuarios.EliminarAsync` (`:296`) — insert
  **one** guard call **after** `PoliticaDeRoles.ValidarPuedeIntervenirSobre` and **before** the
  `DeletedAt` write, yielding `usuario_en_uso`. **NO transaction and NO lock** (D12): there is no
  cascade, so the single-`momento` property is already satisfied by one `SaveChanges`, and the lock
  closes no race it faces. Every existing rule is preserved verbatim — Root targets undeletable,
  self-deletion forbidden, `ValidarAlcanceDeTenant`'s deliberate 404-not-403 (ADR-8) untouched, the
  audit record still written. *(UT-R2, BO-R12)*
- [ ] 4.9 Modify `src/Ways.Api/Endpoints/OrganizacionEndpoints.cs` — three `MapDelete`:
  `/api/plataforma/tenants/{id}` under `Politicas.SoloPlataforma`, `/api/empresas/{id}` and
  `/api/puntos-venta/{id}` under `Politicas.GestionDeOrganizacion` (note the deliberate asymmetry:
  **reading** puntos de venta stays `LecturaDePuntosVenta` for the POS selector, **deleting** does
  not). **`Politicas.cs` MUST stay untouched — zero new policies (V5).** The class doc-comment's
  *"acá no hay `POST` ni `DELETE` a propósito"* (`:11-13`) is corrected. *(TO-R4)*
- [ ] 4.10 **Budgeted relocation** — move the `EliminarAsync` cases of
  `tests/Ways.Application.Tests/Organizacion/ServicioDeOrganizacionTests.cs` and
  `tests/Ways.Application.Tests/Usuarios/ServicioDeUsuariosTests.cs` to
  `tests/Ways.IntegrationTests/BajasDeOrganizacionTests.cs`: the guard's raw SQL **cannot run on the
  InMemory provider** (design fact 9, the `ServicioDeOfertas.ActualizarAsync` precedent). Every
  relocated case must keep asserting the **same** behaviour it asserted before — a relocation that
  quietly weakens an assertion is a regression, and the PR body must state that each moved case is
  behaviour-identical. *(UT-R2; Reconciliación 5)*

### Tests (all in `tests/Ways.IntegrationTests/BajasDeOrganizacionTests.cs` unless stated)

- [ ] 4.11 [P] **N4 — pristine regression (never degradable).** A **freshly provisioned** tenant, its
  empresa, its punto de venta and its admin are **all pristine**. This is the only net that can see
  the provisioning baseline drifting: it goes red the moment
  `ServicioDeAprovisionamiento.cs:46`'s single-clock-reading property breaks, or a future stage makes
  provisioning create an untimestamped row. *(BO-R2; design B/N4)*
- [ ] 4.12 [P] **"One article blocks" (never degradable).** Load exactly **one article** — no sale,
  no stock movement, no shift — then attempt to delete the tenant, the empresa and the punto de
  venta, and assert each returns **its own named 409**: `tenant_en_uso` at the API,
  `empresa_en_uso`/`punto_venta_en_uso` **below the API** (OD5 — the structural minimum fires first
  through the routes). **B2 proven, not asserted.** *(BO-R3)*
- [ ] 4.13 [P] **Carve-out 1 (never degradable)**: an entity whose only dependents past the anchor
  are `auditoria` rows is **deletable**, and the audit trail keeps rendering afterwards because the
  referenced row survives logically. *(BO-R6, B5; UT-R2's own audit scenario)*
- [ ] 4.14 [P] **Carve-out 2 (never degradable)**: a tenant whose only untimestamped dependent is its
  provisioned `numeraciones_clientes` row is **deletable**. *(BO-R6)*
- [ ] 4.15 [P] **OD4 behavioural proof**: create one article, **delete it logically**, then attempt
  the tenant deletion — it is **still** refused with `tenant_en_uso`. Usage means *"the customer ever
  operated here"*, not *"there is live data right now"*. *(BO-R7; OD4)*
- [ ] 4.16 [P] Cascade, asserted by **instant equality**, not by non-null: tenant + empresa + punto
  de venta + admin all carry an **identical** `deleted_at`, and the tenant carries `estado = 'baja'`.
  Read with query filters ignored. *(BO-R9, TO-R5)*
- [ ] 4.17 [P] Cascade boundary: after the same deletion, `areas`, `medios_pago`, `listas_precio`,
  `clientes` and `numeraciones_clientes` of that tenant are **present with `deleted_at IS NULL`**;
  and `GET /api/empresas`, `GET /api/puntos-venta`, `GET /api/usuarios` return **none** of the
  cascaded rows for a platform actor (no orphan remains visible). *(BO-R9)*
- [ ] 4.18 [P] **S3**: a tenant whose only empresa was **already** logically deleted earlier is still
  deletable, and that empresa keeps its **original, older** `deleted_at` — the cascade must not
  re-stamp it. *(BO-R9, S3)*
- [ ] 4.19 [P] Deleting an empresa cascades **only** to its puntos de venta: with a hand-seeded
  second empresa in the same tenant, the tenant and its usuarios are untouched. *(BO-R9; kills U4)*
- [ ] 4.20 [P] **U1-U3 sibling kills**: a **sibling tenant** with its own empresa, punto de venta and
  admin — every one of its rows stays live after the first tenant's deletion, asserted by identity
  **and** by exact count (rule 12c). *(BO-R9; kills U1, U2, U3)*
- [ ] 4.21 [P] **U5-U6 minimum kills**, below the API (OD5): a sibling tenant's empresa must not be
  counted towards `ultima_empresa_del_tenant`, and a sibling empresa's PV must not be counted towards
  `ultimo_punto_venta_de_la_empresa` — otherwise the minimum never fires. *(BO-R10)*
- [ ] 4.22 [P] **U7 boundary pair under `RelojFijo`** (rule 14): a dependent created **exactly at**
  the anchor instant does **not** block; a dependent created **one tick later** does. This is the
  `>` versus `>=` kill and it is the discriminator's whole correctness. Plus U7(a): a dependent of a
  **sibling** entity must not block. *(BO-R2)*
- [ ] 4.23 [P] **U8**: an untimestamped dependent blocks on **mere existence** — one `stock` row for
  the punto de venta refuses its deletion with no timestamp involved, and no `created_at` comparison
  is applied to a bucket-2 type (the column does not exist). *(BO-R5)*
- [ ] 4.24 [P] **BO-R4 discovery scenarios**: a `movimientos_stock` row referencing the punto de
  venta **only through `id_punto_venta_destino`** blocks it (secondary FK to the same principal); a
  `turnos_caja` row referencing the usuario **only through `id_empleado_cierre`** blocks it
  (non-conventional FK property name). *(BO-R4)*
- [ ] 4.25 [P] **BO-R8 behavioural proof**: a shared catalogue row with `id_empresa IS NULL`, created
  after the empresa's anchor, does **not** contribute to the usage verdict. *(BO-R8)*
- [ ] 4.26 [P] **Structural minimums, below the API (OD5)**: `ultima_empresa_del_tenant` and
  `ultimo_punto_venta_de_la_empresa` fire on their exact condition and **not** on any other; deleting
  one of **two** pristine empresas succeeds; **S2** — an already-deleted sibling does not count as a
  survivor, so the last live empresa still gets `ultima_empresa_del_tenant`; **S6** — when a minimum
  and usage both apply, the response is the **structural** code. *(BO-R10)*
- [ ] 4.27 [P] **The six-code set, exact**: one fixture per code constructed to satisfy only that
  code's condition, each returning `409` with exactly its own `codigo`; and an **unlabelled** blocking
  table still yields the exact `codigo` with the `mensaje` degraded to the generic phrase. Application
  unit test for the label dictionary itself (mapped table → its Spanish word; unmapped → *"datos
  cargados"*). *(BO-R11)*
- [ ] 4.28 [P] **UT-R2 ordering and preservation**: a usuario stamped on a comprobante created after
  their own `created_at` is refused with `409 usuario_en_uso` and `deleted_at` is **not** written; a
  never-used usuario is deleted with the audit record written; the provisioned `admin` is deletable
  **until** it opens a shift and refused afterwards; a **Root target with heavy usage** yields the
  pre-existing `PoliticaDeRoles` error, **not** `usuario_en_uso`; self-deletion is still forbidden
  regardless of usage. *(UT-R2)*
- [ ] 4.29 [P] **BO-R12 / anti-oracle**: an out-of-scope DELETE (empresa or usuario of another
  tenant) returns **404**, identical in status and body shape to a non-existent id — never 403, never
  a 409 that discloses usage, even when the target is heavily used. *(BO-R12, UT-R2)*
- [ ] 4.30 [P] **Idempotent-safe**: a second DELETE on an already-deleted row returns **404**, not
  500, and writes no second `deleted_at`. *(BO-R1)*
- [ ] 4.31 [P] **OD6 login pair**: a **cascade-deleted admin** attempting to log in receives
  **`401 credenciales_invalidas`** (the lookup runs under `"BajaLogica"` with no
  `IgnoreQueryFilters`, so the user is simply not found and the request dies at
  `ServicioDeAutenticacion.cs:104`); and — as a **regression** — a **suspended** tenant's user still
  receives **`403 tenant_suspendido`**. Two tests, two codes. *(TO-R5 as superseded by OD6;
  Reconciliación 1)*
- [ ] 4.32 [P] **Cross-tenant RLS, read AND write pair, on the `ways_app` connection** (rule 5 — a
  superuser fixture proves nothing) for all four routes: the guard sees every dependent of the
  actor's own tenant (cannot under-count) and can never observe another tenant's dependent.
  *(BO-R7, BO-R12)*
- [ ] 4.33 [P] **Authorization regressions**: a tenant `admin` is rejected by
  `Politicas.SoloPlataforma` on the tenant DELETE; a `vendedor` who can `GET /api/puntos-venta`
  through `LecturaDePuntosVenta` is rejected by `Politicas.GestionDeOrganizacion` on the PV DELETE;
  suspension and reactivation behave exactly as before and neither reads nor writes `deleted_at`;
  reactivating a **deleted** tenant is a **404** (S4) with the pre-existing `409 tenant_dado_de_baja`
  preserved unchanged as the unreachable backstop. *(TO-R4, TO-R5, S4)*
- [ ] 4.34 **[S]** Structural: **zero physical deletes** — repository scan for `ExecuteDelete`,
  `ExecuteDeleteAsync`, `Remove(`, `RemoveRange(` and `DELETE FROM` over `tenants`, `empresas`,
  `puntos_venta`, `usuarios`. Recorded as a file/state assertion, **never** dressed up as a runtime
  kill. *(BO-R1; never degradable; verify criterion V4)*
- [ ] 4.35 **[S]** Structural: **disjoint lock sets** — the deletion methods touch only organization
  tables, none of which appears in the program's total order (`numeraciones_fiscales → turnos_caja →
  comprobantes_venta → presupuestos → remitos → lotes → stock/stock_lotes → clientes → ledger
  INSERT`), so no deadlock against an operational path is expressible. Asserted structurally (rule 13
  — a live deadlock cannot be forced through raw ADO and a single-resource race test is blind to
  order). *(design G)*
- [ ] 4.36 **[S]** Structural: **FK index coverage** — compare the generated branch set against
  `pg_indexes` and **report** any branch with no supporting index. The check **must not fix** an
  uncovered branch: that would be DDL and the gate is ZERO-SCHEMA. An uncovered branch becomes a
  **named finding for a later stage**, not a silent seq scan and not a blocker here. *(design D, T6)*
- [ ] 4.37 GATE GUARD + non-regression — re-assert V1-V6 and V13; Domain + Application + Integration
  suites green (**never** run integration suites concurrently against the same Docker daemon);
  `dotnet build Ways.slnx` clean.
- [ ] 4.38 `judgment-day` round to a clean round.
- [ ] 4.39 Open PR 4 `feat/stage20-slice4-bajas-api`, record mutation evidence for **every** U-row
  (U1-U8) plus N4 in the PR body, with the `[S]` rows recording their file/state/definition assertion
  **and saying so** (V11), merge to `main` after the clean round.

---

## Slice 5: Deletion web — buttons, confirmation and code→copy (PR 5)

**BINDING — INPUTS CARRIED FROM SLICE 2 (judgment-day FINAL re-judgment, round budget exhausted).**
None has user impact today; every one becomes reachable exactly when this slice adds delete buttons
to the four screens, so this slice closes them deliberately.

1. **`ocupado` latches on generation mismatch inside the WRITE paths.** R2-3 replicated the ungated
   `finally` only to the post-write refresh. The mismatch `return`s inside the writes themselves —
   `Usuarios.tsx:213/:220/:231`, `Tenants.tsx:88/:95`, `Empresas.tsx:79-84`,
   `PuntosVenta.tsx:107-113` — still exit with `ocupado` set and would freeze the screen. Unreachable
   today (rule 9 disables every generation bumper while `ocupado` is set). **Delete buttons are the
   first second bumper; close this before adding them.**
2. **`ERROR_ALTA_SIN_TENANTS` lives in the shared `error` slot** (`Usuarios.tsx:177`) that R2-1
   proved unsafe for that class — a later successful load erases it. Unreachable (M35 survivor). Give
   it its own slot or fold it into the tenant-universe banner when touching that form.
3. **Sentinel leaks into copy on a crafted name.** With a tenant literally named
   `"Plataforma (sin tenant)"`, `desempatarHomonimos` suffixes the platform option as
   `(tenant sin-tenant)` (`organizacion.ts:147-154`). Keys untouched, platform-authored, copy only.
   Exclude the platform option from the SUFFIX (keep it in the collision count) so the sentinel never
   renders.
4. Cosmetics: `errorPassword` not cleared on Cancelar/Buscar; the tenant-failure banner at
   `Usuarios.tsx:350` is not gated on `esPlataforma` (cannot fire for an admin, but every sibling
   element is gated — add the gate for parity); identifier typo `tenanteReconciliado`
   (`PuntosVenta.tsx:71`).


**Branch**: `feat/stage20-slice5-bajas-web`. **Start**: PRs 2 and 4 merged. **Finish**: the four root
screens can delete, behind a confirmation gate and the full `react-async-state` write discipline, and
every 409 `codigo` maps to its own copy; docs 09/10 carry the Etapa 20 note. **Depends on**: slice 2
(the screens) and slice 4 (the routes). **Estimate**: ~340 lines. **Rollback**: revert removes the
buttons; the API still works but nobody can press it. **Skills**: `react-async-state` (rules 2-6, 9
and **rule 10: the pattern is replicated across all four screens in the same PR**),
`web-descriptor-tests`, `dto-contract-honesty`, `work-unit-commits`.

- [ ] 5.1 Modify `src/Ways.Web/src/api/organizacion.ts` — `eliminarTenant`, `eliminarEmpresa`,
  `eliminarPuntoVenta`. `src/Ways.Web/src/api/usuarios.ts`'s `eliminar` already exists and keeps its
  signature. *(TO-R4, UT-R2)*
- [ ] 5.2 Create the `codigo` → copy mapping (a pure module beside the helpers), covering all six
  codes plus the 404 and the generic fallback. The web keys its copy off **`codigo`**, never off
  `mensaje`: changing `mensaje` must not change which copy is selected. *(BO-R11)*
- [ ] 5.3 Modify `Tenants.tsx` — delete button + confirmation gate + the full `react-async-state`
  write discipline: full-window disabled state per entity while the write is outstanding, supersede
  blocked while a write is outstanding, re-entrancy guard, and a post-write refresh failure reporting
  *"se eliminó, pero no se pudo actualizar la vista"*. *(TO-R4)*
- [ ] 5.4 Modify `Empresas.tsx` — the same pattern, verbatim (rule 10). *(TO-R4)*
- [ ] 5.5 Modify `PuntosVenta.tsx` — the same pattern, verbatim. *(TO-R4)*
- [ ] 5.6 Modify `Usuarios.tsx` — the same pattern applied to the **existing** "Baja" button
  (`:120-122`), which now has to render `usuario_en_uso` and the pre-existing `PoliticaDeRoles`
  refusals. *(UT-R2)*
- [ ] 5.7 [P] Extend the four `*.test.tsx` files created in slice 2: the confirmation gate blocks the
  call until confirmed; the full-window disabled state appears per entity; a second click while a
  write is outstanding is dropped; the post-write refresh failure renders its own copy; **each 409
  `codigo` maps to its own copy** and a changed `mensaje` does not change the selection. *(BO-R11,
  TO-R4, UT-R2; `react-async-state` rules 2-6, 9, 10)*
- [ ] 5.8 [P] Test: a `404` on delete (already-deleted row, or out-of-scope target) renders the
  neutral not-found copy — **never** a usage disclosure, preserving the anti-oracle at the UI layer
  too. *(BO-R12)*
- [ ] 5.9 Modify `docs/09-multi-tenancy.md` and `docs/10-modelo-de-datos.md` — an **"Etapa 20"** note
  covering the deletion semantics, the pristine discriminator, the three buckets and two carve-outs,
  the cascade boundary and the six codes. **No schema table changes** — the stage ships zero DDL.
- [ ] 5.10 GATE GUARD + non-regression — `npm --prefix src/Ways.Web run test`, `run build`
  (typecheck) and `run lint` clean; re-assert V1-V6 on the full stage diff (this is the last slice:
  the last migration must **still** be `20260822002214_FiscalArcaEtapa19a.cs`,
  `has-pending-model-changes` clean, `InicializadorDeBaseDeDatos.cs` / `Politicas.cs` /
  `ManejadorDeErrores.cs` untouched, zero physical deletes across the whole stage).
- [ ] 5.11 `judgment-day` round to a clean round.
- [ ] 5.12 Open PR 5 `feat/stage20-slice5-bajas-web`, merge to `main` after the clean round. **Then
  report to the owner, at delivery (OD5): empresa and punto de venta deletion ships LATENT** —
  correct and tested below the API, but unreachable through the API until an endpoint that creates a
  second empresa or punto de venta exists, because the structural minimum fires first on every
  attempt. Also report the two deferred items that remain open by decision: **R1** (a sale committed
  between the guard's read and the deletion's commit — accepted, recovery is a one-line `UPDATE`
  because nothing is destroyed) and **T6** (FK index coverage is *reported*, not guaranteed, since
  adding an index would be DDL).

---

## Deferred, unchanged (no task in this stage)

Force/override delete · undelete/restore · retro-guarding the other unguarded soft deletes
(`ServicioDeCatalogo<T>`, `ServicioDeClientes`, `ServicioDeProveedores`, `ServicioDeArticulos`,
`ServicioDeOfertas` — `InspectorDeUso` is entity-agnostic so adoption is a one-line change) ·
`Estado`/suspension for empresa and punto de venta · server-side pagination or filtering for the four
root lists · the minimum-administrators-per-tenant invariant (`ultimo_admin_del_tenant`) · drill-down
navigation from a tenant into its children · a "show deleted" toggle · and the owner's reserved
carryovers (the `importe` CHECK micro-gate, the `articulos_empresas` replace-set gap,
`stage-18-etiquetas-y-consulta`). Each keeps the reopen condition recorded in `state.yaml:416-433`.
