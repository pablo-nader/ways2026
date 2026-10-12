# combos

## Objective

A combo is a set of two or more units (each component is a specific article or an article
family, with a quantity) sold at one fixed price. Example: 1 pancho ($3.000) + 1 coca ($2.500)
= $5.000. Selling it decrements the stock of the real articles.

## Problem

Ofertas discount one line at a time; nothing can express "N different articles at a fixed
price". The legacy combo feature was dead and `docs/11-programa-post-paridad.md:47-50` left
combos out of parity as "una etapa nueva con su propio diseño". Requested by the owner on
2026-10-11.

## Owner decisions (2026-10-11)

- A combo never becomes a sale line. The sale always holds the real article lines; the combo is
  a discount spread over them. Stock, IVA, annulment and reports keep working per article.
- Two entry paths, one engine: (1) the cashier loads the articles separately and the combo is
  detected; (2) the cashier picks a combo and the POS adds the same article lines.
- POS: a "Buscar combo" button next to the article search, and typing `C` + digits in the code
  box loads the combo with that number.
- A component may be an article or a family; 2 or more components, any mix.

## Design decisions taken by the agent (owner may overrule; each is cheap to change before T3)

- D1. Combo units are priced at list price minus the combo share; ofertas do not stack on the
  units a combo consumes. A combo applies only when it is cheaper than what ofertas give those
  same units. Units of the same line outside the combo keep their oferta.
- D2. The combo applies as many times as the document holds complete sets (2 panchos + 2 cocas
  = 2 combos; 2 panchos + 1 coca = 1 combo + 1 pancho at its normal price).
- D3. The combo saving is split across the consumed lines in proportion to their list amount,
  in cents, with the rounding remainder on the last line, so the set totals exactly the combo
  price. It is a LINE amount (the existing `descuento` column is already a line amount); a unit
  discount cannot represent it exactly.
- D4. The combo price does not depend on the price list.
- D5. Components are `UnidadVenta.Unidad` only (integer quantities). Weighed articles cannot be
  components in v1.
- D6. Several combos competing for the same units: greatest real gain first (what those
  units cost with their ofertas minus the combo price; the winner is applied as many times as
  its current line assignment allows, then everything is re-evaluated), ties by lowest
  `id_combo`. A unit is consumed by one combo only. Within a combo, article components are
  satisfied before family components.
- D7. `C` + digits is tried as a combo first; if no active combo has that number the text falls
  back to the article lookup (`codigo_interno` is free `citext`, an article may be called C12).
- D8. Out of scope for v1: presupuestos and remitos (stay per line), detection on credit notes
  (NCX returns lines at list price minus recorded discount as today), validity windows.

## Model (doc 10 category: catálogo, like `ofertas`)

- `combos`: `id_combo`, `id_tenant`, `id_empresa NULL`, `codigo integer NOT NULL` (the number
  typed after `C`; unique per tenant among live rows), `nombre`, `precio numeric(14,2) > 0`,
  `activo`, `created_at/updated_at/deleted_at`. RLS via `HabilitarRlsDeTenant`.
- `combos_componentes`: `id_combo_componente`, `id_tenant`, `id_combo`, `id_articulo NULL`,
  `id_familia NULL`, `cantidad numeric(12,3) > 0`;
  `CHECK num_nonnulls(id_articulo, id_familia) = 1`; composite FKs with `id_tenant`, Restrict
  to articulos/familias, Cascade from combos; an article or family appears once per combo.
- `items_comprobante_venta.id_combo integer NULL` (traceability; added in T3) with FK Restrict.
- Domain invariant: total component quantity ≥ 2.

## Scope

Backend (model, pure engine, ABM API, sale integration, price preview), back-office screen,
POS online and offline (snapshot, local engine, combo search, `C` code), POS release.

## Configuration

- TDD: test-first where a runnable deterministic test exists (the pure engine above all).
- Review: PR gate is judgment-day until a clean round (`CLAUDE.md`).
- Delivery: forecast well above 400 authored lines → chained PRs, `stacked-to-main` (cached
  owner preference); each slice ships PR + auto-merge + Auto-fix without asking.

## Tasks

- [x] T1 — Model and engine. Migration + entities/config for `combos` and
  `combos_componentes`, RLS, doc 10; pure `ResolvedorDeCombos` in `Ways.Domain` (detection per
  D2/D5/D6, allocation per D3, comparison input for D1) with unit tests; constraint backstops
  with SQLSTATE-asserted tests. Route: delegated (writer; multiple non-trivial files).
