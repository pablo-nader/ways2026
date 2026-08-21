# Design: Stage 17 — Presupuestos y remitos

## Technical Approach

**Two documents in their own tables, one untouched checkout, one new stock writer that says so out
loud, and one invariant that makes two traps unreachable: the goods leave exactly once.**

The proposal's `Modelo de datos propuesto` (§A-§K) is the ratified gate contract. This design **adds
no DDL** — not a column, not an index, not a constraint, not an enum value. Everything below is code
over that exact schema.

Six structural facts decide the shape.

1. **The pricing engine is the single price authority, and that is the trap.**
   `ServicioDeVentas.cs:86-91` re-resolves every price server-side, *"nunca lo que mostró el
   carrito"*. If `/para-venta` merely pre-filled the cart, the checkout would silently reprice and the
   stage's central promise would die by the very mechanism meant to deliver it. So the quote's own
   snapshot **replaces** `ServicioDeOfertas.ResolverAsync` for a conversion — the price still comes
   from the server, it just comes from an earlier server decision (decision 2).

2. **`MaterializarItems` cannot deliver a frozen snapshot, and widening it would be the wrong fix.**
   It reads `IdAlicuotaIva` from today's `articulos` (`:1049`, `:1059`) and `porcentaje_iva` from
   today's `alicuotas_iva` (`:103-105`) — both are part of the frozen promise (decision 4 of the
   proposal). This design ships a **second private materializer** and leaves `:1007-1065` literally
   untouched; both call `CalculadorDeTotales.Calcular`, which stays the single arithmetic authority
   (tension T2).

3. **The presupuesto row is the first contended lock of a conversion, and it is taken before
   anything is written.** The comprobante of `EjecutarTransaccionAsync` step 2 (`:781-801`) is a
   **new row**, so it waits on nobody; the first row another transaction can hold is
   `turnos_caja` (`:773`). The guarded `UPDATE presupuestos … RETURNING` therefore goes at
   **position 1.5**, between the turno guard and the comprobante INSERT — the stage-16 *"immediately
   after the header, never after"* criterion, applied to a transaction whose header is an insert. The
   race loser pays nothing: no comprobante, no items, no stock, no cuenta corriente.

4. **`AnularAsync` needs no presupuesto coupling at all, and that is a decision, not an omission.**
   Reverting `convertido → enviado` would resurrect a frozen price after its `vencimiento` may have
   passed — and, structurally, it would also require nulling `id_presupuesto_origen` on the annulled
   comprobante (otherwise `ux_comprobantes_venta_presupuesto_origen` refuses the second sale), which
   erases the record of what actually happened. The widened `RETURNING` of `MarcarAnuladoAsync`
   (`:741-758`) therefore carries the tipo's **`codigo`**, not `id_presupuesto_origen`, and its one
   guarded call is the **`TXR` un-link** (tension T1).

5. **`RC` proves that an itemless comprobante is a construction, not a flag.**
   `ServicioDeCuentaCorriente.cs:287-325` writes a comprobante with *"cero items por construcción"*
   and no stock. The consolidation copies that shape exactly, which makes the double decrement and
   the phantom restock of `AnularAsync`'s unconditional item loop **unreachable**, not avoided.

6. **The fourth stock write site is written independently, and the `id_punto_venta` component of the
   lock key degenerates — as it already does at write site 1.** `stock/spec.md:178-189` demands
   `ORDER BY id_articulo, id_punto_venta, id_lote NULLS FIRST`, *"implemented identically and
   independently … the duplication is not refactored away"*. A remito, like a sale, has ONE punto de
   venta, so the middle component is constant and the loop ships the exact shape of
   `ServicioDeVentas.cs:866-870` — not the three-component shape of `ServicioDeStock.cs:470-473`,
   which is a transfer between two.

`EjecutarTransaccionAsync`'s pinned order, its stock loop (`:866-885`) and its cuenta-corriente loop
(`:890-914`) are **byte-identical**. `AsignadorDeNumeroComprobante` is **read, not edited** — fourth,
fifth and sixth reuse (`'PRES'`, `'REM'`, `'TXR'`).

## Architecture Decisions

