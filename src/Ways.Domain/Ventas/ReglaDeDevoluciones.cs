using Ways.Domain.Common;
using Ways.Domain.Fiscal;

namespace Ways.Domain.Ventas;

/// <summary>Por qué un ticket de venta no admite una devolución.</summary>
public enum MotivoDeNoDevolvible
{
    /// <summary>El ticket está anulado.</summary>
    Anulado,

    /// <summary>El ticket tiene factura fiscal aprobada o en trámite, o la decisión de facturarlo todavía está pendiente (la factura que nazca después se arma con todos sus items): devolverlo exige una nota de crédito fiscal, que todavía no existe.</summary>
    ConFacturaFiscal,

    /// <summary>Ya se devolvió todo lo que vendió.</summary>
    SinUnidadesPorDevolver,

    /// <summary>No es un ticket de venta (es una nota de crédito, un recibo, una factura...).</summary>
    NoEsUnTicketDeVenta
}

/// <summary>
/// Reglas de las devoluciones (notas de crédito <c>NCX</c>) que dependen del estado del ticket de origen. Las de la forma de
/// la asociación (mismo punto de venta y cliente, tipo) están en <see cref="ReglaDeComprobantes.ValidarComprobanteAsociado"/>;
/// el tope por artículo y el importe, en <see cref="DevolucionDeTicket"/>.
/// </summary>
public static class ReglaDeDevoluciones
{
    private const string DevolucionConFacturaFiscal = "devolucion_requiere_nota_de_credito_fiscal";

    /// <summary>Lo que impide devolver el ticket, o <c>null</c> si se puede. El orden es el de la gravedad: un ticket anulado
    /// no se devuelve aunque tenga factura. <paramref name="resultadosFiscales"/> son los del propio ticket y los del espejo de
    /// su factura (el de una nota de crédito no cuenta); uno rechazado no bloquea porque ARCA lo libera. <paramref name="decisionDeFacturacionPendiente"/>: la decisión de facturar el ticket está debida o en <c>pendiente</c> sin espejo todavía; como el espejo se arma con TODOS los items del ticket, bloquea igual que una factura.</summary>
    public static MotivoDeNoDevolvible? Evaluar(
        string codigoTipo, EstadoComprobante estado, IEnumerable<ResultadoFiscal?> resultadosFiscales,
        bool decisionDeFacturacionPendiente)
    {
        if (codigoTipo != ReglaDeComprobantes.CodigoDeVenta)
        {
            return MotivoDeNoDevolvible.NoEsUnTicketDeVenta;
        }

        if (estado != EstadoComprobante.Emitido)
        {
            return MotivoDeNoDevolvible.Anulado;
        }

        return decisionDeFacturacionPendiente || resultadosFiscales.Any(r => r is ResultadoFiscal.Aprobado or ResultadoFiscal.AprobadoConObservaciones or ResultadoFiscal.Pendiente)
            ? MotivoDeNoDevolvible.ConFacturaFiscal
            : null;
    }

    /// <summary>Re-juzga, con el ticket ya bloqueado, lo que pudo cambiar desde que se armó el plan: que siga emitido (409
    /// <c>comprobante_asociado_invalido</c> si lo anularon en el medio) y que no tenga factura fiscal (409
    /// <c>devolucion_requiere_nota_de_credito_fiscal</c>).</summary>
    public static void ExigirDevolvible(
        EstadoComprobante estado, IEnumerable<ResultadoFiscal?> resultadosFiscales, bool decisionDeFacturacionPendiente)
    {
        switch (Evaluar(ReglaDeComprobantes.CodigoDeVenta, estado, resultadosFiscales, decisionDeFacturacionPendiente))
        {
            case MotivoDeNoDevolvible.Anulado:
                throw new ErrorDominio(
                    "comprobante_asociado_invalido", "El comprobante asociado tiene que estar emitido.", 409);
            case MotivoDeNoDevolvible.ConFacturaFiscal:
                throw new ErrorDominio(
                    DevolucionConFacturaFiscal,
                    "El ticket tiene factura fiscal (aprobada, en trámite ante ARCA o por emitirse): no se puede devolver todavía, porque la " +
                    "devolución exige una nota de crédito fiscal.",
                    409);
        }
    }
}
