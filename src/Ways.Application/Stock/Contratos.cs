namespace Ways.Application.Stock;

/// <summary>
/// Cuerpo de <c>POST /api/stock/ajustes</c> (design: API Surface; spec: stock / Manual Ajuste
/// Path Is Admin-Only). <see cref="Cantidad"/> es con signo (positiva carga, negativa descarga)
/// y nunca cero (<c>ck_movimientos_stock_cantidad_no_cero</c>). Sin campo de empleado, mismo
/// criterio que <c>Ways.Application.Ventas.SolicitudDeVenta</c>: <c>id_empleado</c> siempre sale
/// del actor autenticado.
/// </summary>
public sealed record SolicitudDeAjusteDeStock(int IdPuntoVenta, int IdArticulo, decimal Cantidad, string? Observaciones);

/// <summary>
/// Balance de <c>GET /api/stock</c> (design: API Surface — "balance for the POS badge").
/// <see cref="Cantidad"/> es <c>0</c> mientras no exista todavía una fila de <c>stock</c> para el
/// par (creación perezosa, mismo criterio que <c>numeraciones_comprobante</c>).
/// </summary>
public sealed record StockActual(int IdPuntoVenta, int IdArticulo, decimal Cantidad);
