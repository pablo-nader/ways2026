# Proposal: Stage 18 — Etiquetas, carteles y consulta de precios

## Intent

doc-11:347-363 asks for **shop-floor operation**: printable **gondola labels and price posters**
(selectable by artículo, categoría, marca or active offer) and a **price-lookup surface for the
salón**, meant for barcode scanning from a device in the store.

`explore.md` proved two things that shape this whole stage.

| Fact | Evidence | Consequence |
|---|---|---|
| **Zero legacy parity** | `docs/06-roadmap.md:158-159` lists both items under *"Después del cutover… ninguna de estas entra en la paridad funcional"*; `alsina/imprimirArticulos.php` is a **reposición list**, not a label | Greenfield, same profile as stages 16/17 — nothing to port, nothing to preserve |
| **The pricing half is already built** | `POST /api/ofertas/resolver` returns `PrecioOriginal` / `PrecioFinal` / `Aplicadas` per line (`Contratos.cs:109-115`) over a batch in ~7 queries (`ServicioDeOfertas.cs:328,436` → `ServicioDePrecios.PreciosVigentesEnLoteAsync`) | The *"precio tachado / precio final"* pair a poster needs **already exists**; this stage builds **presentation and selection**, not pricing |

Three costs the business pays today. **A price change never reaches the shelf**: the system knows the
current price and the active offer, and the only way to put them on a gondola is to write them by
hand. **An offer is invisible where it is decided**: `ofertas` resolves *"llevando 2, 20% off"*
server-side at the counter, after the customer already chose. And **the floor cannot answer
"¿cuánto sale esto?"** without walking to a POS terminal that is busy charging someone else.

This stage is **purely additive and read-only**: no table, no column, no migration, no new policy, no
write path. Every existing screen, endpoint and print view stays byte-identical.

## Scope

### In Scope

- **Label/poster print engine, CSS-only** (decision 1), extending the stage-11 print infrastructure:
  **four fixed format descriptors** — two A4 die-cut label grids (`A4-3x8`, 70×37 mm, 24 per sheet;
  `A4-2x7`, 99×38 mm, 14 per sheet) and two poster sizes (`CARTEL-A4` full sheet, `CARTEL-A5` half
  sheet, two per A4). The descriptor is an **internal type**, never mm hardcoded in a component
  (decision 3).
- **The alignment SPIKE as task 1 of slice 1, with a binary exit criterion** (decision 1). It is the
  first thing built and the first thing that can stop the stage.
- **A selection screen** (`/etiquetas`): filter by búsqueda / área / categoría (**including
  descendants**) / marca / **con oferta vigente**, multi-select with *"elegir todos"* (the
  `FacturarRemitos.tsx:134,142-144` reducer pattern), **copies per row**, format selector, lista
  selector, sheet-count preview, and one **Imprimir** button that calls `window.print()` — the
  `CajaZ.tsx:87` pattern verbatim.
- **One new read-only endpoint**, `POST /api/etiquetas/datos` (decision 12), plus **three additive
  optional filters** (`idArea`, `idCategoria`, `idMarca`) on the existing `GET /api/articulos`.
  **Nothing else.**
- **The salón price-lookup screen** (`/consulta-precios`, decision 2): responsive, `autoFocus`
  scan input, oversized typography, final price plus struck-through original when an offer applies,
  idle auto-reset. **Zero new endpoints** — it composes `GET /api/articulos/escaneo` and
  `POST /api/ofertas/resolver`, both already under `OperacionDePos`.
- **doc 11 gains the Etapa 18 status block** with its three open decisions answered (orchestrator,
  outside the phase).

### Out of Scope

- **The login-less device surface (B2/B3 of the explore).** Deferred **as a decision of the owner's
  risk appetite, not a technical one** (decision 2). No authentication surface is invented in
  autonomous mode. **Reopen condition**: the owner states that a salón device must serve traffic with
  nobody logged in; that reopens B2 (device token) as its own change with its own threat model, and
  decision 2 keeps the door open by assuming nothing about the session beyond the policy.
- **Per-empresa configurable formats (C1, `formatos_etiqueta` table).** doc-11:360 leaves it open;
  decision 3 answers *"fixed for now"*. **Reopen condition**: a second tenant asks for a die-cut sheet
  none of the four descriptors matches. The expansion is then a table + an ABM + a descriptor loader —
  and the descriptor type this stage ships is exactly its interface.
- **QuestPDF or any PDF library** (decision 1). Still rejected on the licence grounds stage 11 stated.
  If the spike fails, the decision is **escalated to the owner**, never taken here.
- **Barcode symbology on the label** (decision 9). The label prints the code as **text**.
- **Cost, margin or supplier data on any printed sheet** (decision 10). The response DTO carries no
  cost field at all.
- **Bulk/queued printing, print jobs, printer drivers, label-printer hardware (Zebra/ZPL).** The
  browser print dialog is the entire delivery channel, exactly as in stage 11.
- **A history of what was printed.** No table, no audit row — printing is a read.
- **Price editing from the label screen.** It is a read surface; the artículo ABM stays the only
  place a price changes.
- **Stock, lot or availability data on a label.** A shelf label is a price, not an inventory report.
- **The owner's reserved carryovers** — the `importe` CHECK micro-gate, the `articulos_empresas`
  replace-set gap, `ways_owner`, `stage-13b` conteo por planilla. Untouched.