| # | Decision | Alternatives considered | Rationale |
|---|---|---|---|
| 1 | **One clause, at `ServicioDeVentas.cs:930`**: `|| !tipo.AfectaStock`, appended to the existing boolean chain. No signature change, no new statement, no new error code | The explore's line-conditional guard (`!AfectaStock` **with product lines**) | The resolver runs at `:67`, **40 lines before** `MaterializarItems` (`:107`) and before `articulos` is loaded (`:98-100`); `EsProducto` is an artículo attribute. The conditional form needs a second query to buy a **weaker** rule that still admits an itemless `PRE` sale. Verified safe: the only other `afecta_stock = false` type resolves in its own service (`ServicioDeCuentaCorriente.ResolverTipoRcAsync:358-363`) and never reaches this method |
| 2 | **The snapshot branch replaces exactly two calls of the decide phase**: `ServicioDeOfertas.ResolverAsync` (`:91`) and `MaterializarItems` (`:107`). Everything else — punto de venta, turno, `articulos`/`alicuotas` snapshot, the parametros batch, the FEFO block (`:119-231`), `ValidadorDePagos`, `PlanDeVenta`, the whole transaction — runs **unchanged** | Accept `precioUnitario` from the request; a second checkout for conversions | Accepting money from the cart punches a hole in the one rule stage 5 defended hardest. A second checkout duplicates stock, lotes, pagos, cuenta corriente and numeración — a **fifth** stock write site for a document that is an ordinary sale |
| 3 | **`MaterializarItemsDesdePresupuesto` is a NEW private static in the same file; `MaterializarItems` is not touched.** Both run `CalculadorDeTotales.Calcular` + `ReglaDeComprobantes.ValidarSignoDeLineas`, so the arithmetic authority stays single | Widen `MaterializarItems` with per-line frozen overrides | Fact 2. The two materializers have genuinely different inputs (a live resolution vs a frozen snapshot); sharing the body would mean nullable override lists threading through the hottest method of the checkout. Drift is closed by decision 4, not by sharing |
| 4 | **The conversion asserts its own totals against the quote's stored header** (`subtotal`, `descuento_total`, `total`): a mismatch is `409 presupuesto_inconsistente` | Trust the recomputation | This is the one assertion that keeps the two materializers from drifting, and it is also the source-of-truth proof of `mutation-proof-tests` rule 12(a): a raw `UPDATE` desyncing `presupuestos.total` from its items must surface as a refusal, never as a silently different sale |
| 5 | **The guarded transition is ONE statement with FOUR conjuncts**: `UPDATE presupuestos SET estado='convertido', updated_at=$m WHERE id_presupuesto=$1 AND id_tenant=$2 AND id_punto_venta=$pv AND estado='enviado' AND vencimiento >= $hoy RETURNING id_presupuesto`. 0 rows ⇒ **reclassify under a `FOR UPDATE` read** into the precise 404/409 | Guard only `estado` and let the decide phase own expiry and PV | Expiry and the PV agreement are both **client-reachable** (`idPresupuestoOrigen` and `idPuntoVenta` arrive independently in the same body), so leaving them outside the atomic statement leaves a pre-check as the only guard. `vencimiento` is immutable once `enviado`, so the conjunct cannot flap; the reclassifying read is what keeps 0 rows from collapsing four distinct causes into one code |
| 6 | **Position 1.5** — after `ExigirTurnoAbiertoBajoLockAsync` (`:773`), before the comprobante INSERT (`:781`). For a sale without `idPresupuestoOrigen`: **zero extra statements** | Position 6 (after the CC loop), where the id is "most available" | Fact 3 and the total lock order. Late placement would burn the entire sale's writes — number, comprobante, items, pagos, stock, cuenta corriente — before discovering a 409, and would put `presupuestos` after `clientes` in the order |
| 7 | **`AnularAsync`'s `RETURNING` gains a scalar subquery, not a column**: `RETURNING id_punto_venta, (SELECT t.codigo FROM tipos_comprobante t WHERE t.id_tipo_comprobante = comprobantes_venta.id_tipo_comprobante) AS codigo_tipo`, read with `ExecuteReaderAsync` | `RETURNING id_tipo_comprobante` + a lookup of the `TXR` id | A second statement to translate an id into a code would run on **every** anulación, including the 100% that are ordinary sales. The subquery is evaluated inside the same round trip: the guarded un-link stays free for an ordinary comprobante (tension T1) |
| 8 | **`ServicioDeRemitos.EmitirAsync` is the fourth write site**, its stock loop shaped on `ServicioDeVentas.cs:866-885`: aggregate `stock` upsert first, `stock_lotes` only for a lot-effective line, ascending `(id_articulo, id_lote NULLS FIRST)`, its own `INSERT`/upsert statements | Extract the checkout's loop into a shared helper | `stock/spec.md:178-189` asks for **independent implementations with their own concurrency tests** — the duplication is the method of proof. Extracting it would couple the untouchable service to a new caller and destroy exactly the property the rule buys |
| 9 | **The remito's annulment carries NO negative-balance guard.** It reads the original `motivo = remito` movements from the ledger and inserts their exact inverses with `motivo = anulacion` and the same `id_remito`/`id_lote` | Copy `ServicioDeCompras.cs:632-658`'s `nueva < 0m` refusal | A remito **decrements**, so its reversal **adds** — it can never drive a balance negative. The compra guard exists because a compra adds and its reversal subtracts. Copying it here would be dead code claiming to protect something; the `ServicioDeVentas.cs:1130-1135` posture applies verbatim (tension T8) |
| 10 | **Two `hoy` values, deliberately different.** FEFO uses the checkout's UTC-naive `hoy` (`ServicioDeVentas.cs:163`, documented interim); the presupuesto's expiry uses `DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(reloj.Ahora, zona).DateTime)` with the PV's `zona_horaria` | One `hoy` for both | `lotes-y-vencimientos/spec.md:318-320` is binding for the **vencimientos rule**; the FEFO rule's delta says *"byte-identical, only the subject widens"*. Giving the remito a zone-aware FEFO would make write site 4 pick a different lot than write site 1 for the same data — a silent divergence dressed as a fix (tension T4) |
| 11 | **`ReglaDePresupuestos` is pure Domain, no database** (`PoliticaDeRoles`/`ReglaDeLotes` pattern), and its boundary is `vencimiento < hoy` — a quote is convertible **on** its expiry day | `<=`; a service-level inline comparison | `ReglaDeLotes.EstaVencido` (`:90`) is `fecha.Value < hoy`, and a document that prints *"válido hasta el 30/09"* is valid **on** the 30th. Same operator, same file family, one unit-testable function the read model and the guarded `UPDATE` both agree with |
| 12 | **The consolidation locks its remitos ascending `FOR UPDATE` BEFORE inserting the comprobante**, then writes pagos and cuenta corriente, then links with one state-guarded N-row `UPDATE … RETURNING id_remito` whose row count must equal the request's | Rely on the link `UPDATE` alone | The link `UPDATE` alone is race-correct but takes the `remitos` lock **after** `clientes`, inverting the total order against the `TXR` annulment. The explicit ascending lock puts `remitos` back in its position and makes the later `UPDATE` a no-wait write on rows already held |
| 13 | **The consolidation re-implements the checkout's credit-limit backstop** (`ServicioDeVentas.cs:901-908`): after `ActualizarSaldoClienteAsync`, `!CreditoIlimitado && nuevoSaldo > LimiteCredito` ⇒ `400 limite_credito_excedido` | Trust `ValidadorDePagos`'s pre-check | The pre-check runs outside the transaction against the saldo of that moment. Without the in-transaction backstop, a consolidation on cuenta corriente would bypass a limit an ordinary sale enforces — a hole opened by a path that claims to behave *"exactly like a sale"* (tension T9) |
| 14 | **`emitir` refuses a remito with zero items** (`400 remito_sin_items`) and a **non-product** line (`400 articulo_no_es_producto`) | Allow either | Zero items produces a numbered delivery that moved nothing; a service line breaks *"every remito line moves stock"*, which is what removes the `EsProducto` skip-branch (`:867`) from write site 4 entirely. Both mirror `compra_sin_items` / stage-16 decision 7 (tension T12) |
| 15 | **`enviar`, `anular`, `emitir` and the consolidation run under `FabricaDeEstrategiaSinReintento.CrearEstrategiaSinReintento(db)`**; the number is drawn **before** the transaction inside `db.Database.CreateExecutionStrategy()` | `CreateExecutionStrategy` + an idempotency detector like `BuscarPorNumeroComprometidoAsync` | These are human, manual acts without an idempotency key (`AnularAsync:478-486`'s reasoning verbatim). An automatic retry over an ambiguous commit would re-run a state-guarded `UPDATE` that no longer matches and report 409 to a request that succeeded. The burnt-number residue (`AsignadorDeNumeroComprobante.cs:29-32`) is inherited knowingly — `'PRES'`, `'REM'` and `'TXR'` are not fiscal series (tension T6) |
| 16 | **The `vencido` listing filter requires `idPuntoVenta`** (`400 punto_venta_requerido`); the projected `Vencido` field is computed per **distinct** `id_punto_venta` of the page | A global `vencido` filter in UTC | *"Hoy"* has no meaning without a zone, and a page can span punto de venta. Resolving one zone per distinct PV of the page is bounded by page size and reuses `ServicioDeParametros.ResolverAsync`; a UTC filter would be the exact defect `FechaDelRango.cs:9-16` documents (tension T5) |
| 17 | **No new policy.** `/api/presupuestos`, `/api/remitos` and `/api/remitos/facturacion` group under `Politicas.OperacionDePos`, with **nothing** stacked — the exact shape of `VentasEndpoints.cs:16-43` | `GestionDeCatalogo` on the remito because it moves stock | Proposal decision 10, verified. The manual `ajuste` is Admin-only because it is discretionary stock writing with no document and no customer; a remito has both. Gating a delivery harder than the sale of the same goods is an accident dressed as caution |
| 18 | **`ManejadorDeErrores`'s branches ship with their migrations** — 3 exact-name `23505` + 4 exact-name `23514` in slice 1, 2 exact-name `23505` + 3 exact-name `23514` in slice 4 | Ship them with the write paths | `db-error-backstops` Execution Step 3: the SQLSTATE test belongs to the work unit that adds the constraint, and at schema time every branch is reachable **only** out of band — which is exactly how it is proven (raw insert asserting the SQLSTATE **and** the translated code) |

## Interfaces / Contracts

### Domain — pure, no database

```csharp
// Ways.Domain/Ventas/EstadoPresupuesto.cs — el ORDEN de los miembros ES el orden de valores del
// tipo nativo (npgsql.MapEnum<T>, gate §A). Un escritor por valor, ningún valor especulativo.
public enum EstadoPresupuesto { Borrador, Enviado, Convertido, Anulado }
public enum EstadoRemito      { Borrador, Emitido, Facturado, Anulado }

// Ways.Domain/Stock/MotivoStock.cs — Remito va ÚLTIMO: el orden de miembros es el orden de
// valores del tipo nativo y 'remito' entra por ALTER TYPE ... ADD VALUE (noveno).
public enum MotivoStock { Venta, Compra, Anulacion, Ajuste, Transferencia, Inventario,
                          Decomiso, Reclasificacion, Remito }

// Ways.Domain/Ventas/ReglaDePresupuestos.cs — decisión 11. `hoy` SIEMPRE llega resuelto en la
// zona del punto de venta; esta función no conoce relojes ni zonas (patrón ReglaDeLotes).
public static bool EstaVencido(EstadoPresupuesto estado, DateOnly? vencimiento, DateOnly hoy) =>
    estado is EstadoPresupuesto.Enviado && vencimiento is { } v && v < hoy;

public static bool EsConvertible(EstadoPresupuesto estado, DateOnly? vencimiento, DateOnly hoy) =>
    estado is EstadoPresupuesto.Enviado && !EstaVencido(estado, vencimiento, hoy);
```

### Application — the two containment classes

```csharp
// Ways.Application/Ventas/EscriturasDePresupuesto.cs
// Copia estructural de EscriturasDeOrdenDeCompra (:28-58): static, misma postura de
// conexión/transacción del llamador, nunca abre/flushea/comitea nada. La ÚNICA clase que escribe
// presupuestos.estado = 'convertido' — llamada DESDE la transacción de ServicioDeVentas, jamás
// desde ServicioDePresupuestos (la contención ES el producto; un DI seam invita a la segunda
// implementación que esta clase existe para impedir).
public static class EscriturasDePresupuesto
{
    /// UN solo statement (decisión 5). 0 filas ⇒ el llamador reclasifica bajo FOR UPDATE.
    public static Task<bool> MarcarConvertidoAsync(
        DbConnection conexion, DbTransaction? transaccion, int idTenant, int idPresupuesto,
        int idPuntoVenta, DateOnly hoyEnZonaDelPuntoVenta, DateTimeOffset momento, CancellationToken ct);

    /// Reclasificación bajo lock: traduce el 0-filas en 404 / 409 presupuesto_no_convertible /
    /// 409 presupuesto_vencido / 409 presupuesto_ya_convertido / 400 punto_venta_no_coincide.
    public static Task ExigirCausaDelRechazoAsync(
        DbConnection conexion, DbTransaction? transaccion, int idTenant, int idPresupuesto,
        int idPuntoVenta, DateOnly hoyEnZonaDelPuntoVenta, CancellationToken ct);
}

// Ways.Application/Ventas/EscriturasDeRemito.cs — misma forma. La ÚNICA clase que liga y desliga
// remitos de un comprobante; el desligue lo llama AnularAsync bajo el guard del `codigo` = TXR.
public static class EscriturasDeRemito
{
    /// Lock ascendente explícito (decisión 12) — devuelve los estados leídos bajo el lock.
    public static Task<IReadOnlyList<(int IdRemito, string Estado, int? IdComprobante)>> BloquearAscendenteAsync(
        DbConnection conexion, DbTransaction? transaccion, int idTenant, IReadOnlyList<int> idsRemito,
        CancellationToken ct);

    /// UPDATE guardado de N filas. Row count != idsRemito.Count ⇒ 409 (perdedor de la carrera).
    public static Task<int> LigarAsync(
        DbConnection conexion, DbTransaction? transaccion, int idTenant, IReadOnlyList<int> idsRemito,
        int idComprobanteVenta, DateTimeOffset momento, CancellationToken ct);

    /// Desligue de la anulación del TXR: estado y link vuelven JUNTOS (ck_remitos_facturacion).
    public static Task<int> DesligarAsync(
        DbConnection conexion, DbTransaction? transaccion, int idTenant, int idComprobanteVenta,
        DateTimeOffset momento, CancellationToken ct);
}
```

```sql
-- MarcarConvertidoAsync — la ÚNICA autoridad de transición (decisión 5).
UPDATE presupuestos
SET estado = 'convertido'::estado_presupuesto, updated_at = $6
WHERE id_presupuesto = $1 AND id_tenant = $2 AND id_punto_venta = $3
  AND estado = 'enviado'::estado_presupuesto
  AND vencimiento >= $4                    -- `hoy` YA resuelto en la zona del PV (decisión 10)
  AND deleted_at IS NULL
RETURNING id_presupuesto

-- LigarAsync — N filas en UN statement guardado (decisión 12).
UPDATE remitos
SET estado = 'facturado'::estado_remito, id_comprobante_venta = $3, updated_at = $4
WHERE id_remito = ANY($1) AND id_tenant = $2
  AND estado = 'emitido'::estado_remito AND id_comprobante_venta IS NULL AND deleted_at IS NULL
RETURNING id_remito
-- filas != N ⇒ 409 remito_no_facturable (otro consolidado ganó, o alguien lo anuló)

-- DesligarAsync — estado y link vuelven JUNTOS o ck_remitos_facturacion tira 23514.
UPDATE remitos
SET estado = 'emitido'::estado_remito, id_comprobante_venta = NULL, updated_at = $3
WHERE id_comprobante_venta = $1 AND id_tenant = $2 AND estado = 'facturado'::estado_remito
RETURNING id_remito
```

### Application — read/write contracts

```csharp
// Ways.Application/Ventas/ContratosDePresupuesto.cs
public sealed record LineaDePresupuesto(int IdArticulo, decimal Cantidad);
// `orden` NO viaja: server-asignado 1..N dentro del replace-set. Sin dinero en la solicitud —
// el precio lo resuelve el motor al guardar el borrador, igual que el checkout (decisión 2).
public sealed record SolicitudDePresupuesto(
    int IdPuntoVenta, int? IdCliente, string? Observaciones, IReadOnlyList<LineaDePresupuesto> Lineas);
public sealed record SolicitudDeEnvio(DateOnly Vencimiento);

public sealed record ItemDePresupuesto(int Orden, int IdArticulo, string Descripcion, decimal Cantidad,
    decimal PrecioUnitario, decimal Descuento, decimal Total, int IdListaPrecio, int? IdOferta,
    int IdAlicuotaIva, decimal PorcentajeIva);

public sealed record PresupuestoDetalle(int Id, int IdPuntoVenta, int IdCliente, int IdEmpleado,
    long? Numero, string? NumeroFormateado, DateTimeOffset FechaEmision, DateTimeOffset? FechaEnvio,
    DateOnly? Vencimiento, bool Vencido, bool Convertible, string ZonaId, string? Observaciones,
    decimal Subtotal, decimal DescuentoTotal, decimal Total, EstadoPresupuesto Estado,
    int? IdComprobanteVenta, IReadOnlyList<ItemDePresupuesto> Items);

// `/para-venta`: lectura PARA MOSTRAR, jamás un SolicitudDeVenta pre-armado (dto-contract-honesty
// regla 1 — un shape que el POS pudiera postear haría creíble al carrito para el dinero).
public sealed record PresupuestoParaVenta(int IdPresupuesto, long? Numero, int IdPuntoVenta,
    int IdCliente, DateOnly? Vencimiento, bool Vencido, bool Convertible, decimal Subtotal,
    decimal DescuentoTotal, decimal Total, IReadOnlyList<ItemDePresupuesto> Items);

// Ways.Application/Ventas/ContratosDeRemito.cs
public sealed record LineaDeRemito(int IdArticulo, decimal Cantidad, int? IdLote);
public sealed record SolicitudDeRemito(int IdPuntoVenta, int? IdCliente, string? DireccionEntrega,
    string? Observaciones, IReadOnlyList<LineaDeRemito> Lineas);

// Sin idCliente: se deriva de los remitos y un valor en conflicto no tendría destino
// (dto-contract-honesty regla 1).
public sealed record SolicitudDeFacturacionDeRemitos(int IdPuntoVenta, IReadOnlyList<int> IdsRemito,
    IReadOnlyList<PagoDeVenta> Pagos, string? Observaciones);
```

`SolicitudDeVenta` gains `int? IdPresupuestoOrigen` **and `ComprobanteEmitido` gains
`int? IdPresupuestoOrigen`** — `dto-contract-honesty` rule 2 requires the round-trip assertion, which
a request-only field cannot satisfy (tension T7). No route, no policy and no other field changes in
`VentasEndpoints`.

```csharp
/// Cláusulas bajo prueba (mutation-proof-tests), en orden de daño si se pierden:
///   Where(p => p.Id == id) / Where(p => p.IdCliente == c)  → un presupuesto filtra los de otro
///   ThenByDescending(p => p.Id) → con fecha_emision empatada (RelojFijo) la paginación duplica
///   cada if (idPuntoVenta/idCliente/estado/vencido/desde/hasta is { } x) → filtro ignorado
private IQueryable<Presupuesto> ConstruirQuery(
    int? idPuntoVenta, int? idCliente, EstadoPresupuesto? estado, DateTimeOffset? desde, DateTimeOffset? hasta);
```

## Transactions (binding statement order)

```
── EMITIR VENTA (ServicioDeVentas.EmitirAsync :50-300) ─────────────────────────────
  FASE DECIDE (fuera de transacción) — el ÚNICO tramo que cambia:
    :59  lineas := idPresupuestoOrigen is null ? ExigirLineasValidas(solicitud.Lineas)
                                               : ExigirSinLineas(solicitud.Lineas)   ← 400
    :67  ResolverTipoComprobanteAsync   ← + UNA cláusula: || !tipo.AfectaStock
    :68  ResolverPuntoVentaAsync · :75 turno · :77 cliente                 ← SIN CAMBIOS
    ── RAMA DEL SNAPSHOT (solo con idPresupuestoOrigen) ──
       p1. leer presupuesto + items (AsNoTracking) · exigir mismo tenant/PV
       p2. hoy := DateOnly(TimeZoneInfo.ConvertTime(momento, zona del PV))   ← decisión 10
       p3. ReglaDePresupuestos.EsConvertible ⇒ si no: 409 (pre-chequeo, NO la autoridad)
       p4. cliente := el del presupuesto; idCliente en conflicto ⇒ 400
       p5. tipo.Signo <= 0 ⇒ 400 conversion_requiere_signo_positivo
       p6. lineas := items del presupuesto (IdLote null: FEFO se resuelve abajo, sin cambios)
    :91  resolucion := ResolverAsync(...)   ←→   sintetizada desde items_presupuesto
    :98-105 articulos + alicuotas          ← SIN CAMBIOS (EsProducto, IdArea, CostoNominal)
    :107 MaterializarItems(...)            ←→   MaterializarItemsDesdePresupuesto(...)
                                                + assert totales == header del presupuesto (409)
    :116-231 parametros + FEFO             ← SIN CAMBIOS (BYTE-IDÉNTICO)
    :234-262 medios + ValidadorDePagos     ← SIN CAMBIOS
    :278-280 AsignarComprometidoAsync      ← SIN CAMBIOS (su PROPIA transacción, antes)

  EjecutarTransaccionAsync (:762-919)  BEGIN
   0.   ExigirTurnoAbiertoBajoLockAsync (:773)                             ← SIN CAMBIOS
   1.5  si plan.IdPresupuestoOrigen is { } idP   (si es NULL: CERO statements extra)
        EscriturasDePresupuesto.MarcarConvertidoAsync  ← ÚNICO lock nuevo (decisión 6)
        false ⇒ ExigirCausaDelRechazoAsync (FOR UPDATE) ⇒ 404/409
   2.   comprobante INSERT (:781-801)  + id_presupuesto_origen              ← una columna
   3+4. items y pagos (:803-850)                                           ← SIN CAMBIOS
   5.   stock ascendente (:866-885)                                        ← BYTE-IDÉNTICO
   6.   cuenta corriente (:890-914)                                        ← BYTE-IDÉNTICO
  COMMIT (:916)

── ANULAR VENTA (EjecutarAnulacionAsync :489-668) ──────────────────────────────────
   0.   ExigirTurnoNoCerradoAsync (:517)                                   ← SIN CAMBIOS
   1.   MarcarAnuladoAsync (:527) — RETURNING id_punto_venta,
        (SELECT t.codigo FROM tipos_comprobante t WHERE ...) AS codigo_tipo ← decisión 7
   1.6  si codigo_tipo == "TXR"   (cualquier otro: CERO statements extra)
        EscriturasDeRemito.DesligarAsync   ← posición 2, nunca después de stock/clientes
   1.5  auditoría (:553-561)                                               ← SIN CAMBIOS
   2.   reversa de stock (:575-596) — para un TXR el read set es VACÍO POR CONSTRUCCIÓN
   3.   contramovimiento de CC (:607-663)                                  ← SIN CAMBIOS
  COMMIT (:665)

── ENVIAR PRESUPUESTO ──────────────────────────────────────────────────────────────
  fuera: pre-lectura (404 / 409 presupuesto_ya_enviado / 400 presupuesto_sin_items)
         hoy en zona del PV ⇒ vencimiento < hoy ⇒ 400 vencimiento_invalido
  CreateExecutionStrategy ⇒ AsignarComprometidoAsync(db, tenant, pv, "PRES")  ← propia transacción
  EstrategiaSinReintento ⇒ BEGIN
   1. UPDATE presupuestos SET numero, fecha_envio, vencimiento, estado='enviado'
      WHERE id AND tenant AND estado='borrador' AND id_punto_venta = $pv RETURNING numero
      0 filas ⇒ reclasificar bajo lectura (409); el número queda quemado (residuo aceptado)
  COMMIT

── EMITIR REMITO (cuarto write site) ───────────────────────────────────────────────
  fuera: pre-lectura + items · 400 remito_sin_items / articulo_no_es_producto
         FEFO: LeerSaldosAsync → ReglaDeLotes.ElegirFefo(hoy UTC-naive) → ResolverSinIdentificar
  CreateExecutionStrategy ⇒ AsignarComprometidoAsync(db, tenant, pv, "REM")
  EstrategiaSinReintento ⇒ BEGIN
   1. UPDATE remitos SET numero, fecha_salida, estado='emitido'
      WHERE ... AND estado='borrador' AND id_punto_venta = $pv RETURNING numero   ← lock del remito
   2. UPDATE items_remito SET id_lote, costo_unitario, costo_es_estimado (snapshot congelado)
   3. por item, ORDEN ASCENDENTE (id_articulo, id_lote NULLS FIRST):
        INSERT movimientos_stock (motivo='remito', id_remito, id_lote)
        UpsertStockAsync         (agregado, SIEMPRE, primero)
        UpsertStockLoteAsync     (solo línea lote-efectiva)
  COMMIT

── ANULAR REMITO ───────────────────────────────────────────────────────────────────
  EstrategiaSinReintento ⇒ BEGIN
   1. UPDATE remitos SET estado='anulado' WHERE ... AND estado='emitido' RETURNING id_punto_venta
      0 filas ⇒ 404 / 409 remito_facturado / 409 remito_ya_anulado
   2. movimientos ORIGINALES del ledger (id_remito = $, motivo='remito'), orden asc:
        INSERT inversa (motivo='anulacion', mismo id_remito, mismo id_lote) + upserts
        SIN chequeo de negativo (decisión 9)
  COMMIT

── FACTURAR REMITOS (consolidación) ────────────────────────────────────────────────
  fuera: remitos + items · mismo tenant/cliente/PV · todos 'emitido' y sin ligar
         totales := Σ headers, aserción contra Σ items · ValidadorDePagos · turno abierto
  CreateExecutionStrategy ⇒ AsignarComprometidoAsync(db, tenant, pv, "TXR")
  EstrategiaSinReintento ⇒ BEGIN
   0. ExigirTurnoAbiertoBajoLockAsync                       ← decisión 13 del proposal
   1. BloquearAscendenteAsync (FOR UPDATE, ORDER BY id_remito) + re-validación bajo lock
   2. comprobante TXR — CERO items por construcción (precedente RC :287-325)
   3. pagos
   4. cuenta corriente: ActualizarSaldoClienteAsync + backstop de límite + InsertarMovimientoCcAsync
   5. LigarAsync — filas == N o 409
   (CERO movimientos_stock — la mercadería ya salió)
  COMMIT
```

### Lock order — verified against the real call sites

| Path | Locks taken, in order | Verdict |
|---|---|---|
| `EjecutarTransaccionAsync` (sale) | `turnos_caja` (`:773`) → **`presupuestos` (new, 1.5)** → `stock`/`stock_lotes` (`:878-883`) → `clientes` (`:898`) | Extends the pinned order at position 1.5. The comprobante is an INSERT, not a position |
| `EjecutarAnulacionAsync` | `turnos_caja` (`:517`) → `comprobantes_venta` (`:527`) → **`remitos` (new, 1.6)** → `stock`/`stock_lotes` → `clientes` (`:628`) | Same prefix; the audit row (`:553`) locks nothing and does not move |
| `ServicioDeRemitos.EmitirAsync` | `remitos` → `lotes` → `stock`/`stock_lotes` | Suffix, no turno (decision 13 of the proposal) |
| `ServicioDeRemitos.AnularAsync` | `remitos` → `stock`/`stock_lotes` | Suffix |
| `ServicioDeFacturacionDeRemitos` | `turnos_caja` → `remitos` (asc `FOR UPDATE`) → `clientes` | Conforms; its comprobante is an INSERT |
| `ServicioDePresupuestos` (`PUT`/`enviar`/`anular`) | `presupuestos` only | Singleton ⇒ the order stays total |
| `ServicioDeCompras` / `ServicioDeStock` (untouched) | unchanged | Disjoint from `presupuestos`/`remitos` |

**Total order over contended (existing-row) locks:
`turnos_caja → comprobantes_venta → presupuestos → remitos → lotes → stock/stock_lotes → clientes →
ledger INSERT`.** New clause, and it is load-bearing: **a new-row `INSERT` is not a position in this
order** — only locks taken on rows that already exist are. That is what makes the consolidation
(`remitos` then insert a comprobante) and the `TXR` annulment (`comprobantes_venta` then `remitos`)
acyclic rather than merely untested (tension T10).

**Concurrency guarantees.** *Convertir × convertir of one quote*: serialized on the presupuesto row;
the loser's `UPDATE` matches 0 rows and reclassifies to `409 presupuesto_ya_convertido`, having
written nothing except a burnt `TX` number. *Convertir × anular presupuesto*: whoever takes the row
first wins; the other reclassifies under the lock. *Remitir × remitir of the same artículo and lot*:
the same ascending key as the checkout ⇒ serialized on `stock`/`stock_lotes`, no deadlock.
*Remitir × checkout*: the rendezvous test of the fourth write site. *Facturar × facturar over
overlapping sets*: serialized on the ascending `remitos` locks; the loser's `LigarAsync` returns
fewer than N rows ⇒ 409. *Facturar × anular remito*: whoever takes the remito row first wins.
*Anular TXR × facturar*: the annulment holds `comprobantes_venta` then `remitos`; the consolidation
holds `remitos` then inserts — no cycle (fact above).

**Failure semantics.** Any throw rolls the sale and the quote's estado back together: *"stock moved
but the quote still says `enviado`"* and *"the quote is `convertido` but no sale committed"* are both
unrepresentable. The only values that survive a rollback are the drawn numbers, by design.

## API Surface (ADR-8: uniform 404 cross-tenant)

| Route | Policy | Notes |
|---|---|---|
| `GET /api/presupuestos?idPuntoVenta&idCliente&estado&vencido&desde&hasta&pagina&tamanio` | `OperacionDePos` (group) | Paginated, `fecha_emision DESC, id DESC`; `vencido` requires `idPuntoVenta` (decision 16) |
| `GET /api/presupuestos/{id:int}` | group | Header + items + derived `Vencido`/`Convertible` + `ZonaId` + the linked sale id |
| `POST /api/presupuestos` | group | 201; `numero`/`fecha_envio`/`vencimiento` NULL, `estado = 'borrador'` |
| `PUT /api/presupuestos/{id:int}` | group | Full replace-set under `FOR UPDATE`, `borrador` only (409 otherwise) |
| `POST /api/presupuestos/{id:int}/enviar` | group | Body `{ vencimiento }`; number + `fecha_envio` + `vencimiento` together (CHECK 1) |
| `POST /api/presupuestos/{id:int}/anular` | group | From `borrador` or `enviado`; a `convertido` quote is not annullable (409) |
| `GET /api/presupuestos/{id:int}/para-venta` | group | Read for display (decision 2); refuses nothing, reports `Convertible` |
| `GET /api/remitos?idPuntoVenta&idCliente&estado&desde&hasta&pagina&tamanio` · `GET /{id:int}` | group | Same shape |
| `POST` · `PUT /api/remitos/{id:int}` | group | Draft + replace-set, `borrador` only |
| `POST /api/remitos/{id:int}/emitir` · `/anular` | group | The fourth write site and its reversal |
| `POST /api/remitos/facturacion` | group | The consolidation; 201 with the `TXR` `ComprobanteEmitido` |
| `POST /api/ventas` | unchanged | The body gains `idPresupuestoOrigen`; **no route, policy or response change beyond `ComprobanteEmitido.IdPresupuestoOrigen`** |

`Politicas.cs` is **unchanged** (decision 17). The stage-5 `SuperficieDeAutorizacionTests` allowlist
gains the eleven new non-GET routes.

## Backstop Map (`db-error-backstops`)

| Constraint | Client-reachable? | Backstop | Test |
|---|---|---|---|
| `ux_presupuestos_numero` | No (only the assigner writes it) | **Exact-name `23505` ABOVE `ClasificarUnicidad`** → `numero_de_presupuesto_duplicado`, 409 — the `_numero` ordering trap, **fourth occurrence** | Raw out-of-band insert asserting `23505` **and** the translated code + two concurrent `enviar` on one PV ⇒ two distinct numbers, no 409 |
| `ux_remitos_numero` | No | Same treatment → `numero_de_remito_duplicado`, 409 — **fifth occurrence** | Same pair |
| `ux_comprobantes_venta_presupuesto_origen` | **Yes** (`idPresupuestoOrigen`) | Exact-name `23505` (its name must not reach substring classification) → `presupuesto_ya_convertido`, 409. The state-guarded `UPDATE` is the primary authority; this index is the schema backstop | Raw insert; **race**: two concurrent conversions ⇒ exactly one 201 and one 409 |
| `ux_items_presupuesto_orden` · `ux_items_remito_orden` | No (`orden` server-assigned) | Exact-name `23505` → `orden_de_item_duplicado`, 409 | Raw insert; **race-test exemption documented**, same family as `ux_items_comprobante_venta_orden` |
| `ak_presupuestos_…` · `ak_remitos_…` | No — structurally unviolable | **No mapping. Exemption documented** (`ak_gastos_*` / `ak_ordenes_compra_*` precedent) | — |
| `ck_presupuestos_envio_completo` (CHECK 1) | No — server-derived | Exact-name `23514` → `presupuesto_envio_incompleto`, 409 | Raw insert per direction (numero without fecha_envio; without vencimiento; `enviado` without numero) |
| `ck_remitos_salida_completa` (CHECK 3) | No | Exact-name `23514` → `remito_salida_incompleta`, 409 | Raw insert per direction |
| `ck_remitos_facturacion` (CHECK 4) | No | Exact-name `23514` → `remito_facturacion_incoherente`, 409 | Raw insert per direction — and the mutation of `DesligarAsync` that clears only one of the two columns |
| `ck_items_presupuesto_cantidad_positiva` · `ck_items_remito_cantidad_positiva` (CHECK 2, 5) | **Yes** | Service 400 first (`cantidad_de_linea_invalida`) + exact-name `23514` | Service test + raw-insert SQLSTATE |
| `ck_items_remito_costo_no_negativo` · `ck_items_remito_estimado_con_costo` (CHECK 6, 7) | No — server-derived | Exact-name `23514` | Raw insert per direction |
| FK 3/13 `…_cliente`, FK 2/12 `…_punto_venta` | **Yes** (body) | `ResolverClienteAsync`/`ResolverPuntoVentaAsync` 404 first (ADR-8) + generic `23503` → `400 referencia_invalida` | One test per FK asserting the **translated** code |
| FK 7/18 `…_articulo`, FK 8/19 `…_lista_precio`, FK 9/20 `…_oferta`, FK 10/21 `…_alicuota_iva`, FK 22 `…_lote` | **Yes** (item lines) | Same pre-check shape as the venta draft + generic mapping | One test per family |
| FK 23 `…_presupuesto_origen` | **Yes** | `ExigirCausaDelRechazoAsync` 404/409 before any write + generic mapping | **Race**: converting a quote being annulled concurrently |
| FK 15 `…_comprobante_venta`, FK 24 `…_remito` | No — server-derived in-transaction | Generic mapping. **Exemption documented** | One SQLSTATE-asserting test anyway |
| FK 1/5/11/16 `…_tenant`, FK 4/14 `…_empleado`, FK 6/17 (items→parent) | No — session/server-derived | Generic mapping. **Exemptions documented** (`fk_comprobantes_venta_empleado` precedent) | Same |

## Web composition

- **`Presupuestos.tsx`** (`/presupuestos`, `RutaProtegida rolesPermitidos={[ROL.Vendedor, ROL.Supervisor, ROL.Admin]}`):
  filters (punto de venta, cliente, estado, `vencido`, desde/hasta) + the `HistoricoDeCajas.tsx`
  pager. Date limits built with `fechaIsoConOffset` (the browser's own offset, **never `Z`**). The
  `vencido` toggle is disabled until a punto de venta is chosen (decision 16 made visible, never a
  400 the user has to read).
- **`Presupuesto.tsx`** (`/presupuestos/nuevo`, `/presupuestos/:id`): draft editor (the
  `CompraEditor.tsx` line grid) + detail with the expiry state, `enviar` (a date input defaulted to
  `hoy + 30` in the PV zone), `anular`, and **"Convertir en venta"** →
  `navigate('/pos?idPresupuesto=' + id)`, rendered only when `Convertible`.
- **`Pos.tsx`** — reads `idPresupuesto` from `useSearchParams`. When present: fetch `/para-venta`,
  render the *"Esta venta viene del presupuesto N° … (vence el …)"* banner, hydrate the cart
  **read-only** from the quote's frozen lines, **skip the price-resolution effect entirely**
  (`precios`/`avisoPrecios`/`reintentoPrecios` are not used on this path — the displayed money is the
  quote's), disable the scan input, the quantity inputs and the line-removal buttons, and post
  `{ idPuntoVenta, codigoTipoComprobante: 'TX', idPresupuestoOrigen, lineas: undefined, pagos }`.
  `react-async-state` rule 8: `key={idPresupuesto ?? 'libre'}` on the POS subtree, so leaving the
  conversion cannot leave a frozen cart behind.
- **`Remitos.tsx` / `Remito.tsx`** — list + draft/detail with `emitir` (lot picker reusing
  `SelectorDeLote`, `Pos.tsx:228-318`) and `anular`; a `facturado` remito renders its invoice link
  and no actions.
- **`FacturarRemitos.tsx`** — pick a cliente + punto de venta, list its `emitido` unlinked remitos,
  multi-select, show the summed total, take payments with the POS payment rows, post the
  consolidation.
- `react-async-state`: rule 2 `generacionRef` on every fetch; rule 3 the generation bumps **before**
  each write; rule 6 the post-write refetch has its own `try/catch` (a 2xx `enviar`/`emitir` is never
  reported as a failure because the refetch failed); rule 9 first-line re-entrancy guard + full-window
  disable on `enviar`/`emitir`/`anular`/`facturar` (a double `emitir` burns two numbers and could move
  stock twice); rule 10 any recovery path added on one screen is grepped for and replicated in its
  sibling in the same commit.
- **`src/Ways.Web/src/api/presupuestos.ts` · `remitos.ts`** — clients + pure mappers; `tipos.ts`
  mirrors. `web-descriptor-tests`: colocated tests for every new pure helper (the expiry-badge
  formatter, the consolidation total reducer, the filter builder) and for every screen's descriptors.
- **Pre-approved degradation**: the POS banner + read-only hydration is an isolated branch behind one
  search param, and `FacturarRemitos.tsx`'s bulk selection degrades to one remito at a time — both
  are clean non-deliveries (the API still serves them), never retractions.

## File Changes

| File | Action | Description |
|---|---|---|
| `src/Ways.Domain/Ventas/EstadoPresupuesto.cs` · `EstadoRemito.cs` | Create | 4 values each, lifecycle order = the native type's order |
| `src/Ways.Domain/Ventas/Presupuesto.cs` · `ItemPresupuesto.cs` · `Remito.cs` · `ItemRemito.cs` | Create | `EntidadTenant` ⇒ `EntidadBase` (gate §C-§F) |
| `src/Ways.Domain/Ventas/ReglaDePresupuestos.cs` | Create | The pure expiry/convertibility predicate (decision 11) |
| `src/Ways.Domain/Stock/MotivoStock.cs` | Modify | `Remito` **last**, with its irreversibility comment |
| `…/Persistencia/Migraciones/…_PresupuestosEtapa17.cs` | Create | Gate §A(1), §C, §D, §G, §I(1); RLS last |
| `…/Persistencia/Migraciones/…_RemitosEtapa17.cs` | Create | Gate §B, §A(2), §E, §F, §H, §I(2-3); the `ALTER TYPE` named by nothing else; RLS last |
| `…/Configuraciones/PresupuestoConfiguration.cs` · `ItemPresupuestoConfiguration.cs` · `RemitoConfiguration.cs` · `ItemRemitoConfiguration.cs` | Create | Shaped on `ComprobanteVentaConfiguration` / `ItemComprobanteVentaConfiguration`; every support index declared by hand with doc-10 names |
| `…/Configuraciones/ComprobanteVentaConfiguration.cs` | Modify | `IdPresupuestoOrigen` + FK 23 + the **named, filtered** `ux_comprobantes_venta_presupuesto_origen` |
| `…/Configuraciones/MovimientoStockConfiguration.cs` | Modify | `IdRemito` + FK 24 + `ix_movimientos_stock_remito` |
| `…/WaysDbContext.cs` · `IWaysDbContext.cs` | Modify | Four `DbSet`s |
| `…/WaysDbContextFactory.cs` · `DependencyInjection.cs` | Modify | `MapEnum<EstadoPresupuesto>`/`MapEnum<EstadoRemito>` in **both** builders, never also `HasPostgresEnum` |
| `…/Persistencia/InicializadorDeBaseDeDatos.cs` | Modify | `TiposComprobanteBase` gains an explicit `Activo` field (`false` for `PRE` alone) and the `TXR` tuple |
| `src/Ways.Application/Ventas/ServicioDePresupuestos.cs` · `ContratosDePresupuesto.cs` | Create | Draft replace-set, `enviar`, `anular`, list/detail, `/para-venta` |
| `src/Ways.Application/Ventas/EscriturasDePresupuesto.cs` | Create | The two statements + the reclassifying read |
| `src/Ways.Application/Ventas/ServicioDeRemitos.cs` · `ContratosDeRemito.cs` | Create | Draft, `emitir` (**the fourth write site**), `anular`, list/detail |
| `src/Ways.Application/Ventas/EscriturasDeRemito.cs` | Create | Ascending lock, `LigarAsync`, `DesligarAsync` |
| `src/Ways.Application/Ventas/ServicioDeFacturacionDeRemitos.cs` | Create | The consolidation of decision 12/13 |
| `src/Ways.Application/Ventas/ServicioDeVentas.cs` | Modify | **One clause** at `:930`; the snapshot branch of the decide phase (`:59`, `:91`, `:107` + `MaterializarItemsDesdePresupuesto`); **one guarded call** at 1.5; **one guarded call** at 1.6 behind the widened `RETURNING` of `MarcarAnuladoAsync` (`:746-757`). `:762-919`'s pinned order and both loops are **byte-identical** |
| `src/Ways.Application/Ventas/AsignadorDeNumeroComprobante.cs` | **Unmodified** | Reused with `'PRES'`, `'REM'`, `'TXR'` |
| `src/Ways.Api/Endpoints/PresupuestosEndpoints.cs` · `RemitosEndpoints.cs` | Create | 12 routes (API Surface), `OperacionDePos`, nothing stacked |
| `src/Ways.Api/Endpoints/VentasEndpoints.cs` | Modify | Nothing but the DTO surface (the body/response records live in `Contratos.cs`) |
| `src/Ways.Api/Seguridad/ManejadorDeErrores.cs` | Modify | 5 exact-name `23505` (two of them the `_numero` trap, 4th and 5th) + 7 exact-name `23514`, split across slices 1 and 4 |
| `src/Ways.Api/Seguridad/Politicas.cs` | **Unmodified** | Decision 17 |
| `src/Ways.Web/src/paginas/Presupuestos.tsx` · `Presupuesto.tsx` · `Remitos.tsx` · `Remito.tsx` · `FacturarRemitos.tsx` (+ `.test.tsx`) | Create | List + detail/draft + consolidation |
| `src/Ways.Web/src/paginas/Pos.tsx` (+ `.test.tsx`) | Modify | The banner, the read-only hydration, the skipped price effect, the `key` |
| `src/Ways.Web/src/api/presupuestos.ts` · `remitos.ts` (+ `.test.ts`) · `tipos.ts` · `App.tsx` | Create + Modify | Clients, mappers, DTO mirrors, five routes |
| `docs/10-modelo-de-datos.md` | Modify | Four tables, both new columns, the `PRE`/`TXR` notes in §1, and the **"Estado (Etapa 17)"** annotations — opened from inside the schema slices, **closed in the last slice** |
| `openspec/specs/stock/spec.md` · `lotes-y-vencimientos/spec.md` · `comprobantes-venta/spec.md` · `auxiliary-catalogs/spec.md` | Modify | The four capability deltas the proposal names |

## What does NOT change

- **The checkout's pinned statement order.** `ServicioDeVentas.cs:762-919` steps 0, 2, 3+4, 5 and 6
  keep their order and their bodies; the stock loop (`:866-885`) and the cuenta-corriente loop
  (`:890-914`) are **byte-identical**. **A sale without `idPresupuestoOrigen` emits ZERO extra
  statements** — asserted by command count, not claimed.
- **`MaterializarItems`** (`:1007-1065`), `ExigirLineasValidas`, `ResolverParametrosDeVentaAsync`,
  the FEFO block (`:119-231`), `BuscarPorNumeroComprometidoAsync`, `Proyectar`, `PlanDeVenta`'s
  existing members.
- **`AsignadorDeNumeroComprobante.cs`** — read, not edited. `numeraciones_comprobante` gains no
  schema and no seed (verified: `tipo_comprobante` is a plain `varchar(30)`).
- **The other three stock write sites.** No file under `src/Ways.Application/Compras/` or
  `src/Ways.Application/Stock/` is in this stage's diff; `ServicioDeCompras`, `ServicioDeStock` and
  `ServicioDeLotes` are **consumed**, not edited.
- **The ledgers.** `movimientos_stock`, `movimientos_cuenta_corriente` and the stage-15 proveedor
  ledger keep exactly the writers they had, plus the fourth stock writer's own rows.
  `EscriturasDeCuentaCorriente` is consumed unchanged.
- **`Politicas.cs`**, the `RC` path (`ServicioDeCuentaCorriente.cs:275-363`), `turnos_caja`,
  `ValidadorDePagos`, `ServicioDeOfertas.ResolverAsync`, `CalculadorDeTotales`.
- **The reserved carryovers**: the `importe` CHECK micro-gate, the `articulos_empresas` replace-set
  gap, `ways_owner` superuser, `stage-13b` conteo por planilla.

## Testing Strategy

| Layer | What | Approach |
|---|---|---|
| Domain unit (no DB) | `ReglaDePresupuestos`: the full truth table over 4 estados × (`vencimiento` before/equal/after `hoy`) × NULL; `EstaVencido` false for every non-`enviado` estado; the **boundary** (`vencimiento == hoy` ⇒ convertible) | xUnit pure, `PoliticaDeRoles` pattern. No fixture, no container |
| Integration — **the four binding gate tests** | (a) **zero extra statements**: a sale with `idPresupuestoOrigen IS NULL` issues the exact command count of the pre-stage path, and so does an anulación of a non-`TXR`; (b) **the phantom sale, both nets**: a freshly seeded DB has `PRE` inactive, `POST /api/ventas` with `"PRE"` ⇒ 400, and an **out-of-band active** venta-class type with `afecta_stock = false` ⇒ 400 (still red if the resolver clause is removed); (c) **frozen price**: convert a quote after the list price, the offer and the alicuota all changed ⇒ the sale carries the **quoted** money; (d) **30 indexes by definition** against `pg_indexes` | Command counter, out-of-band inserts, discriminating fixtures, `pg_indexes` audit |
| Integration — price fidelity (rule 11) | The fixture **discriminates**: quoted price 100 with a 10 discount on list A, then the price moves to 130, the oferta is deactivated, the alicuota moves 21 → 10.5, and the artículo is renamed. Every one of `precio_unitario`, `descuento`, `total`, `id_lista_precio`, `id_oferta`, `id_alicuota_iva`, `porcentaje_iva`, `descripcion` asserted on `items_comprobante_venta`. `costo_unitario` asserted equal to **today's** `costo_nominal`, not the quoting-time one | Never a fixture where quoted and current agree: with equal values every mutant sourcing from any in-scope value passes green |
| Integration — expiry in the PV zone (rule 10) | `RelojFijo` at `2026-09-30T02:00:00Z` with the PV at `America/Argentina/Buenos_Aires`: local date is the **29th**, so a quote with `vencimiento = 2026-09-29` is **still convertible** and one with the 28th is not. The mirror at `+05:30`. Asserted through both the `409 presupuesto_vencido` and the listing's derived `Vencido` | Only a non-zero offset can see this class; a `Z` fixture cannot |
| Integration — the four races | convertir × convertir of one quote (one 201 + one 409, one burnt number, zero orphan writes); remitir × remitir of the same artículo and lot; remitir × checkout on the same artículo and lot (the fourth write site's own rendezvous, `stock/spec.md:197-204`'s form); facturar × anular-remito in **both** orders | Forced rendezvous, `ParametrosTests` precedent; both transactions must complete, never a deadlock |
| Integration — the fourth write site | One `movimientos_stock` row per line with `motivo = remito` **and** `id_remito` set; `stock` and (for lot-effective lines) `stock_lotes` updated in the same transaction; `id_lote` frozen on `items_remito`; a non-product line refused (400); an empty remito refused (400); `stock.cantidad == SUM(movimientos_stock.cantidad)` across a sequence that includes `remito` **and** its `anulacion` — the **nine-motivo** restatement | Real Postgres; the ledger read back row by row |
| Integration — FEFO parity | The same two-lot fixture run through the checkout and through `emitir` must pick the **same** lot; a lot-effective line with no positive balance resolves the sin-identificar lot in both; an explicit `idLote` is honoured in both | The `lotes-y-vencimientos` delta's *"byte-identical, only the subject widens"* made assertable |
| Integration — the consolidation | One **itemless** `TXR` whose total equals Σ of its remitos' frozen lines; **zero** `movimientos_stock` rows on emission **and** on annulment; the remitos land `facturado` with their link; a mixed-cliente / mixed-PV / already-invoiced / non-`emitido` set refused (409); a cuenta-corriente consolidation past the limit refused (400) with a **concurrent** sale raising the saldo between pre-check and commit | `SELECT count(*)` on `items_comprobante_venta` and `movimientos_stock` before/after, asserted as zero |
| Integration — the annulment coupling | Annulling a `TXR` returns its remitos to `emitido` with `id_comprobante_venta` cleared and reverses cuenta corriente; annulling a sale born from a quote leaves the quote **`convertido`** and the comprobante's `id_presupuesto_origen` intact, and a second conversion of that quote is refused (409) | One test per direction, translated domain codes |
| Integration — RLS | `SELECT` with another tenant's GUC ⇒ **0 rows** on all four new tables; `INSERT` with a foreign `id_tenant` ⇒ `42501` by SQLSTATE | `ways_app` (NOSUPERUSER NOBYPASSRLS), statement level (rule 5) |
| Integration — schema | Every CHECK and every `23505` family by raw insert asserting SQLSTATE **and** translated code; `pg_indexes` shows **exactly 30** new indexes and no unnamed EF-generated FK support index (including that the partial `ux_comprobantes_venta_presupuesto_origen` is the **only** index covering FK 23); `has-pending-model-changes` clean | The stage-15/16 index-count discipline, by definition not by name |
| Integration — read models | A **sibling** presupuesto/remito of the same tenant seeded on **every** listing/detail test with its own items (rule 12c), and on every replace-set test with rows that must remain intact; every positional field of `PresupuestoDetalle`/`RemitoDetalle`/`ComprobanteEmitido` read back once with pairwise-distinct values (rule 12b); a raw `UPDATE` desyncing `estado` to a sentinel must surface the sentinel (rule 12a); pagination with `fecha_emision` tied on every row ⇒ page 2 repeats and skips nothing; each filter with asymmetric seeds | Order asserted as a **sequence**, not a set |
| Integration — authorization | Vendedor ⇒ 200/201 on **every** route including `emitir`, `anular` and `facturacion` (no 403 anywhere); Root ⇒ 403; tenant B never sees tenant A's documents; the allowlist covers the eleven new non-GET routes | One test per role per route |
| Integration — reloj + offset (rule 10) | Everything under `RelojFijo`; `fecha_envio`/`fecha_salida` equal the fixed instant exactly; plus listing-boundary tests sending `desde`/`hasta` at the real client offset `-03:00` (never `Z`), asserting the rows **and** the displayed period — the raw-ADO UTC-normalization regression of PR #129 is only visible there | Midday-UTC fixtures for stability, offset fixtures for the boundary |
| Web (vitest) | Pure mappers; the POS renders the banner and a **read-only** cart under `?idPresupuesto=`, issues **no** price-resolution request, and posts **no** `lineas`; a non-convertible quote renders no "Convertir" action; a double click on `enviar`/`emitir`/`facturar` issues exactly one POST; a stale response is discarded (**stale promise resolved inside `act`**, rule 7); the `vencido` toggle disabled without a punto de venta; pager disabled at the edges | `web-descriptor-tests` + `react-async-state` |
| Exempt | Visual styling beyond testids — exemption registered, inherited from stages 12-16 | — |

## Mutation targets

`mutation-proof-tests`: name the clause, apply the mutation, watch the named test fail, revert,
record the evidence (applied → failing test → reverted → green) in the PR body. **60 numbered
targets, colocated with the slice that introduces the clause** — rows 38, 59 and 60 are grouped
families whose members are mutated one at a time, the stage-16 row-34 shape.

| # | Slice | Clause | Mutation | Test that MUST fail |
|---|---|---|---|---|
| 1 | 1 | `HabilitarRlsDeTenant("presupuestos")` / `("items_presupuesto")` | delete either | cross-tenant row count on `ways_app` + `42501` |
| 2 | 1 | `ck_presupuestos_envio_completo` | delete it | raw-insert `23514`, three directions |
| 3 | 1 | `ck_items_presupuesto_cantidad_positiva` | delete it | its raw-insert `23514` |
| 4 | 1 | `HasFilter("numero IS NOT NULL")` on `ux_presupuestos_numero` | delete the filter | two drafts (numero NULL) in one PV ⇒ spurious `23505` |
| 5 | 1 | `HasFilter("id_presupuesto_origen IS NOT NULL")` on index 29 | delete the filter | two ordinary sales (both NULL) ⇒ mass `23505` |
| 6 | 1 | Explicit `HasDatabaseName("ux_comprobantes_venta_presupuesto_origen")` | drop it | `pg_indexes` audit: an EF `IX_…` sibling appears and FK 23 has two covering indexes |
| 7 | 1 | The exact-name `ux_presupuestos_numero` branch **above** `ClasificarUnicidad` | move it below | translated code is `numero_duplicado`, not `numero_de_presupuesto_duplicado` (**4th** occurrence) |
| 8 | 1 | The exact-name `ux_comprobantes_venta_presupuesto_origen` branch | move it below | translated code is not `presupuesto_ya_convertido` |
| 9 | 1 | `MapEnum<EstadoPresupuesto>` in `WaysDbContextFactory` **and** `DependencyInjection` | delete either | that builder's path fails / `has-pending-model-changes` dirty |
| 10 | 1 | `Activo = false` for `PRE` in `TiposComprobanteBase` (**net 1b**) | flip to `true` | freshly-seeded database asserts `PRE` inactive |
| 11 | 1 | Data statement 1, `UPDATE tipos_comprobante SET activo = false WHERE codigo = 'PRE'` (**net 1**) | delete it | migrated-database `PRE` inactive test — independent of #10, neither masks the other |
| 12 | 2 | `WHERE estado = 'borrador'` in the draft `FOR UPDATE` | delete it | `PUT` on an `enviado` quote ⇒ expected 409 |
| 13 | 2 | `RemoveRange(itemsExistentes)` scoped by `IdPresupuesto` | widen the scope to the table | sibling-quote items must remain, asserted by exact count **and** identity (rule 12c) |
| 14 | 2 | Server-assigned `orden` 1..N in the replace-set | take `orden` from the request | `ux_items_presupuesto_orden` ⇒ `orden_de_item_duplicado` |
| 15 | 2 | `AsignarComprometidoAsync(…, "PRES")` | replace with `MAX(numero) + 1` | two concurrent `enviar` on one PV ⇒ same number / `23505` |
| 16 | 2 | The assigner call **outside** the `enviar` transaction | move it inside | nested-transaction failure / the burnt-number semantics test |
| 17 | 2 | `AND id_punto_venta = $pv` in the `enviar` `UPDATE` | delete it | the concurrent-`PUT`-moves-the-PV test: the number lands in the wrong series |
| 18 | 2 | `vencimiento >= hoy(zona del PV)` validation at `enviar` | delete it | a quote born already expired is refused (400) |
| 19 | 2 | `hoy := TimeZoneInfo.ConvertTime(reloj.Ahora, zona)` | replace with `reloj.Ahora.UtcDateTime` | the `-03:00` boundary test (rule 10) |
| 20 | 2 | `ReglaDePresupuestos.EstaVencido`'s `v < hoy` | change to `v <= hoy` | the "convertible **on** its expiry day" boundary test |
| 21 | 2 | `presupuesto_sin_items` guard in `enviar` | delete it | an empty quote draws a number |
| 22 | 2 | `ParametrosDeComando.Agregar` on `fecha_envio` | hand-built parameter without `ToUniversalTime()` | the `-03:00` offset test (a `Z` fixture cannot see it) |
| 23 | 3 | `\|\| !tipo.AfectaStock` at `:930` (**net 2**) | delete the clause | the out-of-band active `afecta_stock = false` type ⇒ expected 400 — **still red with `PRE` deactivated** |
| 24 | 3 | The synthesized resolution (`idPresupuestoOrigen is null ? ResolverAsync(…) : DesdePresupuesto(…)`) | always call the engine | the price-changed-after-quoting fidelity test |
| 25 | 3 | `precio_unitario` / `descuento` sourced from `items_presupuesto` | source from the engine | same test, per field |
| 26 | 3 | `id_alicuota_iva` / `porcentaje_iva` frozen from the quote | read today's `articulos`/`alicuotas_iva` | the alicuota-changed-after-quoting test |
| 27 | 3 | `id_lista_precio` / `id_oferta` frozen from the quote | read the customer's current list | the provenance test |
| 28 | 3 | `descripcion` frozen from the quote | read `articulo.Nombre` | the renamed-artículo test |
| 29 | 3 | `costo_unitario` from **today's** `costo_nominal` | freeze it from quoting time | the cost test (decision 4 of the proposal: the cost is at emission) |
| 30 | 3 | The totals-fidelity assertion (recomputed == the quote's header) | delete it | a raw `UPDATE` desyncing `presupuestos.total` must produce `409`, not a silently different sale (rule 12a) |
| 31 | 3 | `AND estado = 'enviado'` in `MarcarConvertidoAsync` | widen it | double conversion / converting a `borrador` or `anulado` quote |
| 32 | 3 | `AND vencimiento >= $hoy` in the same `UPDATE` | delete it | the expired-quote conversion test **and** its race form |
| 33 | 3 | `AND id_punto_venta = $pv` in the same `UPDATE` | delete it | the cross-punto-de-venta conversion test |
| 34 | 3 | `if (plan.IdPresupuestoOrigen is { } idP)` | call unconditionally | the zero-extra-statements command count |
| 35 | 3 | The call's **position 1.5** | move it after the cuenta-corriente loop | the convertir × convertir rendezvous: the loser must have written **nothing** (no comprobante, no stock, no CC) |
| 36 | 3 | `ExigirSinLineas` (400 `lineas_no_admitidas`) and the conflicting-`idCliente` refusal | accept either silently | their `dto-contract-honesty` tests |
| 37 | 3 | `id_presupuesto_origen` written on the comprobante | drop it | the `ComprobanteEmitido.IdPresupuestoOrigen` round-trip (rule 2) + the unique-index race |
| 38 | 4 | `HabilitarRlsDeTenant("remitos")` / `("items_remito")`; `ck_remitos_salida_completa`; `ck_remitos_facturacion`; the three `items_remito` CHECKs; `HasFilter` on `ux_remitos_numero`; the exact-name `ux_remitos_numero` (**5th** trap) and `ux_items_remito_orden` branches; `MapEnum<EstadoRemito>` in both builders; the named `ix_movimientos_stock_remito`; the guarded `TXR` `INSERT`; `TXR` with `AfectaStock = false` in the seed | delete/move one at a time | its own named test (42501 / raw-insert `23514` / spurious `23505` / translated code / `pg_indexes` = 30 / the `TXR`-unemittable test, which is #23's mirror) |
| 39 | 4 | `MotivoStock.Remito` declared **last** in the C# enum | insert it in the middle | every `motivo` round-trip: existing rows read back as the wrong value |
| 40 | 5 | The ascending `(id_articulo, id_lote NULLS FIRST)` order of the remito's stock loop | delete or reverse it | the remitir × checkout rendezvous ⇒ deadlock/timeout |
| 41 | 5 | The aggregate `stock` upsert **before** `stock_lotes` | swap them | the same rendezvous |
| 42 | 5 | `MotivoStock.Remito` and `id_remito` on the movement | write `Ajuste` / write NULL | the ledger-motivo assertion and the *"the movements of this remito"* read |
| 43 | 5 | The `EsProducto` refusal and the `remito_sin_items` guard | delete either | their 400 tests |
| 44 | 5 | `WHERE estado = 'borrador' AND id_punto_venta = $pv` in `emitir` | drop either conjunct | double `emitir` (409) / the wrong-series test |
| 45 | 5 | The anulación reading the **original** movements from the ledger | recompute from `items_remito` | the "the lot travels structurally" test: a partially-annulled/soft-deleted fixture diverges |
| 46 | 5 | `WHERE estado = 'emitido'` in the remito anulación | widen it | annulling a `facturado` remito (409) and double annulment |
| 47 | 5 | FEFO's UTC-naive `hoy` (parity with the checkout) | resolve it in the PV zone | the FEFO-parity test: write sites 1 and 4 must pick the **same** lot |
| 48 | 6 | `ORDER BY id_remito` ascending on `BloquearAscendenteAsync` | reverse it | the facturar × facturar rendezvous over overlapping sets ⇒ deadlock |
| 49 | 6 | The remitos lock taken **before** the comprobante INSERT and before `clientes` | move it after the CC loop | the facturar × anular-remito rendezvous |
| 50 | 6 | `AND estado = 'emitido' AND id_comprobante_venta IS NULL` + the `RETURNING` row-count == N check | delete either | the double-invoice race ⇒ exactly one 201 and one 409 |
| 51 | 6 | The same-cliente / same-PV / same-tenant agreement guards | drop any | its 400/409 test |
| 52 | 6 | The itemless construction and the absence of a stock loop | add items / add the loop | *"zero items"* and *"zero `movimientos_stock`"* on emission **and** on the `TXR` annulment (the phantom-restock proof) |
| 53 | 6 | The credit-limit backstop inside the consolidation transaction | delete it | the concurrent-CC-sale test (parity with `ServicioDeVentas.cs:901-908`) |
| 54 | 6 | `ExigirTurnoAbiertoBajoLockAsync` as statement 0 of the consolidation, **and its deliberate absence** in `EmitirAsync` | delete it / add it to the remito | the closed-turno 409 test / the *"a delivery leaves with no open till"* test (decision 13 of the proposal) |
| 55 | 6 | The `codigo` scalar subquery inside `MarcarAnuladoAsync`'s `RETURNING` | resolve the tipo with a separate `SELECT` | the zero-extra-statements count for an ordinary anulación |
| 56 | 6 | `if (codigoTipo == "TXR")` around the un-link call | call unconditionally | the same count |
| 57 | 6 | `DesligarAsync` clearing `estado` **and** `id_comprobante_venta` together | clear only one | `ck_remitos_facturacion` ⇒ `23514` |
| 58 | 6 | The un-link's **position 1.6** | move it after the CC loop | the anular-TXR × facturar rendezvous |
| 59 | 7-8 | Each `Where(p => p.Id == id)` / `Where(… == idCliente)`; `ThenByDescending(p => p.Id)`; each `if (filtro is { } x)`; the per-distinct-PV zone resolution of `Vencido`; the `idPuntoVenta` requirement of the `vencido` filter; every positional field of the three detail DTOs | delete one at a time | its own named test (sibling-seed identity; tied-`fecha` pagination; that filter's asymmetric-seed test; the `-03:00` `Vencido` test; the 400; the integral "every field with its truth" test) |
| 60 | 7-8 | `.RequireAuthorization(Politicas.OperacionDePos)` per group, with **nothing** stacked; the read-only POS branch under `?idPresupuesto=`; the skipped price-resolution effect; `key={idPresupuesto ?? 'libre'}`; the single-POST re-entrancy guards | delete one at a time | the Vendedor 200 matrix; the descriptor tests (no price request issued, no `lineas` posted, cart inputs disabled); the double-click test |
| — | — | **Non-regression**: the existing `VentasCheckoutTests` / `VentasAnulacionTests` / `VentasAtomicidadYConcurrenciaTests` suites | — | verify criterion: green and **unedited** |

## Slicing (8 PRs, stacked-to-main — the proposal's plan, ratified with two adjustments)

| # | Branch | Content | ~Lines | Test plan |
|---|---|---|---|---|
| 1 | `feat/stage17-slice1-schema-presupuestos` | `PresupuestosEtapa17` (type, 2 tables, 10 FKs, 2 CHECKs, 13 indexes, the `comprobantes_venta` ALTER + FK 23 + index 29, data statement 1, RLS last) + entities + `ReglaDePresupuestos` + EF configs + `MapEnum` in both builders + **the seed change** + **the 7 `ManejadorDeErrores` branches** + doc 10 | ~520 | RLS/`42501`; both CHECKs and the `23505` families by raw insert with translated codes; the 4th `_numero` trap; `pg_indexes`; Domain truth table |
| 2 | `feat/stage17-slice2-presupuestos-abm` | `ServicioDePresupuestos`: draft replace-set under `FOR UPDATE`, `POST/PUT/GET/list`, `enviar` with `'PRES'`, `anular`, the derived `vencido` in the PV zone | ~480 | Two concurrent `enviar`; `borrador`-only mutation; the `-03:00` boundary; the expiry-day boundary; empty-quote refusal; sibling-seed replace-set |
| 3 | `feat/stage17-slice3-guard-y-conversion` | The resolver clause + its two binding mutation tests; `/para-venta`; the decide-phase snapshot branch + `MaterializarItemsDesdePresupuesto`; `EscriturasDePresupuesto`; the guarded call at 1.5; `ComprobanteEmitido.IdPresupuestoOrigen` | ~520 | Zero extra statements; frozen-price fidelity (discriminating fixture); expired conversion at `-03:00`; convertir × convertir; the totals-fidelity 409 |
| 4 | `feat/stage17-slice4-schema-remitos` | `RemitosEtapa17` (`ALTER TYPE` isolated, type, 2 tables, 12 FKs, 5 CHECKs, 15 indexes, the `movimientos_stock` ALTER + FK 24 + index 30, data statement 2, RLS last) + entities + configs + `MotivoStock.Remito` + **the 5 `ManejadorDeErrores` branches** + doc 10 | ~500 | Same shape as slice 1; the 5th `_numero` trap; `pg_indexes` = 30 cumulative; the `TXR` seed/data pair |
| 5 | `feat/stage17-slice5-remito-write-site` | `ServicioDeRemitos`: draft, `emitir` (numbering, FEFO, **the fourth write site** with its independent lock order), `anular` | ~540 | The remitir × checkout rendezvous; remitir × remitir; FEFO parity; the nine-motivo consistency; the ledger-sourced reversal |
| 6 | `feat/stage17-slice6-consolidacion` | `ServicioDeFacturacionDeRemitos` + `EscriturasDeRemito` + the widened `RETURNING` and the guarded un-link in `AnularAsync` | ~500 | Zero items / zero stock on both directions; facturar × facturar; facturar × anular-remito; anular-TXR × facturar; the credit-limit backstop; zero extra statements on an ordinary anulación |
| 7 | `feat/stage17-slice7-web-presupuestos` | `Presupuestos.tsx` + `Presupuesto.tsx` + client + routes + **the `Pos.tsx` conversion branch** | ~470 | Descriptor tests; no price request under `?idPresupuesto=`; no `lineas` posted; stale inside `act`; single POST on double click |
| 8 | `feat/stage17-slice8-web-remitos` | `Remitos.tsx` + `Remito.tsx` + `FacturarRemitos.tsx` + client + routes + **the doc-10 "Estado (Etapa 17)" headers closed to "implementada — etapa completa (PRs #…)"** | ~470 | Descriptor tests; the multi-select reducer; the disabled-action matrix by estado |

Total ≈ **4 000**. Merge order `1 → 2 → 3 → 4 → 5 → 6 → 7 → 8`. Slice 1 blocks 2, 3 and 7; slice 4
blocks 5, 6 and 8; 3 depends on 2; 6 depends on 5. **Slices 1-3 and 4-6 are independent tracks** and
may interleave if the chain allows it.

**Two adjustments to the proposal's table.** (a) The `ManejadorDeErrores` branches move into the two
**schema** slices, split 7/5 (decision 18 — the stage-16 decision-10 precedent). (b) The doc-10
*"Estado (Etapa 17)"* headers are **opened** in slices 1 and 4 and **closed** in slice 8, which is the
last work unit of the stage — the programme's new closing rule, so the header never claims
*"implementada"* while a write path is still unmerged.

**Decision needed before apply: No** · **Chained PRs recommended: Yes** · **400-line budget risk:
High** (`delivery_strategy: auto-chain`, `chain_strategy: stacked-to-main`, one `judgment-day` round
per slice). **All eight** slices sit above the cap on the estimate alone, and stages 13-16 came in
1.5-3× their naive estimate; a **10-12 PR outturn is the expected case**. `size:exception`
anticipated: **No** — the four pre-authorized splits absorb it.

**Pre-approved degradation**, in priority order (the proposal's, ratified):

1. **Slice 1 overflows** — `1a` (type + both presupuesto tables + entities + configs + the seed
   change + data statement 1 + RLS/CHECK tests) and `1b` (the `comprobantes_venta` ALTER + index 29 +
   the seven backstops + doc 10). **One migration per document** is the invariant that must not
   degrade.
2. **Slice 4 overflows** — `4a` (`ALTER TYPE` + type + both remito tables) and `4b` (the
   `movimientos_stock` ALTER + the `TXR` data statement + the five backstops + doc 10).
3. **Slice 6 overflows** — `6a` (the consolidation itself) and `6b` (the widened `RETURNING`, the
   un-link and its races).
4. **Slices 7/8 overflow** — ship list + detail + draft and drop the POS banner (leaving the plain
   conversion link) and `FacturarRemitos.tsx`'s bulk selection. A documented reduction, never silent.
5. **Never degraded**: the two `PRE` nets and their independent mutation tests, the frozen-price
   fidelity assertion, the fourth write site's lock order and its rendezvous test, and the
   zero-items/zero-stock assertions of the consolidation. A phantom sale, a silent reprice or a
   double decrement is worse than no stage at all — those are split, never trimmed.

## Binding verify criteria

1. Exactly **two** migrations, `PresupuestosEtapa17` and `RemitosEtapa17`, with the DDL of gate
   §A-§I and nothing else; **30 new indexes verified by definition against `pg_indexes`** (not by
   name), including that the partial `ux_comprobantes_venta_presupuesto_origen` is the only index
   covering FK 23 and that no EF-generated sibling exists; `has-pending-model-changes` clean. Any
   extra DDL reopens the gate.
2. Exactly **three** data statements + the `TiposComprobanteBase` change; the only
   `ALTER TYPE … ADD VALUE` is `motivo_stock` `'remito'`, in `RemitosEtapa17`, named by no `Sql()` of
   that migration.
3. The diff of `ServicioDeVentas.cs` is bounded to: **one clause** in `ResolverTipoComprobanteAsync`,
   the decide-phase snapshot branch plus the new private materializer, **one** guarded call inside
   `EjecutarTransaccionAsync`, and **one** guarded call in `EjecutarAnulacionAsync` behind the widened
   `RETURNING`. The pinned statement order and both loops are byte-identical; a sale without
   `idPresupuestoOrigen` and an anulación of a non-`TXR` each emit the pre-stage command count
   exactly; the existing ventas suites are green **and do not appear edited**.
4. Both `PRE` nets proven **independently**: removing either one alone still fails the suite.
5. The `stock` spec says **four** write sites, names `ServicioDeRemitos`, and its fourth site ships
   its **own** concurrency test; the `cantidad` invariant is restated over **nine** motivos.
6. `Politicas.cs` unchanged; `AsignadorDeNumeroComprobante.cs` unchanged; no file under
   `src/Ways.Application/Compras/` or `src/Ways.Application/Stock/` in the diff.
7. Mutation evidence recorded in the PR body for **every** row of the table above belonging to that
   slice.
8. Domain / Application / Integration / vitest suites green; colocated tests for every new pure web
   helper and every new screen descriptor (`web-descriptor-tests`).
9. doc 10 carries the four tables, both new columns, the `PRE`/`TXR` notes and the
   *"Estado (Etapa 17)"* annotations **closed** in the last slice.

## Threat Matrix

N/A — this stage touches no routing, shell command, subprocess, VCS/PR automation, executable-file
classification or process integration. Its real risk surfaces (tenant isolation, authorization, lock
order, a new stock writer, price-snapshot fidelity, timezone-resolved expiry) are covered by the
mutation-target table, which **is** binding.

## Open Questions / tensions with the proposal

- [ ] **T1 — `AnularAsync` does NOT revert `convertido → enviado`, and the widened `RETURNING`
      carries the tipo's `codigo`, not `id_presupuesto_origen`.** Proposal decision 9 says the quote
      stays `convertido`; this design adds the structural reason the proposal does not draw:
      reverting would also require nulling `id_presupuesto_origen` on the annulled comprobante, or
      `ux_comprobantes_venta_presupuesto_origen` refuses the second sale — and that nulling erases
      the record of what happened. The expiry argument alone would leave the door open for a
      still-valid quote; this one closes it.
- [ ] **T2 — `MaterializarItems` cannot be reused for a conversion.** It reads `IdAlicuotaIva` from
      today's `articulos` (`:1049`) and `porcentaje_iva` from today's `alicuotas_iva` (`:103-105`),
      both of which the proposal freezes. A spec sentence saying *"the same materializer runs"* is
      false; this design ships a second private materializer and keeps `CalculadorDeTotales` as the
      single arithmetic authority, bound by mutation target 30.
- [ ] **T3 — one `id_lista_precio` per quote is an invariant, not a schema fact.**
      `items_presupuesto` carries it per line, but `MaterializarItems`' contract takes one scalar for
      the whole document and a quote is priced in one resolution against one list. The conversion
      sources it from the quote's items and asserts they agree (`InvalidOperationException`, a broken
      invariant, never a business error). If `sdd-spec` states a per-line list, the two disagree.
- [ ] **T4 — two `hoy` values, deliberately.** FEFO keeps the checkout's UTC-naive `hoy`
      (`ServicioDeVentas.cs:163`); the expiry uses the PV's `zona_horaria`
      (`lotes-y-vencimientos/spec.md:318-320`). The `lotes-y-vencimientos` delta must say the FEFO
      rule is byte-identical **including its interim UTC `hoy`**, or write site 4 will silently pick
      a different lot than write site 1 for the same data (mutation target 47).
- [ ] **T5 — the `vencido` listing filter requires `idPuntoVenta`** (400). *"Hoy"* has no meaning
      without a zone and a page can span punto de venta. The proposal does not say this; the derived
      `Vencido` field is resolved per **distinct** PV of the page.
- [ ] **T6 — a conversion race loser burns a `TX` number**, because the assigner commits before the
      transaction opens (`:278-280`). Stage 16 recorded the same residue for an OC series; here the
      burnt series is a **sale** series. Accepted (it is not fiscal), but the proposal's success
      criteria do not mention it and `sdd-spec` may state *"no number is consumed on a failed
      conversion"*.
- [ ] **T7 — `ComprobanteEmitido` must gain `IdPresupuestoOrigen`.** The proposal says the checkout
      changes with "no response shape changes"; a request-only field cannot satisfy
      `dto-contract-honesty` rule 2 (the round-trip assertion). Exactly stage-16 T7. One nullable
      field, nothing else.
- [ ] **T8 — the remito's annulment has NO negative-balance guard, and that is correct.** The
      prompt's *"inversos never-negative"* describes `ServicioDeCompras.cs:632-658`, which exists
      because a compra **adds** and its reversal **subtracts**. A remito decrements and its reversal
      adds, so the guard would be dead code; `ServicioDeVentas.cs:1130-1135` documents the same
      posture for a sale.
- [ ] **T9 — the consolidation must re-implement the checkout's credit-limit backstop**
      (`:901-908`). *"Takes payments and cuenta corriente exactly like a sale"* is not enough: the
      `ValidadorDePagos` pre-check runs outside the transaction, so without the in-transaction
      backstop a consolidation on cuenta corriente can exceed a limit an ordinary sale enforces.
- [ ] **T10 — the total lock order needs one new clause to stay honest**: *a new-row `INSERT` is not
      a position in the order; only locks on rows that already exist are.* Without it, the
      consolidation (`remitos` → insert a comprobante) and the `TXR` annulment (`comprobantes_venta`
      → `remitos`) read as a cycle. They are not, but the reason must be written down.
- [ ] **T11 — the `TXR` carries no `direccion_entrega`.** The goods left under the remitos, which
      each carry their own; copying one onto the consolidation would be a lie when N > 1 and a
      denormalized copy when N = 1. The printed detail joins the remitos. The proposal does not say.
- [ ] **T12 — `emitir` refuses a remito with zero items** (`400 remito_sin_items`), mirroring
      `compra_sin_items` and stage-16 decision 7. The proposal does not forbid it; without the guard
      a numbered delivery that moved nothing exists, and *"every remito line moves stock"* becomes
      vacuous.
- [ ] **Deferred, unchanged**: stock reservation by a quote, repricing at conversion, editing a
      converted quote's lines, the fiscal consolidation type (Etapa 19), `presupuesto → remito`
      directly, partial conversion, partial invoicing of a remito, rentabilidad over the remito
      circuit, auditing these transitions in `auditoria`, and printing/emailing either document — all
      refused in writing by the proposal with their reopen conditions.
