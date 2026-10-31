using System.Linq.Expressions;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Ways.Application.Abstracciones;
using Ways.Application.Exportacion;
using Ways.Application.Listados;
using Ways.Domain.Common;

namespace Ways.Application.Auditoria;

/// <summary>
/// El lado de lectura del log de auditoría (design decisiones 12-16). Un único
/// <see cref="ConstruirQuery"/>, pensado para ser reusado tal cual por el sibling de export
/// (Slice 6, <c>ConsultarParaExportacionAsync</c> — no declarado todavía en esta slice) —
/// <c>accion</c>/<c>entidad</c> NO se validan contra el catálogo acá (design decisión 15): una
/// acción retirada deja filas consultables, la base es la parte permisiva, la aplicación es la
/// estricta solo en ESCRITURA.
/// </summary>
public sealed class ServicioDeConsultaDeAuditoria(IWaysDbContext db)
{
    /// <summary>Forma cruda de una fila mientras todavía es <c>IQueryable</c> — los dos payloads
    /// viajan como <c>string?</c>/<c>string</c> (el shape de columna real, <c>jsonb</c> ya
    /// serializado por <see cref="SerializadorDeAuditoria"/>) y se parsean a
    /// <see cref="JsonElement"/> recién DESPUÉS de materializar (<see cref="Materializar"/>) — EF
    /// no puede traducir <c>JsonSerializer.Deserialize</c> a SQL.</summary>
    private sealed record FilaCrudaDeAuditoria(
        long IdAuditoria,
        DateTimeOffset CreadoEl,
        string Accion,
        string Entidad,
        int IdEntidad,
        int IdActor,
        string? Actor,
        int? IdPuntoVenta,
        string? ValorAnterior,
        string ValorNuevo);

    public async Task<PaginaDeAuditoria> ConsultarAsync(
        FiltrosDeAuditoria filtros, int pagina = 1, int tamanio = 25, string? orden = null, string? direccion = null,
        CancellationToken ct = default)
    {
        var ordenPedido = OrdenDeListado.Interpretar(orden, direccion, ClavesDeOrden);

        pagina = Math.Max(pagina, 1);
        tamanio = Math.Clamp(tamanio, 1, 200);

        var query = ConstruirQuery(filtros, ordenPedido);

        var total = await query.CountAsync(ct);

        var crudas = await query
            .Skip((pagina - 1) * tamanio)
            .Take(tamanio)
            .ToListAsync(ct);

        var items = crudas.Select(Materializar).ToList();

        return new PaginaDeAuditoria(items, total, pagina, tamanio);
    }

    /// <summary>
    /// Sibling de export (Slice 6, design decisión 13) — shape LISTADO: <c>Contar → GuardaDeTope.
    /// Exigir → Take(tope+1) → segundo Exigir</c> (etapa 11, <c>ServicioDeVentas.
    /// ListarParaExportacionAsync</c> verbatim), sobre el MISMO <see cref="ConstruirQuery"/> que
    /// <see cref="ConsultarAsync"/> — nunca un predicado propio. El orden ya viene de
    /// <see cref="ConstruirQuery"/> (<c>creado_el desc, id desc</c>), sin un <c>OrderBy</c>
    /// adicional acá.
    /// </summary>
    public async Task<IReadOnlyList<FilaDeAuditoria>> ConsultarParaExportacionAsync(
        FiltrosDeAuditoria filtros, int tope, CancellationToken ct = default)
    {
        var query = ConstruirQuery(filtros, null);

        var cantidad = await query.CountAsync(ct);
        GuardaDeTope.Exigir(cantidad, tope);

        var crudas = await query
            .Take(tope + 1)
            .ToListAsync(ct);

        GuardaDeTope.Exigir(crudas.Count, tope);

        return crudas.Select(Materializar).ToList();
    }

    private static FilaDeAuditoria Materializar(FilaCrudaDeAuditoria f) =>
        new(
            f.IdAuditoria, f.CreadoEl, f.Accion, f.Entidad, f.IdEntidad, f.IdActor, f.Actor, f.IdPuntoVenta,
            f.ValorAnterior is null ? null : JsonSerializer.Deserialize<JsonElement>(f.ValorAnterior),
            JsonSerializer.Deserialize<JsonElement>(f.ValorNuevo));

