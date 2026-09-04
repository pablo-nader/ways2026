# Design: Stage 20 — Organization relationships and usage-guarded logical deletion

## Technical Approach

**Ask EF what points at this row, ask the clock whether the customer put it there, and refuse loudly
— and because no constraint can ever fire behind that refusal, every classification decision is
pinned by a checked-in inventory instead of by a hope.**

The proposal is the ratified contract (`state.yaml:9-55`, `db_gate: ZERO-SCHEMA-RATIFIED`). This
design fixes the *how* and arbitrates the seven things the proposal deliberately left open
(A: untimestamped types · B: the completeness test · C: `IModel` access · D: query cost ·
E: query filters · F: cascade atomicity · G: concurrency), and corrects three claims that did not
survive contact with the tree.

Nine structural facts decide the shape. Each was read, not assumed.

1. **`Empresa` and `PuntoVenta` carry no navigation properties.** `Empresa : EntidadTenant` has
   `Id, RazonSocial, NombreFantasia, Cuit, IdCondicionFiscal` and nothing else (`Empresa.cs:9-26`);
   the existing projections read raw ids (`ServicioDeOrganizacion.cs:95, 136-138`). Owner names can
   therefore only be projected as **correlated scalar subqueries**, never as `e.Tenant!.Nombre` —
   which also removes the INNER-JOIN-drops-the-row trap for free.

2. **There is no endpoint that creates a second empresa or a second punto de venta.**
   `AprovisionamientoEndpoints` exposes only `POST /api/plataforma/tenants`, and
   `OrganizacionEndpoints.cs` has no `POST` at all. Every tenant in the tree therefore has exactly
   **one** empresa and **one** punto de venta. Consequence the proposal did not draw: decision 3's
   structural minimums fire on **every** empresa/PV delete attempt, so `empresa_en_uso` and
   `punto_venta_en_uso` are **unreachable through the API today** — see D8 and T1.

3. **A cascade-deleted admin cannot reach the 403 branch.** The login lookup runs under the
   `"BajaLogica"` filter (`ServicioDeAutenticacion.cs:85-87`, no `IgnoreQueryFilters`), so a
   soft-deleted usuario is `null` and the request dies at `:104` with **401
   `credenciales_invalidas`**. The `tenant is null → 403 tenant_suspendido` branch (`:141-147`) is
   correct and untouched, but for a tenant this stage deletes it is **unreachable**, because the
   cascade takes the admin with it. See T2.

4. **The clock is read once and stamped everywhere.** `ServicioDeAprovisionamiento.cs:46`
   (`var ahora = reloj.Ahora;`) reaches the tenant (`:53-54`), empresa (`:69-70`), punto de venta
   (`:79-80`), área (`:89-90`), medios de pago (`:104-105`), lista de precios (`:121-122`), cliente
   (`:142-143`) and admin (`:158-160`). B3 holds exactly.

5. **The metadata-walk idiom is already the house style.** `AplicarFiltroDeBajaLogica`
   (`WaysDbContext.cs:426-447`) and `AplicarFiltroDeTenantEnTenant` (`:477-486`) both walk
   `modelBuilder.Model` by reflection to install behaviour uniformly. The guard is the third.

6. **Nobody implements `IWaysDbContext` by hand.** `rg ": IWaysDbContext"` over `src/` and `tests/`
   returns **zero** matches; the four test files that name the type consume it as a parameter and
   pass a real `WaysDbContext` on the InMemory provider. C's blast radius is therefore one line.

7. **The model can be built without a database, and the repo already does it.**
   `tests/Ways.Application.Tests/Persistencia/Modelo*Tests.cs` construct `WaysDbContext` over
   `UseNpgsql(...)` with a connection string that is never opened, then read `db.Model` (see
   `ModeloDeOrganizacionTests.cs:17-30`). The completeness nets live there, cost no container, and
   need no interface change.

8. **Raw ADO on the caller's connection is the established escape hatch**, with its two documented
   traps: never `Database.SqlQuery<T>`/`FromSqlRaw` against this model, and always open through
   `Database.OpenConnectionAsync` so `InterceptorDeContextoDeTenant` sets the RLS GUCs
   (`AsignadorDeNumeroCliente.cs:9-35`). `pg_advisory_xact_lock($1, $2)` on that same connection is
   the concurrency idiom (`ServicioDeOfertas.cs:602-623`).

9. **The InMemory provider blocks transactions**, and this repo already paid that bill once:
   `ServicioDeOfertas.ActualizarAsync`'s tests moved from `ServicioDeOfertasTests` to
   `OfertasEndpointsTests` when the advisory lock landed (`ServicioDeOfertas.cs:43-46`). The same
   relocation is a **budgeted cost of slice 4**, not a discovery — see D12.

**Size note.** The 800-word budget of `sdd-design` is overridden by the project precedent the
orchestrator named as binding (archived stage-17/18/19a `design.md`), which the prompt's Slice
column and mutation-target requirements presuppose.

## Architecture Decisions

