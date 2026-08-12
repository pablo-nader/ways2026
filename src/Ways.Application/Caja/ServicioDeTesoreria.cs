using Microsoft.EntityFrameworkCore;
using Ways.Application.Abstracciones;
using Ways.Application.Exportacion;
using Ways.Domain.Caja;

namespace Ways.Application.Caja;

/// <summary>
/// G3 — el libro de tesorería encadenado (design: G2/G3 — minimal aggregation; spec tesoreria:
/// Tesorería Book Has A Read/Listing Endpoint). CERO derivación: cada fila ya trae su
/// <see cref="MovimientoTesoreria.Inicio"/>/<see cref="MovimientoTesoreria.Final"/> calculados y
/// persistidos por <see cref="ServicioDeTurnos"/> al cierre — este servicio solo lee, ordenado por
/// <c>Id</c> ascendente (design decisión 11: NUNCA por <c>Fecha</c> — el significado del libro es
/// el orden de inserción; la cadena <c>Inicio</c>/<c>Final</c> no tiene por qué coincidir con el
/// orden cronológico si dos cierres caen en el mismo segundo). <c>IdPuntoVenta</c> es obligatorio
/// (a diferencia de G2): mezclar puntos de venta rompería el propio significado de la cadena.
/// </summary>
public class ServicioDeTesoreria(IWaysDbContext db)
{
    public async Task<PaginaDeMovimientosTesoreria> ListarAsync(
        int idPuntoVenta,
        DateTimeOffset? desde = null,
        DateTimeOffset? hasta = null,
        int pagina = 1,
        int tamanio = 25,
        CancellationToken ct = default)
    {
        pagina = Math.Max(pagina, 1);
        tamanio = Math.Clamp(tamanio, 1, 200);

        var query = ConstruirQuery(idPuntoVenta, desde, hasta);

        var total = await query.CountAsync(ct);

        var items = await query
            .OrderBy(m => m.Id)
            .Skip((pagina - 1) * tamanio)
            .Take(tamanio)
            .Select(m => new MovimientoTesoreriaListado(
                m.Id, m.IdPuntoVenta, m.Fecha, m.Tipo, m.IdTurnoCaja, m.Concepto, m.Inicio, m.Ingreso, m.Egreso,
                m.Final, m.IdEmpleado))
            .ToListAsync(ct);

        return new PaginaDeMovimientosTesoreria(items, total, pagina, tamanio);
    }

    /// <summary>stage-11-exportacion-reportes (Slice 7, design decisión 7): mismo
    /// <see cref="ConstruirQuery"/> que <see cref="ListarAsync"/>, <c>Contar → refuse → lectura
    /// única con .Take(topeDeFilas + 1)</c>, nunca paginada — la tesorería es un LISTADO (design
    /// decisión 6), corre <c>COUNT(*)</c> como <c>ServicioDeVentas.ListarParaExportacionAsync</c>,
    /// a diferencia de un agregado acotado por construcción. El segundo
    /// <see cref="GuardaDeTope.Exigir"/> es el backstop de carrera: si la lectura trae
    /// <c>topeDeFilas + 1</c> filas, el <c>COUNT(*)</c> de arriba quedó desactualizado.</summary>
    public async Task<IReadOnlyList<MovimientoTesoreriaListado>> ListarParaExportacionAsync(
        int idPuntoVenta,
        DateTimeOffset? desde,
        DateTimeOffset? hasta,
        int topeDeFilas,
        CancellationToken ct = default)
    {
        var query = ConstruirQuery(idPuntoVenta, desde, hasta);

        var cantidad = await query.CountAsync(ct);
        GuardaDeTope.Exigir(cantidad, topeDeFilas);

        var items = await query
            .OrderBy(m => m.Id)
            .Take(topeDeFilas + 1)
            .Select(m => new MovimientoTesoreriaListado(
                m.Id, m.IdPuntoVenta, m.Fecha, m.Tipo, m.IdTurnoCaja, m.Concepto, m.Inicio, m.Ingreso, m.Egreso,
                m.Final, m.IdEmpleado))
            .ToListAsync(ct);

        GuardaDeTope.Exigir(items.Count, topeDeFilas);

        return items;
    }

    /// <summary>Filtro compartido de <see cref="ListarAsync"/> y
    /// <see cref="ListarParaExportacionAsync"/> (design decisión 7): un solo lugar declara el
    /// predicado, nunca dos copias que puedan derivar.</summary>
    private IQueryable<MovimientoTesoreria> ConstruirQuery(int idPuntoVenta, DateTimeOffset? desde, DateTimeOffset? hasta)
    {
        var query = db.MovimientosTesoreria.Where(m => m.IdPuntoVenta == idPuntoVenta);

        if (desde is { } d)
        {
            query = query.Where(m => m.Fecha >= d);
        }

        if (hasta is { } h)
        {
            query = query.Where(m => m.Fecha <= h);
        }

        return query;
    }
}
