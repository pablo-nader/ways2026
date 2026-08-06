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

/// <summary>
/// Cuerpo de <c>POST /api/stock/transferencias</c> (stage-8-compras-transferencias-inventario,
/// Slice 3; design: API Surface; Interfaces/Contracts; decisión 9). <see cref="Observaciones"/>
/// es obligatoria, mismo criterio que <see cref="SolicitudDeAjusteDeStock"/>. Cada
/// <see cref="LineaDeTransferencia"/> lleva una cantidad siempre POSITIVA — el signo por punto de
/// venta (origen negativo, destino positivo) lo decide el servidor, nunca el cliente.
/// </summary>
public sealed record SolicitudDeTransferencia(
    int IdPuntoVentaOrigen, int IdPuntoVentaDestino, string Observaciones,
    IReadOnlyList<LineaDeTransferencia> Lineas);

public sealed record LineaDeTransferencia(int IdArticulo, decimal Cantidad);

/// <summary>Resultado de una transferencia: el stock resultante de cada artículo en AMBOS puntos
/// de venta tras la transacción (design: Transactions — TRANSFERENCIA).</summary>
public sealed record ResultadoTransferencia(
    int IdPuntoVentaOrigen, int IdPuntoVentaDestino, IReadOnlyList<LineaTransferida> Lineas);

public sealed record LineaTransferida(int IdArticulo, decimal CantidadOrigen, decimal CantidadDestino);

/// <summary>
/// Cuerpo de <c>POST /api/stock/conteos</c> (stage-8-compras-transferencias-inventario, Slice 3;
/// design: API Surface; Interfaces/Contracts; decisión 10). <see cref="Contada"/> es el TOTAL
/// físicamente contado — nunca un delta (spec: conteo-de-inventario / Conteo Input Is The Counted
/// Total, Never A Delta): el servidor deriva el ajuste bajo el lock de la fila de <c>stock</c>.
/// </summary>
public sealed record SolicitudDeConteo(int IdPuntoVenta, int IdArticulo, decimal Contada, string Observaciones);
