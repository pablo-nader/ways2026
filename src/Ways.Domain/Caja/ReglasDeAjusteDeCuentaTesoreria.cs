using Ways.Domain.Common;

namespace Ways.Domain.Caja;

/// <summary>Resultado de resolver un ajuste manual contra el saldo vigente de la cuenta: el importe con signo y los
/// lados ya calculados de la fila del libro. <see cref="Final"/> es siempre <c>Inicio + Ingreso − Egreso</c>.</summary>
public sealed record AjusteDeCuentaResuelto(decimal Importe, decimal Inicio, decimal Ingreso, decimal Egreso, decimal Final);

/// <summary>
/// Reglas puras del ajuste manual de una <see cref="CuentaTesoreria"/>, sin base de datos. La solicitud trae
/// exactamente uno de dos modos: un importe con signo, o el saldo real leído de la plataforma, cuya diferencia contra
/// el saldo vigente se calcula recién bajo el lock de la cuenta (<see cref="Resolver"/>).
/// </summary>
public static class ReglasDeAjusteDeCuentaTesoreria
{
    /// <summary>Tope propio del concepto: la columna es <c>text</c> y no lo limita.</summary>
    public const int LargoMaximoDeConcepto = 200;

    /// <summary>Tope de <c>numeric(14,2)</c>, en valor absoluto.</summary>
    public const decimal ImporteMaximo = ReglasDeCuentaTesoreria.SaldoInicialMaximo;

    public static string NormalizarConcepto(string? concepto)
    {
        var limpio = concepto?.Trim() ?? string.Empty;

        if (limpio.Length == 0)
        {
            throw new ErrorDominio("concepto_requerido", "El campo concepto es obligatorio.", 400);
        }

        if (limpio.Length > LargoMaximoDeConcepto)
        {
            throw new ErrorDominio(
                "concepto_muy_largo",
                $"El campo concepto no puede superar los {LargoMaximoDeConcepto} caracteres.",
                400);
        }

        return limpio;
    }

    /// <summary>Exige exactamente uno de los dos modos y que el valor enviado sea válido. Corre antes de tocar la base.</summary>
    public static void ValidarSolicitud(decimal? importe, decimal? saldoObjetivo)
    {
        if (importe is null && saldoObjetivo is null)
        {
            throw new ErrorDominio(
                "importe_o_saldo_objetivo_requerido",
                "Hay que enviar el importe del ajuste o el saldo objetivo de la cuenta.",
                400);
        }

        if (importe is not null && saldoObjetivo is not null)
        {
            throw new ErrorDominio(
                "importe_y_saldo_objetivo_excluyentes",
                "El importe y el saldo objetivo no se pueden enviar juntos: hay que elegir uno.",
                400);
        }

        if (importe is { } i && (i == 0m || !EsMontoValido(i)))
        {
            throw new ErrorDominio(
                "importe_invalido",
                "El importe debe ser distinto de cero, hasta 999999999999.99 en valor absoluto y con hasta dos decimales.",
                400);
        }

        if (saldoObjetivo is { } s && !EsMontoValido(s))
        {
            throw new ErrorDominio(
                "saldo_objetivo_invalido",
                "El saldo objetivo debe estar entre -999999999999.99 y 999999999999.99, con hasta dos decimales.",
                400);
        }
    }

    /// <summary>Calcula el ajuste contra el saldo vigente, que el llamador leyó bajo el lock de la cuenta. Un saldo objetivo
    /// igual al vigente no genera movimiento y se informa como conflicto.</summary>
    public static AjusteDeCuentaResuelto Resolver(decimal saldoActual, decimal? importe, decimal? saldoObjetivo)
    {
        var delta = importe ?? saldoObjetivo!.Value - saldoActual;

        if (delta == 0m)
        {
            throw ErrorDominio.Conflicto(
                "ajuste_sin_diferencia", "El saldo de la cuenta ya coincide con el saldo objetivo: no hay nada que ajustar.");
        }

        var final = saldoActual + delta;

        if (Math.Abs(final) > ImporteMaximo)
        {
            throw new ErrorDominio(
                "saldo_resultante_fuera_de_rango",
                "El saldo resultante quedaría fuera de -999999999999.99 y 999999999999.99.",
                400);
        }

        return new AjusteDeCuentaResuelto(
            delta, saldoActual, Ingreso: delta > 0m ? delta : 0m, Egreso: delta < 0m ? -delta : 0m, final);
    }

    private static bool EsMontoValido(decimal valor) =>
        Math.Abs(valor) <= ImporteMaximo && decimal.Round(valor, 2) == valor;
}
