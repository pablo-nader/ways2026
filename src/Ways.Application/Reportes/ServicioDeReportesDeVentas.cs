using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Ways.Application.Abstracciones;
using Ways.Application.Parametros;
using Ways.Domain.Catalogos;
using Ways.Domain.Common;
using Ways.Domain.Reportes;

namespace Ways.Application.Reportes;

/// <summary>
/// El agregado de ventas de stage-10-agregacion-dashboard (design: Data Flow). Resuelve el
/// alcance (empresa → puntos de venta), la zona horaria (<c>ServicioDeParametros</c>, misma
/// precedencia punto de venta → empresa → default que cualquier otro parámetro), arma el
/// <see cref="RangoDeReporte"/> puro y left-joinea sus buckets contra las filas crudas de
/// <see cref="LectorDeSerieTemporal"/> — un bucket sin ventas queda en <c>0</c>, nunca
/// desaparece (design decisión 4).
/// </summary>
public class ServicioDeReportesDeVentas(IWaysDbContext db, LectorDeSerieTemporal lector, ServicioDeParametros parametros)
{
    public async Task<ResumenDeVentas> ObtenerResumenAsync(
        int idEmpresa, int? idPuntoVenta, DateOnly desde, DateOnly hasta, Granularidad granularidad,
        CancellationToken ct = default)
    {
        var idTenant = await ExigirEmpresaAsync(idEmpresa, ct);
        var idsPuntoVenta = await ResolverPuntosDeVentaAsync(idEmpresa, idPuntoVenta, ct);
        var (zonaId, zona) = await ResolverZonaAsync(idEmpresa, idPuntoVenta, ct);

        var rango = RangoDeReporte.Crear(desde, hasta, granularidad, zona);

        IReadOnlyList<FilaSerieDeVentas> filas = idsPuntoVenta.Count == 0
            ? Array.Empty<FilaSerieDeVentas>()
            : await lector.EjecutarVentasAsync(
                granularidad, zonaId, idTenant, idsPuntoVenta, rango.DesdeUtc, rango.HastaUtcExclusivo, ct);

        var filaPorBucket = filas.ToDictionary(f => f.Bucket);

        var serie = rango.Buckets()
            .Select(bucket =>
            {
                filaPorBucket.TryGetValue(bucket.Inicio, out var fila);
                var cantidadTx = fila?.CantidadTx ?? 0;
                var ticketPromedioDelBucket = cantidadTx > 0 ? fila!.NetoTx / cantidadTx : (decimal?)null;
                return new BucketDeVentas(bucket.Etiqueta, bucket.Inicio, fila?.Neto ?? 0m, cantidadTx, ticketPromedioDelBucket);
            })
            .ToList();

        var netoVendido = filas.Sum(f => f.Neto);
        var cantidadTxTotal = filas.Sum(f => f.CantidadTx);
        var netoTxTotal = filas.Sum(f => f.NetoTx);
        var ticketPromedio = cantidadTxTotal > 0 ? netoTxTotal / cantidadTxTotal : (decimal?)null;
        var cantidadNcx = filas.Sum(f => f.CantidadNcx);
        var netoNcx = filas.Sum(f => f.NetoNcx);

        return new ResumenDeVentas(
            desde, hasta, granularidad, zonaId, serie, netoVendido, cantidadTxTotal, ticketPromedio, cantidadNcx, netoNcx);
    }

    private async Task<int> ExigirEmpresaAsync(int idEmpresa, CancellationToken ct)
    {
        var idTenant = await db.Empresas
            .Where(e => e.Id == idEmpresa)
            .Select(e => (int?)e.IdTenant)
            .FirstOrDefaultAsync(ct);

        // ADR-8: mismo 404 para "no existe" y "es de otro tenant" — el filtro de EF/RLS ya deja
        // invisible una empresa ajena, mismo criterio que el resto de los servicios de Application.
        return idTenant ?? throw ErrorDominio.NoEncontrado($"No existe la empresa {idEmpresa}.");
    }

    /// <summary>Sin <paramref name="idPuntoVenta"/>: todos los puntos de venta de la empresa
    /// (empresa-wide, design decisión 5). Con él: la misma regla de pertenencia que
    /// <c>ServicioDeParametros.ValidarPuntoVentaDeLaEmpresaAsync</c> — un punto de venta real pero
    /// de otra empresa del mismo tenant no tiene FK que lo impida (nada en el esquema lo evita),
    /// así que esta consulta, scopeada por tenant vía el filtro de EF, es quien lo valida.</summary>
    private async Task<IReadOnlyList<int>> ResolverPuntosDeVentaAsync(int idEmpresa, int? idPuntoVenta, CancellationToken ct)
    {
        var puntosDeLaEmpresa = db.PuntosVenta.Where(pv => pv.IdEmpresa == idEmpresa);

        if (idPuntoVenta is { } id)
        {
            var pertenece = await puntosDeLaEmpresa.AnyAsync(pv => pv.Id == id, ct);
            if (!pertenece)
            {
                throw new ErrorDominio(
                    "punto_venta_no_pertenece_a_la_empresa",
                    "El punto de venta indicado no pertenece a la empresa declarada.",
                    400);
            }

            return [id];
        }

        return await puntosDeLaEmpresa.Select(pv => pv.Id).ToListAsync(ct);
    }

    /// <summary>design decisión 5: la zona se resuelve UNA vez, al alcance que pidió el caller —
    /// con <paramref name="idPuntoVenta"/>, PV → empresa → default; sin él, empresa → default,
    /// ignorando cualquier override de punto de venta (mismo comportamiento que
    /// <c>ServicioDeParametros.ResolverAsync</c> con <c>idPuntoVenta = null</c>, que solo mira las
    /// filas de nivel empresa).</summary>
    private async Task<(string ZonaId, TimeZoneInfo Zona)> ResolverZonaAsync(
        int idEmpresa, int? idPuntoVenta, CancellationToken ct)
    {
        var resuelto = await parametros.ResolverAsync(ParametroConocido.ZonaHoraria.Clave, idEmpresa, idPuntoVenta, ct);
        var zonaId = JsonSerializer.Deserialize<string>(resuelto.Valor)!;
        return (zonaId, TimeZoneInfo.FindSystemTimeZoneById(zonaId));
    }
}
