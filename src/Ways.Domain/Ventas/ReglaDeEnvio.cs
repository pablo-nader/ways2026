using Ways.Domain.Common;

namespace Ways.Domain.Ventas;

/// <summary>
/// Reglas puras de una venta con envío. El envío es una línea de concepto libre del comprobante
/// (<c>items_comprobante_venta.id_articulo</c> NULL): cantidad 1, sin descuento, oferta, combo ni ajuste manual, y
/// fuera de stock y lotes, y con costo 0 (cuenta en la rentabilidad como una línea de costo conocido). Entra al <see cref="CalculadorDeTotales"/> como cualquier otra línea, así que
/// subtotal, total e IVA la incluyen.
/// </summary>
public static class ReglaDeEnvio
{
    public const string DescripcionDeLaLinea = "Envío";

    /// <summary>Código ARCA de la alícuota general del 21 % (<c>alicuotas_iva.codigo_afip</c>): es lo que identifica la
    /// fila a snapshotear, no el nombre ni el id, que son datos de la plataforma.</summary>
    public const short CodigoAfipDeLaAlicuota = 5;

    /// <summary>Un envío exige un cliente identificado (no Consumidor Final) con domicilio cargado: es la dirección a la
    /// que se entrega. 400 con un código por causa, para que el POS pueda decir cuál falta.</summary>
    public static void ExigirClienteConDomicilio(bool esConsumidorFinal, string? domicilio)
    {
        if (esConsumidorFinal)
        {
            throw new ErrorDominio(
                "envio_requiere_cliente", "Una venta con envío necesita un cliente identificado, no Consumidor Final.", 400);
        }

        if (string.IsNullOrWhiteSpace(domicilio))
        {
            throw new ErrorDominio(
                "envio_requiere_domicilio", "El cliente no tiene domicilio cargado; una venta con envío lo necesita.", 400);
        }
    }

    /// <summary>La línea de envío tal como la ve el calculador: una unidad al <paramref name="importe"/>, sin descuento
    /// de oferta, combo ni ajuste manual.</summary>
    public static LineaParaCalcular LineaParaCalcular(decimal importe) =>
        new(Cantidad: 1m, PrecioUnitario: importe, DescuentoUnitario: 0m);
}
