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
/// El margen de stage-10-agregacion-dashboard, Slice 4 (design: Interfaces / Contracts —
/// <c>Rentabilidad</c>; spec rentabilidad-y-comisiones). LINQ puro sobre
/// <c>items_comprobante_venta</c> ⋈ <c>comprobantes_venta</c> ⋈ <c>tipos_comprobante</c> — los
/// filtros de <c>deleted_at</c>/<c>id_tenant</c> los aportan los query filters de EF (design
/// decisión 1: "carries the BajaLogica and Tenant query filters for free"), el resto (estado,
/// clase, alcance de punto de venta, rango de fecha) es explícito, mismo criterio que
/// <see cref="ServicioDeReportesDeVentas"/> (design: Raw-SQL Invariant Checklist, fila
/// <c>articulos/top, rentabilidad</c>).
///
/// Costo en tres estados (stage-9-costo-congelado): real (<c>costo_unitario</c> no nulo,
/// <c>costo_es_estimado = false</c>), estimado (<c>costo_es_estimado = true</c>, excluido del
/// margen salvo <c>incluirEstimados</c>) y desconocido (<c>costo_unitario IS NULL</c>, jamás
/// tratado como cero — se salta del margen y se reporta aparte en <see cref="CoberturaDeCosto"/>,
/// spec: NULL Cost Is Never Treated As Zero, And Coverage Is Mandatory).
/// </summary>
public class ServicioDeReportesDeRentabilidad(IWaysDbContext db, ServicioDeParametros parametros)
{
    public async Task<Rentabilidad> ObtenerRentabilidadAsync(
        int idEmpresa, int? idPuntoVenta, DateOnly desde, DateOnly hasta, bool incluirEstimados,
        CancellationToken ct = default)
    {
        await ExigirEmpresaAsync(idEmpresa, ct);
        var idsPuntoVenta = await ResolverPuntosDeVentaAsync(idEmpresa, idPuntoVenta, ct);
        var (zonaId, zona) = await ResolverZonaAsync(idEmpresa, idPuntoVenta, ct);

        // Granularidad.Dia es un placeholder: Rentabilidad no bucketea (sin Serie/Granularidad en
        // el contrato), solo necesita DesdeUtc/HastaUtcExclusivo — Buckets() nunca se invoca acá.
        var rango = RangoDeReporte.Crear(desde, hasta, Granularidad.Dia, zona);

        var lineas = idsPuntoVenta.Count == 0
            ? []
            : await (
                from item in db.ItemsComprobanteVenta
                join header in db.ComprobantesVenta on item.IdComprobanteVenta equals header.Id
                join tipo in db.TiposComprobante on header.IdTipoComprobante equals tipo.Id
                where header.Estado != EstadoComprobante.Anulado
                   && tipo.Clase == ClaseComprobante.Venta
                   && idsPuntoVenta.Contains(header.IdPuntoVenta)
                   && header.Fecha >= rango.DesdeUtc
                   && header.Fecha < rango.HastaUtcExclusivo
                orderby item.Id
                select new LineaDeCosto(
                    item.IdArticulo, item.Descripcion, item.Total, item.Cantidad, item.CostoUnitario, item.CostoEsEstimado))
                .ToListAsync(ct);

        var cobertura = ArmarCobertura(lineas, incluirEstimados);
        var consideradas = lineas.Where(l => EsConsiderada(l, incluirEstimados)).ToList();

        var ventaConsiderada = consideradas.Sum(l => l.Total);
        var costoConsiderado = consideradas.Sum(l => l.CostoUnitario!.Value * l.Cantidad);
        var margen = ventaConsiderada - costoConsiderado;
        var margenPorcentaje = ventaConsiderada != 0 ? margen / ventaConsiderada * 100m : (decimal?)null;

        var porArticulo = ArmarPorArticulo(consideradas);

        return new Rentabilidad(
            desde, hasta, zonaId, ventaConsiderada, costoConsiderado, margen, margenPorcentaje, cobertura, porArticulo);
    }

