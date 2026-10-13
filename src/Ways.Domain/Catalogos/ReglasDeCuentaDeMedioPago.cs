using Ways.Domain.Common;

namespace Ways.Domain.Catalogos;

/// <summary>Lo que un medio de pago guarda sobre su cuenta de tesorería: la cuenta, la comisión y las tres
/// retenciones. <see cref="Vacia"/> es "sin cuenta y todo en 0".</summary>
public readonly record struct CuentaDeMedioPago(
    int? IdCuentaTesoreria, decimal Comision, decimal RetencionIibb, decimal RetencionIva, decimal RetencionGanancias)
{
    public static CuentaDeMedioPago Vacia => new(null, 0m, 0m, 0m, 0m);
}

/// <summary>Lo que el cuerpo de la solicitud dice sobre la cuenta. <c>IdCuentaPresente</c> distingue "no vino"
/// de "vino en null"; en los porcentajes, ausente y null significan lo mismo: se conserva el guardado.</summary>
public readonly record struct CuentaEnviadaDeMedioPago(
    bool IdCuentaPresente, int? IdCuenta, decimal? Comision, decimal? Iibb, decimal? Iva, decimal? Ganancias);

/// <summary>Los datos de la cuenta que deciden si un medio puede apuntarle.</summary>
public readonly record struct CuentaAsignable(int IdEmpresa, bool Activa);

/// <summary>
/// Reglas puras de la cuenta de tesorería, la comisión y las retenciones de un medio de pago, sin base de datos.
/// </summary>
public static class ReglasDeCuentaDeMedioPago
{
    public const int DecimalesMaximos = 3;

    public const decimal Tope = 100m;

    /// <summary>
    /// Resuelve lo que queda guardado a partir de lo guardado, lo enviado y el comportamiento resultante.
    /// Cada campo enviado reemplaza al guardado (incluido null para la cuenta y 0 para un porcentaje); el que no
    /// vino se conserva. Cada porcentaje enviado va de 0 a 100 con hasta 3 decimales y los cuatro resultantes
    /// suman a lo sumo 100. La cuenta y los porcentajes distintos de cero solo existen con comportamiento
    /// <see cref="ComportamientoMedioPago.Electronico"/>: si el medio no lo es, enviar una cuenta o un porcentaje
    /// distinto de cero se rechaza, y si no se envía nada que lo contradiga lo guardado se limpia (cuenta en null,
    /// porcentajes en 0) en vez de fallar, que es lo que pasa al cambiar el comportamiento de un medio que ya la
    /// tenía.
    /// </summary>
    public static CuentaDeMedioPago Resolver(
        ComportamientoMedioPago comportamiento, CuentaDeMedioPago guardada, CuentaEnviadaDeMedioPago enviada)
    {
        ExigirPorcentajeValido("comisionPorcentaje", enviada.Comision);
        ExigirPorcentajeValido("retencionIibbPorcentaje", enviada.Iibb);
        ExigirPorcentajeValido("retencionIvaPorcentaje", enviada.Iva);
        ExigirPorcentajeValido("retencionGananciasPorcentaje", enviada.Ganancias);

        if (comportamiento != ComportamientoMedioPago.Electronico)
        {
            if (enviada.IdCuenta is not null
                || enviada.Comision is > 0m
                || enviada.Iibb is > 0m
                || enviada.Iva is > 0m
                || enviada.Ganancias is > 0m)
            {
                throw new ErrorDominio(
                    "cuenta_solo_electronico",
                    "La cuenta de tesorería, la comisión y las retenciones solo se admiten en medios de pago electrónicos.",
                    400);
            }

            return CuentaDeMedioPago.Vacia;
        }

        var resuelta = new CuentaDeMedioPago(
            enviada.IdCuentaPresente ? enviada.IdCuenta : guardada.IdCuentaTesoreria,
            enviada.Comision ?? guardada.Comision,
            enviada.Iibb ?? guardada.RetencionIibb,
            enviada.Iva ?? guardada.RetencionIva,
            enviada.Ganancias ?? guardada.RetencionGanancias);

        if (resuelta.Comision + resuelta.RetencionIibb + resuelta.RetencionIva + resuelta.RetencionGanancias > Tope)
        {
            throw new ErrorDominio(
                "porcentajes_cuenta_suma_invalida",
                "La comisión y las retenciones, sumadas, no pueden superar el 100%.",
                400);
        }

        return resuelta;
    }

    /// <summary>
    /// Que la cuenta pueda asignarse al medio: es de la empresa del medio (un medio de todo el tenant solo toma la
    /// cuenta de la única empresa del tenant, <paramref name="idUnicaEmpresaDelTenant"/>, null si hay varias) y,
    /// si es una asignación nueva, está activa. Un medio que ya apuntaba a una cuenta desactivada después puede
    /// guardarse sin cambiarla.
    /// </summary>
    public static void ExigirCuentaAsignable(
        int? idEmpresaDelMedio, CuentaAsignable cuenta, bool asignacionNueva, int? idUnicaEmpresaDelTenant)
    {
        if (idEmpresaDelMedio is null && idUnicaEmpresaDelTenant is null)
        {
            throw new ErrorDominio(
                "cuenta_tesoreria_requiere_empresa",
                "Un medio de pago de todo el tenant solo puede tener cuenta si el tenant tiene una sola empresa. " +
                "Asignale una empresa al medio.",
                400);
        }

        if (cuenta.IdEmpresa != (idEmpresaDelMedio ?? idUnicaEmpresaDelTenant))
        {
            throw new ErrorDominio(
                "cuenta_tesoreria_de_otra_empresa",
                "La cuenta de tesorería tiene que ser de la misma empresa que el medio de pago.",
                400);
        }

        if (asignacionNueva && !cuenta.Activa)
        {
            throw new ErrorDominio(
                "cuenta_tesoreria_inactiva", "La cuenta de tesorería está inactiva: no se le pueden asignar medios de pago.", 400);
        }
    }

    private static void ExigirPorcentajeValido(string campo, decimal? valor)
    {
        if (valor is { } porcentaje
            && (porcentaje < 0m || porcentaje > Tope || porcentaje != Math.Round(porcentaje, DecimalesMaximos)))
        {
            throw new ErrorDominio(
                "porcentaje_cuenta_invalido",
                $"El campo {campo} tiene que estar entre 0 y 100, con hasta {DecimalesMaximos} decimales.",
                400);
        }
    }
}
