using Ways.Domain.Compras;

namespace Ways.Application.Compras;

/// <summary>Una línea del cuerpo de <c>POST/PUT /api/compras</c> (design: Interfaces/Contracts)
/// — ningún request lleva <c>cantidad</c>, <c>total</c> ni <c>delta</c> (design decisión 3):
/// <c>CalculadorDeCompra</c> deriva todo eso server-side. <see cref="CodigoLote"/>/<see
/// cref="FechaVencimiento"/> (etapa 12, slice 5) son input crudo de recepción — se persisten tal
/// cual mientras la compra es borrador y solo se resuelven contra <c>lotes</c> al confirmar
/// (design: Write site 2 — "nothing is resolved at draft time").
///
/// <see cref="IdArticulo"/> nulo declara una línea por concepto: <see cref="Descripcion"/> es
/// obligatoria y la línea no admite lote, bultos ni <see cref="ActualizaCosto"/> verdadero (se
/// rechaza con 400, nunca se descarta en silencio). <see cref="ActualizaCosto"/> nulo significa
/// "el default de la línea": <c>true</c> para un artículo, <c>false</c> para un concepto.
///
/// <see cref="CodigoProveedor"/> es el código que el proveedor imprimió para la línea: se recorta,
/// vacío equivale a ninguno y más de 50 caracteres es 400. Se guarda en toda línea que lo trae; al
/// confirmar, una línea con artículo lo asocia a ese artículo y al proveedor de la compra si el
/// código está libre. Un concepto nunca asocia.</summary>
public sealed record LineaDeCompraSolicitada(
    int? IdArticulo,
    string Descripcion,
    decimal Unidades,
    decimal? Bultos,
    decimal? UnidadesPorBulto,
    decimal CostoUnitario,
    decimal Descuento,
    int IdAlicuotaIva,
    bool? ActualizaCosto = null,
    string? CodigoLote = null,
    DateOnly? FechaVencimiento = null,
    string? CodigoProveedor = null);

/// <summary>El IVA que el proveedor imprimió para una alícuota: cuando difiere del calculado por
/// redondeo, gana el impreso mientras no se aleje más de <see cref="CalculadorDeCompra.
/// ToleranciaDeIvaImpreso"/>. Solo tiene sentido en un comprobante que discrimina IVA.</summary>
public sealed record IvaImpresoSolicitado(int IdAlicuotaIva, decimal Iva);

/// <summary>Una percepción tal como la imprimió el proveedor (<c>iibb</c> o <c>iva</c>): el
/// <see cref="Importe"/> es lo que dice la factura y suma al total; <see cref="BaseImponible"/> y
/// <see cref="Alicuota"/> son informativas y no se recalculan. Una por tipo y comprobante, solo en
/// un tipo que registra libro IVA (la de IVA, además, solo si el comprobante discrimina IVA).</summary>
public sealed record PercepcionSolicitada(string Tipo, decimal BaseImponible, decimal Alicuota, decimal Importe);

/// <summary>Cuerpo de <c>POST /api/compras</c> (crea un borrador) y de <c>PUT
/// /api/compras/{id}</c> (design decisión 2: replace-set completo del header + los items — un
/// PUT reemplaza <see cref="Items"/> entero, nunca un CRUD incremental por item).
///
/// <see cref="IdOrdenCompra"/> — stage-16-ordenes-de-compra, Slice 3 (design: Interfaces/
/// Contracts; spec comprobantes-compra: "A Comprobante Compra MAY Carry A Linked Orden De
/// Compra"). Al final del record, con default <c>null</c>, para no romper ningún call site
/// posicional existente (`dto-contract-honesty` regla 3 — el campo se traza hasta
/// <c>ServicioDeCompras.ExigirOrdenLigableAsync</c>/<c>ComprobanteCompra.IdOrdenCompra</c>, nunca
/// solo declarado). Seteable/cambiable solo mientras el comprobante es <c>borrador</c>; congelado
/// después (spec: "The link is frozen once the compra is confirmed").
///
/// <see cref="DiscriminaIva"/> nulo toma el valor del tipo. En una factura (tipo que registra libro
/// IVA) lo fija el tipo y pedir lo contrario es 400; en un remito o comprobante no fiscal lo elige
/// quien carga el documento. <see cref="IvaImpreso"/> es el override de redondeo por alícuota.
///
/// <see cref="PreciosIncluyenIva"/> declara que el costo unitario tipeado ya trae el IVA (modo
/// "precio final"): solo vale en un comprobante que discrimina IVA y pedirlo en otro es 400, nunca
/// se descarta. <see cref="Percepciones"/> reemplaza el conjunto completo del borrador; sin ellas
/// (<c>null</c> o vacío) el comprobante queda sin percepciones.</summary>
public sealed record SolicitudDeCompra(
    int IdProveedor,
    int IdTipoComprobante,
    int IdPuntoVenta,
    string? NumeroExterno,
    DateOnly? FechaComprobante,
    string? Observaciones,
    IReadOnlyList<LineaDeCompraSolicitada> Items,
    int? IdOrdenCompra = null,
    bool? DiscriminaIva = null,
    IReadOnlyList<IvaImpresoSolicitado>? IvaImpreso = null,
    bool PreciosIncluyenIva = false,
    IReadOnlyList<PercepcionSolicitada>? Percepciones = null);

