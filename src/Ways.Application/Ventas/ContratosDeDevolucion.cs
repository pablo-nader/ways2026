using Ways.Domain.Ventas;

namespace Ways.Application.Ventas;

/// <summary>El ticket de venta que se quiere devolver, con lo que queda por devolver de cada artículo
/// (<c>GET /api/ventas/devolvibles</c>). <see cref="Devolvible"/> dice si el ticket admite una devolución; si no,
/// <see cref="Motivo"/> es <c>anulado</c>, <c>con_factura_fiscal</c>, <c>sin_unidades_por_devolver</c> o <c>no_es_un_ticket_de_venta</c>.
/// Las <see cref="Lineas"/> se informan siempre, también en un ticket que no se puede devolver.</summary>
public sealed record TicketDevolvible(
    int Id,
    long Numero,
    string NumeroVisible,
    DateTimeOffset Fecha,
    int IdPuntoVenta,
    int IdCliente,
    string? NombreCliente,
    decimal Total,
    bool Devolvible,
    string? Motivo,
    IReadOnlyList<LineaDevolvible> Lineas);

/// <summary>Un artículo del ticket (en un artículo por importe, un artículo por importe vendido). Para devolverlo, la línea de la nota de
/// crédito lleva <see cref="IdArticulo"/>, la cantidad (hasta <see cref="CantidadDevolvible"/>) y, si <see cref="Importe"/> no es
/// nulo, ese importe. <see cref="ImporteDeLoRestante"/> es lo que se acredita por TODAS las unidades pendientes y
/// <see cref="ImporteUnitario"/> el valor por unidad del primer item con unidades pendientes (informativo): el importe de la
/// devolución lo fija el servidor al emitirla, por lo que se cobró en el ticket (descuentos, ofertas, ajuste y combos incluidos).</summary>
public sealed record LineaDevolvible(
    int IdArticulo,
    string Descripcion,
    string? CodigoBarra,
    decimal? Importe,
    decimal CantidadVendida,
    decimal CantidadDevuelta,
    decimal CantidadDevolvible,
    decimal ImporteUnitario,
    decimal ImporteDeLoRestante);

/// <summary>Lo que el POS manda para saber cuánto vale una devolución antes de emitirla: el punto de venta, el ticket de origen y las líneas
/// que emitiría la nota de crédito (mismo contenido que <see cref="SolicitudDeVenta.Lineas"/>).</summary>
public sealed record SolicitudDePrevisualizacionDeDevolucion(
    int IdPuntoVenta, int IdComprobanteAsociado, IReadOnlyList<LineaDeVenta>? Lineas);

/// <summary>El crédito de una línea de la devolución (positivo), en el orden en que se mandó.</summary>
public sealed record LineaPrevisualizadaDeDevolucion(int IdArticulo, decimal Cantidad, decimal Credito);

/// <summary>Lo que acreditaría la devolución: <see cref="Reintegro"/> es el efectivo que la nota de crédito tiene que llevar como pago
/// (exactamente su total). Es una lectura sin lock: la emisión lo re-juzga y puede responder distinto si otra devolución se coló en medio.</summary>
public sealed record PrevisualizacionDeDevolucion(IReadOnlyList<LineaPrevisualizadaDeDevolucion> Lineas, decimal Reintegro);
