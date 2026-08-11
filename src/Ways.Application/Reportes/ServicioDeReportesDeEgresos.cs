using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Ways.Application.Abstracciones;
using Ways.Application.Parametros;
using Ways.Domain.Catalogos;
using Ways.Domain.Common;
using Ways.Domain.Compras;
using Ways.Domain.Reportes;

namespace Ways.Application.Reportes;

/// <summary>
/// Los dos reportes de egresos de stage-10-agregacion-dashboard, slice 5 (design: File Changes,
/// Raw-SQL Invariant Checklist). <c>compras/por-proveedor</c> es LINQ puro (<c>FechaRecepcion</c>,
/// <c>Estado == Confirmada</c>) — las filas de EF ya traen <c>deleted_at IS NULL</c> y el filtro de
/// tenant gratis (<c>WaysDbContext.cs:332,355</c>). <c>gastos/resumen</c> reusa la SERIE cruda de
/// <see cref="LectorDeSerieTemporal.EjecutarGastosAsync"/> (design decisión 2: TODO el SQL crudo de
/// la etapa vive en ese único archivo) y suma un desglose por categoría vía LINQ, aparte.
///
/// Duplica <c>ExigirEmpresaAsync</c>/<c>ResolverPuntosDeVentaAsync</c>/<c>ResolverZonaAsync</c> de
/// <see cref="ServicioDeReportesDeVentas"/> en vez de extraer una base compartida: el design no
/// define una (File Changes lista un archivo POR tipo de reporte) y una base común hubiera obligado
/// a tocar <c>ServicioDeReportesDeVentas.cs</c>, un archivo que las slices 3 y 4 —en paralelo esta
/// misma noche— también extienden.
/// </summary>
public class ServicioDeReportesDeEgresos(IWaysDbContext db, LectorDeSerieTemporal lector, ServicioDeParametros parametros)
{
    /// <summary>Sin bucketing temporal (design: Raw-SQL Invariant Checklist — mecanismo LINQ, sin
    /// serie): agrupa directo por proveedor. El rango sigue resolviéndose en la zona del punto de
    /// venta (<see cref="RangoDeReporte"/>, granularidad <see cref="Granularidad.Dia"/> como valor
    /// fijo — no se expone en la query, la etapa no arma buckets acá) para que el corte de
    /// "fecha_recepcion &gt;= desde" no dependa del huso del contenedor.</summary>
    public async Task<ComprasPorProveedor> ObtenerComprasPorProveedorAsync(
        int idEmpresa, int? idPuntoVenta, DateOnly desde, DateOnly hasta, CancellationToken ct = default)
    {
        await ExigirEmpresaAsync(idEmpresa, ct);
        var idsPuntoVenta = await ResolverPuntosDeVentaAsync(idEmpresa, idPuntoVenta, ct);

        if (idsPuntoVenta.Count == 0)
        {
            return new ComprasPorProveedor(desde, hasta, [], 0m);
        }

        var (_, zona) = await ResolverZonaAsync(idEmpresa, idPuntoVenta, ct);
        var rango = RangoDeReporte.Crear(desde, hasta, Granularidad.Dia, zona);

        var filas = await db.ComprobantesCompra
            .Where(c => idsPuntoVenta.Contains(c.IdPuntoVenta))
            .Where(c => c.Estado == EstadoCompra.Confirmada)
            .Where(c => c.FechaRecepcion >= rango.DesdeUtc && c.FechaRecepcion < rango.HastaUtcExclusivo)
            .GroupBy(c => c.IdProveedor)
            .Select(g => new { IdProveedor = g.Key, Total = g.Sum(c => c.Total), Cantidad = g.Count() })
            .ToListAsync(ct);

        var idsProveedor = filas.Select(f => f.IdProveedor).ToList();
        var razonSocialPorId = await db.Proveedores
            .Where(p => idsProveedor.Contains(p.Id))
            .ToDictionaryAsync(p => p.Id, p => p.RazonSocial, ct);

        var porProveedor = filas
            .Select(f => new CompraPorProveedor(
                f.IdProveedor, razonSocialPorId.GetValueOrDefault(f.IdProveedor, string.Empty), f.Total, f.Cantidad))
            .OrderByDescending(f => f.Total)
            .ToList();

        return new ComprasPorProveedor(desde, hasta, porProveedor, porProveedor.Sum(f => f.Total));
    }

