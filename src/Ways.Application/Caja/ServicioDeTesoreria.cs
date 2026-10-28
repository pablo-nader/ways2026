using System.Linq.Expressions;
using Microsoft.EntityFrameworkCore;
using Ways.Application.Abstracciones;
using Ways.Application.Exportacion;
using Ways.Application.Listados;
using Ways.Domain.Caja;
using Ways.Domain.Common;
using Ways.Domain.Pagos;

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
        string? orden = null,
        string? direccion = null,
        CancellationToken ct = default)
    {
        var ordenPedido = OrdenDeListado.Interpretar(orden, direccion, ClavesDeOrden);

        await ExigirEmpresaAsync(idEmpresa, ct);

        if (idPuntoVenta is { } pvValidar)
        {
            await ValidarPuntoVentaDeLaEmpresaAsync(idEmpresa, pvValidar, ct);
        }

        pagina = Math.Max(pagina, 1);
        tamanio = Math.Clamp(tamanio, 1, 200);

        var query = ConstruirQuery(idEmpresa, idPuntoVenta, desde, hasta);

        var total = await query.CountAsync(ct);

        var paginaQuery = Ordenar(Unir(query), ordenPedido)
            .Skip((int)Math.Min((long)(pagina - 1) * tamanio, int.MaxValue))
            .Take(tamanio);

        var items = await AListado(paginaQuery).ToListAsync(ct);

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

        var tope = Ordenar(Unir(query), null)
            .Take(topeDeFilas + 1);

        var items = await AListado(tope).ToListAsync(ct);

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
    /// <c>NombrePuntoVenta</c> como <c>PagoCategoria</c>/<c>PagoConcepto</c> resuelven a
    /// <c>null</c> tanto sin punto de venta/pago como con uno dado de baja lógica (el filtro
    /// global de baja lógica de EF ya los deja invisibles), nunca excluyen ni rompen la fila —
    /// mismo criterio que <c>ServicioDePagos.ProyeccionDeAdministracion</c>.
    ///
    /// judgment-day PR5, hallazgo #4: <c>PagoCategoria</c>/<c>PagoConcepto</c> resolvían por DOS
    /// subqueries correlacionadas separadas contra <see cref="IWaysDbContext.Pagos"/> (una por
    /// campo) — un <c>let</c> las colapsa en UNA sola consulta (EF Core la traduce a un
    /// <c>OUTER APPLY</c>/<c>LEFT JOIN LATERAL</c> único), reusando su resultado para ambos
    /// campos.</summary>
    private IQueryable<Unido> Unir(IQueryable<MovimientoTesoreria> query) =>
        from m in query
        let pago = m.IdPago == null
            ? null
            : db.Pagos.Where(g => g.Id == m.IdPago).Select(ProyeccionDePago).FirstOrDefault()
        select new Unido
        {
            M = m,
            NombrePuntoVenta = m.IdPuntoVenta == null
                ? null
                : db.PuntosVenta.Where(pv => pv.Id == m.IdPuntoVenta).Select(pv => pv.Nombre).FirstOrDefault(),
            PagoCategoria = pago == null ? null : (CategoriaPago?)pago.Categoria,
            PagoConcepto = pago == null ? null : pago.Concepto,
            PagoEtiqueta = pago == null ? null : pago.Etiqueta,
        };

    private static IQueryable<MovimientoTesoreriaListado> AListado(IQueryable<Unido> query) =>
        query.Select(x => new MovimientoTesoreriaListado(
            x.M.Id,
            x.M.IdEmpresa,
            x.M.IdPuntoVenta,
            x.NombrePuntoVenta,
            x.M.Fecha,
            x.M.Tipo,
            x.M.IdTurnoCaja,
            x.M.IdPago,
            x.PagoCategoria,
            x.PagoConcepto,
            x.M.Concepto,
            x.M.Inicio,
            x.M.Ingreso,
            x.M.Egreso,
            x.M.Final,
            x.M.IdEmpleado));

    /// <summary>Sin orden pedido: <c>Id</c> ascendente, el orden del libro (también el del export). Con
    /// orden: la columna pedida y SIEMPRE <c>Id</c> ascendente como desempate. Es opt-in: cada fila
    /// conserva su <c>Inicio</c>/<c>Final</c> persistidos, así que bajo otro orden la cadena deja de
    /// leerse de fila a fila. El tipo ordena por el orden de declaración del enum de Postgres (ajuste,
    /// deposito, pago, retiro_caja), que coincide con el alfabético de los rótulos de la pantalla
    /// (Ajuste, Depósito, Pago, Retiro de caja). El concepto ordena por el
    /// texto de la celda (<see cref="ConceptoVisible"/>: categoría y concepto del pago, o el propio). Un
    /// movimiento sin punto de venta, o con uno que ya no resuelve, va al final en las dos
    /// direcciones.</summary>
    private static IOrderedQueryable<Unido> Ordenar(IQueryable<Unido> query, OrdenDeListado<ColumnaDeOrdenDeTesoreria>? orden)
    {
        if (orden is not { } pedido)
        {
            return query.OrderBy(x => x.M.Id);
        }

        var desc = pedido.Descendente;
        IOrderedQueryable<Unido> ordenado = pedido.Columna switch
        {
            ColumnaDeOrdenDeTesoreria.Tipo => desc
                ? query.OrderByDescending(x => x.M.Tipo)
                : query.OrderBy(x => x.M.Tipo),
            ColumnaDeOrdenDeTesoreria.Inicio => desc
                ? query.OrderByDescending(x => x.M.Inicio)
                : query.OrderBy(x => x.M.Inicio),
            ColumnaDeOrdenDeTesoreria.Ingreso => desc
                ? query.OrderByDescending(x => x.M.Ingreso)
                : query.OrderBy(x => x.M.Ingreso),
            ColumnaDeOrdenDeTesoreria.Egreso => desc
                ? query.OrderByDescending(x => x.M.Egreso)
                : query.OrderBy(x => x.M.Egreso),
            ColumnaDeOrdenDeTesoreria.Final => desc
                ? query.OrderByDescending(x => x.M.Final)
                : query.OrderBy(x => x.M.Final),
            ColumnaDeOrdenDeTesoreria.Concepto => desc
                ? query.OrderByDescending(ConceptoVisible)
                : query.OrderBy(ConceptoVisible),
            ColumnaDeOrdenDeTesoreria.PuntoVenta => desc
                ? query.OrderBy(x => x.NombrePuntoVenta == null).ThenByDescending(x => x.NombrePuntoVenta)
                : query.OrderBy(x => x.NombrePuntoVenta == null).ThenBy(x => x.NombrePuntoVenta),
            ColumnaDeOrdenDeTesoreria.Empleado => desc
                ? query.OrderByDescending(x => x.M.IdEmpleado)
                : query.OrderBy(x => x.M.IdEmpleado),
            ColumnaDeOrdenDeTesoreria.Fecha => desc
                ? query.OrderByDescending(x => x.M.Fecha)
                : query.OrderBy(x => x.M.Fecha),
            _ => query.OrderBy(x => x.M.Id),
        };

        return ordenado.ThenBy(x => x.M.Id);
    }

    /// <summary>El texto de la celda Concepto: "Etiqueta de categoría — concepto" cuando la fila tiene pago de
    /// origen, el concepto del movimiento en cualquier otro caso (mismo criterio que <c>conceptoEfectivo</c>
    /// de <c>Tesoreria.tsx</c> y <c>ExportacionDeCaja.ConceptoEfectivo</c>).</summary>
    private static readonly Expression<Func<Unido, string>> ConceptoVisible =
        x => x.PagoConcepto != null ? x.PagoEtiqueta + " — " + x.PagoConcepto : x.M.Concepto;

    /// <summary>Lo que se lee del pago de origen. La etiqueta sale de <see cref="EtiquetasDeCategoriaDePago"/>,
    /// armada como <c>CASE</c> sobre la columna del propio pago: el enum se compara dentro de la subconsulta,
    /// donde la columna conserva su tipo de Postgres, y no contra el valor ya proyectado a nullable.</summary>
    private static readonly Expression<Func<Pago, PagoDeOrigen>> ProyeccionDePago = ConstruirProyeccionDePago();

    private static Expression<Func<Pago, PagoDeOrigen>> ConstruirProyeccionDePago()
    {
        var g = Expression.Parameter(typeof(Pago), "g");
        var categoria = Expression.Property(g, nameof(Pago.Categoria));
        var etiqueta = EtiquetaPorCaso.De(categoria, EtiquetasDeCategoriaDePago.Todas, Expression.Constant(string.Empty));

        return Expression.Lambda<Func<Pago, PagoDeOrigen>>(
            Expression.MemberInit(
                Expression.New(typeof(PagoDeOrigen)),
                Expression.Bind(typeof(PagoDeOrigen).GetProperty(nameof(PagoDeOrigen.Categoria))!, categoria),
                Expression.Bind(typeof(PagoDeOrigen).GetProperty(nameof(PagoDeOrigen.Concepto))!, Expression.Property(g, nameof(Pago.Concepto))),
                Expression.Bind(typeof(PagoDeOrigen).GetProperty(nameof(PagoDeOrigen.Etiqueta))!, etiqueta)),
            g);
    }

    public static readonly IReadOnlyDictionary<string, ColumnaDeOrdenDeTesoreria> ClavesDeOrden =
        new Dictionary<string, ColumnaDeOrdenDeTesoreria>(StringComparer.Ordinal)
        {
            ["tipo"] = ColumnaDeOrdenDeTesoreria.Tipo,
            ["inicio"] = ColumnaDeOrdenDeTesoreria.Inicio,
            ["ingreso"] = ColumnaDeOrdenDeTesoreria.Ingreso,
            ["egreso"] = ColumnaDeOrdenDeTesoreria.Egreso,
            ["final"] = ColumnaDeOrdenDeTesoreria.Final,
            ["concepto"] = ColumnaDeOrdenDeTesoreria.Concepto,
            ["puntoVenta"] = ColumnaDeOrdenDeTesoreria.PuntoVenta,
            ["empleado"] = ColumnaDeOrdenDeTesoreria.Empleado,
            ["fecha"] = ColumnaDeOrdenDeTesoreria.Fecha,
        };

    private sealed class Unido
    {
        public MovimientoTesoreria M { get; init; } = null!;

        public string? NombrePuntoVenta { get; init; }

        public CategoriaPago? PagoCategoria { get; init; }

        public string? PagoConcepto { get; init; }

        public string? PagoEtiqueta { get; init; }
    }

    private sealed class PagoDeOrigen
    {
        public CategoriaPago Categoria { get; init; }

        public string Concepto { get; init; } = null!;

        public string Etiqueta { get; init; } = null!;
    }
}

public enum ColumnaDeOrdenDeTesoreria
{
    Tipo,
    Inicio,
    Ingreso,
    Egreso,
    Final,
    Concepto,
    PuntoVenta,
    Empleado,
    Fecha,
}
