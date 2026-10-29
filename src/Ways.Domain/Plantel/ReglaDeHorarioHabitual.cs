using Ways.Domain.Common;

namespace Ways.Domain.Plantel;

/// <summary>
/// Forma del horario habitual de <see cref="Personal"/>, sin base de datos: siete posiciones en
/// orden ISO (índice 0 = lunes … 6 = domingo), cada una entre 0 y 24 horas con hasta dos decimales.
/// 0 es franco. El CHECK <c>ck_personal_horario_habitual</c> es la defensa en profundidad.
/// </summary>
public static class ReglaDeHorarioHabitual
{
    public const int DiasPorSemana = 7;
    public const decimal HorasMaximasPorDia = 24m;

    /// <summary><c>null</c> significa sin horario habitual y es válido.</summary>
    public static void Validar(IReadOnlyList<decimal>? horario)
    {
        if (horario is null)
        {
            return;
        }

        if (horario.Count != DiasPorSemana
            || horario.Any(h => h < 0 || h > HorasMaximasPorDia || decimal.Round(h, 2) != h))
        {
            throw new ErrorDominio(
                "horario_habitual_invalido",
                "El horario habitual debe tener siete valores (lunes a domingo), cada uno entre 0 y 24 horas con hasta dos decimales.",
                400);
        }
    }
}
