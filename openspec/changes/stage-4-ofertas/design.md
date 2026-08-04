# Design: Stage 4 — Ofertas

## Technical Approach

Reuse, not redesign (proposal §Approach). `ofertas` gets a dedicated entity + service (like
`Articulo`/`Precio`, **not** `CatalogoSimple`/`ServicioDeCatalogo<T>`), `ofertas_listas` copies
the `articulos_empresas` junction shape, and the whole rule engine lands as a pure, DB-free
`ResolvedorDeOfertas` in `Ways.Domain.Ofertas`, mirroring `ResolvedorDePrecios`.

**Key insight that shapes the whole stage:** the resolution rules that are hard (precedence,
additive-over-original stacking, clamp, vigencia windows, `cantidad_minima`, multi-lista
targeting) are *exactly* the rules that must be exhaustively unit-tested. So the SQL side is
deliberately dumb: it only applies the cheap, index-supported, high-selectivity predicates
(`activo`, `deleted_at`, scope ids, `id_empresa`, date window) and hands **candidates** to the
pure resolver, which owns every remaining rule. That split is what makes batch-first cheap:
**7 constant queries per resolution call, independent of N articles × M listas** — 1 articulos,
1 categorias (ancestor map), 1 ofertas, 1 ofertas_listas, 3 precios (listas → base listas →
`precios` rows).

## Architecture Decisions

| # | Decision | Alternatives considered | Rationale |
|---|---|---|---|
| 1 | `Oferta : EntidadTenant` keeps doc-10's **raw nullable columns** for both exclusive groups; the invariant lives in a pure `Ways.Domain.Ofertas.ReglaDeOfertas` (à la `ReglaDeArticulos`) **and** in two DB CHECKs | Discriminated domain types / EF owned types / converters per group | `ListaPrecio` already ships the nullable-group + service-validation precedent (`Modo`/`IdListaBase`/`Porcentaje` + `ValidarModoAsync`); owned types would fight both `num_nonnulls` CHECKs and the candidate query, which needs the raw columns as plain `= ANY(...)` predicates |
| 2 | The **resolver never sees nullable soup**: `ReglaDeOfertas.LeerAlcance/LeerBeneficio` are total functions that project an `Oferta` row into `AlcanceDeOferta`/`BeneficioDeOferta` record structs (or throw `ErrorDominio`) | Pass `Oferta` entities straight into the resolver | Keeps the invariant in ONE place and makes every resolver test express intent (`Beneficio.Porcentaje(10m)`), not nullable permutations; entities stay doc-10 shaped for EF |
| 3 | `ResolvedorDeOfertas` is a **pure static** class taking pre-decomposed local time (`DateOnly`, `TimeOnly`, ISO `DiaSemana` 1..7) — never a `DateTimeOffset` + `TimeZoneInfo` | Resolver does the timezone conversion itself | Purity/testability bar of `ResolvedorDePrecios`/`PoliticaDeRoles`: no ambient time, no tz database in Domain. `ServicioDeOfertas` owns the instant → local-components conversion (see Open Questions) |
| 4 | `ofertas_listas` targeting is resolved **in memory** (`empty set = every lista`), not by a SQL anti-join | `LEFT JOIN ofertas_listas ... WHERE ol.id_lista_precio = :lista OR ol.id_oferta IS NULL` | The "no rows = all listas" rule is a *business* rule; putting it in SQL makes it untestable without a DB and duplicates it against the resolver. Candidate volume is dozens of rows per tenant |
| 5 | Batch price resolution is an **added** method (`PreciosVigentesEnLoteAsync`), never a rewrite of `PrecioVigenteAsync`/`PreciosVigentesAsync` | Refactor `PreciosVigentesAsync` to batch and have both call it | Proposal risk #3 + rollback plan: stage 4 must not be able to break stage 3. The two shipped methods keep signatures, semantics **and** their documented `Activo` divergence; parity tests pin batch == single |
| 6 | Dedicated `ServicioDeOfertas`, `Oferta` does **not** extend `CatalogoSimple` | Extend `ServicioDeCatalogo<T,TListado,TAlta>` like `ServicioDeListasPrecio` | The base's `ux_*_nombre_compartido/empresa` dedupe indexes would be actively **wrong** here: `nombre` is a ticket label, deliberately not unique (two "2x1 Verano" with different windows are legit). Plus dual exclusivity + junction writes — same divergence `ServicioDeArticulos`/`ServicioDePrecios` already took |
| 7 | Resolution is `POST /api/ofertas/resolver`, **query-only** (writes nothing) | `GET` with a serialized line array in the query string | A batch input is dozens of lines; a query string is the wrong transport. The doc-comment and endpoint summary must state "POST, no muta nada" so nobody later assumes a write path |
| 8 | 23514 gets a `ClasificarCheck` classifier matched by **exact name behind a `ck_ofertas_` prefix guard**, added *after* the two existing exact-name 23514 branches | Extend the `Contains`-based `ClasificarUnicidad` style to CHECKs | Stage-3 lesson (`_codigo` vs `_codigo_interno`): `Contains` ordering is a trap. Exact-name switching cannot collide, and appending after the shipped branches guarantees zero behavior change for `ck_clientes_cf_protegido`/`ck_precios_ventana_valida` |
| 9 | Dedicated `Ofertas.tsx` screen; the **pure mapping helpers live in `src/api/ofertas.ts`**, not inside the component | One more `DescriptorDeCatalogo` entry in `catalogos.ts` | `PaginaCatalogo`'s descriptor machine has no mutually-exclusive field group and no multi-select (`Articulos.tsx` is the precedent for going dedicated). Extracting the mappers is what keeps `web-descriptor-tests` applicable — the skill's bar is the *helper*, not the descriptor literally |

