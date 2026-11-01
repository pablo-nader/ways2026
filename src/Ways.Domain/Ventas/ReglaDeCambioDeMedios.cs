using Ways.Domain.Catalogos;
using Ways.Domain.Common;
using Ways.Domain.Fiscal;

namespace Ways.Domain.Ventas;

/// <summary>Un cobro de la venta tal como lo ve el cambio de medios: el medio con su comportamiento y lo que se entregó.</summary>
public readonly record struct CobroDeVenta(
    int IdMedioPago, ComportamientoMedioPago Comportamiento, decimal Importe, decimal Vuelto, string? Referencia);

/// <summary>
/// Reglas puras del cambio de medios de pago de una venta ya emitida. El total cobrado no cambia: el cambio corrige con qué
/// medio se cobró, no cuánto. La cuenta corriente queda afuera en los dos sentidos porque su cobro es un consumo del cliente
/// con su propio libro.
/// </summary>
public static class ReglaDeCambioDeMedios
{
    public static ErrorDominio VentaAnulada =>
        new("venta_anulada", "La venta está anulada: sus medios de pago no se pueden cambiar.", 409);

    public static ErrorDominio VentaConCae =>
        new("venta_con_cae", "La venta tiene una factura con CAE: para cambiar el medio hay que anularla y volver a venderla.", 409);

    public static ErrorDominio VentaConFacturaEnTramite =>
        new("venta_con_factura_en_tramite", "La factura de la venta está en trámite: esperá su resultado.", 409);

    public static ErrorDominio VentaTurnoCerrado =>
        new("venta_turno_cerrado", "Los medios de la venta solo se pueden cambiar en la caja mientras su turno siga abierto.", 409);

    public static ErrorDominio ComprobanteNoAdmitido =>
        new("comprobante_sin_cambio_de_medios", "Este comprobante no admite cambiar sus medios de pago.", 409);

    /// <summary>Solo un comprobante de venta que cobra (signo positivo) y no es un recibo de cuenta corriente: el recibo es el
    /// cobro de una deuda y sus aplicaciones dependen de él.</summary>
    public static void ExigirComprobanteAdmitido(string codigoTipo, short signo)
    {
        if (signo <= 0 || codigoTipo == ReglaDeAplicacionDeCobro.CodigoDeRecibo)
        {
            throw ComprobanteNoAdmitido;
        }
    }

    /// <summary>Con la factura aprobada el cambio se rechaza; con una en trámite también, porque puede aprobarse después.</summary>
    public static void ExigirSinFactura(ResultadoFiscal? resultadoDeLaVenta, ResultadoFiscal? resultadoDeLaFactura)
    {
        foreach (var resultado in new[] { resultadoDeLaVenta, resultadoDeLaFactura })
        {
            switch (resultado)
            {
                case ResultadoFiscal.Aprobado or ResultadoFiscal.AprobadoConObservaciones:
                    throw VentaConCae;
                case ResultadoFiscal.Pendiente:
                    throw VentaConFacturaEnTramite;
            }
        }
    }

    /// <summary>Ni los cobros que tiene ni los nuevos pueden ser de cuenta corriente.</summary>
    public static void ExigirSinCuentaCorriente(IEnumerable<CobroDeVenta> anteriores, IEnumerable<CobroDeVenta> nuevos)
    {
        if (anteriores.Any(c => c.Comportamiento == ComportamientoMedioPago.CuentaCorriente))
        {
            throw new ErrorDominio(
                "cambio_de_medios_con_cuenta_corriente",
                "La venta se cobró con cuenta corriente: sus medios no se pueden cambiar.", 409);
        }

        if (nuevos.Any(c => c.Comportamiento == ComportamientoMedioPago.CuentaCorriente))
        {
            throw new ErrorDominio(
                "cambio_de_medios_con_cuenta_corriente",
                "El cambio de medios no admite cuenta corriente.", 409);
        }
    }

    /// <summary>Lo cobrado (importe menos vuelto) tiene que ser lo mismo, y el conjunto nuevo tiene que diferir del que hay.</summary>
    public static void ExigirMismoTotalYAlgunCambio(IReadOnlyList<CobroDeVenta> anteriores, IReadOnlyList<CobroDeVenta> nuevos)
    {
        if (Cobrado(anteriores) != Cobrado(nuevos))
        {
            throw new ErrorDominio(
                "total_de_cobros_distinto",
                $"Los medios nuevos tienen que cobrar lo mismo que los anteriores ({Cobrado(anteriores)}).", 400);
        }

        if (Firma(anteriores).SequenceEqual(Firma(nuevos)))
        {
            throw new ErrorDominio("cambio_de_medios_sin_diferencia", "Los medios indicados son los que la venta ya tiene.", 409);
        }
    }

    /// <summary>Cuánto cambia, por medio, lo que el cierre del turno cuenta como cobrado (el importe entregado, como
    /// <c>LectorDeMovimientosDelTurno</c>). Solo figuran los medios que cambian.</summary>
    public static IReadOnlyDictionary<int, decimal> VariacionDeBrutoPorMedio(
        IEnumerable<CobroDeVenta> anteriores, IEnumerable<CobroDeVenta> nuevos)
    {
        var variacion = new SortedDictionary<int, decimal>();
        foreach (var cobro in anteriores)
        {
            variacion[cobro.IdMedioPago] = variacion.GetValueOrDefault(cobro.IdMedioPago) - cobro.Importe;
        }

        foreach (var cobro in nuevos)
        {
            variacion[cobro.IdMedioPago] = variacion.GetValueOrDefault(cobro.IdMedioPago) + cobro.Importe;
        }

        return variacion.Where(v => v.Value != 0m).ToDictionary(v => v.Key, v => v.Value);
    }

    private static decimal Cobrado(IEnumerable<CobroDeVenta> cobros) => cobros.Sum(c => c.Importe - c.Vuelto);

    private static List<(int, decimal, decimal, string)> Firma(IEnumerable<CobroDeVenta> cobros) =>
        cobros
            .Select(c => (c.IdMedioPago, c.Importe, c.Vuelto, c.Referencia ?? string.Empty))
            .OrderBy(c => c.IdMedioPago).ThenBy(c => c.Importe).ThenBy(c => c.Vuelto).ThenBy(c => c.Item4, StringComparer.Ordinal)
            .ToList();
}
