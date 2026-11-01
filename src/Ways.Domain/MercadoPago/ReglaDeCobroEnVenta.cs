using Ways.Domain.Common;

namespace Ways.Domain.MercadoPago;

/// <summary>Códigos de error de un pago de venta que se cobra con Mercado Pago.</summary>
public static class CodigosDePagoConMercadoPago
{
    public const string Requerido = "cobro_mercadopago_requerido";

    public const string EnMedioSinProveedor = "cobro_mercadopago_en_medio_sin_proveedor";

    public const string SinNumeroPreasignado = "cobro_mercadopago_sin_numero_preasignado";

    public const string ReferenciaNoAdmitida = "cobro_mercadopago_referencia_no_admitida";

    public const string NoAdmitidoEnDevolucion = "cobro_mercadopago_no_admitido_en_devolucion";

    public const string Repetido = "cobro_mercadopago_repetido";

    public const string Inexistente = "cobro_mercadopago_inexistente";

    public const string NoAprobado = "cobro_mercadopago_no_aprobado";

    public const string DeOtraVenta = "cobro_mercadopago_de_otra_venta";

    public const string ImporteDistinto = "cobro_mercadopago_importe_distinto";

    public const string YaUsado = "cobro_mercadopago_ya_usado";

    /// <summary>Un pago ya cobrado con Mercado Pago no se cambia: el dinero entró por ese medio y su devolución es de la conciliación.</summary>
    public const string NoEditable = "pago_mercadopago_no_editable";
}

/// <summary>Lo que la venta necesita saber de un cobro de Mercado Pago, leído bajo su lock.</summary>
public readonly record struct CobroParaVender(
    int IdPuntoVenta, long NumeroPreasignado, int IdMedioPago, string Estado, decimal? ImportePagado, string? IdPagoMercadoPago,
    int? IdComprobanteVenta);

/// <summary>
/// Cuándo un pago de venta puede apoyarse en un cobro aprobado de Mercado Pago. Un medio con proveedor se cobra con una orden del
/// proveedor, así que su pago tiene que traer el cobro (y solo ese medio puede traerlo); el cobro tiene que ser de esta venta,
/// estar aprobado por el mismo importe y no haber pagado otra venta.
/// </summary>
public static class ReglaDeCobroEnVenta
{
    /// <summary>La forma del pago, antes de tocar la base: el medio con proveedor exige un cobro, el medio sin proveedor no lo admite,
    /// el cobro solo existe para un número de venta que el dispositivo reservó y su referencia sale del pago de Mercado Pago (la que
    /// trajo el cliente no tiene destino). En una devolución (<paramref name="esDevolucion"/>) el medio con proveedor es solo el medio por
    /// el que se devuelve el dinero —su reintegro es de la conciliación— y no admite un cobro.</summary>
    public static void ExigirFormaDelPago(
        string? proveedorDelMedio, int? idCobro, string? referencia, bool traeNumeroPreasignado, bool esDevolucion = false)
    {
        if (esDevolucion)
        {
            if (idCobro is not null)
            {
                throw new ErrorDominio(
                    CodigosDePagoConMercadoPago.NoAdmitidoEnDevolucion,
                    "Una devolución no lleva un cobro de Mercado Pago.",
                    400);
            }

            return;
        }

        if (proveedorDelMedio is not null && idCobro is null)
        {
            throw new ErrorDominio(
                CodigosDePagoConMercadoPago.Requerido,
                "Un pago con un medio de Mercado Pago tiene que traer el cobro aprobado.",
                400);
        }

        if (proveedorDelMedio is null && idCobro is not null)
        {
            throw new ErrorDominio(
                CodigosDePagoConMercadoPago.EnMedioSinProveedor,
                "El cobro de Mercado Pago solo se admite en un medio de pago con proveedor.",
                400);
        }

        if (idCobro is not null && !traeNumeroPreasignado)
        {
            throw new ErrorDominio(
                CodigosDePagoConMercadoPago.SinNumeroPreasignado,
                "El cobro de Mercado Pago solo se admite en una venta con número preasignado.",
                400);
        }

        if (idCobro is not null && !string.IsNullOrWhiteSpace(referencia))
        {
            throw new ErrorDominio(
                CodigosDePagoConMercadoPago.ReferenciaNoAdmitida,
                "Un pago con cobro de Mercado Pago no lleva referencia: se toma del pago de Mercado Pago.",
                400);
        }
    }

    /// <summary>Un cobro paga un solo pago de la venta.</summary>
    public static void ExigirSinCobrosRepetidos(IEnumerable<int> idsDeCobro)
    {
        if (idsDeCobro.GroupBy(id => id).Any(g => g.Count() > 1))
        {
            throw new ErrorDominio(
                CodigosDePagoConMercadoPago.Repetido,
                "Un cobro de Mercado Pago solo puede pagar un medio de la venta.",
                400);
        }
    }

    /// <summary>El cobro, leído bajo su lock, tiene que ser de esta venta (punto de venta, número y medio), estar aprobado, no estar
    /// ligado a otra venta y haber cobrado exactamente el importe del pago.</summary>
    public static void ExigirCobroUtilizable(
        CobroParaVender cobro, int idPuntoVenta, long numeroPreasignado, int idMedioPago, decimal importe)
    {
        if (cobro.IdPuntoVenta != idPuntoVenta
            || cobro.NumeroPreasignado != numeroPreasignado
            || cobro.IdMedioPago != idMedioPago)
        {
            throw ErrorDominio.Conflicto(
                CodigosDePagoConMercadoPago.DeOtraVenta, "El cobro de Mercado Pago es de otra venta.");
        }

        if (cobro.Estado != EstadosDeCobroMercadoPago.Aprobado)
        {
            throw ErrorDominio.Conflicto(
                CodigosDePagoConMercadoPago.NoAprobado, "El cobro de Mercado Pago todavía no está aprobado.");
        }

        if (cobro.IdComprobanteVenta is not null)
        {
            throw ErrorDominio.Conflicto(
                CodigosDePagoConMercadoPago.YaUsado, "El cobro de Mercado Pago ya pagó otra venta.");
        }

        if (cobro.ImportePagado != importe)
        {
            throw ErrorDominio.Conflicto(
                CodigosDePagoConMercadoPago.ImporteDistinto, "El importe del pago no coincide con lo que cobró Mercado Pago.");
        }
    }
}
