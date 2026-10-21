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

    /// <summary>Tope de <c>numeric(14,2)</c>: el mayor importe de envío que entra en la columna.</summary>
    public const decimal ImporteMaximo = 999_999_999_999.99m;

    /// <summary>Un importe de envío válido: de 0 al tope, con a lo sumo dos decimales (el mismo criterio del parámetro
    /// <c>importe_envio</c>).</summary>
    public static bool ImporteEsValido(decimal importe) =>
        importe >= 0m && decimal.Round(importe, 2) == importe && importe <= ImporteMaximo;

    /// <summary>El importe que trae el dispositivo (<c>SolicitudDeVenta.ImporteEnvio</c>) solo tiene destino en una venta con
    /// envío y número pre-asignado: en cualquier otra se rechaza en vez de ignorarse, y uno inválido no se graba.</summary>
    public static void ExigirImporteDelDispositivo(decimal? importe, bool conEnvio, bool tieneNumeroPreasignado)
    {
        if (importe is not { } valor)
        {
            return;
        }

        if (!conEnvio || !tieneNumeroPreasignado)
        {
            throw new ErrorDominio(
                "importe_envio_no_admitido",
                "Solo una venta con envío y número pre-asignado puede traer el importe del envío; en otra lo pone el servidor.",
                400);
        }

        if (!ImporteEsValido(valor))
        {
            throw new ErrorDominio(
                "importe_envio_invalido",
                $"El importe del envío tiene que ser de 0 a {ImporteMaximo:0.00} y tener como máximo 2 decimales.",
                400);
        }
    }

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
