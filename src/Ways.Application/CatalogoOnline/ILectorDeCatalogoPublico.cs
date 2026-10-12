namespace Ways.Application.CatalogoOnline;

/// <summary>
/// Lee el catálogo que un comercio publica en su dominio, sin sesión. Lo único que lo separa de una lectura de
/// tenant es el modo de acceso: corre en <c>publico</c>, que además de las tres tablas de la vitrina puede
/// hacer <c>SELECT</c> sobre <c>articulos</c> (disponibles online y a la venta, y activos), <c>articulos_empresas</c>,
/// <c>listas_precio</c>, <c>precios</c>, <c>categorias</c>, <c>imagenes_articulo</c>, <c>ofertas</c> y
/// <c>ofertas_listas</c>, <c>combos</c> y <c>combos_componentes</c> del tenant que se le pasa (ver
/// <see cref="ConsultaDeCatalogoPublico"/> y <see cref="ConsultaDeCombosPublicos"/>).
/// </summary>
public interface ILectorDeCatalogoPublico
{
    /// <summary><c>null</c> cuando el tenant no tiene una vitrina publicada.</summary>
    Task<CatalogoPublico?> ObtenerAsync(int idTenant, ConsultaDeCatalogo consulta, CancellationToken ct = default);

    /// <summary>Los combos que la tienda ofrece, ordenados por nombre. <c>null</c> cuando el tenant no tiene una vitrina
    /// publicada.</summary>
    Task<IReadOnlyList<ComboPublico>?> ObtenerCombosAsync(int idTenant, CancellationToken ct = default);

    /// <summary>Tipo y marca de carga de la imagen, sin leer la columna <c>contenido</c>. <c>null</c> cuando no
    /// hay vitrina publicada, el artículo no está publicado o disponible para la empresa, o no tiene imagen.</summary>
    Task<VersionDeImagen?> ObtenerVersionDeImagenAsync(int idTenant, int idArticulo, CancellationToken ct = default);

    /// <summary>Los bytes de la imagen. Misma regla que <see cref="ObtenerVersionDeImagenAsync"/>.</summary>
    Task<ImagenServida?> ObtenerImagenAsync(int idTenant, int idArticulo, CancellationToken ct = default);
}
