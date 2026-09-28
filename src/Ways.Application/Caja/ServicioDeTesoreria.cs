using Microsoft.EntityFrameworkCore;
using Ways.Application.Abstracciones;
using Ways.Application.Exportacion;
using Ways.Domain.Caja;
using Ways.Domain.Common;
using Ways.Domain.Gastos;

namespace Ways.Application.Caja;

/// <summary>
/// G3 — el libro de tesorería encadenado (design: G2/G3 — minimal aggregation; spec tesoreria:
/// Tesorería Book Has A Read/Listing Endpoint). CERO derivación: cada fila ya trae su
/// <see cref="MovimientoTesoreria.Inicio"/>/<see cref="MovimientoTesoreria.Final"/> calculados y
/// persistidos por <see cref="ServicioDeTurnos"/> al cierre — este servicio solo lee, ordenado por
/// <c>Id</c> ascendente (design decisión 11: NUNCA por <c>Fecha</c> — el significado del libro es
/// el orden de inserción; la cadena <c>Inicio</c>/<c>Final</c> no tiene por qué coincidir con el
/// orden cronológico si dos cierres caen en el mismo segundo).
///
/// stage-tesoreria-por-empresa (PR5): la cadena es una por empresa (design: docs/10 §7) — este
/// servicio se re-ancla a <see cref="IdEmpresa"/> como filtro OBLIGATORIO (ADR-8: 404 si la
/// empresa no existe o es de otro tenant, mismo criterio que
/// <c>ServicioDeReportesDeArticulos.ExigirEmpresaAsync</c>), con <c>idPuntoVenta</c> como filtro
/// OPCIONAL adicional sobre esa misma cadena. Un filtro por punto de venta muestra un SUBCONJUNTO
/// de la cadena de la empresa: sus filas no se encadenan entre sí (el `Inicio` de una fila del
/// subconjunto no tiene por qué coincidir con el `Final` de la anterior DEL SUBCONJUNTO — sí con
/// el `Final` de la fila anterior EN LA CADENA COMPLETA, que puede no estar en la página). La UI
/// muestra una nota cuando este filtro está activo (spec: PV filter is a subset, never a second
/// chain).
///
/// judgment-day PR5, ronda 1, hallazgo confirmado #1 (export==JSON parity): un
/// <c>idPuntoVenta</c> ajeno a <paramref name="idEmpresa"/> pasaba silencioso en
/// <c>ListarAsync</c> (200 con página vacía) mientras <c>/tesoreria/export</c> lo rechazaba con
/// 400 vía <c>ServicioDeParametros.ResolverAsync</c> — las dos rutas ahora comparten la MISMA
/// validación (<see cref="ValidarPuntoVentaDeLaEmpresaAsync"/>), mismo código/mensaje/status que
/// <c>ServicioDeParametros.ValidarPuntoVentaDeLaEmpresaAsync</c> (privado, no reusable desde acá,
/// duplicado a propósito con el mismo contrato de error — nunca un código nuevo).
/// </summary>
public class ServicioDeTesoreria(IWaysDbContext db)
{
    public async Task<PaginaDeMovimientosTesoreria> ListarAsync(
        int idEmpresa,
        int? idPuntoVenta = null,
        DateTimeOffset? desde = null,
        DateTimeOffset? hasta = null,
        int pagina = 1,
        int tamanio = 25,
        CancellationToken ct = default)
    {
        await ExigirEmpresaAsync(idEmpresa, ct);

        if (idPuntoVenta is { } pvValidar)
        {
            await ValidarPuntoVentaDeLaEmpresaAsync(idEmpresa, pvValidar, ct);
        }

        pagina = Math.Max(pagina, 1);
        tamanio = Math.Clamp(tamanio, 1, 200);

        var query = ConstruirQuery(idEmpresa, idPuntoVenta, desde, hasta);

        var total = await query.CountAsync(ct);

        var paginaQuery = query
            .OrderBy(m => m.Id)
            .Skip((pagina - 1) * tamanio)
            .Take(tamanio);

        var items = await Proyectar(paginaQuery).ToListAsync(ct);

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
        int idEmpresa,
        int? idPuntoVenta,
        DateTimeOffset? desde,
        DateTimeOffset? hasta,
        int topeDeFilas,
        CancellationToken ct = default)
    {
        await ExigirEmpresaAsync(idEmpresa, ct);

        if (idPuntoVenta is { } pvValidar)
        {
            await ValidarPuntoVentaDeLaEmpresaAsync(idEmpresa, pvValidar, ct);
        }

        var query = ConstruirQuery(idEmpresa, idPuntoVenta, desde, hasta);

        var cantidad = await query.CountAsync(ct);
        GuardaDeTope.Exigir(cantidad, topeDeFilas);

        var tope = query
            .OrderBy(m => m.Id)
            .Take(topeDeFilas + 1);

        var items = await Proyectar(tope).ToListAsync(ct);

        GuardaDeTope.Exigir(items.Count, topeDeFilas);

        return items;
    }