    /// <summary>Reusa <see cref="LectorDeSerieTemporal.EjecutarGastosAsync"/> para la serie
    /// bucketeada (mismo left-join de gap-fill en C# que <c>ServicioDeReportesDeVentas.
    /// ObtenerResumenAsync</c>, design decisión 4) y agrega, aparte, un desglose LINQ por
    /// categoría sobre el mismo rango y alcance — <c>gastos</c> no necesita SQL crudo para
    /// agrupar por columna, solo para bucketear por fecha (design decisión 1).</summary>
    public async Task<ResumenDeGastos> ObtenerGastosResumenAsync(
        int idEmpresa, int? idPuntoVenta, DateOnly desde, DateOnly hasta, Granularidad granularidad,
        CancellationToken ct = default)
    {
        var idTenant = await ExigirEmpresaAsync(idEmpresa, ct);
        var idsPuntoVenta = await ResolverPuntosDeVentaAsync(idEmpresa, idPuntoVenta, ct);
        var (zonaId, zona) = await ResolverZonaAsync(idEmpresa, idPuntoVenta, ct);

        var rango = RangoDeReporte.Crear(desde, hasta, granularidad, zona);

        IReadOnlyList<FilaSerieDeGastos> filas = idsPuntoVenta.Count == 0
            ? Array.Empty<FilaSerieDeGastos>()
            : await lector.EjecutarGastosAsync(
                granularidad, zonaId, idTenant, idsPuntoVenta, rango.DesdeUtc, rango.HastaUtcExclusivo, ct);

        var filaPorBucket = filas.ToDictionary(f => f.Bucket);

        var serie = rango.Buckets()
            .Select(bucket =>
            {
                filaPorBucket.TryGetValue(bucket.Inicio, out var fila);
                return new BucketDeGastos(bucket.Etiqueta, bucket.Inicio, fila?.Importe ?? 0m);
            })
            .ToList();

        var porCategoria = idsPuntoVenta.Count == 0
            ? []
            : await db.Gastos
                .Where(g => idsPuntoVenta.Contains(g.IdPuntoVenta))
                .Where(g => g.Fecha >= rango.DesdeUtc && g.Fecha < rango.HastaUtcExclusivo)
                .GroupBy(g => g.Categoria)
                .Select(g => new GastoPorCategoria(g.Key, g.Sum(x => x.Importe), g.Count()))
                .ToListAsync(ct);

        return new ResumenDeGastos(desde, hasta, granularidad, zonaId, serie, filas.Sum(f => f.Importe), porCategoria);
    }

    // ---- resolución de alcance — duplicada de ServicioDeReportesDeVentas por diseño (doc-comment de clase) ----

    private async Task<int> ExigirEmpresaAsync(int idEmpresa, CancellationToken ct)
    {
        var idTenant = await db.Empresas
            .Where(e => e.Id == idEmpresa)
            .Select(e => (int?)e.IdTenant)
            .FirstOrDefaultAsync(ct);

        // ADR-8: mismo 404 para "no existe" y "es de otro tenant".
        return idTenant ?? throw ErrorDominio.NoEncontrado($"No existe la empresa {idEmpresa}.");
    }

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

    private async Task<(string ZonaId, TimeZoneInfo Zona)> ResolverZonaAsync(
        int idEmpresa, int? idPuntoVenta, CancellationToken ct)
    {
        var resuelto = await parametros.ResolverAsync(ParametroConocido.ZonaHoraria.Clave, idEmpresa, idPuntoVenta, ct);
        var zonaId = JsonSerializer.Deserialize<string>(resuelto.Valor)!;
        return (zonaId, TimeZoneInfo.FindSystemTimeZoneById(zonaId));
    }
}