    /// <summary>
    /// Cláusulas bajo prueba (<c>mutation-proof-tests</c>), en orden de daño si se pierden:
    ///   <c>idEntidad</c> sin <c>entidad</c>       → un filtro polimórfico sin desambiguar
    ///                                                mezclaría articulo 7, usuario 7 y
    ///                                                comprobante 7 en una sola respuesta
    ///                                                (design decisión 16) — 400 antes de armar
    ///                                                el query.
    ///   LEFT JOIN sobre usuarios                  → un INNER JOIN borra del log las filas de
    ///                                                un actor root o de un usuario dado de baja
    ///                                                (design decisión 14).
    ///   <c>IgnoreQueryFilters(["BajaLogica"])</c> → sin él, el nombre del actor eliminado
    ///                                                desaparece.
    ///   <c>ThenByDescending(a.Id)</c>              → sin él, con <c>creado_el</c> empatado
    ///                                                (RelojFijo, o varias filas de la misma
    ///                                                operación) la paginación duplica y saltea
    ///                                                filas (design decisión 12).
    ///   cada <c>if (filtro is { } x)</c>           → un filtro ignorado devuelve de más, en
    ///                                                silencio.
    /// </summary>
    private IQueryable<FilaCrudaDeAuditoria> ConstruirQuery(FiltrosDeAuditoria f, OrdenDeListado<ColumnaDeOrdenDeAuditoria>? orden)
    {
        if (f.IdEntidad is not null && f.Entidad is null)
        {
            throw new ErrorDominio("entidad_requerida", "idEntidad requiere entidad.", 400);
        }

        // El orden va ANTES de proyectar a FilaCrudaDeAuditoria (un record posicional no traduce en un
        // key selector): se ordena sobre `Unido`, una clase con inicializador de miembros, y recién
        // después se proyecta.
        var baseUnida =
            from a in db.Auditoria
            where (f.Desde == null || a.CreadoEl >= f.Desde)
               && (f.Hasta == null || a.CreadoEl <= f.Hasta)
               && (f.Accion == null || a.Accion == f.Accion)
               && (f.IdActor == null || a.IdActor == f.IdActor)
               && (f.Entidad == null || a.Entidad == f.Entidad)
               && (f.IdEntidad == null || a.IdEntidad == f.IdEntidad)
               && (f.IdPuntoVenta == null || a.IdPuntoVenta == f.IdPuntoVenta)
            join u in db.Usuarios.IgnoreQueryFilters(["BajaLogica"]) on a.IdActor equals u.Id into actores
            from u in actores.DefaultIfEmpty()
            select new Unido { A = a, Actor = u == null ? null : u.NombreUsuario };

        // El LEFT JOIN a puntos de venta existe SOLO cuando se ordena por esa columna: el listado de
        // siempre, el conteo y el export no lo pagan. La baja lógica se filtra a mano: el
        // IgnoreQueryFilters(["BajaLogica"]) de usuarios alcanza a toda la consulta, así que el filtro global ya no
        // protege acá. Un punto de venta dado de baja o colgante llega como null, igual que un evento de todo el tenant.
        var unido = orden is { Columna: ColumnaDeOrdenDeAuditoria.PuntoVenta }
            ? from x in baseUnida
              join pv in db.PuntosVenta.Where(p => p.DeletedAt == null) on x.A.IdPuntoVenta equals pv.Id into puntosVenta
              from pv in puntosVenta.DefaultIfEmpty()
              select new Unido { A = x.A, Actor = x.Actor, PuntoVenta = pv == null ? null : pv.Nombre }
            : baseUnida;

        return Ordenar(unido, orden).Select(x => new FilaCrudaDeAuditoria(
            x.A.Id, x.A.CreadoEl, x.A.Accion, x.A.Entidad, x.A.IdEntidad, x.A.IdActor,
            x.Actor, x.A.IdPuntoVenta, x.A.ValorAnterior, x.A.ValorNuevo));
    }

    /// <summary>Sin orden pedido: <c>creado_el desc, id desc</c> (el orden de siempre, también el del
    /// export). Con orden: la columna pedida y SIEMPRE <c>Id</c> ascendente como desempate, para que dos
    /// páginas consecutivas nunca repitan ni salteen una fila. Los vacíos (actor sin usuario visible,
    /// evento de todo el tenant o con punto de venta que ya no resuelve) van al final en las dos
    /// direcciones. La acción ordena por la etiqueta que muestra la celda
    /// (<see cref="EtiquetasDeAccion"/>; sin etiqueta, el código crudo, como la pantalla).</summary>
    private static IOrderedQueryable<Unido> Ordenar(IQueryable<Unido> query, OrdenDeListado<ColumnaDeOrdenDeAuditoria>? orden)
    {
        if (orden is not { } pedido)
        {
            return query.OrderByDescending(x => x.A.CreadoEl).ThenByDescending(x => x.A.Id);
        }

        var desc = pedido.Descendente;
        IOrderedQueryable<Unido> ordenado = pedido.Columna switch
        {
            ColumnaDeOrdenDeAuditoria.Fecha => desc
                ? query.OrderByDescending(x => x.A.CreadoEl)
                : query.OrderBy(x => x.A.CreadoEl),
            ColumnaDeOrdenDeAuditoria.Accion => desc
                ? query.OrderByDescending(AccionVisible)
                : query.OrderBy(AccionVisible),
            ColumnaDeOrdenDeAuditoria.Entidad => desc
                ? query.OrderByDescending(x => x.A.Entidad)
                : query.OrderBy(x => x.A.Entidad),
            ColumnaDeOrdenDeAuditoria.IdEntidad => desc
                ? query.OrderByDescending(x => x.A.IdEntidad)
                : query.OrderBy(x => x.A.IdEntidad),
            ColumnaDeOrdenDeAuditoria.Actor => desc
                ? query.OrderBy(x => x.Actor == null).ThenByDescending(x => x.Actor)
                : query.OrderBy(x => x.Actor == null).ThenBy(x => x.Actor),
            ColumnaDeOrdenDeAuditoria.PuntoVenta => desc
                ? query.OrderBy(x => x.PuntoVenta == null).ThenByDescending(x => x.PuntoVenta)
                : query.OrderBy(x => x.PuntoVenta == null).ThenBy(x => x.PuntoVenta),
            _ => query.OrderByDescending(x => x.A.CreadoEl),
        };

        return ordenado.ThenBy(x => x.A.Id);
    }

