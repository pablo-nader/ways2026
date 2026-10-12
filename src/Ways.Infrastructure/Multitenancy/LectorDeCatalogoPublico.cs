using Microsoft.Extensions.DependencyInjection;
using Ways.Application.CatalogoOnline;

namespace Ways.Infrastructure.Multitenancy;

/// <summary>
/// Lee el catálogo de un comercio para una request sin sesión, en el modo de acceso
/// <see cref="ModoDeAcceso.Publico"/>. Cada lectura abre un alcance de DI propio (ver
/// <see cref="AlcanceEnModoPublico"/>) y corre ahí <see cref="ConsultaDeCatalogoPublico"/>, que es quien
/// decide qué se proyecta; este lector solo aporta el modo y el tenant. Las policies de lectura pública
/// (<c>articulos</c>, <c>articulos_empresas</c>, <c>listas_precio</c>, <c>precios</c>, <c>categorias</c>,
/// <c>imagenes_articulo</c>, <c>ofertas</c>, <c>ofertas_listas</c>, <c>combos</c>, <c>combos_componentes</c>, más las
/// tres de la vitrina) dejan ver filas enteras: nada de lo que sale de acá es una entidad, solo los records de
/// <see cref="CatalogoPublico"/> y <see cref="ComboPublico"/>.
/// </summary>
public sealed class LectorDeCatalogoPublico(IServiceScopeFactory alcances) : ILectorDeCatalogoPublico
{
    public async Task<CatalogoPublico?> ObtenerAsync(
        int idTenant, ConsultaDeCatalogo consulta, CancellationToken ct = default)
    {
        await using var alcance = AlcanceEnModoPublico.Abrir(alcances, idTenant);

        var catalogo = await alcance.ServiceProvider
            .GetRequiredService<ConsultaDeCatalogoPublico>()
            .ObtenerCatalogoAsync(consulta, ct);

        // Los combos viajan solo en la primera página sin búsqueda ni categoría: el visitante que filtra o pagina
        // no los ve repetidos. Pagina es la ya acotada por la consulta, y "sin búsqueda" lo decide la misma
        // normalización que aplica el catálogo (una búsqueda que son solo caracteres de control no filtra nada).
        if (catalogo is null || catalogo.Pagina != 1 || ConsultaDeCatalogoPublico.Normalizar(consulta.Buscar) is not null
            || consulta.IdCategoria is not null)
        {
            return catalogo;
        }

        var combos = await alcance.ServiceProvider
            .GetRequiredService<ConsultaDeCombosPublicos>()
            .ObtenerAsync(ct);

        return catalogo with { Combos = combos ?? [] };
    }

    public async Task<IReadOnlyList<ComboPublico>?> ObtenerCombosAsync(int idTenant, CancellationToken ct = default)
    {
        await using var alcance = AlcanceEnModoPublico.Abrir(alcances, idTenant);

        return await alcance.ServiceProvider
            .GetRequiredService<ConsultaDeCombosPublicos>()
            .ObtenerAsync(ct);
    }

    public async Task<VersionDeImagen?> ObtenerVersionDeImagenAsync(
        int idTenant, int idArticulo, CancellationToken ct = default)
    {
        await using var alcance = AlcanceEnModoPublico.Abrir(alcances, idTenant);

        return await alcance.ServiceProvider
            .GetRequiredService<ConsultaDeCatalogoPublico>()
            .ObtenerVersionDeImagenAsync(idArticulo, ct);
    }

    public async Task<ImagenServida?> ObtenerImagenAsync(int idTenant, int idArticulo, CancellationToken ct = default)
    {
        await using var alcance = AlcanceEnModoPublico.Abrir(alcances, idTenant);

        return await alcance.ServiceProvider
            .GetRequiredService<ConsultaDeCatalogoPublico>()
            .ObtenerImagenAsync(idArticulo, ct);
    }
}
