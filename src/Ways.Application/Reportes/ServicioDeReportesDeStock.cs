using Microsoft.EntityFrameworkCore;
using Ways.Application.Abstracciones;

namespace Ways.Application.Reportes;

/// <summary>
/// stage-11-exportacion-reportes, Slice 9 (proposal decisión 10; design: "Two cap shapes, by
/// report shape" — <c>/stock/existencias</c> es un AGREGADO acotado por construcción, sin
/// <c>COUNT(*)</c> propio; spec reportes-de-gestion: Existencias Report Joins Stock To Artículos
/// Under The Same Gate): <c>GET /api/reportes/stock/existencias</c> — LINQ puro sobre
/// <c>stock</c> ⋈ <c>articulos</c> para UN punto de venta, cubierto por <c>ix_stock_punto_venta</c>.
/// Sin <c>idArticulo</c> (spec: Existencias Needs No idArticulo, Unlike GET /api/stock) y sin
/// <c>idEmpresa</c> (mismo criterio que <c>ServicioDeTesoreria</c>: la ruta solo pide
/// <c>idPuntoVenta</c>, la empresa se resuelve del lado HTTP cuando hace falta para el export).
/// Los filtros de <c>Tenant</c>/<c>BajaLogica</c> de EF aplican gratis sobre <c>articulos</c>
/// (design decisión 1); <c>stock</c> usa su propio filtro de tenant manual
/// (<c>WaysDbContext.AplicarFiltroDeTenantEnStock</c>) — ambos activos sobre este join.
/// </summary>
public class ServicioDeReportesDeStock(IWaysDbContext db)
{
    public async Task<Existencias> ObtenerExistenciasAsync(int idPuntoVenta, CancellationToken ct = default)
    {
        // Cláusula bajo prueba (mutation-proof-tests): Where(s => s.IdPuntoVenta == idPuntoVenta)
        // es lo único que discrimina un punto de venta del otro — mezclar dos PVs del mismo tenant
        // en una sola respuesta rompería el significado del reporte tanto como en
        // ServicioDeTesoreria (design decisión 11, misma familia de bug).
        var filas = await db.Stock
            .Where(s => s.IdPuntoVenta == idPuntoVenta)
            .Join(db.Articulos, s => s.IdArticulo, a => a.Id, (s, a) => new { a.Id, a.Nombre, s.Cantidad })
            .OrderBy(x => x.Id)
            .Select(x => new FilaExistencia(x.Id, x.Nombre, x.Cantidad))
            .ToListAsync(ct);

        return new Existencias(idPuntoVenta, filas);
    }
}