    /// <summary>Etiquetas en español de las acciones que muestra la pantalla de auditoría. Espejo de
    /// <c>CATALOGO_DE_ACCIONES_AUDITADAS</c> en <c>Ways.Web/src/api/tipos.ts</c> (la base solo guarda el
    /// código): que no se desfasen lo cuidan <c>EtiquetasDeAccionTests</c> (backend) y el test de
    /// <c>CATALOGO_DE_ACCIONES_AUDITADAS</c> en <c>tipos.test.ts</c> (web), que fijan la misma lista literal.
    /// Una acción sin etiqueta se muestra, y por lo tanto se ordena, por su código.</summary>
    public static readonly IReadOnlyDictionary<string, string> EtiquetasDeAccion =
        new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["precio.cambio"] = "Cambio de precio",
            ["venta.anulacion"] = "Anulación de venta",
            ["compra.anulacion"] = "Anulación de compra",
            ["compra.edicion"] = "Edición de compra",
            ["compra.redondeo"] = "Ajuste de redondeo de compra",
            ["pago.aplicacion"] = "Aplicación de pago a compras",
            ["pago.desaplicacion"] = "Desaplicación de pago de una compra",
            ["pago.redondeo"] = "Ajuste de redondeo de pago",
            ["pago.reasignacion"] = "Reasignación de pago de sueldo",
            ["cobro.aplicacion"] = "Aplicación de cobro a ventas",
            ["cobro.desaplicacion"] = "Desaplicación de cobro de una venta",
            ["stock.ajuste"] = "Ajuste de stock",
            ["stock.decomiso"] = "Decomiso de stock",
            ["stock.conteo"] = "Conteo de inventario",
            ["cc.reliquidacion"] = "Reliquidación de cuenta corriente",
            ["usuario.alta"] = "Alta de usuario",
            ["usuario.actualizacion"] = "Actualización de usuario",
            ["usuario.baja"] = "Baja de usuario",
            ["usuario.desbloqueo"] = "Desbloqueo de usuario",
            ["usuario.password"] = "Cambio de contraseña",
            ["tenant.baja"] = "Baja de tenant",
            ["empresa.baja"] = "Baja de empresa",
            ["pv.baja"] = "Baja de punto de venta",
        };

    private static readonly Expression<Func<Unido, string>> AccionVisible = ConstruirAccionVisible();

    private static Expression<Func<Unido, string>> ConstruirAccionVisible()
    {
        var x = Expression.Parameter(typeof(Unido), "x");
        var codigo = Expression.Property(Expression.Property(x, nameof(Unido.A)), nameof(Domain.Auditoria.Auditoria.Accion));
        return Expression.Lambda<Func<Unido, string>>(EtiquetaPorCaso.De(codigo, EtiquetasDeAccion, codigo), x);
    }

    public static readonly IReadOnlyDictionary<string, ColumnaDeOrdenDeAuditoria> ClavesDeOrden =
        new Dictionary<string, ColumnaDeOrdenDeAuditoria>(StringComparer.Ordinal)
        {
            ["fecha"] = ColumnaDeOrdenDeAuditoria.Fecha,
            ["accion"] = ColumnaDeOrdenDeAuditoria.Accion,
            ["entidad"] = ColumnaDeOrdenDeAuditoria.Entidad,
            ["idEntidad"] = ColumnaDeOrdenDeAuditoria.IdEntidad,
            ["actor"] = ColumnaDeOrdenDeAuditoria.Actor,
            ["puntoVenta"] = ColumnaDeOrdenDeAuditoria.PuntoVenta,
        };

    private sealed class Unido
    {
        public Domain.Auditoria.Auditoria A { get; init; } = null!;

        public string? Actor { get; init; }

        public string? PuntoVenta { get; init; }
    }
}

public enum ColumnaDeOrdenDeAuditoria
{
    Fecha,
    Accion,
    Entidad,
    IdEntidad,
    Actor,
    PuntoVenta,
}
