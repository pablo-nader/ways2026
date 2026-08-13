using Ways.Domain.Compras;

namespace Ways.Application.Compras;

/// <summary>Una línea del cuerpo de <c>POST/PUT /api/compras</c> (design: Interfaces/Contracts)
/// — ningún request lleva <c>cantidad</c>, <c>total</c> ni <c>delta</c> (design decisión 3):
/// <c>CalculadorDeCompra</c> deriva todo eso server-side. <see cref="CodigoLote"/>/<see
/// cref="FechaVencimiento"/> (etapa 12, slice 5) son input crudo de recepción — se persisten tal
/// cual mientras la compra es borrador y solo se resuelven contra <c>lotes</c> al confirmar
/// (design: Write site 2 — "nothing is resolved at draft time").</summary>
public sealed record LineaDeCompraSolicitada(
    int IdArticulo,
    string Descripcion,
    decimal Unidades,
    decimal? Bultos,
    decimal? UnidadesPorBulto,
    decimal CostoUnitario,
    decimal Descuento,
    int IdAlicuotaIva,
    bool ActualizaCosto = true,
    string? CodigoLote = null,
    DateOnly? FechaVencimiento = null);

/// <summary>Cuerpo de <c>POST /api/compras</c> (crea un borrador) y de <c>PUT
/// /api/compras/{id}</c> (design decisión 2: replace-set completo del header + los items — un
/// PUT reemplaza <see cref="Items"/> entero, nunca un CRUD incremental por item).</summary>
public sealed record SolicitudDeCompra(
    int IdProveedor,
    int IdTipoComprobante,
    int IdPuntoVenta,
    string? NumeroExterno,
    DateOnly? FechaComprobante,
    string? Observaciones,
    IReadOnlyList<LineaDeCompraSolicitada> Items);

/// <summary>Un item ya persistido, con su <c>precioSugerido</c> (design: API Surface — "Header +
/// items + precioSugerido per item"). Sin <c>unidades</c> propio: solo <see cref="Cantidad"/>
/// (ya derivada) y los dos inputs de auditoría (<see cref="Bultos"/>/<see
/// cref="UnidadesPorBulto"/>) se persisten (design: Table Shapes — B). <see cref="CodigoLote"/>/
/// <see cref="FechaVencimiento"/> son el input crudo de borrador; <see cref="IdLote"/> es el lote
/// resuelto (get-or-create), <c>NULL</c> mientras la compra es borrador y para artículos que no
/// controlan lote (etapa 12, slice 5).</summary>
public sealed record ItemDeCompra(
    int Orden,
    int IdArticulo,
    string Descripcion,
    decimal Cantidad,
    decimal? Bultos,
    decimal? UnidadesPorBulto,
    decimal CostoUnitario,
    decimal Descuento,
    int IdAlicuotaIva,
    decimal PorcentajeIva,
    decimal Total,
    bool ActualizaCosto,
    decimal? PrecioSugerido,
    string? CodigoLote,
    DateOnly? FechaVencimiento,
    int? IdLote);

/// <summary>Detalle completo de una compra — respuesta de <c>GET /api/compras/{id}</c>,
/// <c>POST /api/compras</c>, <c>PUT /api/compras/{id}</c>, <c>POST …/confirmar</c>.</summary>
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
    IReadOnlyList<ItemDeCompra> Items);

/// <summary>Fila de <c>GET /api/compras</c> — shape reducido, mismo criterio que
/// <c>ComprobanteListado</c>/<c>GastoListado</c>. <see cref="EstadoPago"/> lo resuelve
/// <c>ServicioDeSaldoDeProveedor</c> (Slice 4) — <c>null</c> en esta slice.</summary>
public sealed record CompraListada(
    int Id,
    int IdProveedor,
    int IdTipoComprobante,
    string? NumeroExterno,
    EstadoCompra Estado,
    DateTimeOffset? FechaRecepcion,
    decimal Total);

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
/// confirmar) no aborta las demás.</summary>
public sealed record ResultadoAplicarPrecio(int IdArticulo, bool Aplicado, decimal? Precio, string? Error);