| # | Slice | Decision | Alternatives considered | Rationale |
|---|---|---|---|---|
| **D1** | 3 | **`IWaysDbContext` gains `IModel Model { get; }`.** One line on the interface, **zero** lines anywhere else — `DbContext.Model` already satisfies it implicitly | (a) A dedicated port `IInventarioDeDependientes` implemented in Infrastructure. (b) Inject `IModel` as its own DI service (`sp => sp.GetRequiredService<WaysDbContext>().Model`) | The interface's own doc-comment sets the criterion — *"`DatabaseFacade` es la misma abstracción de EF Core que ya expone la superficie pública de cualquier `DbContext`, no un tipo de Infrastructure"* (`:150-152`) — and `IModel` satisfies it **identically**: it is EF Core, it is already public on every `DbContext`, and `DbSet<T>` (37 of them on this interface) is a heavier EF leak than `IModel` is. (a) buys a boundary whose only implementation is `=> contexto.Model` while moving a walk that Infrastructure already performs twice into a third place; (b) adds a registration and a lifetime question (the model is effectively a singleton, the context is scoped) to avoid a line the doc-comment already authorises. **Blast radius, verified (fact 6): zero hand-written implementations, zero fakes, zero mocks.** The `DbSet<NumeracionCliente>` precedent also settles that exposure here is per-need, not per-purity |
| **D2** | 3 | **The metadata walk is a pure static function, split from the executor.** `InventarioDeDependientes.Construir(IModel, Type ancla) → IReadOnlyList<RamaDeUso>` has no DB, no clock and no DI; `InspectorDeUso` renders those branches into one statement and runs it | One class doing both; a method on `InspectorDeUso` | The `MaquinaDeEstadosCae` / `PoliticaDeRoles` pattern: the part that must be exhaustively tested is pure, so its whole truth table is a unit test with no container. It is also what makes net **N3** (the golden inventory) possible at all — a golden over a function that needs a live database is a golden nobody regenerates |
| **D3** | 3 | **Three buckets, keyed on the DEPENDENT ENTITY TYPE, evaluated in a fixed order: carve-out → timestamped → untimestamped.** The classifier is **total by construction** | A four-bucket scheme with an explicit `Desconocido`; classify per FK instead of per type | See **A** below. Per-type is right because the discriminator is a property of the *table* (does it carry `created_at`), never of the *column* that points at us — `MovimientoStock` contributes two branches (`IdPuntoVenta`, `IdPuntoVentaDestino`) and both are untimestamped for the same reason. A `Desconocido` bucket is rejected as theatre: any `else` branch that throws at **runtime** converts a future stage's omission into a **production 500 on a delete attempt** instead of a red test at build time. Totality plus N1-N3 moves the detection to where it belongs |
| **D4** | 3 | **The completeness requirement ships as FOUR named nets (N1-N4), and this design records that the proposal's literal wording is unachievable.** A total classifier has no "unclassified" state, so a test asserting *"every type falls into exactly one bucket"* is a tautology — forbidden by `mutation-proof-tests` rule 1 | Ship the tautology; assert bucket membership against a hand-written type list | See **B** below. N3 (the checked-in inventory golden) is the executable form of the proposal's intent: a future stage that adds a referencing table gets a **red test naming the exact table and column**, which is what *"fails the build"* was actually asking for. Recorded as a deliberate substitution, not a silent one |
| **D5** | 3 | **One statement per delete attempt: `UNION ALL` of `SELECT '<tabla>' WHERE EXISTS (…)` branches, outer `LIMIT 1`, parameterised raw ADO on the caller's connection.** Returns the first blocking table name, or `null` | ~40 sequential `AnyAsync` round trips; one `COUNT(*)` per table; a single giant `OR EXISTS` | See **D** below |
| **D6** | 3 | **The guard drops `"BajaLogica"` and keeps tenant isolation — and both fall out of the mechanism rather than being configured.** Raw SQL applies no EF query filter (so a soft-deleted dependent still blocks, proposal decision 6 side effect A); RLS lives on the connection, so it still applies. **No `id_tenant` conjunct is added to any branch** | Add `AND d.id_tenant = @idTenant` to every branch as defence in depth; run the guard through EF with `IgnoreQueryFilters(["BajaLogica"])` per type | See **E** below. The extra conjunct is rejected on **direction of failure**: it can only ever *narrow* the result, and a narrowing bug under-blocks, which is the one direction this stage refuses. The EF route needs `Set<T>()` on the interface, dynamic generic dispatch over ~40 CLR types and ~40 round trips, to reproduce filter semantics the raw route gets for free |
| **D7** | 4 | **One transaction, one advisory lock, one clock reading, ONE evaluation of the guard — no pre-check.** Order: `pg_advisory_xact_lock(idTenant, −20)` → re-read the anchor → structural minimum → usage guard → `momento = reloj.Ahora` → cascade + anchor writes → single `SaveChangesAsync` → COMMIT | A cheap pre-check outside the transaction plus an authoritative re-check inside ("belt and braces") | `mutation-proof-tests` rule 3 names the pre-check-mirroring-a-guard shape as **this repository's most common confound** (stage 17 slices 3 and 5, all conjuncts surviving). Running the guard once removes the confound instead of writing tests to defeat it, and it costs one aborted empty transaction on the 409 path — nothing, on an action a platform operator performs a handful of times a year |
| **D8** | 4 | **Structural minimums stay ordered before the usage guard (proposal decision 3), and this design records that they make `empresa_en_uso` / `punto_venta_en_uso` unreachable through the API.** Their tests are **below-the-confound service-level tests** with a hand-seeded second empresa/PV | Reorder usage before minimum so both codes are API-reachable; drop the empresa/PV DELETE routes as dead surface | Fact 2. Reordering would answer *"delete the tenant instead"* with *"there is data here"* — the strictly less actionable message, which is exactly what decision 3 rejected. Dropping the routes would leave the tenant cascade as the only deletion path and re-open the asymmetry the stage exists to close. The honest cost is that two of the six 409 codes are proven below the API until a create-empresa endpoint exists; `mutation-proof-tests` rule 3 already prescribes that exact remedy |
| **D9** | 4 | **The cascade is `Where(hijo.IdPadre == id)` over live rows only, in one `SaveChangesAsync`, sharing one `momento` across `DeletedAt` AND `UpdatedAt` of parent and every child.** Write **order is not claimed** | Order children-first and assert the sequence; cascade with `IgnoreQueryFilters` to also re-stamp already-deleted children | See **F** below. EF chooses statement order inside one `SaveChanges`; claiming an order we do not control would be a lie a structural test could not honestly assert (rule 13). **Atomicity** is the property, and one transaction delivers it. Re-stamping already-deleted children would destroy the restore-by-instant property by overwriting an older, unrelated deletion |
| **D10** | 4 | **Tenant deletion writes `Estado = EstadoTenant.Baja` and `DeletedAt` in the same `SaveChangesAsync`** — the enum's first writer (proposal decision 1) | Write only `DeletedAt`; a separate statement for the estado | `Tenant.PuedeOperar` is `Estado == Activo && DeletedAt is null` (`Tenant.cs:20`), so the two agree by construction only if they are written together. Two statements admit an interleaving where the row is deleted but still `activo` |
| **D11** | 4 | **One `pg_advisory_xact_lock(idTenant, −20)` per deletion transaction, keyed on the TENANT, not on the entity — and the POS takes no lock of this family.** Negative second argument on purpose | Lock on the entity id; `SELECT … FOR UPDATE` on the anchor row; no lock at all | See **G** below. Keying on the tenant serialises tenant/empresa/PV deletions of the same tenant against **each other** (which is what breaks the shared-instant property), while leaving every operational path untouched — the stage-19a D1 lesson inverted: an admin action must never enter the counter's lock order. The constant is **negative** because the two-int advisory keyspace is already shared with `ServicioDeOfertas` (`idTenant, idOferta`) and `ServicioDePrecios` (`idTenant, par`), both of which pass **positive** ids; a negative constant cannot collide with any of them. A tenant-less anchor (platform `Usuario`, `IdTenant is null`) uses `(0, −20)` — identity ids start at 1 |
| **D12** | 4 | **`Usuario` deletion takes NO transaction and NO lock**: `PoliticaDeRoles` (unchanged, first) → usage guard → the existing single-`momento` write + audit row, in the implicit `SaveChangesAsync` transaction | Give it the same transaction+lock as the three organization entities, for symmetry | It has no cascade, so the shared-instant property is already trivially satisfied by one `SaveChanges`, and the lock closes no race it faces (a sale stamping the employee never takes it — G). The symmetry would buy nothing and cost the `ExecutionStrategy` wrapper plus the InMemory relocation of *every* `EliminarAsync` test. **The guard alone already forces some relocation (fact 9): the raw SQL cannot run on InMemory.** Budgeted, not discovered — see the Testing Strategy |
| **D13** | 1 | **Owner names are correlated scalar subqueries returning `string?`, and child counts are correlated `Count()` subqueries — one statement per list endpoint, asserted with `ContadorDeComandos`** | `Include`/navigation dot-access; a second round trip; a denormalised column | Fact 1 leaves no navigation to dot into, and the subquery form is the one that keeps the dependent row when the principal is filtered out. `string?` instead of `string` is deliberate: an orphan renders as an anomaly (`—`) instead of vanishing, which **decouples slice 1 from the cascade of slice 4** — Part A must be independently mergeable, and it cannot be if its correctness depends on Part B existing |
| **D14** | 1/2 | **`UsuarioListado` carries `int? IdTenant` and `string? NombreTenant`; the server sends `null` for platform staff and the WEB renders the literal *"Plataforma"*** | The server projects the literal `"Plataforma"` (proposal decision 8's wording) | `nombre` is free text: a tenant actually named *"Plataforma"* would be indistinguishable from platform staff, and the filter — which keys on `idTenant` — would then disagree with the column it sits above. Rendering copy is also the web's job under the language contract. **Judgment call correcting the proposal's wording**; the observable outcome (never an empty cell) is preserved |
| **D15** | 2 | **Filter options are derived from the ALREADY-LOADED rows, never from a second fetch.** The empresa filter on `PuntosVenta.tsx` narrows to the selected tenant, and selecting a tenant clears an empresa selection that no longer belongs to it | Call `listarTenants()` to populate the select | `GET /api/plataforma/tenants` is `Politicas.SoloPlataforma` while `Empresas.tsx` / `PuntosVenta.tsx` are reachable by a tenant admin under `GestionDeOrganizacion` (`OrganizacionEndpoints.cs:19-21, 44-46`) — a fetch would 403 for exactly the users the screen was built for. Deriving from the rows also makes an empty option-set impossible by construction |

---

### A — The classification buckets (the untimestamped problem)

`GetReferencingForeignKeys()` returns the complete dependent set; it does not say how to interrogate
each dependent, because **15 referencing domain types do not inherit `EntidadBase` and have no
`created_at` column** (verified on `Stock.cs` and `ArticuloEmpresa.cs`; the full list is in the
proposal's decision 4). Classification is on the **dependent entity type**, in this order:

| Order | Bucket | Membership test | Branch emitted | Why it is correct |
|---|---|---|---|---|
| 1 | **`Excluido`** (carve-out) | CLR type ∈ a `FrozenSet` of **exactly two**: `Ways.Domain.Auditoria.Auditoria` (B5) and `NumeracionCliente` | **none** | `Auditoria` is a trail *about* the entity, and logical deletion keeps the referenced row so the trail keeps rendering. `NumeracionCliente` is the provisioning counter inserted by raw SQL in `AsignadorDeNumeroCliente.AsegurarContadorAsync` (`:38-51`) — not an `EntidadBase`, not customer data, and the **only** untimestamped row provisioning creates |
| 2 | **`Marcado`** (timestamped) | `typeof(EntidadBase).IsAssignableFrom(t.ClrType)` **and** the type has a property whose column is `created_at` | `WHERE <fk> = @id AND d.created_at > @ancla` | B3 exactly. Provisioned rows share the anchor's instant and are excluded by the **strict** `>` |
| 3 | **`SinMarca`** (untimestamped) | everything else | `WHERE <fk> = @id` (existence) | Provisioning creates **no** row of any untimestamped type except the carved-out counter, so existence already means usage — and it fails safe |

The classifier is **total**: every type lands in exactly one bucket, so no runtime `else` can throw.
`Construir` nevertheless throws `InvalidOperationException` **naming the CLR type and the FK** for the
three *mechanical* impossibilities — an entity type with no mapped table, a `Marcado` type whose
`created_at` column cannot be resolved, or an FK whose principal properties are not all readable from
the anchor. Those are build-time failures via N1, never production 500s: nothing calls the generator
at request time without N1 having run in CI first.

**Composite and alternate-key FKs need no special case.** The branch predicate is built by zipping
`fk.Properties` (dependent columns) with `fk.PrincipalKey.Properties` (principal properties) and
reading each principal value off the loaded anchor — so `(id_punto_venta, id_tenant)` against
`ak_puntos_venta_id_punto_venta_id_tenant` produces a two-conjunct branch automatically, and
`MovimientoStock` contributes two independent branches. **Nullable FKs need no special case either**:
`IdEmpresa IS NULL` means *shared catalogue row* on `Cliente`/`Proveedor`/`Oferta`/
`ConfiguracionDeCatalogo<T>`, and `fk = @id` does not match `NULL`, so a shared row correctly does
not block an empresa's deletion.

### B — The completeness test, made real

The proposal asks for a test that *"fails the build when a referencing type is not classified"*.
With a total classifier (A) that state does not exist, so the literal test would assert a tautology.
**Four nets deliver the intent instead.** All four live in
`tests/Ways.Application.Tests/Persistencia/` beside the existing `Modelo*Tests.cs`, over the real
Npgsql model with no container (fact 7) — except N4, which needs a database.

| Net | Lives in | Asserts | Goes red when |
|---|---|---|---|
| **N1 — totality** | `InventarioDeDependientesTests` | `Construir(db.Model, T)` succeeds for **all four** anchors, and the emitted branch count equals `GetReferencingForeignKeys().Count()` minus the carved-out FKs. No FK is silently dropped | A future type has no mapped table, no resolvable `created_at`, or an unreadable principal key — the exception **names it** |
| **N2 — the rule is read off the TABLE, not restated from the code** | idem | For every branch: `rama.UsaAncla == entityType.GetProperties().Any(p => p.GetColumnName() == "created_at")`, computed independently in the test | The classifier is mutated to `EntidadTenant`, inverted, or hardcoded |
| **N3 — the inventory golden (the trip-wire)** | idem + `Fixtures/inventario-de-dependientes.txt` | A sorted, checked-in line per branch: `<ancla> \| <tabla> \| <columnas> \| <bucket>`, including one `excluido` line per carve-out so the file also pins the two-member carve-out set | **Any** FK added, removed, retargeted or reclassified by a future stage — with a diff naming the exact table and column. Regeneration is a deliberate edit recorded in the PR |
| **N4 — pristine regression** | `tests/Ways.IntegrationTests/BajasDeOrganizacionTests.cs` | A **freshly provisioned** tenant, its empresa, its punto de venta and its admin are all pristine | The single-clock-reading property of `ServicioDeAprovisionamiento.cs:46` breaks, **or** a future stage makes provisioning create an untimestamped row (which would silently make every new tenant undeletable) |

Stated honestly: **N3 does not prove the classification is correct — it proves no classification
changes silently.** N2 proves the rule is derived from the table. N1 proves the mechanism is total.
N4 is the only one that can see the provisioning baseline drifting. Together they are the guarantee;
individually none of them is.

### C — `IModel` access

**Decision: expose `IModel Model { get; }` on `IWaysDbContext`** (D1). Verified blast radius:

- `rg ": IWaysDbContext"` over `src/` and `tests/` → **0 matches**. `WaysDbContext` is the only
  implementation and already inherits `DbContext.Model`, so the interface change adds **zero**
  implementation lines and breaks **zero** test doubles.
- The four test files that mention `IWaysDbContext` (`CifradoDeClavesFiscalesTests`,
  `EscriturasDeCuentaCorrienteProveedorTests`, `ExistenciasTests`, `TesoreriaTests`) consume it as a
  parameter type and pass a real `WaysDbContext`. Unaffected.
- The completeness nets (B) do **not** consume the interface at all — they read
  `WaysDbContext.Model` directly, exactly as `ModeloDeOrganizacionTests.cs:37` already does.

### D — Query cost

| Option | Cost for a pristine tenant (~40 branches) | Cost for a blocked tenant | Verdict |
|---|---|---|---|
| ~40 sequential `AnyAsync` | 40 × RTT + 40 plans | 1..40 × RTT | **Rejected** — the round trips dominate and nothing short-circuits across them |
| One `UNION ALL` of `EXISTS`, outer `LIMIT 1` | 1 × RTT, one plan (~40 index probes returning nothing) | 1 × RTT; the `Append` node **stops at the first branch that yields a row** | **Selected** |
| One `COUNT(*)` per table | strictly worse (no `LIMIT`, no early exit) | worse | Rejected |

Honest accounting, not hand-waving:

- **Every branch is an index probe, by EF convention.** EF Core's `ForeignKeyIndexConvention` creates
  an index on the dependent FK properties of every mapped FK unless an explicit index already covers
  them. The design does **not** assume it: a read-only verify step compares the branch set against
  `pg_indexes` and reports any branch with no supporting index. It cannot *fix* one — that would be
  DDL, and the gate is ZERO-SCHEMA — so an uncovered branch becomes a named finding for a later
  stage, not a silent seq scan nobody knew about.
- **Planning ~40 branches is real** (single-digit milliseconds) and is paid once per delete attempt.
- **This is not on any hot path.** Four admin-only routes, performed a handful of times a year, each
  already behind an authorization policy and an operator confirmation.
- Verdict: **acceptable, batched.** Not over-engineered: there is no caching, no materialised
  summary, no background job, and the statement is regenerated per call from metadata that is itself
  a process-lifetime singleton.

**Injection surface.** Identifiers come from `IEntityType`/`IProperty` metadata only — never from a
request — are schema-qualified and double-quoted, and are rejected by the generator unless they match
`^[a-z_][a-z0-9_]*$`. Every anchor id and the anchor's `CreatedAt` is a **parameter**
(`ParametrosDeComando.Agregar`, the `AsignadorDeNumeroCliente` idiom). No user-supplied string ever
reaches the statement.

### E — Query filters, precisely

| Filter | What the guard needs | How it is achieved | Test |
|---|---|---|---|
| `"BajaLogica"` | **Dropped.** A soft-deleted dependent **still blocks** — the ratified reading of B2 (*"did the customer ever operate here"*, proposal decision 6 side effect A and question-round item 2, and an explicit Success Criterion) | Raw SQL applies no EF query filter. Nothing is configured; the semantics fall out | Load an article, delete it logically, assert the tenant is **still** `tenant_en_uso` |
| `"Tenant"` (EF) | Irrelevant to a raw statement, and **not reintroduced as a conjunct** | The anchor was already resolved through the filtered EF read (`BuscarEmpresaAsync` → `PoliticaDeRoles.ValidarAlcanceDeTenant`), so the guard never runs on an anchor the caller may not see | The existing 404-not-403 scope tests, unchanged |
| RLS (connection) | **Kept.** A tenant admin sees every dependent of their own tenant, so the guard cannot under-count; a platform actor sees everything, which is correct | Lives on the connection, opened through `Database.OpenConnectionAsync` so `InterceptorDeContextoDeTenant` sets the GUCs | Cross-tenant integration test on the `ways_app` connection (`mutation-proof-tests` rule 5) |

**On leakage**: the guard's entire output is a **table name** (or `null`). It returns no row, no id
and no count, so there is no channel through which another tenant's data can leak — the worst a
mistake could produce is a wrong verdict, and the FK predicate `fk = @id` over globally-unique
identity ids cannot match another tenant's row.

> **The prompt's framing and the ratified proposal disagree here.** The prompt states *"a logically
> deleted child must NOT block deletion"*; the proposal settled the **opposite** and requires a test
> proving it (`proposal.md:506`, question round item 2). **This design implements the proposal**, per
> the binding-contract rule, and records the disagreement as **T3** with its exact one-conjunct
> reversal. It is the only place where the two inputs cannot both be satisfied.

### F — Cascade, atomicity and the shared instant

```
DELETE /api/plataforma/tenants/{id}                       [SoloPlataforma]
  ├─ BuscarTenantAsync(id)                    → 404 if already deleted ("BajaLogica" hides it)
  └─ db.Database.CreateExecutionStrategy().ExecuteAsync:   ← ADR-16 trap: never BeginTransaction outside
       BEGIN
        1. SELECT pg_advisory_xact_lock($idTenant, $-20)         ← D11
        2. re-read the anchor under the lock                     → 404 if a concurrent delete won
        3. structural minimum (empresa / PV only)                → 409 ultima_empresa_del_tenant | …
        4. InspectorDeUso.PrimeraDependenciaEnUsoAsync(ancla)    → 409 <entidad>_en_uso   ← ONCE (D7)
        5. var momento = reloj.Ahora;                            ← ONE reading, parent + all children
        6. usuarios  WHERE id_tenant = @id : DeletedAt = UpdatedAt = momento
           puntos_venta WHERE id_tenant = @id : idem
           empresas   WHERE id_tenant = @id : idem
           tenant     : DeletedAt = UpdatedAt = momento, Estado = EstadoTenant.Baja   ← D10
        7. await db.SaveChangesAsync(ct)                         ← ONE call, EF orders the statements
       COMMIT
```

- **The cascade set is provably three rows.** Every `EntidadTenant` descendant references the tenant,
  so a tenant with any usage at all is blocked at step 4; a tenant that reaches step 6 is pristine,
  and a pristine tenant has exactly the provisioned empresa, punto de venta and admin. The code is
  still written generically (`Where(x.IdTenant == id)`) so it cannot miss a row the reasoning did not
  anticipate.
- **`DELETE /api/empresas/{id}`** cascades to `puntos_venta WHERE id_empresa = @id` under the same
  shape. `DELETE /api/puntos-venta/{id}` has no children. `DELETE /api/usuarios/{id}` has no children
  and no transaction (D12).
- **Only live children are cascaded** — an already-deleted child keeps its own older instant, which
  is what keeps `UPDATE … SET deleted_at = NULL WHERE deleted_at = '<instant>'` an **exact** restore.
- **Order is not claimed** (D9). One `SaveChangesAsync` inside one transaction gives atomicity; EF
  chooses the statement order and this design does not pretend otherwise.
- **`EstadoTenant.Baja`'s existing readers, unchanged**: `Tenant.PuedeOperar` (`:20`) is already
  `Activo && DeletedAt is null`; `CambiarEstadoTenantAsync` already refuses to suspend or reactivate
  a `Baja` tenant with `tenant_dado_de_baja` (`ServicioDeOrganizacion.cs:67-72`); login already
  handles it (fact 3, and see **T2**).

### G — Concurrency, and the residual race stated plainly

| Race | Outcome without a lock | Verdict |
|---|---|---|
| Two operators delete the **same** tenant | Both pass the guard; two different instants land on the same row; children may carry instant A while the parent carries instant B, breaking restore-by-instant | **Closed by D11** — the second waits, re-reads under the lock at step 2 and gets a clean **404** |
| Delete a tenant while deleting one of its empresas | Two transactions, different rows, two instants, both commit | **Closed by D11** — same lock key (the tenant), so they serialise |
| Delete two entities of **different** tenants | No interaction | Not a race; different lock keys, full concurrency preserved |
| **A sale is created while the tenant is being deleted** | The guard reads *pristine*, the checkout's `INSERT` touches different rows, both commit. Result: a soft-deleted tenant owning a post-anchor comprobante | **OPEN. Accepted.** |

**Why the last one stays open.** Closing it requires the checkout to take the deletion lock, which
puts a platform-admin action into the POS hot path — precisely the failure mode stage 19a's D1 was
designed to prevent (*"no existing path can ever queue behind"* an admin operation). The window is
milliseconds, on an action performed a handful of times a year, and it requires the operator to
delete a tenant that is **actively selling at that instant**. And because of B1 **nothing is
destroyed**: recovery is `UPDATE tenants SET deleted_at = NULL, estado = 'activo' WHERE id = …` plus
`UPDATE <hijos> SET deleted_at = NULL WHERE deleted_at = '<the shared instant>'`. Registered as
**R1**, with its reopen condition: the first automated or bulk deletion path.

**Lock-order claim, and its honest form.** The deletion transaction takes exactly one advisory lock
plus row locks on `tenants`, `empresas`, `puntos_venta` and `usuarios` — **none** of which appears in
the program's total order (`numeraciones_fiscales → turnos_caja → comprobantes_venta → presupuestos →
remitos → lotes → stock/stock_lotes → clientes → ledger INSERT`). The lock sets are **disjoint**, so
no deadlock against an operational path is expressible. Asserted **structurally** (the deletion path
touches only organization tables — an `rg` over the new methods), not by a probabilistic race:
`mutation-proof-tests` rule 13 is explicit that a single-resource race test is blind to order and
that a live deadlock cannot be forced through raw ADO.

## `db-error-backstops` — structurally N/A

Every FK in `WaysDbContext`/`Configuraciones/*.cs` is `DeleteBehavior.Restrict`, but **B1 forbids
physical deletion**, and `Restrict` contributes exactly zero protection against
`UPDATE … SET deleted_at`. **No Postgres constraint can ever fire on any path this stage adds**, so
there is no SQLSTATE `23503` to classify and no branch to add to `ManejadorDeErrores.cs` — the file
is **untouched**, and that is a binding verify criterion (V6).

The consequence must be said out loud: **the application guard is the sole line of defence.** There
is no database backstop behind it, so its tests are not good practice, they are the entire safety
argument. That is why D4's four nets and the four `never_degrade` items of `state.yaml:256-262` are
non-negotiable, and why the guard is deliberately shipped **inert** in slice 3 (no caller until slice
4) so it can be reviewed on its own merits before anything can invoke it.

## Interfaces / Contracts

```csharp
// src/Ways.Application/Organizacion/InventarioDeDependientes.cs
// PURO (D2): sin base, sin reloj, sin DI — para que el golden N3 se pueda regenerar sin contenedor.
public enum ClasificacionDeDependiente { Excluido, Marcado, SinMarca }

/// <param name="Columnas">Columnas dependientes de la FK, zipeadas con las del principal.</param>
public sealed record RamaDeUso(
    string Tabla,                       // schema-qualified, quoted at render time
    IReadOnlyList<string> Columnas,
    IReadOnlyList<string> PropiedadesDelPrincipal,
    ClasificacionDeDependiente Clasificacion)
{
    public bool UsaAncla => Clasificacion is ClasificacionDeDependiente.Marcado;
}

public static class InventarioDeDependientes
{
    /// <summary>B5 + el contador de aprovisionamiento. EXACTAMENTE dos, cada uno con su razón
    /// escrita y su test propio (proposal decisión 4).</summary>
    public static readonly FrozenSet<Type> Excluidos =
        FrozenSet.Create(typeof(Ways.Domain.Auditoria.Auditoria), typeof(NumeracionCliente));

    /// <summary>Lanza <see cref="InvalidOperationException"/> NOMBRANDO el tipo y la FK ante las
    /// tres imposibilidades mecánicas (sin tabla mapeada, sin columna created_at en un tipo
    /// Marcado, clave principal no legible). N1 es quien lo ejecuta en CI.</summary>
    public static IReadOnlyList<RamaDeUso> Construir(IModel modelo, Type tipoAncla);
}

// src/Ways.Application/Organizacion/InspectorDeUso.cs
public sealed class InspectorDeUso(IWaysDbContext db)
{
    /// <summary>Devuelve el NOMBRE de la primera tabla que bloquea, o null si la entidad es
    /// prístina. Una sola ida y vuelta (D5). ADO crudo sobre la conexión/transacción del
    /// llamador — nunca SqlQuery/FromSqlRaw (trampa de stage-1 slice 2).</summary>
    public Task<string?> PrimeraDependenciaEnUsoAsync(
        Type tipoAncla, IReadOnlyList<object> valoresDeClave, DateTimeOffset ancla,
        CancellationToken ct = default);
}
```

Rendered statement (punto de venta, abbreviated):

```sql
SELECT tabla FROM (
  SELECT 'comprobantes_venta' AS tabla WHERE EXISTS (
    SELECT 1 FROM public."comprobantes_venta" d WHERE d."id_punto_venta" = $1 AND d."created_at" > $2)
  UNION ALL
  SELECT 'movimientos_stock'  AS tabla WHERE EXISTS (
    SELECT 1 FROM public."movimientos_stock"  d WHERE d."id_punto_venta_destino" = $1)
  UNION ALL …
) AS ramas LIMIT 1
```

DTO changes — every added field with its consumer (`dto-contract-honesty` rule 1):

| Record | Added | Consumer | Slice |
|---|---|---|---|
| `TenantListado` | `int CantidadEmpresas, int CantidadPuntosVenta, int CantidadUsuarios` | three columns on `Tenants.tsx` | 1 / 2 |
| `EmpresaListado` | `string? NombreTenant` | tenant column + filter label on `Empresas.tsx` | 1 / 2 |
| `PuntoVentaListado` | `string? NombreTenant, string? RazonSocialEmpresa` | two columns + two filters on `PuntosVenta.tsx` | 1 / 2 |
| `UsuarioListado` | `int? IdTenant, string? NombreTenant` | tenant column (`"Plataforma"` when null, D14) + tenant filter on `Usuarios.tsx` | 1 / 2 |

**The three pre-existing id fields keep a consumer and must not be deleted.**
`EmpresaListado.IdTenant` and `PuntoVentaListado.IdTenant`/`.IdEmpresa` stop being *rendered* and
become the **filter keys** (`<select value>`). Named here so a reviewer applying
`dto-contract-honesty` does not read them as newly dead.

**Counts exclude logically deleted rows for free**: the correlated `Count()` subqueries run inside
the LINQ tree, so the `"BajaLogica"` filter applies to them automatically — asserted, not assumed.

## File Changes

| File | Action | Slice | Description |
|---|---|---|---|
| `src/Ways.Application/Abstracciones/IWaysDbContext.cs` | Modify | 3 | `+1 line`: `IModel Model { get; }` with the doc-comment argument (D1) |
| `src/Ways.Application/Organizacion/InventarioDeDependientes.cs` | **Create** | 3 | Pure metadata walk, three buckets, two carve-outs (D2, D3) |
| `src/Ways.Application/Organizacion/InspectorDeUso.cs` | **Create** | 3 | Statement rendering + raw-ADO execution (D5, D6) |
| `src/Ways.Api/Programa.cs` (or the DI module) | Modify | 3 | Register `InspectorDeUso` (scoped). **No caller until slice 4 — the slice is inert** |
| `src/Ways.Application/Organizacion/Contratos.cs` | Modify | 1 | Three DTOs gain owner names / counts |
| `src/Ways.Application/Usuarios/Contratos.cs` | Modify | 1 | `UsuarioListado` gains `IdTenant` + `NombreTenant` |
| `src/Ways.Application/Organizacion/ServicioDeOrganizacion.cs` | Modify | 1, 4 | Slice 1: three joined projections. Slice 4: `EliminarTenantAsync`/`EliminarEmpresaAsync`/`EliminarPuntoVentaAsync`, cascade, minimums; the class doc-comment's *"este servicio no crea ni elimina nada"* is corrected |
| `src/Ways.Application/Usuarios/ServicioDeUsuarios.cs` | Modify | 1, 4 | Slice 1: the projection at `:76-79` and `:90-92`. Slice 4: one guard call after `PoliticaDeRoles` at `:296` (D12) |
| `src/Ways.Application/Organizacion/EtiquetasDeTablas.cs` | **Create** | 4 | Decision 9's label dictionary (`comprobantes_venta` → *"ventas"*), fallback *"datos cargados"* |
| `src/Ways.Api/Endpoints/OrganizacionEndpoints.cs` | Modify | 4 | Three `MapDelete`; the class doc-comment's *"acá no hay `POST` ni `DELETE` a propósito"* (`:11-13`) is corrected |
| `src/Ways.Api/Seguridad/Politicas.cs` | **Untouched** | — | Binding criterion V5. Zero new policies |
| `src/Ways.Api/Seguridad/ManejadorDeErrores.cs` | **Untouched** | — | Binding criterion V6 — no SQLSTATE can fire |
| `src/Ways.Infrastructure/Persistencia/**` | **Untouched** | — | Binding criterion V1-V3. No migration, no configuration, no initializer change |
| `src/Ways.Web/src/api/tipos.ts` | Modify | 2 | The four DTO mirrors |
| `src/Ways.Web/src/api/organizacion.ts` | Modify | 2, 4 | Slice 2: pure helpers `opcionesDeTenant`, `opcionesDeEmpresa`, `filtrarPorTenant`, `filtrarPorEmpresa`, `etiquetaDeTenant`. Slice 5: `eliminarTenant/Empresa/PuntoVenta` |
| `src/Ways.Web/src/api/organizacion.test.ts` | **Create** | 2 | `web-descriptor-tests`: unit tests per helper branch |
| `src/Ways.Web/src/api/usuarios.ts` | Modify | 2 | `UsuarioListado` mirror (the `eliminar` call already exists) |
| `src/Ways.Web/src/paginas/{Tenants,Empresas,PuntosVenta,Usuarios}.tsx` | Modify | 2, 5 | Slice 2: name columns, counts, filters. Slice 5: delete button + confirmation + `codigo`→copy |
| `src/Ways.Web/src/paginas/{Tenants,Empresas,PuntosVenta,Usuarios}.test.tsx` | **Create** | 2, 5 | **No test file exists for any of the four screens today** — coverage is created |
| `tests/Ways.Application.Tests/Persistencia/InventarioDeDependientesTests.cs` | **Create** | 3 | N1, N2, N3 |
| `tests/Ways.Application.Tests/Persistencia/Fixtures/inventario-de-dependientes.txt` | **Create** | 3 | N3's golden |
| `tests/Ways.Application.Tests/Organizacion/InspectorDeUsoTests.cs` | **Create** | 3 | Statement rendering: bucket → predicate, composite FK, quoting, parameter binding |
| `tests/Ways.IntegrationTests/BajasDeOrganizacionTests.cs` | **Create** | 4 | N4, the guard end to end, cascade, minimums, RLS, carve-outs, boundary |
| `tests/Ways.Application.Tests/{Organizacion/ServicioDeOrganizacionTests,Usuarios/ServicioDeUsuariosTests}.cs` | Modify | 4 | **Relocation** of the deletion cases to the integration suite (fact 9, D12) |
| `docs/09-multi-tenancy.md`, `docs/10-modelo-de-datos.md` | Modify | 5 | An "Etapa 20" note: deletion semantics + the guard. **No schema table changes** |

## Guarded writes — conjunct enumeration (`mutation-proof-tests` rule 3, up front)

The deletion writes are EF `SaveChanges` UPDATEs keyed by PK, so the only hand-written predicates are
the cascade scopes and the minimum counts. Listed **before** any test is written.

| # | Statement | Conjuncts | The test that kills each |
|---|---|---|---|
| **U1** | `usuarios WHERE IdTenant == @id` (cascade) | (a) `IdTenant == id` | A **sibling tenant** seeded with its own admin: its usuario stays live, asserted by identity and by exact count (rule 12c) |
| **U2** | `puntos_venta WHERE IdTenant == @id` | (a) `IdTenant == id` | Same sibling-tenant pair |
| **U3** | `empresas WHERE IdTenant == @id` | (a) `IdTenant == id` | Same sibling-tenant pair |
| **U4** | `puntos_venta WHERE IdEmpresa == @id` (empresa cascade) | (a) `IdEmpresa == id` | A **second empresa of the SAME tenant**, hand-seeded (fact 2): its PV stays live |
| **U5** | `COUNT(empresas WHERE IdTenant == @id)` (minimum) | (a) `IdTenant == id` | A sibling tenant's empresa must not be counted, or the minimum never fires |
| **U6** | `COUNT(puntos_venta WHERE IdEmpresa == @id)` | (a) `IdEmpresa == id` | A sibling empresa's PV must not be counted |
| **U7** | Guard branch, `Marcado` | (a) `<fk> = @id` · (b) `created_at > @ancla` **strict** | (a) a dependent of a **sibling** entity must not block. (b) **two kills**: a row created **exactly at** the anchor must **not** block (rule 14 boundary fixture, under `RelojFijo`), and a row one tick later **must** block |
| **U8** | Guard branch, `SinMarca` | (a) `<fk> = @id` only | Adding `AND created_at > @ancla` must fail to compile/execute (no such column); a `Stock` row for the PV blocks with no timestamp involved |

## Testing Strategy

| Layer | What | Approach | Slice |
|---|---|---|---|
| Model (no container) | **N1, N2, N3** | Real Npgsql model over an unopened connection, `Modelo*Tests.cs` pattern (fact 7) | 3 |
| Application unit | Statement rendering per bucket; composite-FK branch (two conjuncts); alternate-key principal; identifier quoting + the `^[a-z_][a-z0-9_]*$` rejection; parameter count and binding order | `InspectorDeUsoTests`, pure string assertions over `Construir` + the renderer | 3 |
| Application unit | The label dictionary: a mapped table → its Spanish word; an **unmapped** table → *"datos cargados"* | Pure | 4 |
| Integration | **N4**: a freshly provisioned tenant / empresa / PV / admin are all pristine | `BajasDeOrganizacionTests`, real Postgres | 4 |
| Integration | **B2 proven**: load **one article** (no sale, no movement) ⇒ the tenant, its empresa and its punto de venta each return their own named 409 | The `never_degrade` item | 4 |
| Integration | The two carve-outs, independently: an entity with **only** `auditoria` rows past the anchor is deletable; an entity with **only** its provisioned `numeraciones_clientes` row is deletable | The `never_degrade` item | 4 |
| Integration | A **soft-deleted** dependent **still blocks** (E, D6) | Load an article, delete it, retry the tenant delete | 4 |
| Integration | Cascade: one `deleted_at` **instant** shared by tenant + empresa + PV + admin, `estado = 'baja'` on the tenant; an already-deleted child keeps its **older** instant | Assert the instant equality, not just non-null | 4 |
| Integration | U1-U6 sibling kills; U7's boundary pair under `RelojFijo`; U8 | `mutation-proof-tests` rules 12c and 14 | 4 |
| Integration | `ultima_empresa_del_tenant` / `ultimo_punto_venta_de_la_empresa` fire on their exact condition and **not** on any other | Below-the-confound with a hand-seeded second empresa/PV (D8) | 4 |
| Integration | `usuario_en_uso` **after** `PoliticaDeRoles`: Root target, self-deletion and out-of-scope-404 behaviour byte-identical | Relocated + extended cases (D12) | 4 |
| Integration | A second DELETE on an already-deleted row ⇒ **404**, not 500 | Idempotent-safe | 4 |
| Integration | **A cascade-deleted admin cannot log in ⇒ 401 `credenciales_invalidas`** (fact 3, T2), and a **suspended** tenant still ⇒ 403 `tenant_suspendido` (unchanged) | Two tests, two codes, the second a regression | 4 |
| Integration | Cross-tenant isolation on all four new routes, **read and write pair**, on the `ways_app` connection | Rule 5 — a superuser fixture proves nothing | 4 |
| Integration | **One** round trip per list endpoint; counts exclude soft-deleted children; the **orphan** case (an empresa whose tenant is soft-deleted still appears, `nombreTenant = null`) | `ContadorDeComandos`, the `VentasCheckoutTests:930` precedent | 1 |
| Integration | Every positional field of the four listing DTOs read back with **pairwise-distinct** values; a sibling row of the same tenant on every listing test | Rules 12b and 12c | 1 |
| Vitest (unit) | `opcionesDeTenant` / `opcionesDeEmpresa` (dedup, ordering, the `null` → *"Plataforma"* option), `filtrarPorTenant` / `filtrarPorEmpresa`, `etiquetaDeTenant` | Colocated `organizacion.test.ts`, `web-descriptor-tests` | 2 |
| Vitest (component) | The tenant select filters the rendered rows; selecting a tenant **narrows** the empresa select and **clears** an empresa that no longer belongs to it; `"Plataforma"` renders for `idTenant === null` | RTL + `user-event`, `vi.mock('../api/cliente')` | 2 |
| Vitest (component) | Delete: confirmation gate; full-window disabled per entity; supersede blocked while a write is outstanding; re-entrancy guard; post-write refresh failure reports *"se eliminó, pero no se pudo actualizar la vista"*; each 409 `codigo` maps to its own copy | `react-async-state` rules 2-6, 9; **rule 10: the pattern is replicated across all four screens in the same PR** | 5 |
| **Structural only (honest limits)** | (a) **Zero physical deletes**: repository scan for `ExecuteDelete`, `Remove(`, `RemoveRange(` and `DELETE FROM` over the four tables. (b) **Zero migrations**: no new file under `Migraciones/`, `has-pending-model-changes` clean, `InicializadorDeBaseDeDatos.cs` untouched. (c) **Disjoint lock sets**: the deletion methods touch only organization tables (rule 13 — a live deadlock cannot be forced through raw ADO). (d) **FK index coverage**: the branch set compared against `pg_indexes`, reporting uncovered branches without fixing them (D, ZERO-SCHEMA) | Named as structural, never dressed up as a runtime kill | 4 |

## Slicing (5 PRs, stacked-to-main, `review_budget_lines: 800`)

| # | Branch | Content | ~Lines | Depends on | Rollback |
|---|---|---|---|---|---|
| 1 | `feat/stage20-slice1-proyeccion-api` | D13, D14 server side: the four DTOs, the correlated-subquery projections in `ServicioDeOrganizacion` + `ServicioDeUsuarios`, one-round-trip / orphan / counts-exclude-deleted / pairwise-distinct tests | ~390 | — | Revert. DTO fields disappear; the web slice is not merged yet |
| 2 | `feat/stage20-slice2-proyeccion-web` | D14 web side, D15: `tipos.ts`, the five pure helpers + `organizacion.test.ts`, name columns and counts on the four screens, tenant filter ×3 and empresa filter ×1, the **first** Vitest files for these screens | ~430 | 1 | Revert. The screens return to rendering ids |
| 3 | `feat/stage20-slice3-inspector-de-uso` | D1, D2, D3, D5, D6: the `IModel` line, `InventarioDeDependientes`, `InspectorDeUso`, the DI registration, **N1 + N2 + N3** and the rendering unit suite. **No caller — inert by construction** | ~470 | — | Revert. Nothing calls it; the guard cannot have run |
| 4 | `feat/stage20-slice4-bajas-api` | D7-D12: three DELETE routes, three `EliminarAsync`, the cascade, the two minimums, the `Usuario` guard, the six 409 codes, the label dictionary, **N4**, U1-U8, RLS, the carve-outs, the login regression, the test relocations | ~490 | 1, 3 | Revert removes three routes and one guard call. Rows already soft-deleted stay soft-deleted and hidden — a **pre-existing, supported state** (`Usuario` has produced it since stage 1) |
| 5 | `feat/stage20-slice5-bajas-web` | Delete button + confirmation on the four screens, `codigo`→copy mapping, the `react-async-state` write discipline replicated across all four, docs 09/10 | ~340 | 2, 4 | Revert removes buttons. The API still works; nobody can press it |

Merge order `1 → 2 → 3 → 4 → 5`. **Slices 1-2 (Part A) and 3-5 (Part B) are independent**: slice 3
depends on nothing (it is inert), and D13's nullable owner name is what keeps slice 1 correct without
slice 4's cascade.

**Decision needed before apply: No** (the gate is `ZERO-SCHEMA-RATIFIED`, `state.yaml:9-55`) ·
**Chained PRs recommended: Yes** (`chain_strategy: stacked-to-main`, one `judgment-day` round per
slice) · **800-line budget risk: Low** — every slice sits at roughly half the budget, and three split
points are pre-authorized. The named inflators are slice 4's test matrix and its **test relocations**
(fact 9), and slice 2's four from-scratch Vitest files.

**Pre-approved degradation**, in the proposal's priority order: (1) slice 4 splits into `4a`
(tenant + empresa + cascade + minimums, U1-U6) and `4b` (punto de venta + the `Usuario` guard +
relocations, U7-U8); (2) slice 3 splits into `3a` (`IModel` + `InventarioDeDependientes` + N1-N3) and
`3b` (`InspectorDeUso` + rendering); (3) slice 2 splits into `2a` (names and counts) and `2b`
(filters). **Never degraded**: N1-N4, the two carve-out tests, the "one article blocks" test and the
zero-physical-delete scan (`state.yaml:256-262`).

## Binding verify criteria

1. **Zero new files** under `src/Ways.Infrastructure/Persistencia/Migraciones/` — the last migration
   at propose time is still the last one; `dotnet ef migrations has-pending-model-changes` **clean**.
2. `InicializadorDeBaseDeDatos.cs` **untouched** (`git diff --exit-code`).
3. **Zero** `CREATE`/`ALTER`/`DROP`/`INSERT`/`UPDATE`-DDL statements anywhere in the diff outside the
   guard's generated read-only `SELECT`.
4. **Zero physical deletes**: repository scan for `ExecuteDelete`, `Remove(`, `RemoveRange(` and
   `DELETE FROM` over `tenants`, `empresas`, `puntos_venta`, `usuarios`.
5. `src/Ways.Api/Seguridad/Politicas.cs` **untouched** — zero new policies; all four DELETEs reuse the
   policy of the group they belong to.
6. `src/Ways.Api/Seguridad/ManejadorDeErrores.cs` **untouched** — no SQLSTATE branch, because none can
   fire (`db-error-backstops` N/A).
7. `IWaysDbContext.cs` gains **exactly one** member (`IModel Model`) and **zero** implementations
   change (fact 6, asserted by `rg ": IWaysDbContext"` returning only the compile-time satisfaction
   by `WaysDbContext`).
8. **N3's golden is checked in and green**, and any regeneration in the PR is accompanied by a written
   classification decision for each changed line.
9. Each of the four list endpoints performs **exactly one** database round trip (`ContadorDeComandos`).
10. `InspectorDeUso` has **zero callers** in the slice-3 diff (`rg` over `src/`).
11. Mutation evidence recorded in the PR body for **every** U-row belonging to that slice; structural
    rows record the file/state/definition assertion instead of a runtime failure, **and say so**.
12. Domain / Application / Integration / Vitest suites green; `tsc -b`, `oxlint`, `vite build` clean.

## Threat Matrix

**N/A** — this change adds four additive authenticated routes under **existing** policies, runs no
shell command, spawns no subprocess, automates no VCS/PR action, classifies no executable file and
integrates with no external process. Its one genuinely new boundary is **dynamically generated SQL**,
which is not a threat-matrix row but is closed explicitly in **D**: identifiers come from EF metadata
only and are pattern-validated; every value is a bound parameter; the statement is read-only
(`SELECT`/`EXISTS`), so even a defect could not mutate a row.

## Migration / Rollout

**No migration required.** Zero DDL, zero data statements, zero seed changes (the ratified gate).
Rollout is five merges; `git revert` is the complete rollback at every level. The only durable trace
is a row an operator actually deleted, which is a `deleted_at` (and possibly `estado = 'baja'`) that a
one-line `UPDATE` reverses — **the data was never destroyed (B1)**. There is no unrecoverable action
in this stage.

## Open questions / tensions

- [ ] **T1 — two of the six 409 codes are unreachable through the API today.** No endpoint creates a
      second empresa or a second punto de venta (fact 2), so decision 3's structural minimum fires on
      every empresa/PV delete attempt and `empresa_en_uso` / `punto_venta_en_uso` can only be proven
      **below the API** (D8). The routes are still correct and become live the day a create-empresa
      or create-PV endpoint ships. `sdd-tasks` must not write an API-level test for those two codes —
      it would pass for the wrong reason (`mutation-proof-tests` rule 3).
- [ ] **T2 — a Success Criterion is wrong about the login code.** The proposal requires *"a deleted
      tenant's user … receives 403 `tenant_suspendido`"*. Verified (fact 3): the cascade soft-deletes
      the admin, the login lookup runs under `"BajaLogica"`, so the user is not found and the request
      dies at `ServicioDeAutenticacion.cs:104` with **401 `credenciales_invalidas`**. The property the
      criterion cares about — *cannot log in, cleanly, no crash* — holds; only the code differs. The
      403 branch stays reachable for a **suspended** tenant and is asserted unchanged.
- [ ] **T3 — the launch prompt and the ratified proposal disagree on soft-deleted dependents.** The
      prompt states *"a logically deleted child must NOT block deletion"*; the proposal settled the
      opposite (decision 6 side effect A, question round item 2) and requires a test proving it
      (`proposal.md:506`). **This design implements the proposal.** Reversal cost, exactly as the
      proposal states: add `AND d.deleted_at IS NULL` to every `Marcado` branch (and to `SinMarca`
      branches whose table has the column), plus flipping one test. **Needs an owner ruling before
      slice 3 is written**, because it changes what N3's golden and the guard's tests assert.
- [ ] **T4 — the completeness test the proposal describes cannot exist as written.** A total
      classifier has no unclassified state (A), so *"fails the build on an unclassified type"* is
      delivered by **N3's golden inventory** instead of by a bucket-membership assertion (D4). This is
      a substitution, recorded rather than silently performed; if the owner reads the proposal's
      wording as binding literally, the alternative is a `Desconocido` bucket that throws at request
      time — strictly worse, because it turns a future omission into a production 500.
- [ ] **T5 — R1, the sale-during-deletion race, stays open** (G). Accepted: closing it would put a
      platform-admin lock into the POS hot path. Reopen condition: any automated or bulk deletion
      path.
- [ ] **T6 — FK index coverage is verified, not guaranteed.** EF Core's `ForeignKeyIndexConvention`
      should give every branch an index, but the gate forbids adding one if it does not. An uncovered
      branch is reported by the structural check and becomes a finding for a later stage, not a
      blocker here (D).
- [ ] **Deferred, unchanged**: force/override delete, undelete/restore, retro-guarding the other
      unguarded soft deletes, `Estado` for empresa/PV, server-side pagination, the
      minimum-administrators invariant, and the owner's reserved carryovers (`state.yaml:263-280`).
