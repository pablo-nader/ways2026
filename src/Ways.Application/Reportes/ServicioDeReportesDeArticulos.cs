using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Ways.Application.Abstracciones;
using Ways.Application.Parametros;
using Ways.Domain.Catalogos;
using Ways.Domain.Common;
using Ways.Domain.Reportes;
using Ways.Domain.Ventas;

namespace Ways.Application.Reportes;

/// <summary>
/// stage-10-agregacion-dashboard, Slice 5: <c>GET /api/reportes/articulos/top</c> (spec
/// reportes-de-gestion: Top Artículos Ranks By Net Quantity And Revenue; design decisión 10) —
/// LINQ puro sobre <c>ItemsComprobanteVenta</c> ⋈ <c>ComprobantesVenta</c> ⋈
/// <c>TiposComprobante</c>, sin costo ni margen: esos campos viven en <c>/rentabilidad</c>,
/// gateado aparte por <c>LecturaDeRentabilidad</c> (no en esta policy). Los filtros de
/// <c>Tenant</c>/<c>BajaLogica</c> de EF aplican gratis sobre ambas tablas (design decisión 1) —
/// a diferencia de <c>LectorDeSerieTemporal</c>, esta consulta nunca corre SQL crudo.
/// </summary>
public class ServicioDeReportesDeArticulos(IWaysDbContext db, ServicioDeParametros parametros)
{
    public async Task<TopArticulos> ObtenerTopArticulosAsync(
        int idEmpresa, int? idPuntoVenta, DateOnly desde, DateOnly hasta, int? limite,
        CancellationToken ct = default)
    {
        await ExigirEmpresaAsync(idEmpresa, ct);
        var idsPuntoVenta = await ResolverPuntosDeVentaAsync(idEmpresa, idPuntoVenta, ct);
        var (zonaId, zona) = await ResolverZonaAsync(idEmpresa, idPuntoVenta, ct);

        // Granularidad.Dia es un valor interno sin efecto observable acá: este reporte no expone
        // serie ni parámetro de granularidad (design: Interfaces/Contracts — "granularidad solo en
        // las dos series"), pero el corte UTC del período igual depende de la zona resuelta
        // (design decisión 5), así que se reutiliza RangoDeReporte solo por DesdeUtc/HastaUtcExclusivo.
        var rango = RangoDeReporte.Crear(desde, hasta, Granularidad.Dia, zona);

        var lineas = idsPuntoVenta.Count == 0
            ? []
            : await db.ItemsComprobanteVenta
                .Where(i => i.IdArticulo != null)
                .Join(db.ComprobantesVenta, i => i.IdComprobanteVenta, c => c.Id, (i, c) => new { Item = i, Comprobante = c })
                .Join(db.TiposComprobante, x => x.Comprobante.IdTipoComprobante, t => t.Id, (x, t) => new { x.Item, x.Comprobante, Tipo = t })
                // spec: "clase venta, estado <> anulado" — mismo par de filtros que
                // ventas/por-punto-venta y ventas/por-vendedor (design: Raw-SQL Invariant
                // Checklist, fila articulos/top: "idem").
                .Where(x => x.Tipo.Clase == ClaseComprobante.Venta
                    && x.Comprobante.Estado != EstadoComprobante.Anulado
                    && idsPuntoVenta.Contains(x.Comprobante.IdPuntoVenta)
                    && x.Comprobante.Fecha >= rango.DesdeUtc
                    && x.Comprobante.Fecha < rango.HastaUtcExclusivo)
                .Select(x => new { x.Item.IdArticulo, x.Item.Descripcion, x.Item.Cantidad, x.Item.Total, x.Comprobante.Fecha })
                .ToListAsync(ct);

        var articulos = lineas
            .GroupBy(l => l.IdArticulo!.Value)
            .Select(g =>
            {
                // design decisión 10: nunca re-unir contra articulos — la etiqueta sale del
                // snapshot de descripcion de la línea más reciente del período dentro del grupo,
                // nunca del nombre actual del catálogo.
                var etiqueta = g.OrderByDescending(l => l.Fecha).First().Descripcion;
                return new ArticuloTop(g.Key, etiqueta, g.Sum(l => l.Cantidad), g.Sum(l => l.Total));
            })
            .OrderByDescending(a => a.Total)
            .ToList();

        if (limite is { } n && n > 0)
        {
            articulos = articulos.Take(n).ToList();
        }

        return new TopArticulos(desde, hasta, zonaId, articulos);
    }

    private async Task ExigirEmpresaAsync(int idEmpresa, CancellationToken ct)
    {
        // ADR-8: mismo 404 para "no existe" y "es de otro tenant" — el filtro de EF/RLS ya deja
        // invisible una empresa ajena, mismo criterio que ServicioDeReportesDeVentas.
        var existe = await db.Empresas.AnyAsync(e => e.Id == idEmpresa, ct);
        if (!existe)
        {
            throw ErrorDominio.NoEncontrado($"No existe la empresa {idEmpresa}.");
        }
    }

    /// <summary>Sin <paramref name="idPuntoVenta"/>: todos los puntos de venta de la empresa
    /// (empresa-wide, design decisión 5). Con él: la misma regla de pertenencia que
    /// <c>ServicioDeReportesDeVentas.ResolverPuntosDeVentaAsync</c>.</summary>
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

    /// <summary>design decisión 5: la zona se resuelve UNA vez, al alcance que pidió el caller, y
    /// se ecoa en la respuesta — un número cuyo corte de día es invisible no es auditable.</summary>
    private async Task<(string ZonaId, TimeZoneInfo Zona)> ResolverZonaAsync(
        int idEmpresa, int? idPuntoVenta, CancellationToken ct)
    {
        var resuelto = await parametros.ResolverAsync(ParametroConocido.ZonaHoraria.Clave, idEmpresa, idPuntoVenta, ct);
        var zonaId = JsonSerializer.Deserialize<string>(resuelto.Valor)!;
        return (zonaId, TimeZoneInfo.FindSystemTimeZoneById(zonaId));
    }
}