- [x] T2 — ABM API. `GET/POST/PUT/DELETE /api/combos`, validation (≥ 2 units, Unidad-only,
  live references, unique code with 23505 backstop, 404-first), dissolve/baja guards for a
  family or article used by a combo, integration tests. Route: delegated (writer).
- [x] T3 — Online sale. `id_combo` on the item; combos applied in the decide phase of
  `ServicioDeVentas.EmitirAsync` after ofertas (D1); line-amount discount through
  `CalculadorDeTotales`; query-budget guard updated on purpose. Route: delegated (writer).
- [x] T3b — Preview and offline (split out of T3, 2026-10-11). `POST /api/ofertas/resolver`
  (document aggregation) returns the combo share so the POS cart shows the same total the
  server will charge; offline request line carries the combo fields, and the discrepancy audit
  and `ExigirMismoContenido` understand them. Route: delegated (writer).
- [ ] T5 — POS. Snapshot carries combos; TypeScript port of the engine for offline with parity
  tests against the C# cases; "Buscar combo" button; `C` + digits; member picker when a
  component is a family; ticket shows the combo name (the sale response carries only
  `idCombo`); Desktop bump + `pos-v*` tag. Route: delegated (writer).
- [ ] T4 — Back office, LAST on purpose. "Combos" screen (list, create, edit, baja) with
  component editor (article or family + quantity), menu entry, Vitest tests. Until it ships no
  combo can exist without calling the API by hand, which keeps the server-side detection
  dormant while the POS cannot yet preview it (an online client unaware of combos would show
  the list total and the server would charge the combo total). Route: delegated (writer).

## Progress

