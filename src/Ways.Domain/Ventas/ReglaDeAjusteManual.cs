using Ways.Domain.Common;

namespace Ways.Domain.Ventas;

/// <summary>
/// Regla pura del porcentaje de ajuste manual de una línea de venta (negativo = descuento,
/// positivo = recargo). Mismo rango y precisión que la CHECK
/// <c>ck_items_comprobante_venta_ajuste_manual_porcentaje_valido</c>: el servicio rechaza acá con un
/// 400 de dominio y la CHECK queda como backstop de una escritura cruda.
/// </summary>
public static class ReglaDeAjusteManual
{
    public const decimal PorcentajeMinimo = -100m;
    public const decimal PorcentajeMaximo = 100m;
    public const int DecimalesMaximos = 2;

    /// <summary><c>null</c> es "sin ajuste" y siempre es válido. Cero no lo es: un ajuste de 0 %
    /// no cambia nada y la base lo representa con <c>NULL</c>, nunca con 0.</summary>
    public static void Validar(decimal? porcentaje)
    {
        if (porcentaje is not { } p)
        {
            return;
        }

        if (p == 0m
            || p < PorcentajeMinimo
            || p > PorcentajeMaximo
            || decimal.Round(p, DecimalesMaximos, MidpointRounding.AwayFromZero) != p)
        {
            throw new ErrorDominio(
                "ajuste_manual_invalido",
                "El ajuste manual tiene que ser un porcentaje distinto de cero, entre -100 y 100, con hasta 2 decimales.",
                400);
        }
    }
}
