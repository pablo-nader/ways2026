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
/// <c>stock</c> ⋈ <c>articulos</c> para UNA sucursal, cubierto por <c>ix_stock_sucursal</c>.
/// Sin <c>idArticulo</c> (spec: Existencias Needs No idArticulo, Unlike GET /api/stock) y sin
/// <c>idEmpresa</c> (mismo criterio que la tesorería: la ruta pide la sucursal o,
/// por compatibilidad, un punto de venta que se resuelve a la suya; la empresa se resuelve en
/// <see cref="ResolverAlcanceAsync"/>). El stock es de la sucursal: dos cajas de una misma
/// sucursal ven la misma fila, nunca dos.
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
/// mapeada, sin <c>ObtenerReposicionParaExportacionAsync</c> propio — decisión 13).
///
/// stage-13-stock-inteligente, Slice 5 (design decisión 5/6/7/12, spec reposicion-de-stock:
/// "Rotation Excludes Purchase-Reversal Anulaciones And Is Advisory-Only"): agrega <see
/// cref="LeerConsumoAsync"/> — LA definición de consumo, dos llamadores (<see
/// cref="ObtenerReposicionAsync"/>, que ahora completa <c>ConsumoDiarioPromedio</c>/<c>
/// DiasDeCobertura</c> por fila, y <see cref="ObtenerRotacionAsync"/>, el feed independiente de
/// <c>minimoSugerido</c> para el editor). Plain LINQ sobre <c>db.MovimientosStock</c> — cero SQL
/// crudo, <c>LectorDeSerieTemporal</c> intocado (design decisión 7 del proposal).
/// </summary>
public class ServicioDeReportesDeStock(IWaysDbContext db, ServicioDeParametros parametros, IRelojDelSistema reloj)
{
    /// <summary>Resuelve el alcance de un reporte de stock. <paramref name="idSucursal"/> es la entrada nueva;
    /// <paramref name="idPuntoVenta"/> sigue aceptándose (la web y el POS empaquetado lo mandan) y se resuelve a la
    /// sucursal de esa caja. Exactamente uno de los dos. Una sucursal dada de baja se trata como inexistente (404),
    /// también cuando se llega a ella por una caja que todavía la referencia: el reporte nunca nombra una sucursal
    /// que el resto del sistema ya no ve. Con <paramref name="idSucursal"/> los parámetros por caja
    /// (zona horaria, días de alerta) salen de la caja de menor id de la sucursal, hasta que esos parámetros
    /// pasen a ser de la sucursal.</summary>
    public async Task<AlcanceDeStock> ResolverAlcanceAsync(int? idPuntoVenta, int? idSucursal, CancellationToken ct = default)
    {
        if (idPuntoVenta is not null && idSucursal is not null)
        {
            throw new ErrorDominio(
                "alcance_de_stock_ambiguo", "Indicá idSucursal o idPuntoVenta, no los dos.", 400);
        }

        if (idPuntoVenta is { } idCaja)
        {
            var caja = await db.PuntosVenta
                .Where(pv => pv.Id == idCaja)
                .Select(pv => new { pv.IdSucursal, pv.IdEmpresa })
                .FirstOrDefaultAsync(ct)
                ?? throw ErrorDominio.NoEncontrado($"No existe el punto de venta {idCaja}.");

            var nombre = await db.Sucursales
                .Where(su => su.Id == caja.IdSucursal)
                .Select(su => su.Nombre)
                .FirstOrDefaultAsync(ct)
                ?? throw ErrorDominio.NoEncontrado($"No existe la sucursal del punto de venta {idCaja}.");

            return new AlcanceDeStock(caja.IdSucursal, nombre, caja.IdEmpresa, idCaja);
        }

        if (idSucursal is not { } id)
        {
            throw new ErrorDominio(
                "alcance_de_stock_requerido", "Falta idSucursal (o idPuntoVenta, que se resuelve a su sucursal).", 400);
        }

        var sucursal = await db.Sucursales
            .Where(su => su.Id == id)
            .Select(su => new { su.Nombre, su.IdEmpresa })
            .FirstOrDefaultAsync(ct)
            ?? throw ErrorDominio.NoEncontrado($"No existe la sucursal {id}.");

        var referencia = await db.PuntosVenta
            .Where(pv => pv.IdSucursal == id)
            .OrderBy(pv => pv.Id)
            .Select(pv => (int?)pv.Id)
            .FirstOrDefaultAsync(ct);

        return new AlcanceDeStock(id, sucursal.Nombre, sucursal.IdEmpresa, referencia);
    }

