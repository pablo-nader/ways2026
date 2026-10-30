using Ways.Domain.Common;
using Ways.Domain.Plantel;

namespace Ways.Domain.Pagos;

/// <summary>Una entrega de dinero de un pago por un medio. Un mismo medio puede repetirse en varias líneas.</summary>
public sealed record LineaDePago(int IdMedioPago, decimal Importe, string? Referencia);

/// <summary>Líneas ya validadas y normalizadas, con su total.</summary>
public sealed record PlanDeLineasDePago(IReadOnlyList<LineaDePago> Lineas, decimal Total)
{
    /// <summary>Medio de la primera línea: el que exponen los listados que todavía muestran un solo medio por pago.</summary>
    public int IdMedioPagoDeCompatibilidad => Lineas[0].IdMedioPago;
}

/// <summary>
/// Reglas de las líneas por medio de un pago, sin base de datos: de 1 a <see cref="MaximoDeLineas"/>, cada una
/// con un importe mayor que 0 y de hasta dos decimales, y una referencia opcional que se recorta y, si queda
/// vacía, se vuelve nula. El total es la suma de las líneas. Los límites de importe son los de <c>numeric(14,2)</c>.
/// El pago de personal comparte la aritmética (<see cref="EsImporteValido"/>) pero conserva sus propios códigos.
/// </summary>
public static class ReglaDeLineasDePago
{
    public const int MaximoDeLineas = 20;
    public const int LongitudMaximaDeReferencia = 100;
    public const decimal ImporteMaximo = 999_999_999_999.99m;

    public static PlanDeLineasDePago Planificar(IReadOnlyList<LineaDePago?>? lineas)
    {
        if (lineas is null || lineas.Count == 0)
        {
            throw new ErrorDominio("pago_sin_lineas", "El pago necesita al menos una línea.", 400);
        }

        if (lineas.Count > MaximoDeLineas)
        {
            throw new ErrorDominio(
                "pago_demasiadas_lineas", $"Un pago admite hasta {MaximoDeLineas} líneas.", 400);
        }

        var normalizadas = lineas.Select(Normalizar).ToList();
        var total = normalizadas.Sum(l => l.Importe);

        if (total > ImporteMaximo)
        {
            throw new ErrorDominio("importe_fuera_de_rango", "El total del pago supera el máximo admitido.", 400);
        }

        return new PlanDeLineasDePago(normalizadas, total);
    }

    /// <summary>Mayor que 0, hasta el máximo de <c>numeric(14,2)</c> y con a lo sumo dos decimales.</summary>
    public static bool EsImporteValido(decimal importe) =>
        importe > 0 && importe <= ImporteMaximo && decimal.Round(importe, 2) == importe;

    private static LineaDePago Normalizar(LineaDePago? linea)
    {
        if (linea is null || !EsImporteValido(linea.Importe))
        {
            throw new ErrorDominio(
                "pago_linea_importe_invalido",
                "El importe de cada línea debe ser mayor que 0 y de hasta 999999999999.99, con hasta dos decimales.", 400);
        }

        var referencia = linea.Referencia?.Trim();
        if (string.IsNullOrEmpty(referencia))
        {
            return linea with { Referencia = null };
        }

        return referencia.Length <= LongitudMaximaDeReferencia
            ? linea with { Referencia = referencia }
            : throw new ErrorDominio(
                "pago_linea_referencia_muy_larga",
                $"La referencia no puede superar los {LongitudMaximaDeReferencia} caracteres.", 400);
    }
}
