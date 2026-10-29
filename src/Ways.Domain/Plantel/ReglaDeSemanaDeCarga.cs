using Ways.Domain.Common;

namespace Ways.Domain.Plantel;

/// <summary>
/// Reglas de la carga semanal de horas, sin base de datos: la semana empieza el lunes, el índice de
/// día es ISO (0 = lunes … 6 = domingo, el orden de <see cref="Personal.HorarioHabitual"/>) y el total
/// de un día es de 0 a 24 horas con hasta dos decimales.
/// </summary>
public static class ReglaDeSemanaDeCarga
{
    public const int DiasPorSemana = 7;
    public const int EntradasMaximasPorGuardado = 1000;

    public static int IndiceIso(DateOnly fecha) => ((int)fecha.DayOfWeek + 6) % DiasPorSemana;

    public static DateOnly LunesDe(DateOnly fecha) => fecha.AddDays(-IndiceIso(fecha));

    public static void ExigirLunes(DateOnly fecha)
    {
        if (IndiceIso(fecha) != 0)
        {
            throw new ErrorDominio("semana_invalida", "La semana debe empezar un lunes.", 400);
        }
    }

    /// <summary>Total pedido para un día: de 0 (confirmado sin horas) a 24, con hasta dos decimales.</summary>
    public static void ValidarHorasDelDia(decimal horas)
    {
        if (horas < 0 || horas > ReglaDeMovimientoDePersonal.HorasMaximas || decimal.Round(horas, 2) != horas)
        {
            throw new ErrorDominio(
                "horas_invalidas",
                $"Las horas del día deben estar entre 0 y {ReglaDeMovimientoDePersonal.HorasMaximas:0}, con hasta dos decimales.",
                400);
        }
    }
}
