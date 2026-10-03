using Ways.Domain.Common;

namespace Ways.Domain.Compras;

/// <summary>Tipos de percepción que un proveedor puede facturarle a la empresa. Texto con CHECK en
/// la base, mismo criterio que el resto de los enums chicos de compras.</summary>
public static class TiposDePercepcion
{
    public const string Iibb = "iibb";
    public const string Iva = "iva";

    public static bool EsValido(string? tipo) => tipo is Iibb or Iva;
}

/// <summary>Una percepción tal como la imprimió el proveedor: <see cref="Importe"/> es lo que dice
/// la factura y es lo que suma al total; <see cref="BaseImponible"/> y <see cref="Alicuota"/> son
/// informativas (de dónde salió el importe), nunca se recalculan.</summary>
public sealed record PercepcionDeCompra(string Tipo, decimal BaseImponible, decimal Alicuota, decimal Importe);

/// <summary>
/// Reglas de las percepciones de un comprobante de compra. Solo las admite un tipo que registra
/// libro IVA (una factura, no un remito) y la percepción de IVA, además, solo un comprobante que
/// discrimina IVA. Se rechaza con 400 en vez de descartar: aceptar una percepción y no guardarla
/// dejaría un total que no coincide con la factura.
/// </summary>
public static class ReglaDePercepciones
{
    private const decimal MaximoDeAlicuota = 100m;

    public static void Validar(
        bool registraLibroIva, bool discriminaIva, IReadOnlyList<PercepcionDeCompra> percepciones)
    {
        if (percepciones.Count == 0)
        {
            return;
        }

        if (!registraLibroIva)
        {
            throw new ErrorDominio(
                "percepciones_sin_libro_iva",
                "Solo un comprobante que registra libro IVA admite percepciones.",
                400);
        }

        var vistos = new HashSet<string>(StringComparer.Ordinal);

        foreach (var percepcion in percepciones)
        {
            if (!TiposDePercepcion.EsValido(percepcion.Tipo))
            {
                throw new ErrorDominio(
                    "percepcion_tipo_invalido",
                    $"El tipo de percepción '{percepcion.Tipo}' no existe: usá 'iibb' o 'iva'.",
                    400);
            }

            if (!vistos.Add(percepcion.Tipo))
            {
                throw new ErrorDominio(
                    "percepcion_duplicada",
                    $"La percepción de tipo '{percepcion.Tipo}' viene repetida.",
                    400);
            }

            if (percepcion.Tipo == TiposDePercepcion.Iva && !discriminaIva)
            {
                throw new ErrorDominio(
                    "percepcion_iva_sin_discriminar",
                    "La percepción de IVA solo se informa en un comprobante que discrimina IVA.",
                    400);
            }

            if (percepcion.Importe < 0m || percepcion.BaseImponible < 0m)
            {
                throw new ErrorDominio(
                    "percepcion_importes_invalidos",
                    "La base imponible y el importe de una percepción no pueden ser negativos.",
                    400);
            }

            if (percepcion.Alicuota < 0m || percepcion.Alicuota > MaximoDeAlicuota)
            {
                throw new ErrorDominio(
                    "percepcion_alicuota_invalida",
                    "La alícuota de una percepción tiene que estar entre 0 y 100.",
                    400);
            }

            if (percepcion.Importe != Math.Round(percepcion.Importe, 2)
                || percepcion.BaseImponible != Math.Round(percepcion.BaseImponible, 2)
                || percepcion.Alicuota != Math.Round(percepcion.Alicuota, 3))
            {
                throw new ErrorDominio(
                    "percepcion_decimales_invalidos",
                    "Los importes de una percepción admiten hasta 2 decimales y su alícuota hasta 3.",
                    400);
            }
        }
    }
}