## Table Shapes (DB CHANGE GATE — one grouped migration)

| Table | Scope | Key columns | Constraints |
|---|---|---|---|
| `ofertas` | catálogo (`id_tenant` NOT NULL, `id_empresa` NULL = tenant-wide, doc 09 §84) | `nombre citext(150)`, `id_articulo/id_grupo/id_categoria int NULL`, `fecha_desde/hasta date NULL`, `hora_desde/hasta time NULL`, `dias_semana smallint[] NULL`, `cantidad_minima numeric(12,3) NULL`, `precio_unitario numeric(14,2) NULL`, `porcentaje numeric(5,2) NULL`, `importe_fijo numeric(14,2) NULL`, `prioridad int NOT NULL DEFAULT 0`, `acumulable bool NOT NULL DEFAULT false`, `activo` | `ck_ofertas_alcance_exclusivo` (`num_nonnulls(id_articulo,id_grupo,id_categoria)=1`); `ck_ofertas_beneficio_exclusivo` (`num_nonnulls(precio_unitario,porcentaje,importe_fijo)=1`); `ck_ofertas_ventana_valida` (`fecha_hasta >= fecha_desde` and `hora_hasta >= hora_desde`, both NULL-tolerant — same family/naming as `ck_precios_ventana_valida`); `ck_ofertas_dias_semana` (`dias_semana <@ ARRAY[1..7]::smallint[]`); composite FKs `fk_ofertas_{tenant,empresa,articulo,grupo,categoria}`; `ix_ofertas_{tenant,empresa,articulo,grupo,categoria}` (explicit snake_case names — EF's PascalCase default is the stage-3 trap) |
| `ofertas_listas` | junction, tenant-scoped | `id_oferta, id_lista_precio, id_tenant` | PK named explicitly `pk_ofertas_listas` (fixes the `PK_articulos_empresas` naming inconsistency instead of copying it); composite FKs `fk_ofertas_listas_{tenant,oferta,lista_precio}` incl. `id_tenant` (alternate keys already exist on all targets); `ix_ofertas_listas_{tenant,oferta,lista_precio}`; **no** audit/soft-delete columns (same PK-only shape as `articulos_empresas`) |

**No unique index on `ofertas`** — deliberate (decision 6). Documented as an exemption in the
backstop map, not an oversight.

## Resolution Contract (pure, `Ways.Domain.Ofertas`)

```csharp
readonly record struct LineaAResolver(
    int IdArticulo, int? IdGrupo, IReadOnlyList<int> IdsCategorias,   // categoria + ancestros
    int IdListaPrecio, decimal Cantidad, decimal PrecioOriginal,
    DateOnly Fecha, TimeOnly Hora, int DiaSemana);                    // 1 = lunes … 7 = domingo

readonly record struct OfertaCandidata(
    int Id, string Nombre, int Prioridad, bool Acumulable,
    AlcanceDeOferta Alcance, BeneficioDeOferta Beneficio, decimal? CantidadMinima,
    DateOnly? FechaDesde, DateOnly? FechaHasta, TimeOnly? HoraDesde, TimeOnly? HoraHasta,
    IReadOnlySet<int> DiasSemana,          // vacío = todos
    IReadOnlySet<int> ListasObjetivo);     // vacío = todas las listas

readonly record struct OfertaAplicada(int IdOferta, string Nombre, decimal DescuentoUnitario);
readonly record struct PrecioConOfertas(
    decimal PrecioOriginal, decimal PrecioFinal, decimal DescuentoUnitario,
    IReadOnlyList<OfertaAplicada> Aplicadas);

static PrecioConOfertas Resolver(in LineaAResolver linea, IReadOnlyList<OfertaCandidata> candidatas);
```

**Arithmetic (binding, proposal decision 1).** Per candidate, discount is computed
**independently against `PrecioOriginal`** and rounded to 2 decimals with
`MidpointRounding.AwayFromZero` (same POS criterion as `ResolvedorDePrecios`/`SugeridorDePrecio`),
then clamped to `[0, PrecioOriginal]` — an oferta can never *raise* a price, so
`precio_unitario > original` yields 0, not a negative discount:

| Benefit | Discount |
|---|---|
| `porcentaje` | `round(original × pct/100, 2)` |
| `importe_fijo` | `importe_fijo` (per **unit**, see Open Questions) |
| `precio_unitario` | `original − precio_unitario` |

Base = highest `prioridad` among `acumulable = false` candidates; ties → greater discount →
lower `id_oferta`. All matching `acumulable = true` candidates then stack. Total =
`min(Σ discounts, PrecioOriginal)`; `PrecioFinal = original − total ≥ 0`. Summing already-rounded
2-decimal values keeps the ticket line verifiable by hand — the point of additive-over-original.
`Aplicadas` is ordered descending `prioridad`, then ascending `id_oferta` (reporting only).

**Matching (binding defaults for spec alignment):** `fecha_desde/hasta` inclusive both ends;
`hora_desde ≤ hora ≤ hora_hasta` inclusive (inverted = rejected at write, no overnight windows
in v1 — a tenant can create two ofertas); `cantidad ≥ cantidad_minima`; NULL window field = no
restriction; empty `DiasSemana`/`ListasObjetivo` = matches everything.

## Batch Boundary

`ServicioDePrecios.PreciosVigentesEnLoteAsync(ids articulo, ids lista, fecha, ct)` →
`IReadOnlyDictionary<(int IdArticulo, int IdListaPrecio), decimal?>`:
(1) load requested listas by id — **no `Activo` filter**, aligning with `PrecioVigenteAsync`'s
documented explicit-id semantics, not `PreciosVigentesAsync`'s "which listas to show by default";
(2) load base listas of the derivadas; (3) ONE `precios` query with `= ANY` on both id sets plus
the shipped date-window predicate, grouped in memory (`OrderByDescending(VigenteDesde)`, first per
pair — same defensive determinism as `ObtenerPrecioFijaAsync`); (4) derivadas resolved through the
shipped `ResolvedorDePrecios.ResolverPrecioDerivado`, keeping its depth-1 and negative-price
guards. `ServicioDeOfertas.ResolverAsync` consumes this and the candidate query, then calls the
pure resolver per line.

Categoria scope matching walks the **ancestor chain** (`ReglaDeCategorias.ProfundidadMaxima = 3`),
built in memory from one `id_categoria`/`id_categoria_padre` projection of the tenant's categorias
— an oferta on "Bebidas" reaching "Cola" is the only reading that makes a hierarchy useful.

## Protection Rules

| Rule | Enforcement today | DB-level |
|---|---|---|
| Exactly one scope / one benefit | `ReglaDeOfertas` (pure) before any write | both CHECKs (backstop) |
| `porcentaje ∈ (0,100]`, `importe_fijo ≥ 0`, `precio_unitario ≥ 0`, `cantidad_minima > 0` | `ReglaDeOfertas` | `numeric(p,s)` overflow → existing 22003 mapping |
| `dias_semana ⊆ {1..7}`, sin duplicados | `ReglaDeOfertas` | `ck_ofertas_dias_semana` (subset only) |
| Referenced articulo/grupo/categoria/lista belong to the tenant | tenant-scoped existence check in `ServicioDeOfertas` (EF global filter) | composite FKs + generic 23503 |
| Lista set replacement is atomic | delete-all + insert inside one transaction, ids `.Distinct()`ed | `pk_ofertas_listas` |

## Backstop Map (db-error-backstops)

| Constraint | Mapping | Test |
|---|---|---|
| `ck_ofertas_alcance_exclusivo` | 23514 → 400 `alcance_de_oferta_invalido` | raw-SQL INSERT asserting SQLSTATE 23514 + translated code |
| `ck_ofertas_beneficio_exclusivo` | 23514 → 400 `beneficio_de_oferta_invalido` | idem |
| `ck_ofertas_ventana_valida` | 23514 → 400 `ventana_de_oferta_invalida` | idem |
| `ck_ofertas_dias_semana` | 23514 → 400 `dias_semana_invalidos` | idem |
| `pk_ofertas_listas` | 23505 → 409 `oferta_lista_duplicada` (same family as `pk_articulos_empresas`) | **race test**: two concurrent PUTs replacing the same oferta's lista set → exactly one winner, the loser a translated 409/serialization outcome, never a 500 |
| `fk_ofertas_*`, `fk_ofertas_listas_*` | existing generic `fk_` prefix → 400 `referencia_invalida` (no code change) | integration test: lista id of another tenant → 400, never 500 |
| `ofertas` uniqueness | **none — documented exemption**: `nombre` is a ticket label, intentionally non-unique | n/a |

**Reachability, honestly:** all four CHECKs are pre-validated by `ReglaDeOfertas`, so under normal
service operation they are unreachable — exactly the family of `ck_clientes_cf_protegido` and
`ck_precios_ventana_valida`. They stay as schema defense against raw/out-of-band writes, and their
tests prove the *translation*, not a reachable client path. The only genuinely racy new surface is
`pk_ofertas_listas`.

## Migration Sequencing

One migration, `OfertasEtapa4` (same precedent as stages 1–3): both tables, the four CHECKs, FKs,
indexes, and `HabilitarRlsDeTenant("ofertas")` + `HabilitarRlsDeTenant("ofertas_listas")` in the
**same** migration (ADR-15). No new enum this stage — scope/benefit types are derived from
nullability, never stored. `dias_semana` maps as `short[]` → `smallint[]` (Npgsql native, no
converter). The `docs/10-modelo-de-datos.md` edit ships in the **same PR** as the migration so the
definitive schema never drifts. **DB CHANGE GATE summary structure:** (a) the two tables with
columns/types, (b) tenancy scoping, (c) every constraint by name, (d) both RLS policies, (e) an
explicit "DEVIATION: `ofertas_listas` replaces doc-10's `id_lista_precio` column" statement,
(f) rollback = drop both tables.

## ABM Composition

`src/Ways.Web/src/paginas/Ofertas.tsx` (dedicated) + `src/api/ofertas.ts` (pure mappers:
`aAltaOferta`, `aValoresOferta`, `opcionesDeLista`, `resumenDeBeneficio`). Form: identification
(`nombre`, `prioridad`, `acumulable`, `activo`) + scope radio driving one of three pickers
(articulo / grupo / categoria) + optional empresa picker (default tenant-wide, decision 5) +
vigencia block (dates, hours, weekday checkboxes) + `cantidad_minima` + benefit radio driving one
of three numeric inputs + multi-select of listas (empty = all, stated in the UI copy).
`react-async-state` applies in full — notably rule 9 (block supersede-during-write, do **not**
token-reconcile it) and rule 5 (per-entity busy flags, not a page-level boolean).

## Testing Strategy

| Layer | What | Approach |
|---|---|---|
| Unit (Domain) | `ResolvedorDeOfertas`: precedence, both tie-breaks, additive stacking, 100% clamp, per-benefit arithmetic, rounding, window/weekday/hour/`cantidad_minima` boundaries, empty-set lista and weekday semantics, zero-candidate case; `ReglaDeOfertas` exclusivity + ranges | Pure, no DB — same bar as `PoliticaDeRoles`/`ResolvedorDePrecios`. This is the bulk of the stage's test mass |
| Unit (Web) | `ofertas.ts` mappers (coercion, `'' → null`, exclusive group forced to `null`), lista option filtering | Colocated `ofertas.test.ts` per `web-descriptor-tests` |
| Component (Web) | Scope/benefit radio show-hide (the `visibleSi` analogue), multi-lista selector, disabled-window behavior | `Ofertas.test.tsx`, RTL + `user-event`, `vi.mock('../api/cliente')` |
| Integration | RLS raw-SQL proofs on both tables, junction replace semantics, ADR-8 404 uniformity, `/resolver` end-to-end scenario mirroring a spec scenario | Real Postgres, `Ways.IntegrationTests` |
| Integration (parity) | Batch price path == `PrecioVigenteAsync` for the same inputs (fija, derivada, missing price, inactive lista) | Assert value equality per pair, both paths in one test |
| Integration (batch) | Resolution over N articles issues a **constant** query count | Count commands via an EF interceptor/`DbCommand` log — the guard against silently reintroducing the N+1 |
| Backstop | Four 23514 translations, 23503 cross-tenant reference, `pk_ofertas_listas` race | SQLSTATE-asserted or translated-domain-code asserted, never bare exception type |

## Open Questions

- [ ] **Time zone for `hora_desde/hasta` and `dias_semana` matching.** The resolver is
  timezone-free by design (decision 3), so `ServicioDeOfertas` must choose one. v1 default:
  server-configured local time. There is no tenant timezone modeled anywhere today
  (`ParametroConocido` is its natural future home). Needs a product answer before a tenant
  operates in a second timezone — flagged, not blocking stage 4.
- [ ] **`importe_fijo` is per UNIT, not per line** (design default, needed to keep the output a
  unit price that a ticket line can reproduce). Doc 10's "`3x2 a $X`" gloss hints at a per-line
  reading. `sdd-spec` must pin this scenario explicitly; if the answer is per-line, only the
  resolver's `importe_fijo` branch changes (`importe_fijo / cantidad`).
- [ ] **Categoria scope includes descendants** (design default, decision above). Cheap either way
  — it is one flag on the ancestor-chain build — but it is a product statement that spec should
  state out loud.
