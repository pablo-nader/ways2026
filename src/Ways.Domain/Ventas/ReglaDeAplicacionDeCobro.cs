using Ways.Domain.Common;

namespace Ways.Domain.Ventas;

/// <summary>Una venta pedida y el importe que se le aplica del recibo.</summary>
public sealed record AplicacionDeCobroPedida(int IdComprobanteVenta, decimal Importe);

/// <summary>Lo que hace falta saber de un comprobante de venta para aplicarle un cobro o aplicar el suyo: lo lee el
/// servicio en la misma sentencia que su lock.</summary>
public sealed record ComprobanteParaAplicar(int Id, string CodigoTipo, short Signo, EstadoComprobante Estado, int IdCliente);

/// <summary>
/// Reglas de las aplicaciones de cobros a ventas (E7-S2), puras: las mismas que los constraint triggers diferidos de
/// <c>aplicaciones_cobro</c> y con los mismos códigos, para que el servicio rechace antes de escribir. Los triggers son el
/// contrato; esto es la respuesta temprana.
/// </summary>
public static class ReglaDeAplicacionDeCobro
{
    public const string CodigoDeRecibo = "RC";

    public const int MaximoDeVentasPorPedido = 20;

    /// <summary>Lo cobrado de un recibo: la suma de sus cobros menos el vuelto entregado.</summary>
    public static decimal NetoCobrado(IEnumerable<(decimal Importe, decimal Vuelto)> cobros) =>
        cobros.Sum(c => c.Importe - c.Vuelto);

    /// <summary>La parte de una venta que quedó en cuenta corriente: la suma de sus cobros con un medio de comportamiento
    /// cuenta corriente.</summary>
    public static decimal ParteEnCuentaCorriente(IEnumerable<(decimal Importe, bool EsCuentaCorriente)> cobros) =>
        cobros.Where(c => c.EsCuentaCorriente).Sum(c => c.Importe);

    /// <summary>Lo que queda de un recibo para aplicar. Un recibo anulado no tiene disponible: su cobro se revirtió.</summary>
    public static decimal Disponible(EstadoComprobante estado, decimal netoCobrado, decimal aplicado) =>
        estado == EstadoComprobante.Anulado ? 0m : netoCobrado - aplicado;

    /// <summary>Lo que falta cobrar de la parte en cuenta corriente de una venta. Una venta anulada no debe nada.</summary>
    public static decimal SaldoPendiente(EstadoComprobante estado, decimal parteEnCuentaCorriente, decimal cobrado) =>
        estado == EstadoComprobante.Anulado ? 0m : parteEnCuentaCorriente - cobrado;

    /// <summary>dto-contract-honesty: de 1 a <see cref="MaximoDeVentasPorPedido"/> entradas, sin nulos y sin ventas
    /// repetidas. Los importes se validan aparte (<see cref="ExigirImportesValidos"/>), después del 404 de cada venta.</summary>
    public static IReadOnlyList<AplicacionDeCobroPedida> ExigirPedidas(IReadOnlyList<AplicacionDeCobroPedida?>? pedidas)
    {
        if (pedidas is null || pedidas.Count == 0)
        {
            throw new ErrorDominio("aplicacion_sin_ventas", "Indicá al menos una venta a la que aplicar el cobro.", 400);
        }

        if (pedidas.Count > MaximoDeVentasPorPedido)
        {
            throw new ErrorDominio(
                "aplicacion_demasiadas_ventas",
                $"Se pueden aplicar hasta {MaximoDeVentasPorPedido} ventas por pedido.", 400);
        }

        if (pedidas.Any(p => p is null))
        {
            throw new ErrorDominio("aplicacion_sin_ventas", "Cada aplicación tiene que indicar venta e importe.", 400);
        }

        if (pedidas.Select(p => p!.IdComprobanteVenta).Distinct().Count() != pedidas.Count)
        {
            throw new ErrorDominio("aplicacion_venta_repetida", "Una venta no puede figurar dos veces.", 400);
        }

        return pedidas!;
    }

    public static void ExigirImportesValidos(IEnumerable<AplicacionDeCobroPedida> pedidas)
    {
        if (pedidas.Any(p => p.Importe <= 0m || decimal.Round(p.Importe, 2) != p.Importe))
        {
            throw new ErrorDominio(
                "aplicacion_importe_invalido",
                "El importe a aplicar tiene que ser positivo y con hasta dos decimales.", 400);
        }
    }

    /// <summary>El comprobante del cobro es un recibo <c>RC</c> emitido.</summary>
    public static void ExigirRecibo(ComprobanteParaAplicar recibo)
    {
        if (recibo.CodigoTipo != CodigoDeRecibo)
        {
            throw new ErrorDominio(
                "aplicacion_cobro_comprobante_invalido", "Solo se aplica el cobro de un recibo de pago a cuenta.", 409);
        }

        if (recibo.Estado == EstadoComprobante.Anulado)
        {
            throw new ErrorDominio("aplicacion_cobro_comprobante_invalido", "El recibo está anulado.", 409);
        }
    }

    /// <summary>La venta es una venta emitida (no un recibo ni una nota de crédito) del mismo cliente que el recibo.</summary>
    public static void ExigirVenta(ComprobanteParaAplicar recibo, ComprobanteParaAplicar venta)
    {
        if (venta.CodigoTipo == CodigoDeRecibo || venta.Signo <= 0)
        {
            throw new ErrorDominio(
                "aplicacion_cobro_comprobante_invalido",
                $"El comprobante {venta.Id} no es una venta a la que se le pueda aplicar un cobro.", 409);
        }

        if (venta.Estado == EstadoComprobante.Anulado)
        {
            throw new ErrorDominio(
                "aplicacion_cobro_comprobante_invalido", $"La venta {venta.Id} está anulada.", 409);
        }

        if (venta.IdCliente != recibo.IdCliente)
        {
            throw new ErrorDominio(
                "aplicacion_cobro_otro_cliente", $"La venta {venta.Id} es de otro cliente que el recibo.", 409);
        }
    }

    /// <summary>Lo pedido no supera lo disponible del recibo, y lo pedido a cada venta no supera su saldo pendiente.</summary>
    public static void ExigirTopes(
        decimal disponibleDelRecibo, IEnumerable<(AplicacionDeCobroPedida Pedida, decimal SaldoPendiente)> pedidas)
    {
        var lista = pedidas.ToList();
        if (lista.Sum(p => p.Pedida.Importe) > disponibleDelRecibo)
        {
            throw new ErrorDominio(
                "cobro_excede_disponible", "El importe a aplicar supera lo disponible del recibo.", 409);
        }

        foreach (var (pedida, saldo) in lista)
        {
            if (pedida.Importe > saldo)
            {
                throw new ErrorDominio(
                    "aplicacion_excede_saldo_de_venta",
                    $"El importe a aplicar supera el saldo pendiente de la venta {pedida.IdComprobanteVenta}.", 409);
            }
        }
    }
}
