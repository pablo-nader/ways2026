using Ways.Domain.Common;
using Ways.Domain.CuentaCorriente;

namespace Ways.Domain.Plantel;

/// <summary>
/// Cálculo del importe de una carga de horas y forma de los asientos del libro de personal,
/// sin base de datos. Los límites son los de las columnas <c>numeric(14,2)</c> y
/// <c>numeric(6,2)</c> y los de los CHECK de <c>movimientos_personal</c>.
/// </summary>
public static class ReglaDeMovimientoDePersonal
{
    public const decimal HorasMaximas = 24m;
    public const decimal ImporteMaximo = 999_999_999_999.99m;
    public const int LongitudMaximaDetalle = 500;

    /// <summary>Importe de la carga: horas por valor hora, redondeado a dos decimales con los
    /// puntos medios alejándose de cero (misma convención que las compras).</summary>
    public static decimal CalcularImporteDeHoras(decimal horas, decimal valorHora)
    {
        if (horas <= 0 || horas > HorasMaximas || decimal.Round(horas, 2) != horas)
        {
            throw new ErrorDominio(
                "horas_invalidas", $"Las horas deben ser mayores que 0 y hasta {HorasMaximas:0}, con hasta dos decimales.", 400);
        }

        if (valorHora < 0 || valorHora > ImporteMaximo || decimal.Round(valorHora, 2) != valorHora)
        {
            throw new ErrorDominio(
                "valor_hora_invalido", "El valor hora debe estar entre 0 y 999999999999.99, con hasta dos decimales.", 400);
        }

        var importe = Math.Round(horas * valorHora, 2, MidpointRounding.AwayFromZero);

        return importe <= ImporteMaximo
            ? importe
            : throw new ErrorDominio("importe_fuera_de_rango", "El importe de la carga supera el máximo admitido.", 400);
    }

    /// <summary>Valida el ajuste (importe con signo, distinto de cero, detalle de al menos cinco
    /// caracteres) y devuelve el detalle normalizado.</summary>
    public static string ValidarAjuste(decimal importe, string? detalle)
    {
        ReglaDeAjusteDeCuenta.Validar(importe, detalle);

        if (decimal.Round(importe, 2) != importe)
        {
            throw new ErrorDominio("ajuste_importe_invalido", "El importe del ajuste admite hasta dos decimales.", 400);
        }

        if (Math.Abs(importe) > ImporteMaximo)
        {
            throw new ErrorDominio("importe_fuera_de_rango", "El importe del ajuste supera el máximo admitido.", 400);
        }

        return NormalizarDetalleOpcional(detalle)!;
    }

    public static string? NormalizarDetalleOpcional(string? detalle)
    {
        var limpio = detalle?.Trim();

        if (string.IsNullOrEmpty(limpio))
        {
            return null;
        }

        return limpio.Length <= LongitudMaximaDetalle
            ? limpio
            : throw new ErrorDominio(
                "detalle_muy_largo", $"El detalle no puede superar los {LongitudMaximaDetalle} caracteres.", 400);
    }
}