    public async Task<Existencias> ObtenerExistenciasAsync(AlcanceDeStock alcance, CancellationToken ct = default)
    {
        // Cláusula bajo prueba (mutation-proof-tests): el filtro por sucursal es lo único que discrimina una
        // sucursal de otra — mezclar dos del mismo tenant en una sola respuesta rompería el significado del
        // reporte tanto como en una cadena de tesorería (design decisión 11, misma familia de bug). Las cajas de una
        // misma sucursal ven el mismo stock.
        var crudas = await db.Stock
            .Where(s => s.IdSucursal == alcance.IdSucursal)
            .Join(
                db.Articulos, s => s.IdArticulo, a => a.Id,
                (s, a) => new { a.Id, a.Nombre, s.Cantidad, s.Minimo, s.Reposicion })
            .OrderBy(x => x.Id)
            .ToListAsync(ct);

        // stage-13-stock-inteligente, Slice 2 (design decisión 2): existencias clasifica TODA fila
        // stockeada vía ReglaDeReposicion.Clasificar — el mismo borde que reposición (slice 4) y el
        // write path (slice 1) ya usan, nunca una segunda definición del boundary bajo/sin_minimo/ok.
        var filas = crudas
            .Select(x => new FilaExistencia(
                x.Id, x.Nombre, x.Cantidad, x.Minimo, x.Reposicion,
                ReglaDeReposicion.Clasificar(x.Cantidad, x.Minimo)))
            .ToList();

        return new Existencias(alcance.IdSucursal, alcance.NombreSucursal, filas);
    }

    /// <summary>Lotes con saldo positivo de la sucursal, clasificados en la zona horaria de su caja de
    /// referencia — sin paginar (task 13.1). <paramref name="dias"/> ausente ⇒ se resuelve
    /// <c>dias_alerta_vencimiento</c> (PV → empresa → default).</summary>
    public async Task<Vencimientos> ObtenerVencimientosAsync(
        AlcanceDeStock alcance, int? dias, CancellationToken ct = default)
    {
        var (zonaId, hoy) = await ResolverContextoAsync(alcance, ct);
        var diasDeAlerta = dias ?? await ResolverDiasAlertaAsync(alcance, ct);

        var filas = await ConstruirQueryDeVencimientos(alcance.IdSucursal).ToListAsync(ct);

        return new Vencimientos(
            alcance.IdSucursal, alcance.NombreSucursal, hoy, diasDeAlerta, zonaId, Clasificar(filas, hoy, diasDeAlerta));
    }