/// <summary>Un item ya persistido, con su <c>precioSugerido</c> (design: API Surface — "Header +
/// items + precioSugerido per item"). Sin <c>unidades</c> propio: solo <see cref="Cantidad"/>
/// (ya derivada) y los dos inputs de auditoría (<see cref="Bultos"/>/<see
/// cref="UnidadesPorBulto"/>) se persisten (design: Table Shapes — B). <see cref="CodigoLote"/>/
/// <see cref="FechaVencimiento"/> son el input crudo de borrador; <see cref="IdLote"/> es el lote
/// resuelto (get-or-create), <c>NULL</c> mientras la compra es borrador y para artículos que no
/// controlan lote (etapa 12, slice 5). <see cref="CostoUnitario"/>, <see cref="Descuento"/>,
/// <see cref="Total"/> y <see cref="PrecioSugerido"/> son <c>null</c> para el rol vendedor, que
/// no ve el costo de los artículos. <see cref="IdArticulo"/> nulo es una línea por concepto.
/// <see cref="CodigoProveedor"/> es el código impreso por el proveedor tal como se guardó, haya
/// quedado asociado o no a un artículo.</summary>
public sealed record ItemDeCompra(
    int Orden,
    int? IdArticulo,
    string Descripcion,
    decimal Cantidad,
    decimal? Bultos,
    decimal? UnidadesPorBulto,
    decimal? CostoUnitario,
    decimal? Descuento,
    int IdAlicuotaIva,
    decimal PorcentajeIva,
    decimal? Total,
    bool ActualizaCosto,
    decimal? PrecioSugerido,
    string? CodigoLote,
    DateOnly? FechaVencimiento,
    int? IdLote,
    string? CodigoProveedor);

/// <summary>Una fila del desglose de IVA de una compra que discrimina IVA: neto gravado e IVA de
/// una alícuota. Exento y no gravado salen con IVA cero. <see cref="Neto"/> e <see cref="Iva"/> son
/// <c>null</c> para el rol vendedor, igual que el total de cada ítem: el desglose por alícuota
/// reconstruye importes de línea. Los totales del encabezado se conservan.</summary>
public sealed record AlicuotaDeCompra(int IdAlicuotaIva, decimal Porcentaje, decimal? Neto, decimal? Iva);

/// <summary>Una percepción persistida. <see cref="BaseImponible"/> e <see cref="Importe"/> son
/// <c>null</c> para el rol vendedor, igual que el desglose de IVA: reconstruyen importes del
/// comprobante. El tipo y la alícuota se conservan.</summary>
public sealed record PercepcionDeCompraDetalle(string Tipo, decimal Alicuota, decimal? BaseImponible, decimal? Importe);

