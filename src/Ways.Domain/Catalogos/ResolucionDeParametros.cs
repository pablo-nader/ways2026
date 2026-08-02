namespace Ways.Domain.Catalogos;

/// <summary>
/// Precedencia de <c>parametros</c>, pura y testeable sin base de datos (ADR-13):
/// punto de venta gana sobre empresa, empresa gana sobre el default declarado en
/// <see cref="ParametroConocido"/>. Infrastructure trae a lo sumo dos filas candidatas por
/// clave (una de punto de venta, otra de empresa) — la unicidad la garantizan los índices
/// parciales de la migración, esta función no la revalida.
/// </summary>
public static class ResolucionDeParametros
{
    public static string Resolver(
        string clave, IReadOnlyCollection<Parametro> candidatos, int? idPuntoVenta)
    {
        var conocido = ParametroConocido.Buscar(clave);

        var delPuntoVenta = idPuntoVenta is not null
            ? candidatos.FirstOrDefault(c => c.IdPuntoVenta == idPuntoVenta)
            : null;

        var deLaEmpresa = candidatos.FirstOrDefault(c => c.IdPuntoVenta is null);

        return delPuntoVenta?.Valor ?? deLaEmpresa?.Valor ?? conocido.ValorPorDefecto;
    }
}
