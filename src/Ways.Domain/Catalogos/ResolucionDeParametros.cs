namespace Ways.Domain.Catalogos;

/// <summary>
/// Precedencia de <c>parametros</c>, pura y testeable sin base de datos (ADR-13). Una clave de
/// alcance <see cref="AlcanceDeParametro.Caja"/>: punto de venta gana sobre empresa, empresa sobre el
/// default declarado en <see cref="ParametroConocido"/>. Una de alcance
/// <see cref="AlcanceDeParametro.Sucursal"/>: sucursal gana sobre empresa, empresa sobre el default, y las
/// filas por punto de venta no cuentan. Infrastructure trae las filas candidatas de la clave — la
/// unicidad por nivel la garantizan los índices parciales de la migración, esta función no la revalida.
/// </summary>
public static class ResolucionDeParametros
{
    /// <param name="idSucursal">Sucursal del punto de venta en contexto. Es obligatoria para resolver una clave
    /// de alcance sucursal cuando hay <paramref name="idPuntoVenta"/>: omitirla saltearía el nivel sucursal sin
    /// aviso, así que se rechaza.</param>
    public static string Resolver(
        string clave, IReadOnlyCollection<Parametro> candidatos, int? idPuntoVenta, int? idSucursal = null)
    {
        var conocido = ParametroConocido.Buscar(clave);

        var deLaEmpresa = candidatos.FirstOrDefault(c => c.IdPuntoVenta is null && c.IdSucursal is null);

        if (conocido.Alcance == AlcanceDeParametro.Sucursal)
        {
            if (idPuntoVenta is not null && idSucursal is null)
            {
                throw new InvalidOperationException(
                    $"'{clave}' es un parámetro de sucursal: resolverlo desde un punto de venta exige su sucursal.");
            }

            var delaSucursal = idSucursal is not null
                ? candidatos.FirstOrDefault(c => c.IdSucursal == idSucursal)
                : null;

            return delaSucursal?.Valor ?? deLaEmpresa?.Valor ?? conocido.ValorPorDefecto;
        }

        var delPuntoVenta = idPuntoVenta is not null
            ? candidatos.FirstOrDefault(c => c.IdPuntoVenta == idPuntoVenta)
            : null;

        return delPuntoVenta?.Valor ?? deLaEmpresa?.Valor ?? conocido.ValorPorDefecto;
    }
}
