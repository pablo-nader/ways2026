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
///
/// stage-13-stock-inteligente, Slice 4 (design decisión 1/2/3/12/13, spec reposicion-de-stock):
/// agrega <see cref="ObtenerReposicionAsync"/> — la alerta y la sugerencia de compra son la misma
/// lista (<c>minimo IS NOT NULL AND cantidad &lt;= minimo</c>), agregado acotado por el catálogo
/// como existencias (mismo shape de export: guarda sobre <c>TablaExportable.Filas.Count</c> ya
/// mapeada, sin <c>ObtenerReposicionParaExportacionAsync</c> propio — decisión 13). Sin campos de
/// rotación todavía: la slice 5 completa <see cref="FilaDeReposicion"/> con
/// <c>ConsumoDiarioPromedio</c>/<c>DiasDeCobertura</c> vía <c>LeerConsumoAsync</c>.
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

    /// <summary>stage-13-stock-inteligente, Slice 4 (design decisión 12; task 4.2): la alerta y la
    /// sugerencia de compra son la MISMA lista — <see cref="ConstruirQueryDeReposicion"/> se
    /// evalúa una única vez, sin campos de rotación todavía (esos llegan en la slice 5, que
    /// completa la rama <c>crudas.Count &gt; 0</c> de abajo con <c>LeerConsumoAsync</c>).
    /// El corto-circuito de la fila 0 se cablea ACÁ (decisión 12) para que la slice 5 no tenga
    /// que reestructurar el método, solo llenar la rama que hoy es un mapeo puro.</summary>
    public async Task<Reposicion> ObtenerReposicionAsync(
        int idPuntoVenta, int? dias, CancellationToken ct = default)
    {
        var (idEmpresa, zonaId, hoy) = await ResolverContextoAsync(idPuntoVenta, ct);
        var diasDeRotacion = ReglaDeReposicion.ExigirVentanaValida(
            dias ?? await ResolverDiasRotacionAsync(idEmpresa, idPuntoVenta, ct), "dias_rotacion_invalido");

        var crudas = await ConstruirQueryDeReposicion(idPuntoVenta).ToListAsync(ct);

        if (crudas.Count == 0)
        {
            return new Reposicion(idPuntoVenta, hoy, diasDeRotacion, zonaId, []);
        }

        // Sugerido es puro (ReglaDeReposicion.Sugerido no depende de rotación) — la slice 5 solo
        // agrega ConsumoDiarioPromedio/DiasDeCobertura a esta misma proyección, nunca una segunda.
        var filas = crudas
            .Select(f => new FilaDeReposicion(
                f.IdArticulo, f.Articulo, f.Cantidad, f.Minimo, f.Reposicion,
                ReglaDeReposicion.Sugerido(f.Cantidad, f.Reposicion),
                f.IdProveedorHabitual, f.Proveedor))
            .ToList();

        return new Reposicion(idPuntoVenta, hoy, diasDeRotacion, zonaId, filas);
    }

    /// <summary>Cláusulas bajo prueba (mutation-proof-tests, en orden de daño si se pierden):
    ///   <c>s.Cantidad &lt;= s.Minimo</c> → con <c>&lt;</c>, el artículo EXACTAMENTE en el punto de
    ///                                   pedido desaparece (spec: The Low-Stock Boundary Is
    ///                                   Inclusive).
    ///   <c>s.IdPuntoVenta == idPuntoVenta</c> → mezclar dos PVs del mismo tenant rompe el reporte
    ///                                   (misma familia de bug que <see cref="ObtenerExistenciasAsync"/>
    ///                                   documenta).
    ///   <c>candidatos.DefaultIfEmpty()</c> → sin el LEFT JOIN, las filas "Sin proveedor"
    ///                                   desaparecen en silencio (design decisión 3).
    ///   <c>orderby a.IdProveedorHabitual, a.Id</c> (primer campo) → sin él, "Sin proveedor" deja de
    ///                                   ordenar siempre último.
    /// <c>s.Minimo != null</c> se conserva por legibilidad/intención documental (nombra
    /// explícitamente decisión 1 del proposal: "minimo NULL ⇒ no gestionado"), pero se verificó con
    /// <c>ToQueryString()</c> que Npgsql la traduce a un <c>IS NOT NULL</c> aditivo — REDUNDANTE
    /// para la admisión de filas, porque <c>s.cantidad &lt;= s.minimo</c> ya excluye toda fila con
    /// <c>minimo</c> NULL vía la lógica de tres valores de SQL (<c>x &lt;= NULL</c> es siempre
    /// desconocido, nunca verdadero, para cualquier <c>x</c>). Confirmado corriendo la mutación
    /// (borrar esta cláusula) contra un seed con <c>minimo = null, cantidad = 0</c>: la fila sigue
    /// AUSENTE — no hay ninguna combinación de datos que la haga observable como mutation target
    /// (mutation-proof-tests regla 3, agotada: no hay confound de OTRA capa que rodear, es la
    /// semántica NULL de SQL misma). Evidencia y desvío registrados en tasks.md, task 4.6.
    /// El LEFT JOIN a <c>proveedores</c> NO se filtra por empresa: <c>articulos.id_proveedor_habitual</c>
    /// es autoritativo, agregar un predicado de empresa vaciaría el nombre de un proveedor real
    /// (design decisión 3, <c>explore.md</c> §4). El <c>orderby</c> va ANTES del <c>select</c> hacia
    /// el record — EF no traduce un <c>OrderBy</c> sobre la propiedad de un objeto recién construido
    /// (mismo obstáculo que <see cref="ConstruirQueryDeVencimientos"/> ya documenta). Postgres
    /// ordena NULL último en ASC por default, así que "Sin proveedor" cae al final sin <c>NULLS
    /// LAST</c> explícito.</summary>
    private IQueryable<FilaCrudaDeReposicion> ConstruirQueryDeReposicion(int idPuntoVenta) =>
        from s in db.Stock
        where s.IdPuntoVenta == idPuntoVenta && s.Minimo != null && s.Cantidad <= s.Minimo
        join a in db.Articulos on s.IdArticulo equals a.Id
        join p in db.Proveedores on a.IdProveedorHabitual equals p.Id into candidatos
        from p in candidatos.DefaultIfEmpty()
        orderby a.IdProveedorHabitual, a.Id
        select new FilaCrudaDeReposicion(
            a.Id, a.Nombre, s.Cantidad, s.Minimo!.Value, s.Reposicion,
            a.IdProveedorHabitual, p == null ? null : p.RazonSocial);

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

    /// <summary>Slice 4 — mismo patrón que <see cref="ResolverDiasAlertaAsync"/>. La slice 5 agrega
    /// el resolver gemelo de <c>dias_cobertura_objetivo</c>, todavía sin consumidor acá.</summary>
    private async Task<int> ResolverDiasRotacionAsync(int idEmpresa, int idPuntoVenta, CancellationToken ct)
    {
        var resuelto = await parametros.ResolverAsync(ParametroConocido.DiasRotacion.Clave, idEmpresa, idPuntoVenta, ct);
        return JsonSerializer.Deserialize<int>(resuelto.Valor);
    }

    private sealed record FilaCruda(
        int IdArticulo, string Articulo, int IdLote, string CodigoLote, DateOnly? FechaVencimiento, decimal Cantidad);

    /// <summary>Proyección cruda de <see cref="ConstruirQueryDeReposicion"/>, previa a la fórmula
    /// pura de <see cref="ObtenerReposicionAsync"/> — nunca expuesta fuera de este archivo.</summary>
    private sealed record FilaCrudaDeReposicion(
        int IdArticulo, string Articulo, decimal Cantidad, decimal Minimo, decimal? Reposicion,
        int? IdProveedorHabitual, string? Proveedor);
}