    /// <summary>Real, o estimada con el opt-in prendido — nunca una línea con
    /// <c>CostoUnitario IS NULL</c> (spec: NULL Cost Is Never Treated As Zero).</summary>
    private static bool EsConsiderada(LineaDeCosto linea, bool incluirEstimados) =>
        linea.CostoUnitario is not null && (!linea.CostoEsEstimado || incluirEstimados);

    /// <summary>Cobertura sobre TODAS las líneas del período, independiente de
    /// <paramref name="incluirEstimados"/> — el banner tiene que poder mostrar cuánto quedó
    /// afuera aunque el caller no haya pedido incluirlo (spec: Coverage Reflects A Mixed
    /// Period).</summary>
    private static CoberturaDeCosto ArmarCobertura(IReadOnlyList<LineaDeCosto> lineas, bool incluirEstimados)
    {
        var reales = lineas.Where(l => l.CostoUnitario is not null && !l.CostoEsEstimado).ToList();
        var estimadas = lineas.Where(l => l.CostoEsEstimado).ToList();
        var desconocidas = lineas.Where(l => l.CostoUnitario is null).ToList();

        return new CoberturaDeCosto(
            lineas.Count, reales.Count, estimadas.Count, desconocidas.Count,
            lineas.Sum(l => l.Total), reales.Sum(l => l.Total), estimadas.Sum(l => l.Total), desconocidas.Sum(l => l.Total),
            incluirEstimados);
    }

    /// <summary>Agrupa las líneas ya consideradas por <c>id_articulo</c>, etiqueta con la
    /// descripción de la primera línea del grupo (orden estable por <c>item.Id</c> en la query) —
    /// nunca re-join contra <c>articulos</c> (design decisión 10).</summary>
    private static IReadOnlyList<RentabilidadPorArticulo> ArmarPorArticulo(IReadOnlyList<LineaDeCosto> consideradas) =>
        consideradas
            // Lineas de concepto libre (IdArticulo null) se agruparian juntas bajo una sola
            // etiqueta; hoy ningun camino de escritura las produce — si la etapa que las
            // habilite llega, agrupar por (IdArticulo, Descripcion) o excluirlas de PorArticulo.
            .GroupBy(l => l.IdArticulo)
            .Select(grupo =>
            {
                var venta = grupo.Sum(l => l.Total);
                var costo = grupo.Sum(l => l.CostoUnitario!.Value * l.Cantidad);
                var margenDelGrupo = venta - costo;
                return new RentabilidadPorArticulo(
                    grupo.Key, grupo.First().Descripcion, venta, costo, margenDelGrupo,
                    venta != 0 ? margenDelGrupo / venta * 100m : (decimal?)null);
            })
            .OrderByDescending(p => p.Margen)
            .ToList();

    // ---- alcance: idéntico a ServicioDeReportesDeVentas — duplicado a propósito, sin base
    // compartida (design: File Changes asigna un archivo de orquestación propio por reporte) -----

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

    /// <summary>Misma precedencia PV → empresa → default que cualquier otro parámetro (design
    /// decisión 5) — la zona se resuelve una única vez, al alcance que pidió el caller.</summary>
    private async Task<(string ZonaId, TimeZoneInfo Zona)> ResolverZonaAsync(int idEmpresa, int? idPuntoVenta, CancellationToken ct)
    {
        var resuelto = await parametros.ResolverAsync(ParametroConocido.ZonaHoraria.Clave, idEmpresa, idPuntoVenta, ct);
        var zonaId = JsonSerializer.Deserialize<string>(resuelto.Valor)!;
        return (zonaId, TimeZoneInfo.FindSystemTimeZoneById(zonaId));
    }

    private sealed record LineaDeCosto(
        int? IdArticulo, string Descripcion, decimal Total, decimal Cantidad, decimal? CostoUnitario, bool CostoEsEstimado);
}