Created 2026-10-11. Branch `pnader/combos-stock-discount-2180c2`, fast-forwarded to
`4728dcb6` (main with #373).

- T1 implemented — commits `05476e89` (model + engine) and `4011305d` (judgment fixes).
  Route: delegated writer (multiple non-trivial files); golden
  `inventario-de-dependientes.txt` updated inline by the parent. Migration
  `20261011195148_Combos`. Engine API: `ResolvedorDeCombos.Resolver(lineas, combos)` →
  per line `UnidadesConsumidas`, `AhorroDeCombo` (line amount), `IdsDeCombo`; caller formula:
  line discount = oferta unit discount × unconsumed units + combo share. Validator
  `ReglaDeCombos.Validar` with codes `combo_precio_invalido`, `combo_componente_invalido`,
  `combo_cantidad_invalida`, `combo_componente_duplicado`, `combo_unidades_insuficientes`.
  Observed (fix actor): build 0 errors; Domain 1183/1183; integration `~Combos` 29/29;
  Application 795/795; `has-pending-model-changes` clean. Parent spot checks: golden inventory
  43/43, Domain `~Combos` 48/48. RED 42 failing → GREEN; mutation evidence: 7 engine + 9
  migration mutants killed, plus K1, K3, K4 and the EF-filter test. Full integration suite not
  run locally (CI runs it).
- Judgment-day, T1: round 1 — 0 CRITICAL from either judge; confirmed by both and fixed in
  `4011305d`: K1 ranking by real gain, K2 batch application, K3 bounded remainder, K4
  `codigo > 0`, K5 caps, K6 doc claims, K7 test claims. Scoped re-judgment: both judges, all
  resolved, 0 CRITICAL → APPROVED. Left as info: the engine assumes non-negative oferta
  discounts (callers already guarantee it; with a negative one a set may not total the combo
  price) — T3 must keep that precondition.
- Carried to T2: `Combos`/`CombosComponentes` on `IWaysDbContext`; 23505 → 409 mappings for
  `ux_combos_codigo`, `ux_combos_componentes_articulo|familia`; Unidad-only check in the
  service; baja guards must ignore components of soft-deleted combos.
- Review tier: RDD off (clone-local) → no native review; PR gate is judgment-day.

- T1 delivered: PR pablo-nader/ways2026#379, merged 2026-10-11.
- T2 implemented on `pnader/combos-abm` — commits `e5c978a8` (ABM + guards) and `31a09e0e`
  (judgment fixes). Route: delegated writer. Contract: `GET /api/combos`, `GET /{id}` (POS
  roles, like ofertas); `POST`, `PUT`, `DELETE` (Admin). Body `{ codigo?, nombre, precio,
  idEmpresa?, activo, componentes[{ idArticulo?, idFamilia?, cantidad }] }`; `activo` required
  on PUT. Codes 1..999999, auto-assigned as max live + 1 under a per-tenant advisory lock
  (`"COMB"` bigint key, first statement of every combo write). Errors: 400
  `combo_precio_invalido` (incl. scale > 2), `combo_componente_invalido`,
  `combo_cantidad_invalida`, `combo_componente_duplicado`, `combo_unidades_insuficientes`,
  `combo_componentes_excedidos` (> 50), `combo_codigo_invalido`, `combo_componente_no_unitario`,
  `referencia_invalida`, `nombre_requerido`, `nombre_muy_largo`, `activo_requerido`; 404
  `no_encontrado` (404-first); 409 `combo_codigo_duplicado`, `combo_codigo_agotado`,
  `combo_componente_duplicado` (23505 backstop). Read model: a component whose article or
  family is soft-deleted stays with `nombre = null`, `disponible = false`. DELETE soft-deletes
  the combo and physically removes its components, so bajas stop being blocked. Guards: a
  family or article used by a live combo cannot be given a baja (409 `familia_en_uso` /
  `articulo_en_uso`); the writer takes `FOR KEY SHARE` on the rows it references.
  Observed (fix actor): build 0 errors; Domain 1198/1198; Application 795/795; integration
  `~Combos` 109/109; earlier writer run `Combos|Familias|ManejadorDeErrores` 439/439 and
  `Combos|Articulos|Familias` 664/664. Parent spot checks: `BajasEstructurales` 13/13, Domain
  `~Combos` 63/63. Integration tests were written after the service (no RED observed);
  mutation evidence stands in: 16 mutants killed, one surviving redundant clause removed.
- Judgment-day, T2: round 1 — 0 CRITICAL. Confirmed by both and fixed: C1 PUT without
  `activo` reactivated the combo; C2 doc claimed weighed components are ignored by the engine
  (false); C3 no cap on components. Also fixed (one judge, verified): F1 price scale, W3 three
  missing tests, S2 stale comment. Scoped re-judgment: both judges, all resolved, 0 CRITICAL →
  APPROVED. Info/suspect, not fixed: empresa existence check outside the lock (same residual
  as ofertas); a lock-order cycle (combo write × family dissolve × article baja) claimed by
  one judge and refuted by the other.
- Carried to T3: exclude weighed articles from detection by unit of sale, never by quantity
  (the ABM check is point-in-time); keep oferta discounts non-negative into the engine; the
  `C` + digits parser accepts 1..999999.

- T2 delivered: PR pablo-nader/ways2026#381 (auto-merge + Auto-fix).
- T3 implemented on `pnader/combos-venta` — commits `23853064` and `4f8ff1d8` (judgment
  fixes). Route: delegated writer. Migration `20261011211019_ItemsDeVentaConCombo`
  (`items_comprobante_venta.id_combo`, composite FK Restrict, index). Gate:
  `presupuestoOrigen is null && NumeroPreasignado is null && tipo.Signo > 0`. The plan carries
  discount, `IdCombo` (lowest id among the combos consuming the line) and `IdOferta` (nulled
  only when every unit is consumed); the retried lambda never queries combos. One extra
  constant query (candidates pre-filtered by the cart's articles and families), skipped under
  2 units: checkout budget 16 → 17, one-unit sale pinned at 16. Excluded from detection:
  non-Unidad articles (by unit of sale), lines without list price, negative oferta discount.
  `ajuste_manual_porcentaje` applies over the net that already includes the combo share.
  Response: `ItemEmitido.IdCombo`. Observed (fix actor): build 0 errors; Domain 1203/1203;
  Application 805/805; integration `Combos|Ventas` 330/330; `has-pending-model-changes` clean
  (writer run). Parent spot check: `CalculadorDeTotales` 25/25. RED observed only for the
  calculator; the rest is backed by mutation evidence (9 clause mutants + P1, P3, P5).
- Judgment-day, T3: round 1 — 0 CRITICAL. Confirmed and fixed in `4f8ff1d8`: P1 candidates
  pre-filtered by cart, P2 single discount formula + literal test, P3 FK backstop tests, P4
  manual adjustment documented and tested, P5 lowest-id test, P6 lot and repeated-line tests.
  Scoped re-judgment: both judges, all resolved, 0 CRITICAL → APPROVED. Delivery-order finding
  (one judge) accepted: T4 moved last, T3b split out.
- Consumers of sale lines checked (writer): fiscal composition, libro IVA, profitability,
  article reports, cash summaries and annulment read persisted `total`/movements → safe.
- OPEN, owner decisions pending:
  1. Credit note of a combo sale credits list price (5500 against 5000 paid): NCX prices
     today's list, not the associated sale's lines.
  2. Account-consumption reliquidation reprices lines at current list and ignores discounts,
     so a combo sale on account gets its saving charged back (same as ofertas today).
  3. Quantity-tier and family-minimum ofertas are resolved over the full quantity before the
     combo removes units (consequence of D1).

- T3 delivered: PR pablo-nader/ways2026#382 (auto-merge + Auto-fix).
- CI incident 2026-10-11: `backend-integration` was cancelled at its 45-minute cap on #381,
  #382 and on main after #380; no test failed. The suite takes 24–28 or 41–43 minutes
  depending on the runner and the day's new tests pushed slow runs past the cap. Fixed in
  PR pablo-nader/ways2026#386 (cap 75); the same commit was merged into the combo branches
  to rerun their CI. Follow-up proposed to the owner: split the suite into two jobs.
- Owner decision 2026-10-11: "que devuelva lo que se pagó" — OPEN item 1 is resolved.
- TNC implemented on `pnader/combos-nota-credito` — commit `ebbfce42`. Route: delegated
  writer. Rule: an NCX with `IdComprobanteAsociado` (no `NumeroPreasignado`) whose associated
  sale has combo items of a returned article credits, per original item, what was paid, pro
  rata by cumulative units: credit of a draw = round(total × units drawn so far / quantity) −
  already credited (exact at exhaustion, no negative draw for total ≥ 0). Units come only from
  combo items in ascending `orden`; excess units are priced as today in a separate item; a
  request line crossing several original items yields one item per tranche. The item keeps the
  original unit price, IVA rate, `id_combo`, `id_oferta` and `id_lista_precio`. One extra
  query, only on associated NCX. NCX of sales without combos are unchanged. Pure rule in
  `Ways.Domain/Ventas/DevolucionDeCombos`. Observed (fix actor): build 0 errors; Domain
  1219/1219; Application 805/805; integration `Combos|Ventas|Ncx` 362/362. Parent spot check:
  `DevolucionDeCombos` 15/15. Implementation preceded the tests (no RED); mutation evidence:
  gate, pro-rata formula, rounding, ordering, original-IVA, per-draw rounding, `IdOferta`.
- Judgment-day, TNC: round 1 — 0 CRITICAL. Confirmed by both and fixed: N1 the exhausting
  draw could come out negative by a cent (now cumulative). Also fixed: N2 test gaps, N3 stale
  `IdCombo` comments, N4 doc (surcharge case, per-tranche adjustment). Scoped re-judgment:
  both judges recomputed every literal, all resolved, 0 CRITICAL → APPROVED.
- OPEN, found while doing TNC (existing behaviour, not changed):
  4. The system does not track units already returned: a second NCX of the same sale can
     credit the same item again.
  5. A credit note accepts fractional quantities of a per-unit article (the integer check runs
     only for positive-sign documents), so half a pancho can be returned.
  6. NCX of a sale WITHOUT combos still credits today's list and ofertas, not what was paid.

- Delivered and merged 2026-10-11: #381 (T2), #382 (T3), #386 (CI cap), #390 (TNC).
- T3b implemented on `pnader/combos-pos-servidor` — commits `6a185c47` and `dd16c95c`
  (judgment fixes). Route: delegated writer. Contract for the TypeScript port:
  - Preview: `POST /api/ofertas/resolver-con-combos` (sibling route; `/resolver` returns a bare
    array and is unchanged). Same body as `/resolver`. Response `{ lineas: [ …fields of
    /resolver…, combo?: { unidadesConsumidas:int, ahorro:decimal (LINE amount), idCombo:int } ],
    combos: [ { idCombo, codigo, nombre, veces, ahorro } ] }`; `combo` key absent on lines no
    combo consumed. Line net = round(cantidad × precioOriginal) − (round(descuentoUnitario ×
    (cantidad − unidadesConsumidas)) + ahorro). The client MUST send the point of sale's
    `idEmpresa` and the customer's `idListaPrecio` on every line; mixed values → 400
    `empresa_inconsistente` / `lista_inconsistente`. `agregacion: PorLinea` → `combos: []`.
  - Snapshot `GET /api/pos/instantanea?version=2`: optional top-level `combos` (key omitted
    when none): `{ idCombo, codigo, nombre, precio, componentes: [{ idArticulo? | idFamilia?,
    cantidad }] }`, combos by ascending id, article components before family ones. A combo is
    sent whole or not at all: dropped if a component cannot be sold offline.
  - Offline line: `unidadesConsumidasPorCombo?:int`, `ahorroDeCombo?:decimal`, `idCombo?:int`,
    all three or none, only with offline `precioUnitario`, never on credit notes (400
    `combo_offline_no_admitido|incompleto|invalido`). Stored literally. Unknown combo id →
    amounts kept, `id_combo` null, audited. The discrepancy audit compares against the
    server's own detection; `PrecioDiscrepante` is also true for combo-only differences. The
    three fields are not part of `ExigirMismoContenido`.
  Observed (fix actor): build 0 errors; Application 820/820; `SuperficieDeAutorizacion` 5/5;
  integration `Combos|VentaOffline|OfertasResolucion|Instantanea|VentasCheckout` 315/315;
  writer run before fixes `Combos|Ventas|Venta|Ofertas|Instantanea|Pos` 992/992. After merging
  main (parent): build 0 errors, Application 820/820, Domain 1219/1219. Query pins in
  `CombosConsultasTests`: offline sale 21 / 20 / 19, preview 6 / 5. No RED (implementation
  first); 39 mutants killed, two survivors fixed.
- Judgment-day, T3b: round 1 — 1 CRITICAL (one judge, deterministic, confirmed by running the
  test): the new POST route was missing from the allow-list of
  `SuperficieDeAutorizacionTests`, CI would have failed. Fixed with the allow-list entry and a
  Vendedor-role test. Also fixed: Q2 preview trusts the request's empresa (documented as a
  contract requirement, mixed lists rejected, divergence pinned by a test), Q3 query costs
  pinned, Q4 overflow order, Q5 `combo_esperado` null, Q6 docs, Q7 test code. Scoped
  re-judgment: both judges, all resolved, 0 CRITICAL → APPROVED.
- Skills loop: the same miss three times (golden inventory in T1, `RemoveRange` allow-list in
  T2, endpoint allow-list in T3b) → new project skill `structural-guard-tests`.
- Rollout note: a device that does not apply combos (older POS build or snapshot) selling a
  cart that matches an active combo is recorded at what it charged and audited as discrepant.
  Another reason for T4 (the screen that creates combos) to ship after the POS release.

- T3b delivered and merged: PR pablo-nader/ways2026#394.
- T5 split (2026-10-11): T5a cart pricing and display (this entry), T5b combo entry ("Buscar
  combo", `C` + digits, family member picker), T5c Desktop bump + `pos-v*` tag.
- T5a implemented on `pnader/combos-pos-carrito` — commits `4f1e9beb` and `c15a6ac3`
  (judgment fixes). Route: delegated writer, web only. `src/Ways.Web/src/pos/motorDeCombos.ts`
  is the TypeScript port of `ResolvedorDeCombos` in exact `bigint` arithmetic; every C# engine
  test case is mirrored with the same numbers. One shared rule, `resolverDocumentoOffline`
  (`pos/instantaneaOffline.ts`), feeds the offline preview, the queued payload and the
  synthetic ticket; the line formula is `calcularTotalesDeLineaConCombo` (`api/combos.ts`).
  Online, the cart preview calls `resolver-con-combos` (it already sent the point of sale's
  empresa and the customer's list) and falls back to `/ofertas/resolver` on 404/405. UI: a
  badge on each consumed line and one row per applied combo in "Datos de la venta"; no new
  buttons. Snapshot combos are sanitized (a malformed one is dropped, the snapshot stays
  valid). A combo block with more units than the line is ignored until the preview
  re-resolves. A share with more than 2 decimals drops combos for that offline document.
  The printed ticket shows the combo only inside the per-line "Descuento" (the template
  prints no oferta names either). Observed (fix actor, from `src/Ways.Web`): `tsc -b` clean;
  lint 0 errors; `vitest run src/pos src/api` 1646/1646; `Pos.test.tsx` 281/281; whole suite
  4847/4848 — the one failure, `NuevaFamilia.test.tsx` timing out at 5 s under load, passed
  alone (49/49) and again in the parent's run. Parent: `ventas.ts` aligned with
  `comboVigenteDeLinea` inline, `tsc -b` clean, `ventas.test.ts` + `NuevaFamilia.test.tsx`
  92/92. RED observed for the engine suite only; mutation evidence for the rest.
- Judgment-day, T5a: round 1 — 0 CRITICAL; both judges traced engine parity against the C#
  code line by line. Confirmed and fixed: R1 malformed combos invalidated the whole snapshot,
  R2 badge name with several combos, R3 no fallback against a server without the route; plus
  R4–R9 (stale combo on a reduced quantity, sub-cent shares, comments, dead guard, test gaps,
  `idOferta` on the synthetic item). Scoped re-judgment: both judges, all resolved, 0 CRITICAL
  → APPROVED.

## Next step

PR for T5a (slice 6); then T5b, T5c, and T4 last.
