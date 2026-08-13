using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Ways.Application.Abstracciones;
using Ways.Application.Exportacion;
using Ways.Application.Parametros;
using Ways.Domain.Catalogos;
using Ways.Domain.Common;
using Ways.Domain.Stock;

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
/// Los filtros globales de <c>Tenant</c>/<c>BajaLogica</c> de EF aplican gratis sobre
/// <c>articulos</c>; <c>stock</c> usa su propio filtro de tenant manual
/// (<c>WaysDbContext.AplicarFiltroDeTenantEnStock</c>) — ambos activos sobre este join. Trade-off
/// deliberado: el stock de un artículo eliminado queda oculto del reporte (cubierto por
/// <c>ExistenciasTests.UnArticuloEliminadoNuncaApareceEnLasExistencias</c>).
///
/// stage-12-lotes-vencimientos, Slice 13 (design decisión 15/16/17, spec lotes-y-vencimientos:
/// "Vencimientos Report Resolves 'Hoy' In The Punto De Venta's Own Zona Horaria, With An Export
/// Sibling"): agrega <see cref="ObtenerVencimientosAsync"/>/<see cref="ObtenerResumenDeVencimientosAsync"/>/
/// <see cref="ObtenerVencimientosParaExportacionAsync"/>. A diferencia de existencias (agregado
/// acotado por el catálogo), vencimientos es un LISTADO cuyo volumen crece con el tiempo (design
/// decisión 17) — solo el export exige tope (<c>Contar → rechazar → .Take(tope + 1)</c>, mismo
/// shape que <c>ServicioDeHistoricoDeCajas.ListarCierresParaExportacionAsync</c>); el JSON no lo
/// pagina, mismo criterio que <c>ObtenerExistenciasAsync</c>.
/// </summary>
public class ServicioDeReportesDeStock(IWaysDbContext db, ServicioDeParametros parametros, IRelojDelSistema reloj)
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

    /// <summary>Lotes con saldo positivo del punto de venta, clasificados en la zona horaria del
    /// PV — sin paginar (task 13.1). <paramref name="dias"/> ausente ⇒ se resuelve
    /// <c>dias_alerta_vencimiento</c> (PV → empresa → default).</summary>
    public async Task<Vencimientos> ObtenerVencimientosAsync(
        int idPuntoVenta, int? dias, CancellationToken ct = default)
    {
        var (idEmpresa, zonaId, hoy) = await ResolverContextoAsync(idPuntoVenta, ct);
        var diasDeAlerta = dias ?? await ResolverDiasAlertaAsync(idEmpresa, idPuntoVenta, ct);

        var filas = await ConstruirQueryDeVencimientos(idPuntoVenta).ToListAsync(ct);

        return new Vencimientos(idPuntoVenta, hoy, diasDeAlerta, zonaId, Clasificar(filas, hoy, diasDeAlerta));
    }

    /// <summary>Tile de Tablero (task 13.2) — reusa <see cref="ObtenerVencimientosAsync"/> con
    /// <c>dias</c> nulo (el horizonte default) y agrupa por <see cref="EstadoDeVencimiento"/>: el
    /// tile nunca puede divergir del reporte porque no hay una segunda query de agregación.</summary>
    public async Task<ResumenDeVencimientos> ObtenerResumenDeVencimientosAsync(
        int idPuntoVenta, CancellationToken ct = default)
    {
        var vencimientos = await ObtenerVencimientosAsync(idPuntoVenta, dias: null, ct);

        return new ResumenDeVencimientos(
            idPuntoVenta,
            vencimientos.Filas.Count(f => f.Estado == EstadoDeVencimiento.Vencido),
            vencimientos.Filas.Count(f => f.Estado == EstadoDeVencimiento.PorVencer),
            vencimientos.Filas.Count(f => f.Estado == EstadoDeVencimiento.SinFecha));
    }

    /// <summary>design decisión 17: vencimientos es un LISTADO, no un agregado — <c>Contar →
    /// rechazar → lectura única con .Take(tope + 1)</c>, nunca una guarda sobre
    /// <c>TablaExportable.Filas.Count</c> ya mapeada (esa es la forma de un agregado acotado por
    /// construcción, como <see cref="ObtenerExistenciasAsync"/>). Mismo <see cref="ConstruirQueryDeVencimientos"/>
    /// que el JSON, así que las figuras del export son estructuralmente las mismas que las del
    /// endpoint — nunca dos consultas que puedan divergir.</summary>
    public async Task<Vencimientos> ObtenerVencimientosParaExportacionAsync(
        int idPuntoVenta, int? dias, int topeDeFilas, CancellationToken ct = default)
    {
        var (idEmpresa, zonaId, hoy) = await ResolverContextoAsync(idPuntoVenta, ct);
        var diasDeAlerta = dias ?? await ResolverDiasAlertaAsync(idEmpresa, idPuntoVenta, ct);

        var query = ConstruirQueryDeVencimientos(idPuntoVenta);

        var cantidad = await query.CountAsync(ct);
        GuardaDeTope.Exigir(cantidad, topeDeFilas);

        var filas = await query.Take(topeDeFilas + 1).ToListAsync(ct);
        GuardaDeTope.Exigir(filas.Count, topeDeFilas);

        return new Vencimientos(idPuntoVenta, hoy, diasDeAlerta, zonaId, Clasificar(filas, hoy, diasDeAlerta));
    }

    /// <summary>Proyección cruda de <c>stock_lotes ⋈ lotes ⋈ articulos</c> (spec: "lot rows...
    /// with a positive stock_lotes.cantidad") — <c>Cantidad &gt; 0</c> estrictamente, nunca
    /// <c>&lt;&gt; 0</c> (spec: "A zero-balance lot never appears in the report"; un saldo
    /// negativo tampoco debería listarse acá, mismo criterio). Orden <c>fecha_vencimiento ASC</c>
    /// ANTES del <c>select</c> hacia el record: EF Core no traduce un <c>OrderBy</c> sobre la
    /// propiedad de un objeto recién construido (mismo obstáculo de traducción que
    /// <c>ServicioDeReportesDeVentas.ComprobantesVentaDelPeriodo</c> documenta para <c>GroupBy</c>).
    /// Postgres ya ordena NULL último en ascendente por default, sin necesitar <c>NULLS LAST</c>
    /// explícito.</summary>
    private IQueryable<FilaCruda> ConstruirQueryDeVencimientos(int idPuntoVenta) =>
        from stockLote in db.StockLotes
        where stockLote.IdPuntoVenta == idPuntoVenta && stockLote.Cantidad > 0m
        join lote in db.Lotes on stockLote.IdLote equals lote.Id
        join articulo in db.Articulos on lote.IdArticulo equals articulo.Id
        orderby lote.FechaVencimiento
        select new FilaCruda(articulo.Id, articulo.Nombre, lote.Id, lote.Codigo, lote.FechaVencimiento, stockLote.Cantidad);

    private static IReadOnlyList<FilaDeVencimiento> Clasificar(
        IReadOnlyList<FilaCruda> filas, DateOnly hoy, int diasDeAlerta) =>
        filas
            .Select(f => new FilaDeVencimiento(
                f.IdArticulo, f.Articulo, f.IdLote, f.CodigoLote, f.FechaVencimiento, f.Cantidad,
                ReglaDeLotes.Clasificar(f.FechaVencimiento, hoy, diasDeAlerta)))
            .ToList();

    /// <summary>"hoy" es el ÚNICO valor sensible a zona del reporte (design decisión 15) —
    /// <c>reloj.Ahora</c> (instante absoluto) se convierte a la zona del PV ANTES de tomar el
    /// <c>DateOnly</c>. Mutation target (task 13.5): reemplazar la conversión por
    /// <c>reloj.Ahora.UtcDateTime</c> directo tiene que hacer fallar el test de zona (spec: "'Hoy'
    /// Is Resolved In The Punto De Venta's Own Zona Horaria, Not UTC" — vinculante), mismo
    /// precedente que el fix de <c>/stock/existencias/export</c> (commit 08e7707).</summary>
    private async Task<(int IdEmpresa, string ZonaId, DateOnly Hoy)> ResolverContextoAsync(
        int idPuntoVenta, CancellationToken ct)
    {
        var idEmpresa = await db.PuntosVenta
            .Where(pv => pv.Id == idPuntoVenta)
            .Select(pv => (int?)pv.IdEmpresa)
            .FirstOrDefaultAsync(ct)
            ?? throw ErrorDominio.NoEncontrado($"No existe el punto de venta {idPuntoVenta}.");

        var resuelto = await parametros.ResolverAsync(ParametroConocido.ZonaHoraria.Clave, idEmpresa, idPuntoVenta, ct);
        var zonaId = JsonSerializer.Deserialize<string>(resuelto.Valor)!;
        var zona = TimeZoneInfo.FindSystemTimeZoneById(zonaId);

        var hoy = DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(reloj.Ahora, zona).DateTime);

        return (idEmpresa, zonaId, hoy);
    }

    private async Task<int> ResolverDiasAlertaAsync(int idEmpresa, int idPuntoVenta, CancellationToken ct)
    {
        var resuelto = await parametros.ResolverAsync(ParametroConocido.DiasAlertaVencimiento.Clave, idEmpresa, idPuntoVenta, ct);
        return JsonSerializer.Deserialize<int>(resuelto.Valor);
    }

    private sealed record FilaCruda(
        int IdArticulo, string Articulo, int IdLote, string CodigoLote, DateOnly? FechaVencimiento, decimal Cantidad);
}
