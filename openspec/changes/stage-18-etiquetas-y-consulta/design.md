# Design: Stage 18 — Etiquetas, carteles y consulta de precios

## Technical Approach

**A format is a tuple, a page box is a named page, a price is the resolver's answer at `cantidad = 1`
— and the one thing CSS cannot promise is measured on paper before anything depends on it.**

The proposal's gate section is the ratified contract: **zero DDL, zero index, zero policy, zero
write**. Everything below is code and CSS over the existing schema, plus one physical measurement.

Six structural facts decide the shape.

1. **The pricing question is answered and must not be answered twice.**
   `ServicioDeOfertas.ResolverAsync` (`:328-436`) is a fixed ~7-query batch that already returns
   `PrecioOriginal` / `PrecioFinal` / `Aplicadas` per line. `ServicioDeEtiquetas` **composes** it; it
   never reads `ofertas` itself, not even to narrow candidates (decision 6).

2. **The `@page` conflict is real and it is scopeable without touching the shared stylesheet.**
   `impresion.css` is global (`main.tsx:7`) and declares `@page { margin: 12mm }` (`:25-27`).
   `@page` takes no ordinary selector — but CSS **named pages** do exactly this scoping, and they
   live in a NEW file. The regression risk collapses to *"does `impresion.css` appear in the diff"*,
   which is a `git diff --exit-code` criterion, not a hope (decision 1).

3. **The selection query already exists and must stay one implementation.**
   `ServicioDeArticulos.ListarAsync` (`:41-93`) owns the `busqueda` predicate, the tenant/empresa
   scoping, the `Nombre` ordering, the paging and the `[1,200]` clamp (`:50`). The three new filters
   go **there**, and `ServicioDeEtiquetas`'s filter path **calls it** — one query builder, one
   descendant expansion, one clamp (decisions 7-9).

4. **The descendant expansion is the ancestor map read in the other direction, from the same query.**
   `ResolverAsync:365-367` projects the tenant's whole `id_categoria → id_categoria_padre` map in
   **one** query and expands it in memory (`CadenaDeCategorias.ConstruirAncestros`). Categories are
   bounded at `ReglaDeCategorias.ProfundidadMaxima = 3`. So the filter needs one new **pure Domain
   function in the same class**, not a recursive CTE (decision 8).

5. **Every axis of the three new filters is already indexed.** Verified in
   `ArticuloConfiguration.cs:129-131`: `ix_articulos_area`, `ix_articulos_categoria`,
   `ix_articulos_marca`, each `(id_x, id_tenant)`. **Zero new indexes** — the gate holds by
   coincidence of good prior design, and the verify criterion asserts it rather than assuming it.

6. **The salón screen is the POS input with the cart, the writes and the session state removed.**
   `Pos.tsx:1068-1078` (keyboard-wedge `autoFocus` + `Enter`), `ServicioDeEscaneo` (identity only, by
   design `:9-10`), then `POST /api/ofertas/resolver`. **Two calls, never a third.**

`Politicas.cs`, `ServicioDeOfertas`, `ServicioDePrecios`, `ServicioDeEscaneo`, `impresion.css` and
every existing screen are **read, not edited**.

## Architecture Decisions

