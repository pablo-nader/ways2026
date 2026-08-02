using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Ways.Application.Abstracciones;
using Ways.Domain.Catalogos;
using Ways.Domain.Common;

namespace Ways.Application.Parametros;

/// <summary>
/// Resolución de <c>parametros</c> (ADR-13): punto de venta gana sobre empresa, empresa
/// gana sobre el default declarado. <see cref="ResolucionDeParametros"/> (Domain, pura) hace
/// la precedencia; acá solo se trae el puñado de filas candidatas (a lo sumo dos por clave)
/// y se valida la clave contra el registro tipado.
///
/// <see cref="ITenantActual"/>/ADR-10: no hay "empresa actual" en la sesión todavía (la
/// selección de empresa/punto de venta es una etapa operativa posterior), así que
/// <paramref name="idEmpresa"/>/<c>idPuntoVenta</c> viajan explícitos en cada llamada.
/// </summary>
public class ServicioDeParametros(IWaysDbContext db, IRelojDelSistema reloj)
{
    public async Task<ParametroResuelto> ResolverAsync(
        string clave, int idEmpresa, int? idPuntoVenta, CancellationToken ct = default)
    {
        // Valida la clave antes de tocar la base: una clave desconocida no debería ni
        // disparar la query.
        ParametroConocido.Buscar(clave);

        var candidatos = await db.Parametros
            .Where(p => p.Clave == clave && p.IdEmpresa == idEmpresa
                && (p.IdPuntoVenta == null || p.IdPuntoVenta == idPuntoVenta))
            .ToListAsync(ct);

        var valor = ResolucionDeParametros.Resolver(clave, candidatos, idPuntoVenta);
        return new ParametroResuelto(clave, valor);
    }

    public async Task<IReadOnlyList<ParametroListado>> ListarAsync(
        int idEmpresa, CancellationToken ct = default) =>
        await db.Parametros
            .Where(p => p.IdEmpresa == idEmpresa)
            .OrderBy(p => p.Clave).ThenBy(p => p.IdPuntoVenta)
            .Select(p => new ParametroListado(p.Id, p.Clave, p.Valor, p.IdPuntoVenta))
            .ToListAsync(ct);

    /// <summary>Alta o edición (upsert por <c>clave</c> + <c>idPuntoVenta</c>, mismo par que
    /// los índices únicos parciales de ADR-13).</summary>
    public async Task<ParametroListado> EstablecerAsync(
        int idEmpresa, ParametroAlta datos, CancellationToken ct = default)
    {
        var conocido = ParametroConocido.Buscar(datos.Clave);
        ValidarTipo(conocido, datos.Valor);

        var existente = await db.Parametros.FirstOrDefaultAsync(
            p => p.IdEmpresa == idEmpresa && p.Clave == datos.Clave && p.IdPuntoVenta == datos.IdPuntoVenta, ct);

        var ahora = reloj.Ahora;

        if (existente is null)
        {
            existente = new Parametro
            {
                IdEmpresa = idEmpresa,
                IdPuntoVenta = datos.IdPuntoVenta,
                Clave = datos.Clave,
                Valor = datos.Valor,
                CreatedAt = ahora,
                UpdatedAt = ahora
            };
            db.Parametros.Add(existente);
        }
        else
        {
            existente.Valor = datos.Valor;
            existente.UpdatedAt = ahora;
        }

        await db.SaveChangesAsync(ct);

        return new ParametroListado(existente.Id, existente.Clave, existente.Valor, existente.IdPuntoVenta);
    }

    private static void ValidarTipo(ParametroConocido conocido, string valorJson)
    {
        try
        {
            JsonSerializer.Deserialize(valorJson, conocido.TipoClr);
        }
        catch (JsonException)
        {
            throw new ErrorDominio(
                "parametro_tipo_invalido",
                $"El valor de '{conocido.Clave}' tiene que ser un {conocido.TipoClr.Name} válido.",
                400);
        }
    }
}
