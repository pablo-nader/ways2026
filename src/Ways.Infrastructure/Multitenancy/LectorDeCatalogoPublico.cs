using Microsoft.Extensions.DependencyInjection;
using Ways.Application.CatalogoOnline;

namespace Ways.Infrastructure.Multitenancy;

/// <summary>
/// Lee el catálogo de un comercio para una request sin sesión, en el modo de acceso
/// <see cref="ModoDeAcceso.Publico"/>. Cada lectura abre un alcance de DI propio (ver
/// <see cref="AlcanceEnModoPublico"/>) y corre ahí <see cref="ConsultaDeCatalogoPublico"/>, que es quien
/// decide qué se proyecta; este lector solo aporta el modo y el tenant. Las policies de lectura pública
/// (<c>articulos</c>, <c>articulos_empresas</c>, <c>listas_precio</c>, <c>precios</c>, <c>categorias</c>,
/// <c>imagenes_articulo</c>, más las tres de la vitrina) dejan ver filas enteras: nada de lo que sale de acá
/// es una entidad, solo los records de <see cref="CatalogoPublico"/>.
/// </summary>
public sealed class LectorDeCatalogoPublico(IServiceScopeFactory alcances) : ILectorDeCatalogoPublico
{
    public async Task<CatalogoPublico?> ObtenerAsync(
        int idTenant, ConsultaDeCatalogo consulta, CancellationToken ct = default)
    {
        await using var alcance = AlcanceEnModoPublico.Abrir(alcances, idTenant);

        return await alcance.ServiceProvider
            .GetRequiredService<ConsultaDeCatalogoPublico>()
            .ObtenerCatalogoAsync(consulta, ct);
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
