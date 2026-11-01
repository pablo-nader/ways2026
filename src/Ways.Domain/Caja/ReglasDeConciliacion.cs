using System.Globalization;
using Ways.Domain.Common;

namespace Ways.Domain.Caja;

/// <summary>
/// Reglas puras de la conciliación de la tesorería, sin base de datos. La marca congela, inclusive, todo lo que alimenta el libro
/// con fecha menor o igual: una fecha igual a la marca está congelada y una posterior queda libre.
/// </summary>
public static class ReglasDeConciliacion
{
    public const int LargoMaximoDeNota = 500;

    public static DateTimeOffset ExigirHasta(DateTimeOffset? hasta)
    {
        if (hasta is null || hasta.Value == default)
        {
            throw new ErrorDominio("hasta_requerido", "El campo hasta es obligatorio.", 400);
        }

        return hasta.Value.ToUniversalTime();
    }

    public static string? NormalizarNota(string? nota)
    {
        var limpia = nota?.Trim();
        if (string.IsNullOrEmpty(limpia))
        {
            return null;
        }

        if (limpia.Length > LargoMaximoDeNota)
        {
            throw new ErrorDominio(
                "nota_muy_larga", $"El campo nota no puede superar los {LargoMaximoDeNota} caracteres.", 400);
        }

        return limpia;
    }

    /// <summary>Una marca no puede estar en el futuro ni retroceder: tiene que ser posterior a la vigente.</summary>
    public static void ExigirMarcaValida(DateTimeOffset hasta, DateTimeOffset ahora, DateTimeOffset? marcaVigente)
    {
        if (hasta > ahora)
        {
            throw new ErrorDominio(
                "conciliacion_futura", "No se puede conciliar la tesorería hasta un instante que todavía no pasó.", 409);
        }

        if (marcaVigente is { } vigente && hasta <= vigente)
        {
            throw new ErrorDominio(
                "conciliacion_anterior",
                $"La tesorería ya está conciliada hasta {Formatear(vigente)}: la nueva marca tiene que ser posterior.",
                409);
        }
    }

    public static bool EstaCongelado(DateTimeOffset fecha, DateTimeOffset? marcaVigente) =>
        marcaVigente is { } marca && fecha <= marca;

    public static ErrorDominio PeriodoConciliado(DateTimeOffset marcaVigente) =>
        new(
            "periodo_conciliado",
            $"El período está conciliado hasta {Formatear(marcaVigente)}: no se puede modificar. Corregilo con un movimiento nuevo.",
            409);

    public static ErrorDominio ConTurnoAbierto() =>
        new(
            "conciliacion_con_turno_abierto",
            "Hay un turno abierto que empezó antes de ese instante: cerralo antes de conciliar.",
            409);

    private static string Formatear(DateTimeOffset instante) =>
        instante.UtcDateTime.ToString("dd/MM/yyyy HH:mm 'UTC'", CultureInfo.InvariantCulture);
}
