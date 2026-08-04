using Microsoft.EntityFrameworkCore;
using Ways.Application.Abstracciones;
using Ways.Domain.Common;
using Ways.Domain.Ventas;

namespace Ways.Application.Ventas;

/// <summary>Respuesta de <c>GET /api/articulos/escaneo</c> (design: API Surface) — identidad y
/// campos de snapshot únicamente, nunca precio ni oferta (design decisión 7: la resolución de
/// precio queda en el único camino existente, <c>POST /api/ofertas/resolver</c>).
/// <see cref="CodigoBarra"/> es <c>null</c> cuando la entrada resolvió por
/// <c>codigo_interno</c> — el llamador (carrito del POS) usa <see cref="Cantidad"/> tal cual la
/// devolvió <see cref="Ways.Domain.Ventas.ParserDeEscaneo"/>.</summary>
public record ArticuloEscaneado(
    int IdArticulo,
    string CodigoInterno,
    string Nombre,
    string? CodigoBarra,
    decimal Cantidad);

/// <summary>
/// Resolución de escaneo del POS (design decisiones 7 y 10) — servicio de Application dedicado,
/// distinto de <see cref="Articulos.ServicioDeArticulos"/> (ABM): acá no hay autorización de
/// catálogo ni ciclo de vida de escritura, solo una lectura identity-only. Parsea con
/// <see cref="ParserDeEscaneo"/> (pure) y corre UNA query contra <c>articulos</c>/
/// <c>codigos_barra</c>, ambas ya filtradas por tenant (EF query filter) y por
/// <c>activo = true</c> (spec: "Inactive articulo is not resolved").
/// </summary>
public class ServicioDeEscaneo(IWaysDbContext db)
{
    public async Task<ArticuloEscaneado> ResolverAsync(string entrada, CancellationToken ct = default)
    {
        var parseado = ParserDeEscaneo.Parsear(entrada);

        var articulo = parseado.Objetivo switch
        {
            ObjetivoDeEscaneo.CodigoInterno => await db.Articulos
                .Where(a => a.Activo && a.CodigoInterno == parseado.Codigo)
                .FirstOrDefaultAsync(ct),

            ObjetivoDeEscaneo.CodigoBarra => await (
                from a in db.Articulos
                join c in db.CodigosBarra on a.Id equals c.IdArticulo
                where a.Activo && c.Activo && c.Codigo == parseado.Codigo
                select a).FirstOrDefaultAsync(ct),

            _ => null
        };

        if (articulo is null)
        {
            throw ErrorDominio.NoEncontrado($"No se encontró un artículo activo para el código {parseado.Codigo}.");
        }

        return new ArticuloEscaneado(
            articulo.Id,
            articulo.CodigoInterno,
            articulo.Nombre,
            parseado.Objetivo == ObjetivoDeEscaneo.CodigoBarra ? parseado.Codigo : null,
            parseado.Cantidad);
    }
}