    /// <summary>Tile de Tablero (task 13.2) — reusa <see cref="ObtenerVencimientosAsync"/> con
    /// <c>dias</c> nulo (el horizonte default) y agrupa por <see cref="EstadoDeVencimiento"/>: el
    /// tile nunca puede divergir del reporte porque no hay una segunda query de agregación.</summary>
    public async Task<ResumenDeVencimientos> ObtenerResumenDeVencimientosAsync(
        AlcanceDeStock alcance, CancellationToken ct = default)
    {
        var vencimientos = await ObtenerVencimientosAsync(alcance, dias: null, ct);

        return new ResumenDeVencimientos(
            alcance.IdSucursal, alcance.NombreSucursal,
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
        AlcanceDeStock alcance, int? dias, int topeDeFilas, CancellationToken ct = default)
    {
        var (zonaId, hoy) = await ResolverContextoAsync(alcance, ct);
        var diasDeAlerta = dias ?? await ResolverDiasAlertaAsync(alcance, ct);

        var query = ConstruirQueryDeVencimientos(alcance.IdSucursal);

        var cantidad = await query.CountAsync(ct);
        GuardaDeTope.Exigir(cantidad, topeDeFilas);

        var filas = await query.Take(topeDeFilas + 1).ToListAsync(ct);
        GuardaDeTope.Exigir(filas.Count, topeDeFilas);

        return new Vencimientos(
            alcance.IdSucursal, alcance.NombreSucursal, hoy, diasDeAlerta, zonaId, Clasificar(filas, hoy, diasDeAlerta));
    }

    /// <summary>stage-13-stock-inteligente, Slice 4/5 (design decisión 12; task 4.2/5.2): la
    /// alerta y la sugerencia de compra son la MISMA lista — <see cref="ConstruirQueryDeReposicion"/>
    /// se evalúa una única vez. El corto-circuito de la fila 0 (decisión 12) evita que una fila
    /// vacía dispare la query de rotación: una sucursal sin mínimos configurados cuesta exactamente UNA
    /// query (la de acá arriba), nunca <see cref="LeerConsumoAsync"/>.</summary>
    public async Task<Reposicion> ObtenerReposicionAsync(
        AlcanceDeStock alcance, int? dias, CancellationToken ct = default)
    {
        var (zonaId, hoy) = await ResolverContextoAsync(alcance, ct);
        var diasDeRotacion = ReglaDeReposicion.ExigirVentanaValida(
            dias ?? await ResolverDiasRotacionAsync(alcance, ct), "dias_rotacion_invalido");

        var crudas = await ConstruirQueryDeReposicion(alcance.IdSucursal).ToListAsync(ct);

        if (crudas.Count == 0)
        {
            return new Reposicion(alcance.IdSucursal, alcance.NombreSucursal, hoy, diasDeRotacion, zonaId, []);
        }

        // Slice 5: misma ventana y misma definición de consumo que ObtenerRotacionAsync (design
        // decisión 5) — LeerConsumoAsync acotado a los ids de ESTAS filas (decisión 12: nunca todo
        // el catálogo de la sucursal, el set "bajo mínimo" ya es chico por construcción).
        var (desdeUtc, hastaUtc) = ReglaDeReposicion.VentanaDeRotacion(hoy, diasDeRotacion, TimeZoneInfo.FindSystemTimeZoneById(zonaId));
        var consumo = await LeerConsumoAsync(
            alcance.IdSucursal, crudas.Select(f => f.IdArticulo).ToList(), desdeUtc, hastaUtc, ct);

        // Sugerido es puro (ReglaDeReposicion.Sugerido no depende de rotación); ConsumoDiarioPromedio/
        // DiasDeCobertura sí — honestos en null (nunca 0) cuando el artículo no calificó ningún
        // movimiento en la ventana (spec: "A zero-history articulo shows no suggestion...").
        var filas = crudas
            .Select(f =>
            {
                var netoConsumido = consumo.TryGetValue(f.IdArticulo, out var neto) ? -neto : (decimal?)null;
                var consumoDiarioPromedio = ReglaDeReposicion.ConsumoDiario(netoConsumido, diasDeRotacion);

                return new FilaDeReposicion(
                    f.IdArticulo, f.Articulo, f.Cantidad, f.Minimo, f.Reposicion,
                    ReglaDeReposicion.Sugerido(f.Cantidad, f.Reposicion),
                    f.IdProveedorHabitual, f.Proveedor,
                    consumoDiarioPromedio, ReglaDeReposicion.DiasDeCobertura(f.Cantidad, consumoDiarioPromedio));
            })
            .ToList();

        return new Reposicion(alcance.IdSucursal, alcance.NombreSucursal, hoy, diasDeRotacion, zonaId, filas);
    }

    /// <summary>Tile de Tablero (stage-13-stock-inteligente, Slice 7; design decisión 8/9; tarea
    /// 7.1) — reusa <see cref="ObtenerReposicionAsync"/> con <c>dias</c> nulo (el horizonte
    /// default, mismo criterio que <see cref="ObtenerResumenDeVencimientosAsync"/>) y foldea sus
    /// propias filas: el tile nunca puede divergir del reporte porque no hay una segunda query de
    /// agregación. <c>SinProveedor</c> cuenta el grupo "Sin proveedor" (<c>IdProveedor is null</c>)
    /// — orchestrator decision 5 (tasks.md): NUNCA "sin sugerido" (<c>Sugerido is null</c>), el
    /// nombre que design.md usaba antes de la corrección de la spec ratificada.</summary>
    public async Task<ResumenDeReposicion> ObtenerResumenDeReposicionAsync(
        AlcanceDeStock alcance, CancellationToken ct = default)
    {
        var reposicion = await ObtenerReposicionAsync(alcance, dias: null, ct);

        // Cláusula bajo prueba (mutation-proof-tests, tarea 7.7): f.Cantidad <= 0 — con < 0, el
        // artículo EXACTAMENTE en cero (agotado, el caso más urgente) deja de contar como sinStock.
        return new ResumenDeReposicion(
            alcance.IdSucursal, alcance.NombreSucursal,
            reposicion.Filas.Count,
            reposicion.Filas.Count(f => f.Cantidad <= 0),
            reposicion.Filas.Count(f => f.IdProveedor is null));
    }

    /// <summary>stage-13-stock-inteligente, Slice 5 (design decisión 14; task 5.4): el feed
    /// independiente de <c>minimoSugerido</c> para el editor — a diferencia de <see
    /// cref="ObtenerReposicionAsync"/>, NO depende de <c>minimo</c>: agrega sobre TODO el catálogo
    /// de la sucursal (decisión 12, "el único lugar donde ese costo es el punto"), pero solo emite una fila
    /// por artículo con AL MENOS UN movimiento calificado en la ventana — la ausencia es la
    /// respuesta para un artículo sin historia, nunca una fila con <c>minimoSugerido = 0</c>
    /// (decisión 14). Misma definición de consumo y misma resolución de ventana que <see
    /// cref="ObtenerReposicionAsync"/> (design decisión 5): nunca una segunda.</summary>
    public async Task<Rotacion> ObtenerRotacionAsync(
        AlcanceDeStock alcance, int? dias, CancellationToken ct = default)
    {
        var (zonaId, hoy) = await ResolverContextoAsync(alcance, ct);
        var diasDeRotacion = ReglaDeReposicion.ExigirVentanaValida(
            dias ?? await ResolverDiasRotacionAsync(alcance, ct), "dias_rotacion_invalido");
        var diasDeCoberturaObjetivo = ReglaDeReposicion.ExigirVentanaValida(
            await ResolverDiasCoberturaAsync(alcance, ct), "dias_cobertura_invalido");

        var (desdeUtc, hastaUtc) = ReglaDeReposicion.VentanaDeRotacion(hoy, diasDeRotacion, TimeZoneInfo.FindSystemTimeZoneById(zonaId));
        var consumo = await LeerConsumoAsync(alcance.IdSucursal, idsArticulo: null, desdeUtc, hastaUtc, ct);

        if (consumo.Count == 0)
        {
            return new Rotacion(alcance.IdSucursal, alcance.NombreSucursal, hoy, diasDeRotacion, diasDeCoberturaObjetivo, zonaId, []);
        }

        // El ledger es append-only: un artículo dado de baja después de sus movimientos sigue
        // calificando en LeerConsumoAsync (que no conoce baja lógica), pero el filtro global de
        // EF sobre Articulo sí lo excluye acá — mismo trade-off ya documentado que
        // ExistenciasTests.UnArticuloEliminadoNuncaApareceEnLasExistencias (design: Open
        // Questions), nunca una excepción por nombre ausente.
        var idsArticulo = consumo.Keys.ToList();
        var nombres = await db.Articulos
            .Where(a => idsArticulo.Contains(a.Id))
            .Select(a => new { a.Id, a.Nombre })
            .ToDictionaryAsync(a => a.Id, a => a.Nombre, ct);

        var filas = consumo
            .Where(par => nombres.ContainsKey(par.Key))
            .Select(par =>
            {
                var consumoEnVentana = Math.Max(0m, -par.Value);
                var consumoDiarioPromedio = ReglaDeReposicion.ConsumoDiario(consumoEnVentana, diasDeRotacion)!.Value;
                var minimoSugerido = ReglaDeReposicion.MinimoSugerido(consumoDiarioPromedio, diasDeCoberturaObjetivo)!.Value;

                return new FilaDeRotacion(par.Key, nombres[par.Key], consumoEnVentana, consumoDiarioPromedio, minimoSugerido);
            })
            .OrderBy(f => f.IdArticulo)
            .ToList();

        return new Rotacion(alcance.IdSucursal, alcance.NombreSucursal, hoy, diasDeRotacion, diasDeCoberturaObjetivo, zonaId, filas);
    }

    /// <summary>Cláusulas bajo prueba (mutation-proof-tests, en orden de daño si se pierden):
    ///   <c>s.Cantidad &lt;= s.Minimo</c> → con <c>&lt;</c>, el artículo EXACTAMENTE en el punto de
    ///                                   pedido desaparece (spec: The Low-Stock Boundary Is
    ///                                   Inclusive).
    ///   el filtro por sucursal (<c>s.IdSucursal == idSucursal</c>) → mezclar dos sucursales del mismo tenant rompe el reporte
    ///                                   (misma familia de bug que <see cref="ObtenerExistenciasAsync"/>
    ///                                   documenta).
    ///   <c>candidatos.DefaultIfEmpty()</c> → sin el LEFT JOIN, las filas "Sin proveedor"
    ///                                   desaparecen en silencio (design decisión 3).
    ///   <c>orderby (p == null), a.IdProveedorHabitual, a.Id</c> (primer campo) → sin la clave de
    ///                                   presencia, una fila cuyo proveedor está soft-deleted
    ///                                   (FK apuntando a un id vivo pero <c>p == null</c> por el
    ///                                   filtro global de baja lógica) vuelve a ordenar por su FK
    ///                                   crudo en lugar de caer al bucket final "Sin proveedor"
    ///                                   (orchestrator decision 12, tasks.md).
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
    /// LAST</c> explícito. <c>IdProveedor</c>/orderby usan <c>p == null</c>, nunca el FK crudo de
    /// <c>a.IdProveedorHabitual</c>, como clave de presencia: un FK que apunta a un proveedor
    /// soft-deleted resuelve <c>p == null</c> igual que un FK NULL, así que ambos casos caen en el
    /// MISMO bucket final "Sin proveedor" — nunca un FK colgante viajando al cliente ni una
    /// segunda fila "Sin proveedor" a mitad de lista (orchestrator decision 12, tasks.md;
    /// design decisión 3 es la letra autoritativa sobre el snippet pinneado de la task 4.1).</summary>
    private IQueryable<FilaCrudaDeReposicion> ConstruirQueryDeReposicion(int idSucursal) =>
        from s in db.Stock
        where s.IdSucursal == idSucursal
            && s.Minimo != null && s.Cantidad <= s.Minimo
        join a in db.Articulos on s.IdArticulo equals a.Id
        join p in db.Proveedores on a.IdProveedorHabitual equals p.Id into candidatos
        from p in candidatos.DefaultIfEmpty()
        orderby (p == null), a.IdProveedorHabitual, a.Id
        select new FilaCrudaDeReposicion(
            a.Id, a.Nombre, s.Cantidad, s.Minimo!.Value, s.Reposicion,
            p == null ? null : (int?)a.IdProveedorHabitual, p == null ? null : p.RazonSocial);

    /// <summary>Proyección cruda de <c>stock_lotes ⋈ lotes ⋈ articulos</c> (spec: "lot rows...
    /// with a positive stock_lotes.cantidad") — <c>Cantidad &gt; 0</c> estrictamente, nunca
    /// <c>&lt;&gt; 0</c> (spec: "A zero-balance lot never appears in the report"; un saldo
    /// negativo tampoco debería listarse acá, mismo criterio). Orden <c>fecha_vencimiento ASC</c>
    /// ANTES del <c>select</c> hacia el record: EF Core no traduce un <c>OrderBy</c> sobre la
    /// propiedad de un objeto recién construido (mismo obstáculo de traducción que
    /// <c>ServicioDeReportesDeVentas.ComprobantesVentaDelPeriodo</c> documenta para <c>GroupBy</c>).
    /// Postgres ya ordena NULL último en ascendente por default, sin necesitar <c>NULLS LAST</c>
    /// explícito.</summary>
    private IQueryable<FilaCruda> ConstruirQueryDeVencimientos(int idSucursal) =>
        from stockLote in db.StockLotes
        where stockLote.IdSucursal == idSucursal
            && stockLote.Cantidad > 0m
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
    /// <c>reloj.Ahora</c> (instante absoluto) se convierte a la zona de la caja de referencia ANTES de tomar el
    /// <c>DateOnly</c>. Mutation target (task 13.5): reemplazar la conversión por
    /// <c>reloj.Ahora.UtcDateTime</c> directo tiene que hacer fallar el test de zona (spec: "'Hoy'
    /// Is Resolved In The Punto De Venta's Own Zona Horaria, Not UTC" — vinculante), mismo
    /// precedente que el fix de <c>/stock/existencias/export</c> (commit 08e7707).</summary>
    private async Task<(string ZonaId, DateOnly Hoy)> ResolverContextoAsync(AlcanceDeStock alcance, CancellationToken ct)
    {
        var resuelto = await parametros.ResolverAsync(
            ParametroConocido.ZonaHoraria.Clave, alcance.IdEmpresa, alcance.IdPuntoVentaDeReferencia, ct);
        var zonaId = JsonSerializer.Deserialize<string>(resuelto.Valor)!;
        var zona = TimeZoneInfo.FindSystemTimeZoneById(zonaId);

        var hoy = DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(reloj.Ahora, zona).DateTime);

        return (zonaId, hoy);
    }

