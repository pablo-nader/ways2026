using Ways.Application.Abstracciones;
using Ways.Domain.Articulos;

namespace Ways.Application.Articulos;

/// <summary>
/// Resolución de disponibilidad por empresa (design: "Availability resolution" query
/// extension, spec: articulos / Availability Model) — un único método reusable, llamado hoy
/// desde <see cref="ServicioDeArticulos.ListarAsync"/> y, en una etapa futura, desde la
/// consulta de catálogo del POS (stage 5).
/// </summary>
public static class ArticuloConsultas
{
    /// <summary><see cref="Articulo.DisponibleParaTodas"/> = <c>true</c> ⇒ visible sin
    /// importar <paramref name="idEmpresa"/> (incluidas empresas creadas después, sin
    /// backfill). <c>false</c> ⇒ visible solo si existe una fila de
    /// <see cref="ArticuloEmpresa"/> para esa empresa puntual. El <c>EXISTS</c> correlacionado
    /// se apoya en <c>ix_articulos_empresas_empresa (id_empresa, id_tenant)</c> (design: Table
    /// Shapes).</summary>
    public static IQueryable<Articulo> DisponibleEnEmpresa(
        this IQueryable<Articulo> query, IWaysDbContext db, int idEmpresa) =>
        query.Where(a =>
            a.DisponibleParaTodas ||
            db.ArticulosEmpresas.Any(ae => ae.IdArticulo == a.Id && ae.IdEmpresa == idEmpresa));
}