| # | Decision | Alternatives considered | Rationale |
|---|---|---|---|
| 1 | **The page box is a CSS named page declared in a NEW file**: `etiquetas.css` carries `@page etiquetas { size: A4; margin: 0 }` and `.hoja-de-etiquetas { page: etiquetas }`. `impresion.css` is **byte-identical to `main`** and its global `@page { margin: 12mm }` keeps governing every existing print | (a) **A single global page-box declaration in `impresion.css`** (the proposal's second candidate): set `@page { margin: 0 }` and re-add 12 mm as padding on the report views. (b) **Print in a detached `<iframe>`/new window** with its own document | (a) is **rejected**: it turns every existing print view into a diff and converts a page-box change into a silent typography shift on `CajaZ`/`CuentaCorriente`, whose only automated nets assert `d-print-none` — they cannot see a margin. An untestable regression on a print path nobody re-prints for weeks is the worst shape available. (b) is the **recorded fallback**, not the primary: it is regression-proof by construction but abandons the stage-11 *"the component you see is the component you print"* pattern, needs the sheet rendered into a foreign document, and collides with popup blockers. Named pages get (b)'s isolation at (a)'s cost. **Support is an assumption, not a fact** — which is why it is spike exit criterion E2 |
| 2 | **The spike prints a calibration grid, not a label sheet**, and records a numeric verdict in `spike-alineacion.md` before slice 3 exists | Print the real sheet and eyeball it against the die-cut paper | An eyeball cannot separate *"the grid is 1.5 mm off"* from *"the browser printed at 96%"*. The grid separates the two causes by construction (the scale square), so a FAIL names which one failed and the fail path (escalate the QuestPDF licence to the owner) is taken on evidence |
| 3 | **`DescriptorDeFormato` is a flat, function-free, frozen data record**; every derived quantity (cells per sheet, sheets needed) comes from a pure helper, **never a stored field**; no component imports a descriptor by literal id | Give the descriptor methods/render hints; store `celdasPorHoja` | A stored derived count is a second source of truth that a C1 table row would have to back-fill correctly. A flat record maps 1:1 onto a `formatos_etiqueta` row, so C1 becomes *"replace the frozen array with a fetch"* and `HojaDeEtiquetas` never learns where the tuple came from (OD3's whole point) |
| 4 | **Copies live on the client.** `POST /api/etiquetas/datos` carries **no** `copias` field; the screen expands rows into cells with the pure `expandirCeldas(filas, copias)` | Send `copias` per id and let the server return duplicated rows | The proposal's endpoint input is exactly *"`idPuntoVenta`, `idListaPrecio`, and either `idsArticulo[]` or a filter"* (decision 12) — copies appear only in decision 7, as a screen concern. A copy is a **presentation multiplier**: putting it in the DTO would let a caller ask the server to transmit the same row 99 times and would leak print layout into a data contract (`dto-contract-honesty` rule 1). Registered as tension **T1** |
| 5 | **The label resolves at `cantidad = 1` and the PV supplies `IdEmpresa`.** Each `LineaDeResolucion` is `(idArticulo, idEmpresa: PV's empresa, idListaPrecio, cantidad: 1m)` | Resolve at the copies count; omit `IdEmpresa` | `cantidad = 1` is proposal decision 5 — a *"llevando 3"* price on a shelf is a false price. `IdEmpresa` is **load-bearing and easy to lose**: `ReglaDeOfertas.CoincideEmpresa` is the one per-line filter that runs in `ServicioDeOfertas` itself (`:456`); passing `null` would print another empresa's oferta on this shop's gondola. Mutation target 9 |
| 6 | **`soloConOfertaVigente` is a post-filter over `Aplicadas.Count > 0`, and the coarse candidate query does NOT narrow by oferta at all** | Pre-narrow the candidate set with a join on `ofertas` | The proposal permits the coarse query to narrow; this design declines the permission. An `ofertas` join is a **second matching implementation** in embryo — it must reason about `alcance` (artículo / grupo / categoría-with-descendants), `id_empresa`, the vigencia window, `dias_semana` and `cantidad_minima`, i.e. everything `ResolvedorDeOfertas` exists to own. The cap (200) already bounds the work and `ResolverAsync` is O(1) in queries, so the narrowing buys nothing and risks the exact divergence the proposal names as a Med risk |
| 7 | **`ServicioDeEtiquetas`'s filter path calls `ServicioDeArticulos.ListarAsync`**; `truncado` comes from its `PaginaDe<T>.Total`, not from a second `COUNT` | Duplicate the query builder inside `ServicioDeEtiquetas`; `Take(cap + 1)` and compare | `ListarAsync` already runs `CountAsync` unconditionally (`:79`), so `Total > 200` is **exact and free**, while `Take(201)` would clamp back to 200 (`:50`) and make `truncado` unobservable. Duplicating the builder would duplicate the `busqueda` predicate, the empresa scoping and the descendant expansion — three drift surfaces for zero gain |
| 8 | **`CadenaDeCategorias` gains `ConstruirDescendientes(idCategoria, padrePorCategoria)`** — pure Domain, same class, same one-query projection, same `ProfundidadMaxima` bound | A recursive CTE in SQL; a denormalized `nivel`/path column | doc-11's *"same semantics, not a new concept"* made literal: one query over a few dozen rows, expanded in memory, unit-testable with no database, and provably consistent with the existing direction (`d ∈ Desc(c) ⟺ c ∈ Anc(d)`, a property test). A CTE is a second traversal implementation that only a container can test; a path column is **schema**, which reopens the gate |
| 9 | **`ServicioDeArticulos` exports `public const int TamanioMaximoDePagina = 200`** and its own clamp consumes it; `ServicioDeEtiquetas` references the constant | A local `200` in `ServicioDeEtiquetas` with a comment citing `ServicioDeArticulos.cs:50` | Decision 7 of the proposal says the label job *"reuses **that** number instead of inventing a constant"*. A comment is not a reuse — it is a magic number with a footnote that drifts on the first tuning. The constant makes the coupling structural: one mutation breaks the listing clamp test **and** the `truncado` test together (target 18). Behaviour is unchanged, asserted |
| 10 | **The request carries no `Momento`.** The server resolves `IRelojDelSistema.Ahora` **once** for the whole sheet and **echoes it** in the response | Mirror `SolicitudDeResolucion.Momento` (`Contratos.cs:99`) | A shelf label priced for a hypothetical instant is a wrong price on paper. One instant per sheet also stops a job from straddling an oferta's `hora_hasta` (target 26). The hypothetical path already exists on `/api/ofertas/resolver` for whoever legitimately needs it; the echo makes the sheet's *"generado"* header server-truth |
| 11 | **`NombreDeLista` is read by the server from `listas_precio` and returned**; the client's selector label is never printed | Print the label the selector already holds (`ListaPrecioAsignable.Nombre`) | Decision 4's promise is *"the operator always knows which price left the printer"* — that only holds if the printed name and the priced list come from the **same** read. A client-side label can disagree with the id actually sent (stale selector, renamed list). Source-of-truth mutant killed by a raw-`UPDATE` sentinel test (`mutation-proof-tests` rule 12a, target 23) |
| 12 | **`idsArticulo` XOR `filtro`.** Both ⇒ `400 seleccion_ambigua`; neither ⇒ `400 seleccion_requerida`; `idsArticulo.Count > 200` ⇒ `400 seleccion_excedida` (**not** a silent truncation) | Accept both and let ids win; truncate an oversized id list | A field with no destination is the exact shape `dto-contract-honesty` rule 1 refuses. And the two overflow paths are **different facts**: a filter matching 4 000 artículos is the system saying *"narrow it"* (`truncado`), while 300 explicit ids is a caller ignoring a cap its own screen enforces — silently dropping 100 of them prints an incomplete job that looks complete |
| 13 | **No new policy, and the exposure clause is a DTO invariant.** `POST /api/etiquetas/datos` groups under `OperacionDePos` with nothing stacked (`/api/ofertas/resolver` precedent), and `SuperficieDeAutorizacionTests`'s allowlist gains exactly one entry | Stack `GestionDeCatalogo`; a new `ImpresionDeEtiquetas` policy | Proposal decision 10. `Politicas.cs` gains a name only for a new **kind** of risk (the stage-15 criterion); this is the read the POS already accepts. The allowlist entry is mandatory: without it the write-surface guard fails the build on a read-only POST, which is why `/api/ofertas/resolver` is already listed there |
| 14 | **The idle reset is an effect keyed on the resolution, with its `clearTimeout` in the cleanup and a generation bump on fire** — no timer id held in a ref across renders | `useRef` holding the timer id, cleared imperatively by each handler | The repo's own timer pattern is the debounce of `CompraEditor.tsx:90-110` / `Existencias.tsx:58-79`: `setTimeout` inside the effect, `clearTimeout` in the returned cleanup, plus a `generacionRef`. A ref-held id needs every handler to remember to clear it; the effect form makes a second scan cancel the first timer **structurally**. The generation bump on reset is what stops a slow `resolver` from repainting the previous customer's price after the screen already cleared (`react-async-state` rules 2/3, target 30) |

## The spike (slice 1, task 1)

**Subject**: `A4-3x8` — deliberately the tightest geometry (edge-to-edge, zero gutters). If the
tolerant format is measured first, the failure mode most likely to bite (the printer's non-printable
margin clipping the outer columns) is never seen.

**What the calibration sheet prints** — one component, `HojaDeEtiquetas` in `modo="calibracion"`,
driven by the **same** descriptor tuple the real sheet uses (a grid drawn from a second set of
numbers proves nothing):

| Element | Purpose |
|---|---|
| A 0.2 mm hairline box on every nominal cell, at exact pitch | The grid itself: what should coincide with the die-cut |
| A 6 mm registration cross centred on **each cell's top-left origin** (the measured datum) | Origin deviation is measured to a point, not to a fuzzy edge |
| `f{row}c{col}` in the centre of every cell | A measurement can name the cell it came from |
| A horizontal 200 mm ruler (top) and a vertical 280 mm ruler (left), 1 mm ticks, 10 mm labels | Cumulative drift is read directly off the page |
| A **100.0 × 100.0 mm scale square**, labelled | Separates *"the grid is wrong"* from *"the browser printed at 96%"* — the single most common false failure |
| A `d-print-none` instruction block on screen | Required print settings: A4, 100 % scale, *"fit to page"* OFF, margins **none**, background graphics ON |

**Exit criteria — both binary, both required.**

- **E1 (geometry)**: printed at 100 % on the reference die-cut sheet, on **at least one target
  browser** — every measured cell origin within **±1.0 mm** of nominal (measured at minimum on the
  four corner cells, the centre cell and both ends of the last row) **and** last-row cumulative drift
  within **±1.5 mm**. Precondition: the scale square measures 100.0 ± 0.3 mm; otherwise the run is
  void, not a FAIL.
- **E2 (non-regression)**: `CajaZ` and `CuentaCorriente` print **exactly as on `main`** — proven three
  ways: `git diff --exit-code src/Ways.Web/src/estilos/impresion.css` is clean, their existing
  `d-print-none` tests are green and **unedited**, and a *"Guardar como PDF"* of each from `main` and
  from the branch is compared page-box to page-box, in the same browser, with the same settings.

**Registration**: `openspec/changes/stage-18-etiquetas-y-consulta/spike-alineacion.md`, one row per
run — date, browser + version, OS, printer make/model, sheet reference, print scale and margin
settings, scale-square measurement, per-cell deviation (6 cells), last-row drift, E1 verdict, E2
verdict, evidence path. The verdict is copied into the slice-1 PR body. **This task requires the
owner's printer and paper**; it is human-in-the-loop by nature.

**Fail path**: STOP. No library is swapped silently — the QuestPDF licence question goes to the owner
as a blocking commercial decision (OD1). Slice 4 is independent and may proceed meanwhile.

## Interfaces / Contracts

### Web — the descriptor (OD3)

```ts
// src/Ways.Web/src/etiquetas/formatos.ts — la ÚNICA fuente de milímetros de la etapa.
// Registro plano, sin funciones ni derivados almacenados: una fila de un futuro
// `formatos_etiqueta` (C1) mapea 1:1 sobre estos campos y el renderer no cambia (OD3).
export type CampoDeCelda =
  | 'nombre' | 'codigo' | 'precioFinal' | 'precioOriginal' | 'unidadVenta' | 'nombreDeOferta'

export type DescriptorDeFormato = {
  readonly id: string
  readonly nombre: string
  readonly familia: 'etiqueta' | 'cartel'
  readonly paginaMm: { readonly ancho: number; readonly alto: number }
  readonly margenSuperiorMm: number
  readonly margenIzquierdoMm: number
  readonly columnas: number
  readonly filas: number
  readonly celdaMm: { readonly ancho: number; readonly alto: number }
  readonly medianilHorizontalMm: number
  readonly medianilVerticalMm: number
  readonly padExternoMm: number      // padding interior de la celda: la defensa contra el
                                     // margen no imprimible del hardware, nunca mover la grilla
  readonly campos: readonly CampoDeCelda[]
  readonly escalaDePrecio: number    // tamaño relativo del precio final dentro de la celda
  readonly referencia: string        // la hoja real de la que salió la geometría
}

// Derivados: SIEMPRE calculados, JAMÁS almacenados (decisión 3).
export const celdasPorHoja = (d: DescriptorDeFormato) => d.columnas * d.filas
export const contarHojas = (celdas: number, d: DescriptorDeFormato) =>
  Math.ceil(celdas / celdasPorHoja(d))
```

| id | Familia | Página | Cols × Filas | Celda (mm) | Offset sup/izq (mm) | Medianil h/v (mm) | Por hoja | Referencia |
|---|---|---|---|---|---|---|---|---|
| `A4-3x8` | etiqueta | A4 210×297 | 3 × 8 | 70.0 × 37.0 | 0.5 / 0.0 | 0.0 / 0.0 | **24** | Hoja A4 autoadhesiva 24 et. 70×37 (Avery 3422 y equivalentes del mercado local). Tesela borde a borde: `3 × 70 = 210`, `8 × 37 = 296` (+0.5 arriba y abajo). `padExternoMm = 5` |
| `A4-2x7` | etiqueta | A4 210×297 | 2 × 7 | 99.1 × 38.1 | 15.15 / 4.65 | 2.5 / 0.0 | **14** | Hoja A4 autoadhesiva 14 et. 99.1×38.1 (Avery L7163 y equivalentes). Cierra exacto: `2×99.1 + 2×4.65 + 2.5 = 210`, `7×38.1 + 2×15.15 = 297`. `padExternoMm = 3` |
| `CARTEL-A4` | cartel | A4 210×297 | 1 × 1 | 190.0 × 277.0 | 10.0 / 10.0 | 0.0 / 0.0 | **1** | Hoja entera, margen 10 mm |
| `CARTEL-A5` | cartel | A4 210×297 | 1 × 2 | 190.0 × 133.5 | 10.0 / 10.0 | 0.0 / 10.0 | **2** | Media hoja: `2×133.5 + 10 + 2×10 = 297` |

The proposal fixes these four tuples (`A4-3x8` 70×37/24, `A4-2x7` 99×38/14, `CARTEL-A4`,
`CARTEL-A5`); this design only sharpens two of them to their real published geometries. The other
common Argentine A4 family — **105×74 (8/hoja)** and **105×37 (16/hoja)** — is **not** shipped: OD1's
pending owner decision reserves the right to replace a tuple with the shop's actual sheet **before**
the spike runs, and adding a fifth speculative tuple costs a spike run each. Registered as tension
**T2**.

**How the C1 door stays open**: (a) nothing derived is stored, so a DB-loaded tuple needs no
back-fill; (b) `HojaDeEtiquetas` and `Etiquetas.tsx` take `descriptores: readonly
DescriptorDeFormato[]` as data — no component imports `FORMATOS` by index or by literal id; (c) the
record has no functions, so it survives JSON round-tripping unchanged.

### Web — the pure sheet renderer

```tsx
// src/Ways.Web/src/etiquetas/HojaDeEtiquetas.tsx — componente de impresión PURO:
// sin fetch, sin estado, sin reloj. Props = descriptor + filas ya expandidas por copias.
type Props = {
  descriptor: DescriptorDeFormato
  celdas: readonly FilaDeEtiqueta[]          // ya multiplicadas por copias (decisión 4)
  nombreDeLista: string                       // decisión 11: viene del servidor
  modo?: 'normal' | 'calibracion'             // el spike usa el MISMO componente
}
// Emite la geometría como custom properties en mm sobre `.hoja-de-etiquetas` — la única
// proyección que jsdom PUEDE medir (--pagina-ancho, --celda-ancho, --pitch-x, --margen-sup…),
// y la regla de tachado es `celda.ofertas.length > 0`, NUNCA `precioOriginal !== precioFinal`.
```

### Application — the etiquetas contracts

```csharp
// src/Ways.Application/Etiquetas/Contratos.cs
public sealed record FiltroDeEtiquetas(
    string? Busqueda, int? IdArea, int? IdCategoria, int? IdMarca, bool SoloConOfertaVigente = false);

// Sin `Momento` (decisión 10) y sin `copias` (decisión 4). Ids XOR filtro (decisión 12).
public sealed record SolicitudDeEtiquetas(
    int IdPuntoVenta, int IdListaPrecio, IReadOnlyList<int>? IdsArticulo, FiltroDeEtiquetas? Filtro);

/// CLÁUSULA DE EXPOSICIÓN (decisión 10 del proposal, `dto-contract-honesty`): este record NO
/// declara —ni declarará— costo_lista, costo_nominal, descuento_proveedor, id_proveedor_habitual
/// ni margen. No están ocultos en la UI: están AUSENTES del contrato. Una hoja impresa se va del
/// local. El costo es admin-only por política (LecturaDeRentabilidad).
public sealed record FilaDeEtiqueta(
    int IdArticulo, string CodigoInterno, string? CodigoBarra, string Nombre, string UnidadVenta,
    decimal PrecioOriginal, decimal PrecioFinal, IReadOnlyList<OfertaAplicadaDto> Ofertas);

public sealed record ArticuloExcluido(int IdArticulo, string CodigoInterno, string Nombre, string Motivo);

public sealed record DatosDeEtiquetas(
    int IdListaPrecio, string NombreDeLista, DateTimeOffset Momento,
    IReadOnlyList<FilaDeEtiqueta> Filas, IReadOnlyList<ArticuloExcluido> Excluidos, bool Truncado);
```

`Ofertas` reuses `Ways.Application.Ofertas.OfertaAplicadaDto` verbatim — a per-oferta discount is a
price fact, not cost, and a parallel type would be a second shape to keep honest.

### Domain — the descendant expansion

```csharp
// src/Ways.Domain/Ofertas/CadenaDeCategorias.cs — MISMA clase, MISMO mapa de una sola consulta,
// MISMA cota de ReglaDeCategorias.ProfundidadMaxima; solo cambia el sentido del recorrido.
// Invariante bajo prueba: d ∈ ConstruirDescendientes(c) ⟺ c ∈ ConstruirAncestros(d).
public static IReadOnlySet<int> ConstruirDescendientes(
    int idCategoria, IReadOnlyDictionary<int, int?> padrePorCategoria);
```

### Application — the three filters

`ServicioDeArticulos.ListarAsync` gains `int? idArea, int? idCategoria, int? idMarca` **after** the
existing parameters, each guarded by its own `if (… is { } x)`. The clamp becomes
`Math.Clamp(tamanio, 1, TamanioMaximoDePagina)` with the constant made `public const` (decision 9).
`idCategoria` loads the `id → id_padre` projection once and applies
`query.Where(a => a.IdCategoria != null && descendientes.Contains(a.IdCategoria.Value))`. **Every
other line, including the ordering, the paging and the `busqueda` predicate, is untouched.**

## Data Flow

```
Etiquetas.tsx ──GET /api/catalogos/{areas,categorias,marcas}──┐
              ──GET /api/listas-precio (default-first)────────┤   filtros + selector
              ──GET /api/articulos?busqueda&idArea&idCategoria&idMarca&tamanio
              │        └─ ServicioDeArticulos.ListarAsync ── CadenaDeCategorias.ConstruirDescendientes
              │           (multi-select reducer, patrón FacturarRemitos.tsx:134,142-144)
              └──POST /api/etiquetas/datos { idPuntoVenta, idListaPrecio, idsArticulo | filtro }
                     └─ ServicioDeEtiquetas
                          1  puntos_venta  → idEmpresa                          (1 consulta)
                          2  listas_precio → NombreDeLista (404 si no existe)   (1)
                          3  selección: ids  → 1 consulta
                                        filtro → ListarAsync (1 categorias + 1 count + 1 page)
                          4  codigos_barra del conjunto                         (1)
                          5  ResolverAsync(lineas @ cantidad=1, idEmpresa, momento)  (7)
                                └─ ServicioDePrecios.PreciosVigentesEnLoteAsync (incluidas)
                          6  PrecioFinal is null      ⇒ Excluidos, NUNCA una fila
                             SoloConOfertaVigente      ⇒ Aplicadas.Count > 0
                                                                       ≤ 11 consultas, O(1) en N
                 DatosDeEtiquetas → expandirCeldas(filas, copias) → HojaDeEtiquetas → window.print()

ConsultaPrecios.tsx ──GET /api/articulos/escaneo?entrada=…──→ identidad (sin precio, por diseño)
                    ──POST /api/ofertas/resolver [1 línea @ cantidad=1]──→ precio + Aplicadas
                    exactamente DOS llamados · CERO escrituras · reset a inactivo a los ~20 s
```

**Query budget, declared and verifiable**: **≤ 11 EF commands per request, independent of N**
(≤ 10 on the explicit-ids path — judgment-day Slice 2, ronda 2, juez A SUGGESTION: measured with
the interceptor after the availability check was folded into the identity query as a correlated
`EXISTS`, never a separate roundtrip; the number is 10, not the ≤ 9 estimated before the fix was
implemented — amended here honestly, see tasks.md). Asserted with the repo's own technique — a
`DbCommandInterceptor` counting `ReaderExecuting`, the `OfertasResolucionTests.ContadorDeComandos` /
`VentasCheckoutTests.ContadorDeComandos` (`:930`) pattern — over a 1-artículo request **and** a
200-artículo request, whose counts must be **equal**.

## Web composition

- **`Etiquetas.tsx`** (`/etiquetas`, `RutaProtegida rolesPermitidos={[Vendedor, Supervisor, Admin]}`):
  filters (búsqueda, área, categoría, marca, *con oferta vigente*), the `FacturarRemitos.tsx:134`
  `useReducer` multi-select with *"elegir todos"*, per-row copies `1..99` with an *"aplicar a todos"*
  helper, format + lista selectors (lista defaulted to the first `EsDefault` row, which
  `GET /api/listas-precio` already returns first), the *"N etiquetas = M hojas"* preview, the
  excluded-count notice, the `d-print-none` print-settings block, and one **Imprimir** button that is
  `window.print()` — `CajaZ.tsx:87` verbatim. `react-async-state`: `generacionRef` on every fetch;
  the selection is cleared when the filter axis changes (a row that left the list must not send a
  phantom id, `FacturarRemitos.tsx:138-140`); first-line re-entrancy guard on the datos POST.
- **`ConsultaPrecios.tsx`** (`/consulta-precios`, same roles): `autoFocus` + `Enter` input
  (`Pos.tsx:1068-1078`), PV and lista from the same selectors the POS uses and remembered locally,
  oversized typography, struck original **only** when `Aplicadas.length > 0`, *"no encontrado"* on
  the 404, *"consultá en caja"* when the resolver returns a null price — **never `$0`** — and the
  idle reset of decision 14 (`MS_DE_RESET = 20_000`, exported so the test cannot drift from the
  component). Zero writes, zero persistence, zero lookup history.
- **`HojaDeEtiquetas.tsx`** — pure, props-only, shared by the screen and the spike.
- `web-descriptor-tests`: colocated tests for `formatos.ts`, `celdasPorHoja`, `contarHojas`,
  `expandirCeldas`, the API clients/mappers and both screens' descriptors.

## File Changes

| File | Action | Description |
|---|---|---|
| `src/Ways.Web/src/etiquetas/formatos.ts` (+ `.test.ts`) | Create | The type + the four tuples + the derived helpers — the only mm of the stage |
| `src/Ways.Web/src/etiquetas/HojaDeEtiquetas.tsx` (+ `.test.tsx`) | Create | Pure grid/cell/poster renderer, `normal` and `calibracion` modes |
| `src/Ways.Web/src/estilos/etiquetas.css` | Create | `@page etiquetas` + `.hoja-de-etiquetas` grid rules (decision 1) |
| `src/Ways.Web/src/estilos/impresion.css` | **Unmodified** | Decision 1 — asserted with `git diff --exit-code` |
| `openspec/changes/stage-18-etiquetas-y-consulta/spike-alineacion.md` | Create | The spike's numeric record and verdict |
| `src/Ways.Domain/Ofertas/CadenaDeCategorias.cs` (+ tests) | Modify | `ConstruirDescendientes`, same map, same bound |
| `src/Ways.Application/Articulos/ServicioDeArticulos.cs` | Modify | Three optional filters + `public const TamanioMaximoDePagina`. **Nothing else** |
| `src/Ways.Api/Endpoints/ArticulosEndpoints.cs` | Modify | Three optional query params on the existing `MapGet("/")` |
| `src/Ways.Application/Etiquetas/Contratos.cs` · `ServicioDeEtiquetas.cs` | Create | The contracts (no cost field) and the composition |
| `src/Ways.Api/Endpoints/EtiquetasEndpoints.cs` | Create | One route, `OperacionDePos`, nothing stacked |
| `src/Ways.Api/Program.cs` / DI registration | Modify | One `AddScoped<ServicioDeEtiquetas>` + one `MapearEtiquetas()` |
| `tests/Ways.IntegrationTests/SuperficieDeAutorizacionTests.cs` | Modify | One allowlist entry: `("POST", "/api/etiquetas/datos")` |
| `src/Ways.Web/src/paginas/Etiquetas.tsx` · `ConsultaPrecios.tsx` (+ `.test.tsx`) | Create | The two screens |
| `src/Ways.Web/src/api/etiquetas.ts` (+ `.test.ts`) · `tipos.ts` · `App.tsx` · `Layout.tsx` | Create + Modify | Client, DTO mirrors, two routes, two menu entries |
| `src/Ways.Api/Seguridad/Politicas.cs` | **Unmodified** | Decision 13 |
| `src/Ways.Infrastructure/**` | **Unmodified** | Zero migration, zero configuration, zero seed |
| `docs/11-programa-post-paridad.md` | Modify | Etapa 18 status block (last slice) |

## What does NOT change

`impresion.css`, `Politicas.cs`, `ServicioDeOfertas`, `ResolvedorDeOfertas`, `ServicioDePrecios`,
`ServicioDeEscaneo`, `ParserDeEscaneo`, `ServicioDeClientes`, every write path, every migration, the
`GET /api/articulos` unfiltered behaviour/ordering/paging/clamp, `CajaZ.tsx`, `CuentaCorriente.tsx`,
`Pos.tsx`, and the owner's reserved carryovers (`importe` CHECK micro-gate, `articulos_empresas`
replace-set gap, `ways_owner`, `stage-13b`).

## Testing Strategy

| Layer | What | Approach |
|---|---|---|
| Domain unit (no DB) | `ConstruirDescendientes` over a fixed 3-level forest: leaf ⇒ itself only; root ⇒ whole subtree; a sibling subtree never leaks; a corrupt cycle terminates; the **duality property** `d ∈ Desc(c) ⟺ c ∈ Anc(d)` over every pair | xUnit pure, `CadenaDeCategoriasTests` pattern — no fixture, no container |
| Web unit (vitest) | `celdasPorHoja` / `contarHojas` (24 → 1 hoja, 25 → 2, 0 → 0) · `expandirCeldas` (copies 1/3/99, order preserved) · the copies clamp · the geometry projection: for **each** of the four descriptors, the emitted mm custom properties equal the tuple, and `columnas × filas` equals the declared per-sheet count | Colocated, `web-descriptor-tests`. **A wrong tuple fails the test, not the paper** |
| Web component (vitest) | `HojaDeEtiquetas`: strike rendered **iff** `ofertas.length > 0` — proven with a constructed DTO where `precioOriginal ≠ precioFinal` **and** `ofertas: []` (no strike) and its mirror (equal prices, non-empty `ofertas` ⇒ strike). The instruction block carries `d-print-none` | The only way to kill the `precioOriginal !== precioFinal` mutant, since production never emits that pair |
| Integration — the query budget | A `DbCommandInterceptor` counting `ReaderExecuting`: the 1-artículo and the 200-artículo requests produce the **same** count, ≤ 11 | `VentasCheckoutTests.ContadorDeComandos:930` / `OfertasResolucionTests` precedent |
| Integration — the exposure clause | The **serialized** response is walked recursively and asserted to contain **no property named** `costo`, `costoLista`, `costoNominal`, `descuentoProveedor`, `idProveedorHabitual`, `proveedor` or `margen` — matched by property **name**, never by substring on the payload (`OfertaAplicadaDto.DescuentoUnitario` legitimately contains *"descuento"*; a substring assertion would be red on a correct DTO and is the trap this row exists to name) | `dto-contract-honesty` |
| Integration — the resolver as sole authority | `soloConOfertaVigente = true` returns **exactly** the artículos for which `POST /api/ofertas/resolver` reports `Aplicadas.Count > 0` at `cantidad = 1`, same lista, same momento, over the same candidates. The fixture **discriminates**: one artículo-scoped oferta in window, one **categoría-scoped** oferta reaching a descendant, one out-of-window, one `cantidad_minima = 3`, one `id_empresa` of another empresa | A divergence test against the real endpoint, never a re-implementation |
| Integration — `cantidad = 1` and the empresa | A `cantidad_minima = 3` oferta ⇒ the row carries **empty** `Ofertas` and `PrecioOriginal == PrecioFinal`. An oferta scoped to empresa B ⇒ absent from a sheet printed for a PV of empresa A | Two discriminating fixtures under `RelojFijo` |
| Integration — exclusion and truncation | An artículo with no vigent price in the chosen lista is **absent** from `Filas` and **present** in `Excluidos` with its identity (not only a count); 201 matching artículos ⇒ 200 rows and `Truncado = true`; 200 ⇒ `false`; 201 explicit ids ⇒ `400 seleccion_excedida`; both/neither selector ⇒ the two 400s | Boundary fixtures at the cap, both sides |
| Integration — the three filters | Each filter alone with **asymmetric** seeds; `idCategoria` on a grandparent returns the grandchild's artículo (three-level fixture); the three combined; **and the unfiltered regression**: with all three absent, items, order, total and paging are identical to the pre-stage path over a seed that would move if any filter defaulted | The proposal's *"byte-identical, asserted not assumed"* criterion |
| Integration — read-model honesty | Every positional field of `FilaDeEtiqueta` and `DatosDeEtiquetas` read back at least once with pairwise-distinct values (rule 12b); `NombreDeLista` desynced by a raw `UPDATE` to a sentinel must surface the sentinel (rule 12a); a **sibling** artículo of the same tenant seeded on every listing test (rule 12c) | `mutation-proof-tests` 12 |
| Integration — authorization | Vendedor / Supervisor / Admin ⇒ 200 on `POST /api/etiquetas/datos`; Root ⇒ 403 — the same matrix as `/api/ofertas/resolver`; tenant B never sees tenant A's artículos; `SuperficieDeAutorizacionTests` green with the single new allowlist entry | One test per role |
| Web (vitest) — Etiquetas | The multi-select reducer (elegir todos / limpiar / cambio de filtro limpia la selección); the sheet-count preview; the excluded notice; a double click on **Imprimir** issues one `window.print()`; a stale `datos` response resolved **inside `act`** is discarded (rule 7) | `react-async-state` + `web-descriptor-tests` |
| Web (vitest) — ConsultaPrecios | Exactly **two** calls per scan and no others (the mocked client's full call log is asserted — this **is** the zero-writes proof); unknown code ⇒ *"no encontrado"*; null price ⇒ *"consultá en caja"* and never `$0`; strike only with an oferta; fake timers: **19.9 s no reset / 20.0 s reset**; a second scan cancels the first timer (exactly one reset fires); a resolution landing **after** a reset does not repaint; the input is cleared and refocused | `vi.useFakeTimers`, RTL |
| **Structural only (honest limits)** | jsdom implements neither `@media print`, nor `@page`, nor physical units, nor pagination. **No automated test in this repo can prove a label lands on a die-cut cell.** What is proven instead: (a) the descriptor → mm projection, (b) the derived counts, (c) `d-print-none` presence, (d) `impresion.css` unchanged by `git diff --exit-code`, (e) the `CajaZ`/`CuentaCorriente` descriptor tests green **and unedited**. Physical alignment is the spike's one-time measurement, recorded with numbers — it is **evidence, not a regression net**, and this design says so rather than implying a coverage it does not have | `mutation-proof-tests` rule 13's posture: when a property cannot be observed dynamically, assert it structurally and name the gap |
| Exempt | Visual styling beyond testids; browser/printer matrix beyond the spike's recorded target — exemption registered, inherited from stages 12-17 | — |

## Mutation targets

`mutation-proof-tests`: name the clause, apply the mutation, watch the named test fail, revert,
record the evidence in the PR body. **37 numbered targets**, colocated with the slice that introduces
the clause. Targets marked **S** are *structural* — their net is a file/state assertion, not a
runtime behaviour, because the behaviour is not observable in jsdom (see the honest-limits row).

| # | Slice | Clause | Mutation | Test that MUST fail |
|---|---|---|---|---|
| 1 **S** | 1 | `@page etiquetas` in `etiquetas.css` + `page: etiquetas` on `.hoja-de-etiquetas` | delete either | the sheet container no longer carries the named-page class/property (descriptor test) + spike E2 |
| 2 **S** | 1 | `impresion.css` untouched | edit its `@page` | `git diff --exit-code src/Ways.Web/src/estilos/impresion.css` (verify criterion 3) |
| 3 | 1 | Each geometry number of each of the four descriptors (celda, offset, medianil, columnas, filas) | change one at a time | that descriptor's geometry test — the emitted mm and the per-sheet count |
| 4 | 1 | `celdasPorHoja` / `contarHojas` **derived**, never stored | add a stored `celdasPorHoja` field and read it | the derived-count test over a mutated tuple (a stored count would not follow) |
| 5 | 1 | `contarHojas = ceil(celdas / porHoja)` | `floor` / integer division | 25 etiquetas on `A4-3x8` ⇒ 2 hojas |
| 6 | 1 | The calibration grid driven by the **same** descriptor as the real sheet | give `modo="calibracion"` its own numbers | the calibration-mode test asserting identical emitted geometry in both modes |
| 7 | 1 | The strike rule `ofertas.length > 0` | `precioOriginal !== precioFinal` | the constructed-DTO pair (distinct prices + empty `ofertas`; equal prices + non-empty) |
| 8 **S** | 1 | The print-settings block's `d-print-none` | delete the class | its descriptor test |
| 9 | 2 | `IdEmpresa` taken from the punto de venta on every `LineaDeResolucion` | pass `null` | the other-empresa oferta fixture (`ReglaDeOfertas.CoincideEmpresa`) |
| 10 | 2 | `Cantidad = 1m` on every `LineaDeResolucion` | use the row count / 3 | the `cantidad_minima = 3` fixture: the row must carry **no** oferta |
| 11 | 2 | `PrecioFinal is null` ⇒ `Excluidos`, never a row | emit the row with `0m` | the sin-precio fixture, both directions (absent from `Filas`, present in `Excluidos`) |
| 12 | 2 | `Excluidos` carries identity, not just a count | return an empty list with a count | the same fixture asserting `IdArticulo`/`CodigoInterno` |
| 13 | 2 | `soloConOfertaVigente` ⇒ `Aplicadas.Count > 0` | `PrecioFinal < PrecioOriginal` | the divergence test with the out-of-window and `cantidad_minima = 3` seeds |
| 14 | 2 | The coarse query does **not** join `ofertas` | add a candidate-narrowing join on `ofertas.id_articulo` | the divergence test's **categoría-scoped** oferta (which the join misses) |
| 15 | 2 | `ConstruirDescendientes` direction | return ancestors / only the node | the three-level fixture: filtering by the grandparent returns the grandchild's artículo |
| 16 | 2 | Its `ProfundidadMaxima` bound and cycle break | unbounded loop | the corrupt-cycle unit test (must terminate) |
| 17 | 2 | The duality invariant | break either direction | the pure property test `d ∈ Desc(c) ⟺ c ∈ Anc(d)` |
| 18 | 2 | `TamanioMaximoDePagina` shared by the clamp **and** the cap | change the constant | the listing clamp test **and** the `truncado` test fail **together** (the coupling is the point) |
| 19 | 2 | `Truncado = pagina.Total > cap` | `>=` / hardcode `false` | the 200/201 boundary pair |
| 20 | 2 | `idsArticulo.Count > cap` ⇒ `400 seleccion_excedida` | truncate silently | its 400 test |
| 21 | 2 | ids **XOR** filtro (`seleccion_ambigua` / `seleccion_requerida`) | accept both / neither | the two 400 tests (`dto-contract-honesty` rule 1) |
| 22 | 2 | The exposure clause: no cost/proveedor property in `FilaDeEtiqueta` | add `CostoNominal` | the recursive **property-name** assertion over the serialized JSON |
| 23 | 2 | `NombreDeLista` read from `listas_precio` by the server | take it from the request | the raw-`UPDATE` sentinel test (rule 12a) |
| 24 | 2 | Every positional field of `FilaDeEtiqueta` / `DatosDeEtiquetas` | drop or transpose one at a time | the pairwise-distinct read-back test (rule 12b) |
| 25 | 2 | One `momento` for the whole sheet, echoed in the response | resolve per row / drop the echo | the pinned-clock test across an oferta's `hora_hasta` |
| 26 | 2 | Each `if (idArea/idCategoria/idMarca is { } x)` in `ListarAsync` | delete one at a time | that filter's asymmetric-seed test |
| 27 | 2 | The **unfiltered** listing path unchanged (order, paging, total, `busqueda`) | let a filter default to a value | the byte-identical regression test |
| 28 | 2 | `.RequireAuthorization(Politicas.OperacionDePos)` on the etiquetas group, nothing stacked | stack `GestionDeCatalogo` / drop it | the Vendedor 200 / Root 403 matrix |
| 29 **S** | 2 | The `("POST", "/api/etiquetas/datos")` allowlist entry | delete it | `SuperficieDeAutorizacionTests` (a read-only POST under a relaxed group) |
| 30 | 2 | The ≤ 11 command budget (no per-row query) | add a per-row `codigos_barra` lookup | the interceptor test: 1-artículo and 200-artículo counts must be equal |
| 31 | 3 | The multi-select reducer + *"cambio de filtro limpia la selección"* | drop the clear | the phantom-id test (a deselected-by-filter row must not be posted) |
| 32 | 3 | `expandirCeldas` copies multiplier and its `1..99` clamp | ignore copies / widen the clamp | the copies test (1/3/99) and the 0/100 refusal |
| 33 | 3 | The excluded-count notice and the *"N etiquetas = M hojas"* preview | drop either | their descriptor tests |
| 34 | 3 | The single-`window.print()` re-entrancy guard | remove it | the double-click test |
| 35 | 4 | The idle reset's `clearTimeout` in the effect cleanup | drop the cleanup | the two-consecutive-scans fake-timer test: exactly **one** reset must fire |
| 36 | 4 | `MS_DE_RESET` consumed from the exported constant | hardcode a divergent value | the 19.9 s / 20.0 s boundary pair |
| 37 | 4 | The generation bump on reset + the *"never `$0`"* and *"no encontrado"* branches + the exact two-call log | drop one at a time | the post-reset stale-resolution test (resolved inside `act`), the null-price test, the 404 test, the call-log test |
| — | 1-4 | **Non-regression**: `CajaZ.test.tsx` / `CuentaCorriente.test.tsx` | — | verify criterion: green **and unedited** |

## Slicing (4 PRs, stacked-to-main — the proposal's plan, ratified)

| # | Branch | Content | ~Lines | Depends on | Rollback |
|---|---|---|---|---|---|
| 1 | `feat/stage18-slice1-spike-y-formatos` | **Task 1 = the spike** (calibration mode + physical run + `spike-alineacion.md` verdict + the page-box mechanism of decision 1); then `formatos.ts` + the four tuples + the derived helpers, `HojaDeEtiquetas.tsx`, `etiquetas.css`, the print-settings block, geometry/strike tests. Targets 1-8 | ~420 | — | `git revert`: no consumer exists yet; `impresion.css` is not in the diff, so the shared surface is provably untouched |
| 2 | `feat/stage18-slice2-datos-de-etiqueta` | `ConstruirDescendientes` + tests; the three filters + `TamanioMaximoDePagina` on `ServicioDeArticulos`/`ArticulosEndpoints`; `ServicioDeEtiquetas` + contracts + `EtiquetasEndpoints` + DI + the allowlist entry; cap/`truncado`/exclusion; the no-cost, divergence, budget and unchanged-listing tests. Targets 9-30 | ~440 | — | `git revert`: the three params are optional and unread by any existing caller; the endpoint has no consumer until slice 3 |
| 3 | `feat/stage18-slice3-web-etiquetas` | `Etiquetas.tsx`, `api/etiquetas.ts`, route + menu, multi-select, copies, selectors, preview, excluded notice, print. Targets 31-34 | ~430 | **1 and 2** | `git revert` removes one route and one menu entry; nothing imports it |
| 4 | `feat/stage18-slice4-consulta-precios` | `ConsultaPrecios.tsx`, route + menu, two-call resolution, idle reset, unknown/no-price paths, descriptor tests + the doc-11 status block. Targets 35-37 | ~330 | **nothing** | Same — one route, one menu entry |

Merge order `1 → 2 → 3 → 4`. **Slice 4 depends on nothing** and may be built first or in parallel —
deliberately, so a failed spike does not strand the stage (proposal, ratified). Slices 1 and 2 are
also mutually independent and may interleave.

**Decision needed before apply: No** · **Chained PRs recommended: Yes** (`chain_strategy:
stacked-to-main`, one `judgment-day` round per slice) · **400-line budget risk: Medium** — three of
four slices sit near the cap on the estimate alone. **Pre-approved degradation**, in priority order
(the proposal's, ratified): (1) slice 1 splits into `1a` (spike + page box + `A4-3x8` + renderer) and
`1b` (the other three descriptors + poster) — **the spike never leaves `1a`**; (2) slice 3 splits into
`3a` (selection + label sheet) and `3b` (poster + copies helper); (3) slice 2 splits into `2a` (the
three filters) and `2b` (the composed endpoint). **Never degraded**: the spike's numeric criterion,
the no-cost DTO assertion, the no-price exclusion, `cantidad = 1`, and the resolver-as-sole-authority
divergence test. A wrong price on a shelf or a cost on paper is worse than no stage at all.

## Binding verify criteria

1. **Zero migrations**: no new file under `src/Ways.Infrastructure/Persistencia/Migraciones/`, and
   `dotnet ef migrations has-pending-model-changes` **clean**. Any DDL reopens the gate.
2. **Zero index changes**: `pg_indexes` shows the same set as `main`; the three filters are served by
   the existing `ix_articulos_area` / `ix_articulos_categoria` / `ix_articulos_marca`
   (`ArticuloConfiguration.cs:129-131`), asserted by definition.
3. **`src/Ways.Api/Seguridad/Politicas.cs` and `src/Ways.Web/src/estilos/impresion.css` do not appear
   in the stage's diff** (`git diff --exit-code` on both against `main`). No file under
   `src/Ways.Infrastructure/` appears either.
4. The spike's verdict is **recorded with numbers** in `spike-alineacion.md` (browser, printer, sheet
   reference, print settings, scale-square measurement, per-cell deviation, last-row drift) and is
   **PASS on both E1 and E2 before slice 3 is opened**. A FAIL stops the stage and escalates the
   QuestPDF licence decision to the owner; it is never resolved inside a phase.
5. `CajaZ` and `CuentaCorriente`: existing tests green **and unedited**, plus the recorded manual
   PDF comparison of E2.
6. `GET /api/articulos` **without** the new filters returns identical items, ordering, total and
   paging to `main` — asserted, not assumed; `idCategoria` on a parent returns descendants, proven
   with a three-level fixture.
7. `soloConOfertaVigente = true` equals `Aplicadas.Count > 0` at `cantidad = 1` for the same lista and
   momento — a divergence test against the live `/api/ofertas/resolver`, never a re-implementation.
8. The serialized `POST /api/etiquetas/datos` response carries **no** property named `costo`,
   `costoLista`, `costoNominal`, `descuentoProveedor`, `idProveedorHabitual`, `proveedor` or `margen`.
9. `POST /api/etiquetas/datos` returns 200 for Vendedor, Supervisor and Admin, 403 for Root;
   `SuperficieDeAutorizacionTests` green with exactly **one** new allowlist entry.
10. The command budget is asserted: the 1-artículo and 200-artículo requests issue the **same**
    number of EF commands, ≤ 11.
11. Mutation evidence recorded in the PR body for **every** row of the table above belonging to that
    slice; structural rows (**S**) record the file/state assertion instead of a runtime failure, and
    say so.
12. Domain / Application / Integration / vitest suites green; colocated tests for every new pure web
    helper and both new screen descriptors (`web-descriptor-tests`).

## Threat Matrix

N/A — this stage touches no routing beyond two additive authenticated routes under an existing
policy, no shell command, no subprocess, no VCS/PR automation, no executable-file classification and
no process integration. Its real risk surfaces (a wrong price on paper, cost leaving the building, a
regressed print box, a re-implemented offer matcher, tenant/empresa scoping) are covered by the
mutation-target table, which **is** binding.

## Migration / Rollout

**No migration required.** Nothing is written to the database by either feature. Rollout is the four
merges; `git revert` of them leaves `main` behaviourally byte-identical.

## Open Questions / tensions with the proposal

- [ ] **T1 — `copias` is NOT in the request DTO.** The proposal's decision 12 enumerates the endpoint
      input and copies are absent from it; decision 7 places copies on the screen. This design makes
      that explicit (decision 4). If `sdd-spec` states a server-side `copias`, the two disagree and
      the proposal's letter governs; `sdd-tasks` reconciles.
- [ ] **T2 — the four tuples are the proposal's, not the prompt's.** `A4-3x8` (70×37, 24/hoja) and
      `A4-2x7` (99×38, 14/hoja) are binding text; the equally common Argentine **105×74 (8/hoja)** and
      **105×37 (16/hoja)** family is **not** shipped. OD1's pending owner decision is the channel: if
      the shop owns one of those sheets, its tuple **replaces** one of the two **before** the spike
      runs. Two of the four geometries are sharpened here to their real published values (`A4-2x7` →
      99.1×38.1 with 15.15/4.65 margins and a 2.5 gutter, which closes 210 and 297 exactly).
- [ ] **T3 — `Excluidos` is a list, not a count.** The proposal's output enumeration does not name it;
      decision 6 and the success criteria require the operator be told *how many* were dropped, and
      the selection list to mark them. A count cannot mark a row, so the DTO carries identity.
- [ ] **T4 — pre-existing cost exposure on the selection screen, deliberately not widened.**
      `ArticuloListado` already carries `CostoLista`, `DescuentoProveedor` and `CostoNominal`
      (`ServicioDeArticulos.cs:88`) and `GET /api/articulos` is under `OperacionDePos` — so a Vendedor's
      browser already receives cost today. This stage **creates** none of that, **renders** none of it,
      and **must not** extend it: the print DTO is clean (decision 10). Registered as an owner
      carryover, not fixed here — tightening `ArticuloListado` is its own change with its own consumers
      to audit (`Articulos.tsx`, `CompraEditor.tsx`, `Existencias.tsx`, `ConteoDeInventario.tsx`).
- [ ] **T5 — named-page browser support is an assumption, not a verified fact.** Decision 1's primary
      mechanism is spike exit criterion E2 for exactly this reason; the isolated-print-document
      fallback is designed, not built. If E2 fails on named pages but the fallback passes, slice 1
      ships the fallback and the design decision is amended in the PR body — that is not a stage stop.
- [ ] **Deferred, unchanged**: the login-less device surface (OD2 / B2), per-empresa configurable
      formats (OD3 / C1), QuestPDF or any PDF library, barcode symbology, label-printer hardware,
      print history, price editing from the label screen, and stock on a label — all refused in
      writing by the proposal with their reopen conditions.