/// <summary>Detalle completo de una compra — respuesta de <c>GET /api/compras/{id}</c>,
/// <c>POST /api/compras</c>, <c>PUT /api/compras/{id}</c>, <c>POST …/confirmar</c>.
///
/// <see cref="IdOrdenCompra"/> — stage-16-ordenes-de-compra, Slice 3 (conflicto #4 de tasks.md,
/// `state.yaml` OD8/T7): corrige el "no response shape changes" original del proposal bajo
/// `dto-contract-honesty` regla 2 — un campo request-only no puede satisfacer la aserción de
/// round-trip (task 3.16).
///
/// <see cref="Pagado"/> y <see cref="SaldoPendiente"/> salen de <c>LectorDePagadoPorCompra</c> y de
/// <c>ReglaDePagoDeCompra</c>; son datos de encabezado (como <see cref="Total"/>), no de costo, así
/// que los ve también el vendedor. El saldo pendiente es cero fuera de una compra confirmada.</summary>
public sealed record CompraDetalle(
    int Id,
    int IdProveedor,
    int IdTipoComprobante,
    int IdPuntoVenta,
    string? NumeroExterno,
    DateOnly? FechaComprobante,
    DateTimeOffset? FechaRecepcion,
    decimal Subtotal,
    decimal DescuentoTotal,
    decimal? IvaTotal,
    decimal Total,
    string? Observaciones,
    EstadoCompra Estado,
    IReadOnlyList<ItemDeCompra> Items,
    int? IdOrdenCompra,
    bool DiscriminaIva,
    IReadOnlyList<AlicuotaDeCompra> Alicuotas,
    bool PreciosIncluyenIva,
    IReadOnlyList<PercepcionDeCompraDetalle> Percepciones,
    decimal Pagado,
    decimal SaldoPendiente);

/// <summary>Fila de <c>GET /api/compras</c> — shape reducido, mismo criterio que
/// <c>ComprobanteListado</c>/<c>GastoListado</c>. <see cref="EstadoPago"/> lo resuelve
/// <c>ServicioDeSaldoDeProveedor</c> (Slice 4) — <c>null</c> en esta slice. <see cref="SaldoPendiente"/>
/// es lo que falta pagar de la compra (cero si no está confirmada): el selector de "vincular gasto
/// a compra" lo muestra para no ofrecer una compra ya saldada como si debiera algo.</summary>
public sealed record CompraListada(
    int Id,
    int IdProveedor,
    int IdTipoComprobante,
    string? NumeroExterno,
    EstadoCompra Estado,
    DateTimeOffset? FechaRecepcion,
    decimal Total,
    decimal SaldoPendiente);

/// <summary>Página de resultados de <c>GET /api/compras</c> — mismo shape que
/// <c>PaginaDeVentas</c>/<c>PaginaDeGastos</c>.</summary>
public sealed record PaginaDeCompras(IReadOnlyList<CompraListada> Items, int Total, int Pagina, int Tamanio);

/// <summary>Respuesta de <c>POST /api/compras/{id}/anular</c> — <see cref="GastosLigados"/> es
/// la regla invertida (design decisión 6): la anulación NUNCA bloquea por gastos ligados, solo
/// REPORTA cuántos pagos quedaron colgados de la compra anulada.</summary>
public sealed record ResultadoAnulacion(CompraDetalle Compra, int GastosLigados);

/// <summary>Cuerpo de <c>POST /api/compras/{id}/precios</c> (design decisión 8) — la lista de
/// precios es siempre explícita: una compra no tiene una lista propia asociada.</summary>
public sealed record SolicitudDeAplicarPrecios(int IdListaPrecio, bool ConfirmarReemplazo = false);

/// <summary>Resultado por línea de aplicar <c>precio_sugerido</c> — partial success es el
/// contrato honesto (design decisión 8): una línea rechazada (p.ej. un precio pendiente sin
/// confirmar) no aborta las demás. <see cref="Orden"/> identifica la línea (su <c>orden</c> en la
/// compra, único dentro de ella); <see cref="IdArticulo"/> por sí solo puede repetirse entre líneas.
/// <see cref="Error"/> es el motivo por el que la línea no se aplicó: un rechazo, o que la supera otra
/// línea de la misma familia (doc 10 §3), de la que se intenta aplicar el precio a toda la familia — en
/// ese caso la línea no se intentó y su motivo nombra a la que la supera; si esa otra línea se aplicó lo
/// dice su propio resultado.</summary>
public sealed record ResultadoAplicarPrecio(int Orden, int IdArticulo, bool Aplicado, decimal? Precio, string? Error);
