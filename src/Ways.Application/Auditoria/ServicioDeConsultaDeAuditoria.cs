using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Ways.Application.Abstracciones;
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
        FiltrosDeAuditoria filtros, int pagina = 1, int tamanio = 25, CancellationToken ct = default)
    {
        pagina = Math.Max(pagina, 1);
        tamanio = Math.Clamp(tamanio, 1, 200);

        var query = ConstruirQuery(filtros);

        var total = await query.CountAsync(ct);

        var crudas = await query
            .Skip((pagina - 1) * tamanio)
            .Take(tamanio)
            .ToListAsync(ct);

        var items = crudas.Select(Materializar).ToList();

        return new PaginaDeAuditoria(items, total, pagina, tamanio);
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
    private IQueryable<FilaCrudaDeAuditoria> ConstruirQuery(FiltrosDeAuditoria f)
    {
        if (f.IdEntidad is not null && f.Entidad is null)
        {
            throw new ErrorDominio("entidad_requerida", "idEntidad requiere entidad.", 400);
        }

        var query =
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
            orderby a.CreadoEl descending, a.Id descending
            select new FilaCrudaDeAuditoria(
                a.Id, a.CreadoEl, a.Accion, a.Entidad, a.IdEntidad, a.IdActor,
                u == null ? null : u.NombreUsuario, a.IdPuntoVenta, a.ValorAnterior, a.ValorNuevo);

        return query;
    }
}