## Capabilities

### New Capabilities

- **`etiquetas-y-carteles`** — what a label and a poster are, the four format descriptors and the
  rule that a descriptor is data, the selection axes (including the descendant rule for categoría and
  the resolver-owned *"con oferta vigente"*), which price is printed and which lista, the
  no-vigent-price exclusion, copies and caps, what a printed sheet may never contain (cost), and the
  authorization gate.
- **`consulta-de-precios`** — the salón lookup: scan → identity → resolved price, what is displayed
  when an offer applies and when none does, the unknown-code and no-price paths, the idle reset, the
  zero-write / zero-persistence rule, and the authorization gate.

Two capabilities, following the stage-11/…/17 precedent: they share data infrastructure but nothing
else — one is a batch print composition, the other a single-item interactive lookup.

### Modified Capabilities

- **`articulos`** — **ADDED**: `GET /api/articulos` accepts optional `idArea`, `idCategoria` and
  `idMarca`; `idCategoria` matches the category **and all its descendants**; absent filters leave the
  existing listing behaviour, ordering, paging and clamp (`tamanio` ∈ [1,200],
  `ServicioDeArticulos.cs:50`) **byte-identical**. **UNCHANGED**: everything else.
- **`operacion-de-pos`** — **ADDED**: *"Etiquetas And Consulta De Precios Read Surfaces Live Under
  OperacionDePos"* — `POST /api/etiquetas/datos` groups under the existing policy with nothing
  stacked, and the two screens are `Vendedor + Supervisor + Admin`. The same shape as the existing
  *"Pago A Cuenta And Estado De Cuenta Reads Live Under OperacionDePos"* requirement
  (`operacion-de-pos/spec.md:161`). **`Politicas.cs` is not touched.**

**Not modified**: `precios`, `ofertas`, `resolucion-de-ofertas`, `codigos-barra`,
`listas-precio-minimal`, `exportacion-de-reportes` and every write-side capability — all consumed
unchanged.

## Approach

**Four fixed sheet descriptors, one new read endpoint, zero schema, zero new authorization — and a
spike that is allowed to stop the stage before anything else is built.**

1. **The pricing question is already answered; do not answer it twice.**
   `POST /api/ofertas/resolver` is the single authority on *"what does this cost right now, and which
   offer applies"* (`resolucion-de-ofertas` spec). Both features consume it: the label sheet in batch,
   the salón screen one line at a time. **No second matching implementation ships** — including for
   the *"con oferta vigente"* filter (decision 12).
2. **The spike is the stage's first gate, not its documentation.** Physical alignment against
   die-cut adhesive paper is the one thing CSS cannot promise from a screenshot, and the one thing
   stage 11 explicitly deferred here (`explore.md:49-60`). It runs first, with a numeric pass/fail.
3. **A format is data, not markup.** One `DescriptorDeFormato` type (page size, margins, columns,
   rows, cell pitch, gutters, and which fields the cell renders) feeds one generic sheet component.
   Adding a fifth die-cut size becomes a tuple; making them tenant-configurable later becomes a table
   that loads the same tuple.
4. **The print channel is the browser, as decided in stage 11.** `window.print()` on a component that
   is already on screen, `d-print-none` on the chrome, no dedicated route and no fetch
   (`impresion.css:1-6`, `CajaZ.tsx:87`). The one genuinely new problem is the **page box**
   (decision 8), and it is the spike's second exit criterion.
5. **The salón screen is the POS input pattern with the cart removed.** Same keyboard-wedge
   `autoFocus` + `Enter` input, same two calls, no cart, no writes, no session state.
6. **DB CHANGE GATE (CLAUDE.md): the model is that there is no model.** See the gate section below —
   **zero schema changes**, declared as a binding criterion, the stage-10/11/13 precedent.

## Orchestrator Decisions (binding)

The explore left three questions *"for the owner"*. Autonomous mode resolves each with the
**conservative and reversible** option and **registers the alternative as a pending owner decision**.
These three are binding on this proposal.

### OD1 — Printing is **A1 (pure print CSS)**, extending stage 11. The spike is task 1 of slice 1 with a binary exit criterion. QuestPDF stays rejected; if the spike fails, the licence decision is **escalated to the owner**.

**Rationale.** Zero new dependency, zero licence exposure, one print mechanism for the whole system,
and the legacy proved mm-exact CSS works for this hardware (`ticket.css`, 80 mm columns). What is
**not** proven is an N-per-sheet grid against pre-die-cut paper — so it is measured before it is
trusted, not after.

**Spike exit criterion (binary, recorded in tasks).** Print the calibration grid of one A4 descriptor
at 100% scale on the reference die-cut sheet, on **at least one target browser**, and measure the
physical result:

- every cell origin within **±1.0 mm** of its nominal die-cut position, **and**
- the last row's cumulative drift within **±1.5 mm** (no accumulating error down the page), **and**
- the existing report prints (`CajaZ`, `CuentaCorriente`) unchanged (decision 8).

**Fail path.** STOP. Document the measurement, do not silently switch libraries: **the QuestPDF
licence question goes to the owner as a blocking decision.** It is a commercial commitment, never a
technical footnote.

