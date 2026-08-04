using Ways.Domain.Common;

namespace Ways.Domain.Ventas;

/// <summary>
/// Reglas de negocio puras de <see cref="ComprobanteVenta"/> (design decisión 4), sin
/// dependencias — mismo criterio que <see cref="Ofertas.ReglaDeOfertas"/>. Todo camino de
/// escritura (Slice 4: <c>ServicioDeVentas.EmitirAsync</c>; Slice 5: <c>AnularAsync</c>) tiene
/// que pasar por acá antes de tocar la fila.
/// </summary>
public static class ReglaDeComprobantes
{
    /// <summary>Signo vs <c>tipos_comprobante.signo</c> (design decisión 4): TX (+1) exige
    /// todas las cantidades positivas, NCX (−1) exige todas negativas — nunca cero. El signo NO
    /// se aplica/flipea acá, solo se valida: <c>signo</c> conserva un trabajo real (rechazar un
    /// TX con líneas negativas), en vez de ser metadata redundante.</summary>
    public static void ValidarSignoDeLineas(short signoTipoComprobante, IReadOnlyList<decimal> cantidades)
    {
        foreach (var cantidad in cantidades)
        {
            if (signoTipoComprobante > 0 && cantidad <= 0)
            {
                throw new ErrorDominio(
                    "signo_de_linea_invalido", "Un comprobante de venta no puede tener líneas con cantidad negativa o cero.", 400);
            }

            if (signoTipoComprobante < 0 && cantidad >= 0)
            {
                throw new ErrorDominio(
                    "signo_de_linea_invalido", "Una nota de crédito tiene que tener todas sus líneas con cantidad negativa.", 400);
            }
        }
    }

    /// <summary>Única transición válida: <see cref="EstadoComprobante.Emitido"/> → <see
    /// cref="EstadoComprobante.Anulado"/> (doc 10 principio 6, spec: Anulación is idempotent-safe
    /// against double-anulación). Cualquier otro estado de origen — incluido <c>Anulado</c> de
    /// nuevo — es un rechazo de negocio, no una excepción técnica: el mismo código
    /// <c>comprobante_ya_anulado</c> que produce el <c>UPDATE ... WHERE estado = 'emitido'</c>
    /// condicional de 0 filas (design: Protection Rules), para que el resultado sea idéntico sin
    /// importar si la doble anulación se detecta acá o en la carrera de la base.</summary>
    public static void ValidarTransicionAEstado(EstadoComprobante actual, EstadoComprobante nuevo)
    {
        if (actual == EstadoComprobante.Anulado)
        {
            throw new ErrorDominio("comprobante_ya_anulado", "El comprobante ya está anulado.", 409);
        }

        if (actual == EstadoComprobante.Emitido && nuevo == EstadoComprobante.Anulado)
        {
            return;
        }

        throw new ErrorDominio("transicion_de_estado_invalida", "Esa transición de estado no es válida.", 400);
    }

    /// <summary><c>id_comprobante_asociado</c> (spec: Devoluciones As NCX Comprobantes) —
    /// siempre opcional; cuando está poblado, exige NCX (nunca un TX asociado a otro) y que el
    /// comprobante referenciado esté <see cref="EstadoComprobante.Emitido"/>, del mismo punto de
    /// venta y del mismo cliente (design: Protection Rules).</summary>
    public static void ValidarComprobanteAsociado(
        short signoTipoComprobante,
        int? idComprobanteAsociado,
        ComprobanteVenta? asociado,
        int idPuntoVenta,
        int idCliente)
    {
        if (idComprobanteAsociado is null)
        {
            return;
        }

        if (signoTipoComprobante > 0)
        {
            throw new ErrorDominio(
                "comprobante_asociado_no_permitido", "Un comprobante de venta no puede asociarse a otro comprobante.", 400);
        }

        if (asociado is null)
        {
            throw new ErrorDominio("comprobante_asociado_invalido", "El comprobante asociado no existe.", 404);
        }

        if (asociado.Estado != EstadoComprobante.Emitido)
        {
            throw new ErrorDominio(
                "comprobante_asociado_invalido", "El comprobante asociado tiene que estar emitido.", 400);
        }

        if (asociado.IdPuntoVenta != idPuntoVenta || asociado.IdCliente != idCliente)
        {
            throw new ErrorDominio(
                "comprobante_asociado_invalido",
                "El comprobante asociado tiene que ser del mismo punto de venta y del mismo cliente.",
                400);
        }
    }
}