    private async Task<int> ResolverDiasAlertaAsync(AlcanceDeStock alcance, CancellationToken ct)
    {
        var resuelto = await parametros.ResolverAsync(
            ParametroConocido.DiasAlertaVencimiento.Clave, alcance.IdEmpresa, alcance.IdPuntoVentaDeReferencia, ct);
        return JsonSerializer.Deserialize<int>(resuelto.Valor);
    }

    /// <summary>Slice 4 — mismo patrón que <see cref="ResolverDiasAlertaAsync"/>.</summary>
    private async Task<int> ResolverDiasRotacionAsync(AlcanceDeStock alcance, CancellationToken ct)
    {
        var resuelto = await parametros.ResolverAsync(
            ParametroConocido.DiasRotacion.Clave, alcance.IdEmpresa, alcance.IdPuntoVentaDeReferencia, ct);
        return JsonSerializer.Deserialize<int>(resuelto.Valor);
    }

    /// <summary>Slice 5 — resolver gemelo de <see cref="ResolverDiasRotacionAsync"/>, para el
    /// horizonte de cobertura que <see cref="ObtenerRotacionAsync"/> multiplica por el consumo
    /// diario promedio (<see cref="Ways.Domain.Stock.ReglaDeReposicion.MinimoSugerido"/>). A
    /// diferencia de <c>dias_rotacion</c>, ninguna ruta acepta un <c>?dias=</c> que lo
    /// sobrescriba — solo el parámetro resuelto (spec: "dias_cobertura_objetivo feeds
    /// minimoSugerido, never minimo directly").</summary>
    private async Task<int> ResolverDiasCoberturaAsync(AlcanceDeStock alcance, CancellationToken ct)
    {
        var resuelto = await parametros.ResolverAsync(
            ParametroConocido.DiasCoberturaObjetivo.Clave, alcance.IdEmpresa, alcance.IdPuntoVentaDeReferencia, ct);
        return JsonSerializer.Deserialize<int>(resuelto.Valor);
    }

