using Microsoft.EntityFrameworkCore;
using Ways.Application.Abstracciones;
using Ways.Domain.Articulos;

namespace Ways.Application.Familias;

/// <summary>
/// Cuáles de los ids de catálogo de unos valores compartidos (<see cref="ValoresCompartidosDeFamilia"/>) apuntan a una
/// fila VISIBLE —viva y del tenant— de su catálogo: el área, la categoría, el grupo y el proveedor habitual. Una baja
/// lógica deja el id guardado en el artículo y esconde la fila, y un id colgante se lee igual que un id ausente
/// (<c>dangling-fk-read-models</c>): <see cref="Presentar"/> lo muestra como <c>null</c>, igual que
/// <see cref="ServicioDeFamilias.ObtenerAsync"/> con los valores de la referencia de una familia.
/// </summary>
internal sealed class CatalogosVisibles
{
    private readonly HashSet<int> areas;
    private readonly HashSet<int> categorias;
    private readonly HashSet<int> grupos;
    private readonly HashSet<int> proveedores;

    private CatalogosVisibles(HashSet<int> areas, HashSet<int> categorias, HashSet<int> grupos, HashSet<int> proveedores)
    {
        this.areas = areas;
        this.categorias = categorias;
        this.grupos = grupos;
        this.proveedores = proveedores;
    }

    /// <summary>Lee, en una consulta por catálogo y sin rastreo, cuáles de los ids que traen <paramref name="valores"/> son
    /// visibles. Solo lee: no toma ningún lock.</summary>
    public static async Task<CatalogosVisibles> LeerAsync(
        IWaysDbContext db, IEnumerable<ValoresCompartidosDeFamilia> valores, CancellationToken ct)
    {
        var todos = valores.ToList();

        var idsDeAreas = todos.Select(v => v.IdArea).Distinct().ToArray();
        var idsDeCategorias = todos.Where(v => v.IdCategoria is not null).Select(v => v.IdCategoria!.Value).Distinct().ToArray();
        var idsDeGrupos = todos.Where(v => v.IdGrupo is not null).Select(v => v.IdGrupo!.Value).Distinct().ToArray();
        var idsDeProveedores = todos
            .Where(v => v.IdProveedorHabitual is not null)
            .Select(v => v.IdProveedorHabitual!.Value)
            .Distinct()
            .ToArray();

        return new CatalogosVisibles(
            await db.Areas.AsNoTracking().Where(a => idsDeAreas.Contains(a.Id)).Select(a => a.Id).ToHashSetAsync(ct),
            await db.Categorias.AsNoTracking().Where(c => idsDeCategorias.Contains(c.Id)).Select(c => c.Id).ToHashSetAsync(ct),
            await db.Grupos.AsNoTracking().Where(g => idsDeGrupos.Contains(g.Id)).Select(g => g.Id).ToHashSetAsync(ct),
            await db.Proveedores.AsNoTracking().Where(p => idsDeProveedores.Contains(p.Id)).Select(p => p.Id).ToHashSetAsync(ct));
    }

    public bool AreaEsVisible(int idArea) => areas.Contains(idArea);

    public bool CategoriaEsVisible(int idCategoria) => categorias.Contains(idCategoria);

    public bool GrupoEsVisible(int idGrupo) => grupos.Contains(idGrupo);

    public bool ProveedorEsVisible(int idProveedor) => proveedores.Contains(idProveedor);

    /// <summary>Los trece valores como los ve un cliente: un id de catálogo que no es visible viaja como <c>null</c>. Lo
    /// demás es el valor guardado.</summary>
    public ValoresCompartidosDeLaFamilia Presentar(ValoresCompartidosDeFamilia valores) =>
        ValoresCompartidosDeLaFamilia.De(valores) with
        {
            IdArea = AreaEsVisible(valores.IdArea) ? valores.IdArea : null,
            IdCategoria = valores.IdCategoria is { } idCategoria && CategoriaEsVisible(idCategoria) ? idCategoria : null,
            IdGrupo = valores.IdGrupo is { } idGrupo && GrupoEsVisible(idGrupo) ? idGrupo : null,
            IdProveedorHabitual = valores.IdProveedorHabitual is { } idProveedor && ProveedorEsVisible(idProveedor)
                ? idProveedor
                : null
        };
}