**Pending owner decision registered.** Does the shop already own a specific die-cut sheet (brand and
reference)? If it does, that sheet's geometry replaces one of the two proposed grids before the spike
runs — cheaper before than after.

### OD2 — The price lookup is a **responsive view of the system under the EXISTING auth**, for a dedicated Vendedor user of the store. The **login-less device surface (B2) is DEFERRED** as an owner risk-appetite decision.

**Rationale.** The explore verified there is **no precedent for anonymous or device access anywhere
in the system** — 4 fixed roles, 11 policies all `RequireAuthenticatedUser`, exactly one
`.AllowAnonymous()` route in the whole API (`explore.md:117-140`). Inventing an authentication
surface is the single highest-risk act available in this stage, and autonomous mode does not take it.
The conservative option ships the **same product value** (the floor can answer *"¿cuánto sale?"*) at
**zero new attack surface**.

**The door stays open.** The screen assumes nothing about the session beyond the policy: no role
claim is read, no user identity is displayed, no per-user state is stored. Adding a device-token
path later means adding a second way to reach the same two endpoints — additive, not a rewrite.

**Pending owner decision registered.** Is a shared salón login acceptable, or must the device serve
with nobody logged in? The second answer is a change of its own, with a threat model, token
issuance/rotation/revocation, and a narrow read-only claim set.

### OD3 — Formats are **C3 (fixed templates in code)**, with the descriptor as an **internal type** so the C1 expansion (a `formatos_etiqueta` table) is natural. **Consequence: ZERO schema changes for the whole stage.**

**Rationale.** doc-11:360 asks *"y si son configurables por empresa"* — for a single-shop tenant the
honest answer today is *"nobody has asked"*, and a configuration table with no second consumer is a
migration, an ABM, an RLS surface and a gate, all bought speculatively. Fixed descriptors deliver the
whole printed outcome; the type boundary is what makes the expansion cheap.

**Binding consequence.** **The DB gate for this stage is CERO CAMBIOS DE SCHEMA** (see gate section).
Any DDL proposed by a later phase is a scope violation that reopens the gate.

## Autonomous decisions

Under delegated technical authority, conservative and reversible bias. Each records context, options,
the decision, and **what it costs to reverse it**.

---

### 4 — **The lista de precios is an EXPLICIT, visible selection**, pre-set to the `EsDefault` lista, and the sheet **prints the lista's name**.

**Context.** doc-11:362 asks *"qué precio muestra cuando hay ofertas y listas diferenciadas"*.

**Verified.** `ListaPrecio.EsDefault` exists and is a **schema-level** invariant — exactly one row per
scope (`ux_listas_precio_default_compartido/empresa`, `ListaPrecio.cs:14-17`). `GET /api/listas-precio`
already returns `ListaPrecioAsignable(Id, Nombre, EsDefault)` **ordered default-first**, under
`OperacionDePos` (`ServicioDeClientes.cs:219-224`, `ClientesEndpoints.cs:56-59`).

| Option | Verdict |
|---|---|
| **Explicit selector, defaulted to `EsDefault`, printed on the sheet** | **Chosen.** Zero API change; the operator always knows which price left the printer |
| Server picks the empresa default silently | **Rejected.** `ListaPrecioAsignable` carries **no `IdEmpresa`** (verified) — with two empresas the DTO cannot distinguish the shared default from the empresa one, so *"silent"* would also be *"ambiguous"* |
| Print every lista on one label | **Rejected.** A shelf shows one price. Multi-list pricing is a B2B concept, not a gondola one |