    /// <summary>Slice 5 (design decisión 5/6/7; task 5.1): LA definición de consumo, con dos
    /// llamadores (<see cref="ObtenerReposicionAsync"/>, <see cref="ObtenerRotacionAsync"/>) —
    /// nunca una segunda. Cláusula bajo prueba (mutation-proof-tests): <c>m.Motivo ==
    /// MotivoStock.Anulacion &amp;&amp; m.IdComprobanteCompra == null</c> — sin el segundo
    /// término, la anulación de una COMPRA (que también escribe <c>motivo = anulacion</c> desde la
    /// etapa 8) se netea silenciosamente dentro de las ventas (la trampa del neteo, design
    /// decisión 6). <paramref name="idsArticulo"/> nulo agrega sobre TODO el catálogo de la sucursal — el
    /// caso de <see cref="ObtenerRotacionAsync"/>, el único lugar donde ese costo es el punto
    /// (design decisión 12); <see cref="ObtenerReposicionAsync"/> siempre pasa la lista acotada
    /// de artículos ya bajo mínimo. Plain LINQ sobre <c>db.MovimientosStock</c> — cero SQL crudo,
    /// hereda los filtros globales de tenant/baja-lógica de EF gratis, mismo criterio que <see
    /// cref="ObtenerExistenciasAsync"/> (design decisión 7 del proposal: el fork con
    /// <c>LectorDeSerieTemporal</c> no aplica acá — una ventana de instantes, sin bucketing).
    /// El llamador NIEGA el neto (<c>-neto</c>): las filas de venta llevan <c>cantidad</c>
    /// negativa, la anulación de venta positiva, y una NCX viaja como venta con <c>cantidad</c>
    /// POSITIVA (<c>ServicioDeVentas</c> ya la negó) — <c>-SUM</c> neteas devoluciones sin una
    /// rama de signo.</summary>
    private async Task<IReadOnlyDictionary<int, decimal>> LeerConsumoAsync(
        int idSucursal, IReadOnlyList<int>? idsArticulo, DateTimeOffset desdeUtc, DateTimeOffset hastaUtcExclusivo,
        CancellationToken ct)
    {
        var query = db.MovimientosStock
            .Where(m => m.IdSucursal == idSucursal
                     && m.CreadoEl >= desdeUtc && m.CreadoEl < hastaUtcExclusivo
                     && (m.Motivo == MotivoStock.Venta
                         || (m.Motivo == MotivoStock.Anulacion && m.IdComprobanteCompra == null)));

        if (idsArticulo is not null)
        {
            query = query.Where(m => idsArticulo.Contains(m.IdArticulo));
        }

        var filas = await query
            .GroupBy(m => m.IdArticulo)
            .Select(g => new { IdArticulo = g.Key, Neto = g.Sum(m => m.Cantidad) })
            .ToListAsync(ct);

        return filas.ToDictionary(f => f.IdArticulo, f => f.Neto);
    }

    private sealed record FilaCruda(
        int IdArticulo, string Articulo, int IdLote, string CodigoLote, DateOnly? FechaVencimiento, decimal Cantidad);

    /// <summary>Proyección cruda de <see cref="ConstruirQueryDeReposicion"/>, previa a la fórmula
    /// pura de <see cref="ObtenerReposicionAsync"/> — nunca expuesta fuera de este archivo.</summary>
    private sealed record FilaCrudaDeReposicion(
        int IdArticulo, string Articulo, decimal Cantidad, decimal Minimo, decimal? Reposicion,
        int? IdProveedorHabitual, string? Proveedor);
}
