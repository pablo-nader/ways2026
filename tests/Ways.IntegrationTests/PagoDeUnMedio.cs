using Ways.Application.Pagos;
using Ways.Domain.Pagos;

namespace Ways.IntegrationTests;

/// <summary>Arma las solicitudes de <c>/api/pagos</c> para el caso común de los tests: un pago con una sola línea. El
/// importe se manda tal cual, sin redondear: la regla de líneas lo valida.</summary>
internal static class PagoDeUnMedio
{
    public static SolicitudDePagoConLineas Pos(
        int idPuntoVenta,
        CategoriaPago categoria,
        int? idProveedor,
        int? idArea,
        string concepto,
        string? detalle,
        int idMedioPago,
        string? numeroFactura,
        decimal importe,
        int? idComprobanteCompra = null,
        OrigenFondosPago origenFondos = OrigenFondosPago.CajaTurno) =>
        new(
            idPuntoVenta, categoria, idProveedor, idArea, concepto, detalle, numeroFactura,
            [new LineaDePago(idMedioPago, importe, null)], idComprobanteCompra, origenFondos);

    public static SolicitudDePagoDeAdministracionConLineas Administracion(
        DateOnly fecha,
        int idEmpresa,
        int? idPuntoVenta,
        CategoriaPago categoria,
        int? idProveedor,
        int? idArea,
        string concepto,
        string? detalle,
        int idMedioPago,
        string? numeroFactura,
        decimal importe,
        int? idComprobanteCompra = null) =>
        new(
            fecha, idEmpresa, idPuntoVenta, categoria, idProveedor, idArea, concepto, detalle, numeroFactura,
            [new LineaDePago(idMedioPago, importe, null)], idComprobanteCompra);

    public static SolicitudDeEdicionDePagoConLineas Edicion(
        CategoriaPago categoria,
        int? idProveedor,
        int? idArea,
        string concepto,
        string? detalle,
        int idMedioPago,
        string? numeroFactura,
        decimal importe) =>
        new(categoria, idProveedor, idArea, concepto, detalle, numeroFactura, [new LineaDePago(idMedioPago, importe, null)]);
}
