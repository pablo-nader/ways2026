using Ways.Domain.Common;

namespace Ways.Domain.Caja;

/// <summary>
/// Validación pura de los campos editables de una <see cref="CuentaTesoreria"/>, sin base de datos.
/// El saldo inicial puede ser negativo a propósito: la tabla no tiene CHECK de signo y una cuenta
/// puede abrirse con deuda (un descubierto, un saldo a favor de la plataforma de cobro).
/// </summary>
public static class ReglasDeCuentaTesoreria
{
    /// <summary>Largo de <c>cuentas_tesoreria.nombre</c>.</summary>
    public const int LargoMaximoDeNombre = 150;

    /// <summary>Tope de <c>numeric(14,2)</c>, en valor absoluto.</summary>
    public const decimal SaldoInicialMaximo = 999_999_999_999.99m;

    public static string NormalizarNombre(string? nombre)
    {
        var limpio = nombre?.Trim() ?? string.Empty;

        if (limpio.Length == 0)
        {
            throw new ErrorDominio("nombre_requerido", "El campo nombre es obligatorio.", 400);
        }

        if (limpio.Length > LargoMaximoDeNombre)
        {
            throw new ErrorDominio(
                "nombre_muy_largo",
                $"El campo nombre no puede superar los {LargoMaximoDeNombre} caracteres.",
                400);
        }

        return limpio;
    }

    public static int ExigirEmpresa(int? idEmpresa) =>
        idEmpresa ?? throw new ErrorDominio("id_empresa_requerido", "El campo idEmpresa es obligatorio.", 400);

    public static bool ExigirActivo(bool? activo) =>
        activo ?? throw new ErrorDominio("activo_requerido", "El campo activo es obligatorio.", 400);

    /// <summary>Exige que el campo venga y que sea válido.</summary>
    public static decimal ExigirSaldoInicialValido(decimal? valor)
    {
        var saldoInicial = valor
            ?? throw new ErrorDominio("saldo_inicial_requerido", "El campo saldoInicial es obligatorio.", 400);

        if (Math.Abs(saldoInicial) > SaldoInicialMaximo || decimal.Round(saldoInicial, 2) != saldoInicial)
        {
            throw new ErrorDominio(
                "saldo_inicial_invalido",
                "El saldo inicial debe estar entre -999999999999.99 y 999999999999.99, con hasta dos decimales.",
                400);
        }

        return saldoInicial;
    }
}
