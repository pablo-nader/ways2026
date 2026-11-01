using Ways.Domain.Ventas;

namespace Ways.Application.Ventas;

/// <summary>Valores de <c>estadoFiscal</c> de la grilla de ventas de administración. <c>con_cae</c>: la venta o su factura está
/// aprobada; <c>en_tramite</c>: la factura (o la venta fiscal) está pendiente; <c>sin_factura</c>: todo lo demás, incluida una
/// factura rechazada, que no bloquea el cambio de medios.</summary>
public static class EstadosFiscalesDeGrilla
{
    public const string SinFactura = "sin_factura";
    public const string EnTramite = "en_tramite";
    public const string ConCae = "con_cae";

    public static readonly IReadOnlyDictionary<string, int> Rangos = new Dictionary<string, int>(StringComparer.Ordinal)
    {
        [SinFactura] = 0,
        [EnTramite] = 1,
        [ConCae] = 2,
    };

    public static string De(int rango) => Rangos.Single(r => r.Value == rango).Key;
}

/// <summary>Un filtro por columna de <c>GET /api/ventas/administracion</c>; los ausentes no filtran. <c>Cliente</c> busca el
/// texto en el nombre mostrado sin mayúsculas ni acentos; <c>Numero</c> es el número correlativo exacto (el punto de venta y el
/// tipo tienen su propio filtro); <c>IdMedioPago</c> deja las ventas con un cobro de ese medio; <c>EstadoFiscal</c> es uno de
/// <see cref="EstadosFiscalesDeGrilla"/>.</summary>
public sealed record FiltrosDeVentasDeAdministracion(
    DateTimeOffset? Desde = null,
    DateTimeOffset? Hasta = null,
    long? Numero = null,
    int? IdTipoComprobante = null,
    int? IdPuntoVenta = null,
    int? IdTurnoCaja = null,
    string? Cliente = null,
    int? IdEmpleado = null,
    int? IdMedioPago = null,
    decimal? TotalDesde = null,
    decimal? TotalHasta = null,
    EstadoComprobante? Estado = null,
    string? EstadoFiscal = null,
    bool? TurnoCerrado = null,
    bool? Conciliado = null);

/// <summary>Una fila de <c>GET /api/ventas/administracion</c>. El punto de venta, el cliente y el empleado dados de baja llegan
/// con id y nombre <c>null</c>. <c>TurnoCerrado</c> es <c>null</c> en una venta sin turno. <c>Medios</c> resume los medios de
/// los cobros vivos, sin repetir y por nombre; es <c>null</c> si la venta no tiene cobros. <c>Conciliado</c>: la fecha con la
/// que el libro asienta la venta (<see cref="ReglaDeCambioDeMedios.FechaQueCuentaParaElLibro"/>) ya está congelada.
/// <c>MedioEditable</c> dice si los medios se pueden cambiar hoy desde administración; si no, <c>MotivoMedioNoEditable</c> trae
/// el código del primer motivo (<c>venta_anulada</c>, <c>comprobante_sin_cambio_de_medios</c>, <c>venta_con_cae</c>,
/// <c>venta_con_factura_en_tramite</c>, <c>periodo_conciliado</c> o <c>cambio_de_medios_con_cuenta_corriente</c>).</summary>
public sealed record FilaDeVentaDeAdministracion(
    int Id,
    long Numero,
    string NumeroVisible,
    DateTimeOffset Fecha,
    int IdTipoComprobante,
    string CodigoTipo,
    string Tipo,
    int? IdPuntoVenta,
    string? PuntoVenta,
    int? IdTurnoCaja,
    bool? TurnoCerrado,
    int? IdCliente,
    string? Cliente,
    int? IdEmpleado,
    string? Empleado,
    decimal Total,
    EstadoComprobante Estado,
    string EstadoFiscal,
    string? Medios,
    bool Conciliado,
    bool MedioEditable,
    string? MotivoMedioNoEditable);

public sealed record PaginaDeVentasDeAdministracion(
    IReadOnlyList<FilaDeVentaDeAdministracion> Items, int Total, int Pagina, int Tamanio);