    /// <summary>ADR-8: mismo 404 para "no existe" y "es de otro tenant" — el filtro de EF/RLS ya
    /// deja invisible una empresa ajena, mismo criterio que
    /// <c>ServicioDeReportesDeArticulos.ExigirEmpresaAsync</c>.</summary>
    private async Task ExigirEmpresaAsync(int idEmpresa, CancellationToken ct)
    {
        var existe = await db.Empresas.AnyAsync(e => e.Id == idEmpresa, ct);
        if (!existe)
        {
            throw ErrorDominio.NoEncontrado($"No existe la empresa {idEmpresa}.");
        }
    }

    /// <summary>judgment-day PR5, hallazgo #1: mismo código/mensaje/status 400 que
    /// <c>ServicioDeParametros.ValidarPuntoVentaDeLaEmpresaAsync</c> (privado en esa clase, no
    /// reusable desde acá) — un <c>idPuntoVenta</c> que no pertenece a <paramref
    /// name="idEmpresa"/> rechaza EXACTAMENTE igual en <c>ListarAsync</c> y
    /// <c>ListarParaExportacionAsync</c>: antes de este fix, el JSON devolvía 200 con página
    /// vacía mientras el export ya rechazaba con 400 (vía <c>ServicioDeParametros.ResolverAsync</c>
    /// en el endpoint de export) — ruptura de "el export es igual al JSON".</summary>
    private async Task ValidarPuntoVentaDeLaEmpresaAsync(int idEmpresa, int idPuntoVenta, CancellationToken ct)
    {
        var pertenece = await db.PuntosVenta.AnyAsync(pv => pv.Id == idPuntoVenta && pv.IdEmpresa == idEmpresa, ct);
        if (!pertenece)
        {
            throw new ErrorDominio(
                "punto_venta_no_pertenece_a_la_empresa",
                "El punto de venta indicado no pertenece a la empresa declarada.",
                400);
        }
    }

    /// <summary>Filtro compartido de <see cref="ListarAsync"/> y
    /// <see cref="ListarParaExportacionAsync"/> (design decisión 7): un solo lugar declara el
    /// predicado, nunca dos copias que puedan derivar.</summary>
    private IQueryable<MovimientoTesoreria> ConstruirQuery(
        int idEmpresa, int? idPuntoVenta, DateTimeOffset? desde, DateTimeOffset? hasta)
    {
        var query = db.MovimientosTesoreria.Where(m => m.IdEmpresa == idEmpresa);

        if (idPuntoVenta is { } pv)
        {
            query = query.Where(m => m.IdPuntoVenta == pv);
        }

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

    /// <summary>Proyección compartida de <see cref="ListarAsync"/> y
    /// <see cref="ListarParaExportacionAsync"/> — dangling-fk-read-models: tanto
    /// <c>NombrePuntoVenta</c> como <c>GastoCategoria</c>/<c>GastoConcepto</c> resuelven a
    /// <c>null</c> tanto sin punto de venta/gasto como con uno dado de baja lógica (el filtro
    /// global de baja lógica de EF ya los deja invisibles), nunca excluyen ni rompen la fila —
    /// mismo criterio que <c>ServicioDeGastos.ProyeccionDeAdministracion</c>.
    ///
    /// judgment-day PR5, hallazgo #4: <c>GastoCategoria</c>/<c>GastoConcepto</c> resolvían por DOS
    /// subqueries correlacionadas separadas contra <see cref="IWaysDbContext.Gastos"/> (una por
    /// campo) — un <c>let</c> las colapsa en UNA sola consulta (EF Core la traduce a un
    /// <c>OUTER APPLY</c>/<c>LEFT JOIN LATERAL</c> único), reusando su resultado para ambos
    /// campos.</summary>
    private IQueryable<MovimientoTesoreriaListado> Proyectar(IQueryable<MovimientoTesoreria> query) =>
        from m in query
        let gasto = m.IdGasto == null
            ? null
            : db.Gastos.Where(g => g.Id == m.IdGasto).Select(g => new { g.Categoria, g.Concepto }).FirstOrDefault()
        select new MovimientoTesoreriaListado(
            m.Id,
            m.IdEmpresa,
            m.IdPuntoVenta,
            m.IdPuntoVenta == null
                ? null
                : db.PuntosVenta.Where(pv => pv.Id == m.IdPuntoVenta).Select(pv => pv.Nombre).FirstOrDefault(),
            m.Fecha,
            m.Tipo,
            m.IdTurnoCaja,
            m.IdGasto,
            gasto == null ? null : (CategoriaGasto?)gasto.Categoria,
            gasto == null ? null : gasto.Concepto,
            m.Concepto,
            m.Inicio,
            m.Ingreso,
            m.Egreso,
            m.Final,
            m.IdEmpleado);
}