**Decision.** One lista per print job, chosen on screen, defaulted to the first `EsDefault` row, and
its name printed in the sheet header (never on the label cell — the cell is the customer's).

**Cost of reversing.** Making it silent later is deleting a control. Making the empresa default
authoritative needs `IdEmpresa` on the DTO — one additive field.

---

### 5 — **The label shows the offer the resolver applies at `cantidad = 1`.** Offers with `cantidad_minima > 1` do **not** reach a shelf label.

**Context.** `LineaDeResolucion` carries a `Cantidad` (`Contratos.cs:89`) and the engine matches
`cantidad_minima` per line (`ServicioDeOfertas.cs:317-319`).

**Decision.** The label resolves at `cantidad = 1`. When `Aplicadas` is non-empty, the cell prints
`PrecioOriginal` struck through **and** `PrecioFinal` prominent; when it is empty, it prints one
price and no strike. A *"llevando 3"* offer therefore prints **no** discounted price.

**Why.** A label promising a price the customer cannot get by taking one unit is a false price at the
shelf — the most expensive kind of error in a store, and the one the counter will have to argue about.

**Cost of reversing.** A dedicated *"promo por cantidad"* poster format is additive: a fifth
descriptor plus the minimum quantity in the cell. Nothing built here blocks it.

---

### 6 — **An artículo with no vigent price never prints a label** — and the operator is told how many were dropped.

**Context.** `ResultadoDeResolucion.PrecioOriginal/PrecioFinal` are `null` when no price is vigent for
the (artículo, lista) pair — documented, verified (`Contratos.cs:104-115`).

**Decision.** Such rows appear in the selection list marked *"sin precio en esta lista"*, are
**excluded from the sheet**, and the screen shows the excluded count before printing.

**Why.** Printing `$0` puts a wrong price on a shelf; dropping silently makes a missing label look
like a printer jam. Neither is acceptable; the third option costs one counter.

**Cost of reversing.** Trivially: the exclusion is one filter in the print composition.

---

### 7 — **Copies are per row (1–99); the selection is capped at 200 artículos — the clamp that already exists.**

**Verified.** `ServicioDeArticulos.ListarAsync` clamps `tamanio` to `[1,200]`
(`ServicioDeArticulos.cs:50`). The label job reuses **that** number instead of inventing a constant.

**Decision.** Per-row copies `1..99` (default 1), plus a *"aplicar a todos"* helper; the selection
itself capped at **200 artículos**; the screen shows *"N etiquetas = M hojas"* before printing, and
the server response carries a `truncado` flag when the filter matched more than the cap.

**Why a cap at all.** The output is physical paper and the resolution is a batch query; an unbounded
*"print the whole catalogue"* is both a 4 000-row resolution and 170 sheets nobody wanted.

**Cost of reversing.** Raising the cap is one constant plus a performance measurement.

---

### 8 — **The label sheet owns its own `@page`, and proving the existing report prints did not regress is part of the spike.** (A conflict the explore did not find.)

**Verified, and it is a real conflict.** `impresion.css` is imported **globally**
(`src/Ways.Web/src/main.tsx:7`) and declares `@page { margin: 12mm }` (`impresion.css:25-27`). A
die-cut sheet needs its **own** page box (typically `margin: 0` plus the sheet's own top/left
offsets), and `@page` cannot be scoped with an ordinary selector.

**Decision.** The mechanism is a **spike output**, not a guess — candidates are CSS **named pages**
(`@page etiquetas` + the `page` property on the sheet container) and a single global page-box
declaration living in `impresion.css` itself. Whichever wins, the criterion is fixed: **the label
sheet prints on its own page box AND `CajaZ` / `CuentaCorriente` / the existing report views print
exactly as they do today.** Their existing `d-print-none` descriptor tests stay green and the manual
print check is part of the spike's evidence.

**Cost of reversing.** Contained to one stylesheet; no consumer outside the print path.

---

### 9 — **No barcode symbology.** The label prints the code as human-readable **text**.

**Decision.** `codigo_interno` (and the `codigo_barra` when the artículo has one) print as text.
No `JsBarcode`/`bwip-js`, no rendered EAN-13/Code128.

**Why.** A scannable symbol needs a new front-end dependency **and** an acceptance criterion this
repo cannot automate — a physical scanner reading physical ink at a physical size. Shipping an
unverified barcode is worse than shipping none: it looks correct and fails at the counter.

**Reopen condition.** The shop wants to **relabel** goods that arrive without a barcode. That is its
own slice, with a scanner read as its acceptance test.

**Cost of reversing.** Additive: one dependency, one cell field, one physical test.

---

### 10 — **Authorization: no new policy — and no printed sheet may ever carry cost.**

**Verified.** `OperacionDePos` already gates *"artículos, códigos de barra, clientes, listas de
precio, parámetros… resolución de ofertas"* (`Politicas.cs:30-38`), and both endpoints these features
consume are already under it (`OfertasEndpoints.cs:12`, `ArticulosEndpoints.cs:30`).

| Option | Verdict |
|---|---|
| **Reuse `OperacionDePos`, nothing stacked** | **Chosen.** A Vendedor who may see a price at the counter may print it on a shelf. Both surfaces are strictly read-only |
| Stack `GestionDeCatalogo` on the label screen | **Rejected.** Printing is a read; stage-17 decision 10 argued this exact direction — gating a read harder than the write it describes is an accident dressed as caution |
| A new `ImpresionDeEtiquetas` policy | **Rejected.** `Politicas.cs` gains a name only when a **new kind of risk** appears (the stage-15 criterion). This is the risk the POS already accepts |

**Data-exposure clause (`dto-contract-honesty`).** `POST /api/etiquetas/datos` returns **no**
`costo_lista`, `costo_nominal`, `descuento_proveedor` or proveedor field — not hidden in the UI,
**absent from the DTO**. Cost is admin-only by policy (`LecturaDeRentabilidad`), and a printed sheet
leaves the building.

**Cost of reversing.** Tightening later is one policy registration on one route.

---

### 11 — **The salón screen adds ZERO endpoints, holds ZERO state, and resets when idle.**

**Decision.** `/consulta-precios`, inside the existing `Layout` under
`RutaProtegida rolesPermitidos={[Vendedor, Supervisor, Admin]}` (the `/pos` shape, `App.tsx:81-88`):

- `GET /api/articulos/escaneo?entrada=…` → identity only, never a price by design
  (`ServicioDeEscaneo.cs:8-19`), then `POST /api/ofertas/resolver` for the price. Exactly the two-call
  path the scanning service's own doc-comment prescribes.
- Punto de venta and lista come from the same selectors the POS uses (`Pos.tsx:388,525`), remembered
  locally like the POS remembers its PV.
- **Nothing is written, nothing is persisted, no lookup history exists.**
- The screen **returns to its idle state after ~20 s**, so the next customer never reads the previous
  customer's answer, and the input regains focus for the next scan.
- Unknown code → *"no encontrado"*; artículo with no vigent price → *"consultá en caja"*, never `$0`
  (decision 6's rule, restated for the single-item path).

**Cost of reversing.** It is one route and one component; deleting it removes nothing else.

---

### 12 — **One new endpoint — `POST /api/etiquetas/datos` — plus three additive filters on `GET /api/articulos`. The resolver stays the single authority on "con oferta vigente".**

**Context.** `GET /api/articulos` filters by `busqueda` and `idEmpresa` only (`ArticulosEndpoints.cs:24`,
`ServicioDeArticulos.cs:41-47`) — doc-11:349 requires categoría and marca as selection axes.

**Decision.**

- **`GET /api/articulos`** gains optional `idArea`, `idCategoria`, `idMarca`. Absent ⇒ today's
  behaviour, ordering, paging and clamp **byte-identical** (asserted as a test). `idCategoria` matches
  the category **and its descendants** — `categorias` is hierarchical (`id_categoria_padre`) and the
  offers engine already materializes the tenant's full ancestor map, so this is the **same** semantics,
  not a new concept.
- **`POST /api/etiquetas/datos`** — read-only POST, the `/api/ofertas/resolver` precedent verbatim
  (*"POST, no muta nada"*, `OfertasEndpoints.cs:43-53`), because the body carries an id set or a
  filter, not a query string. Input: `idPuntoVenta`, `idListaPrecio`, and either `idsArticulo[]` or a
  filter (`busqueda`, `idArea`, `idCategoria`, `idMarca`, `soloConOfertaVigente`). Output, capped at
  200 rows plus a `truncado` flag: `idArticulo`, `codigoInterno`, `codigoBarra`, `nombre`,
  `unidadVenta`, `precioOriginal`, `precioFinal`, `ofertas[]`. Under `OperacionDePos`, nothing stacked.
- **`soloConOfertaVigente` is decided by `ServicioDeOfertas.ResolverAsync`** (`Aplicadas.Count > 0`),
  never by a second matching implementation. The coarse candidate query may narrow; **the resolver
  alone confirms**.

**Why one composed endpoint rather than three calls from the browser.** *"Todos los artículos con
oferta vigente"* is not answerable client-side over a paged list: the engine, not the page, knows
which artículos an offer reaches (its alcance may be artículo, grupo or categoría). Composing
server-side also keeps the query budget flat — `ResolverAsync` is already a batch of ~7 queries
regardless of N (`ServicioDeOfertas.cs:314-321`).

**Cost of reversing.** The endpoint has exactly one consumer; deleting it deletes the screen with it.
The three filters are additive and independently useful.

---

### 13 — **Size reassessed: `media` → `media-baja`.** The risk lives in the spike, not in the volume.

doc-11:358 marks the stage *"media"* assuming both open decisions could grow. With B2 deferred
(OD2) and C1 deferred (OD3), what remains is **zero schema, one endpoint, three optional filters and
two screens**. Four slices, all comfortably plannable. The only thing that can blow the estimate is
the spike failing — which is precisely why it runs first.

## Modelo de datos propuesto — **CERO CAMBIOS DE SCHEMA**

> **DB CHANGE GATE (CLAUDE.md) — this section is the contract.**

**Gate verdict proposed: ZERO migrations. Zero new tables, zero new columns, zero new enums, zero
`ALTER`, zero data statements, zero seed changes, zero index changes.** The stage reads
`articulos`, `codigos_barra`, `categorias`, `marcas`, `areas`, `listas_precio`, `precios` and
`ofertas` through **existing services only** and writes nothing anywhere.

Precedent: stages 10, 11 and 13 shipped whole stages with zero DDL. **Binding criterion for verify**:
`dotnet ef migrations has-pending-model-changes` is clean and `src/Ways.Infrastructure/Persistencia/Migraciones/`
has **no new file**. Any DDL from a later phase is a scope violation that **reopens the gate**.

The format descriptor is a **TypeScript type** (`DescriptorDeFormato`), not a row (OD3). Its C1
expansion — a `formatos_etiqueta` `[catálogo]` table (`id_tenant` + `id_empresa NULL`, doc-09:84) —
is registered as the reopen path and would come with its own gate.

## API surface

| Route | Method | Policy | Status |
|---|---|---|---|
| `/api/articulos` | GET | `OperacionDePos` | **Modified** — `+idArea`, `+idCategoria` (with descendants), `+idMarca`, all optional |
| `/api/etiquetas/datos` | POST (read-only) | `OperacionDePos` | **New** — the only new route of the stage |
| `/api/articulos/escaneo` | GET | `OperacionDePos` | **Unchanged**, consumed |
| `/api/ofertas/resolver` | POST | `OperacionDePos` | **Unchanged**, consumed |
| `/api/listas-precio` | GET | `OperacionDePos` | **Unchanged**, consumed |
| `/api/catalogos/{categorias,marcas,areas}` | GET | existing | **Unchanged**, consumed by the filters |

`Politicas.cs`: **not touched**.

## Affected Areas

| Area | Impact | Description |
|---|---|---|
| `src/Ways.Application/Etiquetas/ServicioDeEtiquetas.cs` | New | Composes the selection query with `ServicioDeOfertas.ResolverAsync`; the cap, the `truncado` flag, the no-price exclusion and the descendant expansion of `idCategoria` |
| `src/Ways.Application/Etiquetas/Contratos.cs` | New | `SolicitudDeEtiquetas` / `FilaDeEtiqueta` — **no cost field** (decision 10) |
| `src/Ways.Application/Articulos/ServicioDeArticulos.cs` | Modified | Three optional filters; the unfiltered path unchanged |
| `src/Ways.Api/Endpoints/EtiquetasEndpoints.cs` | New | One route, `OperacionDePos`, nothing stacked |
| `src/Ways.Api/Endpoints/ArticulosEndpoints.cs` | Modified | Three optional query params on the existing listing |
| `src/Ways.Web/src/etiquetas/formatos.ts` | New | The four `DescriptorDeFormato` values — the single source of truth for every mm |
| `src/Ways.Web/src/etiquetas/HojaDeEtiquetas.tsx` | New | The generic sheet renderer (grid + cell), driven by a descriptor |
| `src/Ways.Web/src/estilos/etiquetas.css` | New | The label page box and the grid rules (decision 8) |
| `src/Ways.Web/src/estilos/impresion.css` | Possibly modified | Only if the spike's page-box mechanism requires it; existing rules preserved |
| `src/Ways.Web/src/paginas/Etiquetas.tsx` | New | Filters, multi-select, copies, format/lista selectors, preview, print |
| `src/Ways.Web/src/paginas/ConsultaPrecios.tsx` | New | The salón screen (decision 11) |
| `src/Ways.Web/src/App.tsx` + `Layout.tsx` | Modified | Two routes with `rolesPermitidos`, two menu entries |
| `docs/11-programa-post-paridad.md` | Modified | Etapa 18 status block, its three open decisions answered (orchestrator, outside the phase) |
| `src/Ways.Infrastructure/**` | **Untouched** | No migration, no configuration, no seed |

## Risks

| Risk | Likelihood | Mitigation |
|---|---|---|
| **The die-cut grid does not align on real paper** — the risk stage 11 deferred to here | **High** | The spike is slice 1 task 1 with a numeric binary criterion; failure **stops the stage** and escalates the licence decision to the owner instead of quietly adding a dependency |
| **The global `@page { margin: 12mm }` regresses existing report prints** | Med-High | Decision 8 makes *"CajaZ and CuentaCorriente print exactly as today"* a spike exit criterion, with their existing descriptor tests plus a manual print check |
| **A wrong price reaches a shelf** (stale lista, `$0`, or an offer requiring 3 units) | **High if unmanaged** | Decisions 4/5/6: the lista is explicit and printed, `cantidad = 1` governs the offer, and no-price artículos are excluded with a visible count |
| **Cost leaking onto paper** | Med | Decision 10: absent from the DTO, not merely hidden — asserted as a contract test |
| **Building an authentication surface in autonomous mode** | **High if unmanaged** | OD2 defers B2 entirely; the stage adds **zero** authorization surface and the design must not assume one |
| **Re-implementing offer matching for the "con oferta vigente" filter** | Med | Decision 12: the resolver alone confirms; a divergence test asserts the filter's result equals `Aplicadas.Count > 0` |
| **Scope creep into configurable formats / label printers / barcodes** | Med | All three refused in writing with reopen conditions (OD3, decisions 9, Out of Scope) |
| **Browser print settings (scale ≠ 100%, "fit to page") silently break alignment** | Med | The sheet prints an on-screen instruction block (`d-print-none`) with the required print settings, and the spike records which settings were used |
| **A 200-artículo resolution is slow** | Low | `ResolverAsync` is a fixed ~7-query batch independent of N (verified); the cap is the existing clamp |

## Rollback Plan

**Fully reversible. There is nothing to undo in the database, because nothing is written there.**

- **Slice 1** (spike + formats + renderer): revert the commits. The files have no consumer yet; if
  `impresion.css` was touched, its diff is the only shared surface and it is one stylesheet.
- **Slice 2** (endpoint + filters): revert. The three query params are optional and unread by any
  existing caller; the endpoint has exactly one consumer, which does not exist until slice 3.
- **Slice 3** (label screen) and **Slice 4** (salón screen): revert removes a route and a menu entry.
  No other screen imports them.
- **The whole stage**: `git revert` of the four merges leaves `main` byte-identical in behaviour —
  no migration to roll back, no seed to restore, no data to repair, no policy to re-register.

**The one irreversible act is commercial, not technical**: if the spike fails and the owner licenses
QuestPDF, that decision belongs to the owner and is out of this stage's rollback surface.

## Dependencies

- **Etapa 11** (archived) — `impresion.css`, `@media print`, `d-print-none`, the
  `window.print()`-on-the-visible-component pattern, and the deferred QuestPDF decision this stage
  revisits.
- **Etapa 4/5** (archived) — `ServicioDeOfertas.ResolverAsync`, `ResolvedorDeOfertas`,
  `ServicioDePrecios.PreciosVigentesEnLoteAsync`, `ServicioDeEscaneo` + `ParserDeEscaneo`, and the POS
  keyboard-wedge input pattern. All consumed **unchanged**.
- **Etapa 3** (archived) — `listas_precio` with `EsDefault` and its per-scope unique index;
  `GET /api/listas-precio`.
- **Etapa 17** (archived) — the `FacturarRemitos.tsx` multi-select reducer pattern.
- Skills: `react-async-state` + `web-descriptor-tests` (every web slice), `dto-contract-honesty`,
  `mutation-proof-tests`, `work-unit-commits`, `judgment-day` before every PR.
- **No new NuGet package, no new web dependency, no migration, no scheduler, no queue.**
- **Physical dependency**: one ream of the reference die-cut A4 label sheet and a printer, in the
  owner's hands, for the spike.

## Success Criteria

- [ ] **Zero migrations**: no new file under `src/Ways.Infrastructure/Persistencia/Migraciones/` and
      `dotnet ef migrations has-pending-model-changes` clean.
- [ ] **`Politicas.cs` unchanged**; `POST /api/etiquetas/datos` returns 200 for Vendedor, Supervisor
      and Admin, and 403 for Root — the same matrix as `/api/ofertas/resolver`.
- [ ] The spike's measurement is **recorded with numbers** (browser, sheet reference, per-cell
      deviation, last-row drift) and its verdict is PASS before slice 3 begins.
- [ ] `CajaZ` and `CuentaCorriente` print views are unchanged after the page-box work — their existing
      `d-print-none` tests green, plus a recorded manual print check.
- [ ] `GET /api/articulos` **without** the new filters returns byte-identical results, ordering and
      paging to `main` — asserted, not assumed.
- [ ] `idCategoria` on a parent category returns artículos of its **descendants** too, proven with a
      three-level fixture.
- [ ] `soloConOfertaVigente=true` returns exactly the artículos for which
      `POST /api/ofertas/resolver` reports `Aplicadas.Count > 0` at `cantidad = 1` for the same lista
      and momento — a divergence test, not a re-implementation.
- [ ] An offer with `cantidad_minima = 3` produces **no** struck-through price on the label
      (decision 5), and an artículo with no vigent price is **excluded** with the count surfaced
      (decision 6).
- [ ] The response DTO contains **no** cost/proveedor field — asserted against the serialized JSON.
- [ ] The label response is capped at 200 rows and sets `truncado` when the filter matched more.
- [ ] Each of the four descriptors renders its exact declared geometry (columns × rows, pitch,
      margins) — asserted from the descriptor, so a wrong tuple fails the test rather than the paper.
- [ ] The salón screen: a scan resolves in **two** calls, shows the struck-through original only when
      an offer applies, shows *"no encontrado"* for an unknown code, never shows `$0`, resets to idle
      after the timeout, and **issues zero writes** (asserted by the absence of any non-GET/resolver
      call).
- [ ] Descriptor tests for both new screens (`web-descriptor-tests`); Domain / Application /
      Integration / vitest suites green.

## Plan de slices (tentative — `sdd-tasks` owns the final breakdown)

Stacked-to-main, one `judgment-day` round per slice (`protocolo-pr-solo-dev`).

| # | Branch | Content | ~lines |
|---|---|---|---|
| 1 | `feat/stage18-slice1-spike-y-formatos` | **Task 1 = the spike** (calibration grid + physical measurement + recorded verdict, decision 8's page box included); then `DescriptorDeFormato`, the four descriptors, `HojaDeEtiquetas.tsx` (grid + cell + poster), `etiquetas.css`, geometry tests from the descriptors, and the print-settings instruction block | ~420 |
| 2 | `feat/stage18-slice2-datos-de-etiqueta` | `ServicioDeEtiquetas` + contracts + `POST /api/etiquetas/datos`; the three optional filters with the descendant expansion; the resolver-owned `soloConOfertaVigente`; cap/`truncado`; the no-cost contract test; the unchanged-listing regression test | ~440 |
| 3 | `feat/stage18-slice3-web-etiquetas` | `Etiquetas.tsx`: filters, multi-select reducer, copies, format + lista selectors, sheet-count preview, excluded-count notice, print button, route + menu, descriptor tests | ~430 |
| 4 | `feat/stage18-slice4-consulta-precios` | `ConsultaPrecios.tsx`: scan input, two-call resolution, large-format display, offer strike-through, unknown/no-price paths, idle reset, route + menu, descriptor tests | ~330 |

Merge order `1 → 2 → 3 → 4`. Slice 3 depends on **both** 1 and 2; **slice 4 depends on nothing** and
may be built first or in parallel if the spike stalls — deliberately, so a failed spike does not
strand the whole stage.

**Pre-approved degradation**, in priority order:

1. **If slice 1 overflows** — split `1a` (spike + page box + one label descriptor + renderer) and
   `1b` (the remaining three descriptors + poster). The spike never moves out of `1a`.
2. **If slice 3 overflows** — split `3a` (selection + label sheet) and `3b` (poster + copies helper).
3. **If slice 2 overflows** — split `2a` (the three filters on `GET /api/articulos`) and `2b` (the
   composed endpoint).
4. **Never degraded**: the spike's numeric criterion, the *"no cost in the DTO"* assertion, the
   no-price exclusion, and the resolver-as-single-authority test. A wrong price on a shelf or a cost
   on paper is worse than no stage at all.

**Review Workload Forecast (preliminary — `sdd-tasks` produces the binding one)**

- Estimated total: **~1 620 lines** across 4 slices. Calibrated against the programme's record
  (stages 13-17 came in 1.5-3× the naive estimate because test depth inflates a slice) — but this
  stage has **no schema, no concurrency, no write path**, so the usual inflators (RLS/SQLSTATE tests,
  rendezvous tests, ledger consistency) are **absent**. The realistic outturn is 4–6 PRs.
- `Decision needed before apply: No`
- `Chained PRs recommended: Yes` — `chain_strategy: stacked-to-main`
- `400-line budget risk: Medium` — three of four slices sit near the cap on the estimate alone; three
  split points are pre-authorized.
- `size:exception` anticipated: **No** — the splits absorb it.

## Tensiones con el explore

| # | Explore position | Verdict |
|---|---|---|
| 1 | *"Recomendación: B2 (device token), con alcance angosto"* (`explore.md:177-182`) | **Overruled by OD2, and the explore itself supplied the argument**: it verified there is no precedent for anonymous or device access anywhere (`:117-140`) and named this *"el riesgo más alto de la etapa"* (`:204-208`). Autonomous mode does not invent an authentication surface; B1-style shared login under the existing auth ships the same product value at zero new attack surface, and B2 stays reopenable |
| 2 | *"Recomendación: C1 si el proposal confirma que 'configurable por empresa' es un requisito real"* (`:192-197`) | **Answered as C3 by OD3.** The explore made the confirmation conditional and nobody confirmed it; a configuration table with no second consumer is speculative schema. The descriptor type is the interface C1 would load |
| 3 | *"El tamaño real puede acercarse a grande"* (`:214-217`) | **Refuted** (decision 13): that estimate assumed B2 **and** C1. With both deferred the stage is media-baja; the schedule risk is the spike, not the volume |
| 4 | *"Sin precedente vivo de grilla N-por-hoja"* (`:62-72`) | **Ratified and sharpened**: the explore did not find the concrete blocker. `impresion.css` is imported globally (`main.tsx:7`) and its `@page { margin: 12mm }` applies to **every** print, so the label sheet needs its own page box **without** regressing the existing report prints — decision 8, now a spike exit criterion |
| 5 | *"Los primeros tres son columnas directas de `articulos`/joins"* (`:107-115`) | **Ratified, with a gap the explore did not check**: `GET /api/articulos` filters by `busqueda` and `idEmpresa` **only** (`ArticulosEndpoints.cs:24`), so categoría/marca/área are three additive params, and `idCategoria` must expand **descendants** because `categorias` is hierarchical (decision 12) |
| 6 | *"El cuarto [eje] es una resolución de ofertas"* (`:113-115`) | **Ratified, and made concrete**: the resolver alone decides, at `cantidad = 1` (decisions 5 and 12) — a `cantidad_minima > 1` offer is deliberately invisible on a shelf label, which the explore never considered |
| 7 | Open question 1 — *"¿el negocio ya tiene un tamaño de etiqueta físico real?"* (`:223-224`) | **Cannot be resolved autonomously**: two market-standard A4 die-cut geometries are proposed, and the question is registered as a pending owner decision inside OD1. If the shop owns a specific sheet, its geometry replaces one of the two **before** the spike runs |

**New material the explore did not raise at all**: the `@page` conflict (decision 8), which lista a
label prints and the `ListaPrecioAsignable` ambiguity behind it (decision 4), the no-vigent-price
exclusion (decision 6), copies and the cap reused from the existing clamp (decision 7), the barcode
refusal (decision 9), the no-cost-on-paper clause (decision 10), the idle reset of the salón screen
(decision 11), and the composed endpoint with the resolver as sole authority (decision 12).

## Proposal question round

Execution mode is `automatic-autonomous`, so these were resolved rather than asked. Each records the
assumption so a correction is cheap. **None blocks spec/design.** The first two are the ones the
orchestrator explicitly registered as **pending owner decisions**.

1. **Does the shop own a specific die-cut label sheet?** Assumed **no** (OD1) — two market-standard
   A4 geometries ship. If a real sheet exists, replacing a descriptor before the spike costs one tuple;
   discovering it after costs the spike.
2. **Must the salón device serve with nobody logged in?** Assumed **no** (OD2). If yes, that is a
   change of its own: a threat model, token issuance/rotation/revocation, and a read-only claim set —
   the single assumption that would most change this stage.
3. **Are label formats configurable per empresa?** Assumed **not yet** (OD3). The reopen path is a
   `formatos_etiqueta` table loading the same descriptor tuple.
4. **Which price does a label show when a customer's lista differs from the default?** Assumed
   **the lista the operator chose, printed on the sheet** (decision 4). A gondola shows one price; if
   the shop really needs per-lista shelf labels, it prints one job per lista — no code change.
5. **Should a "llevando 3, 20% off" offer appear on a shelf label?** Assumed **no** (decision 5). If
   the owner wants it, it is a fifth descriptor that also prints the minimum quantity.
6. **Should the label carry a scannable barcode?** Assumed **no** (decision 9). Reopening it needs a
   dependency and a physical scanner acceptance test.
7. **Who prints labels — the counter or the office?** Assumed **whoever may see a price**
   (decision 10). Tightening later is one policy on one route.
