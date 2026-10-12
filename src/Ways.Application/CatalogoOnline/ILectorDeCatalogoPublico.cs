namespace Ways.Application.CatalogoOnline;

/// <summary>
/// Lee el catálogo que un comercio publica en su dominio, sin sesión. Lo único que lo separa de una lectura de
/// tenant es el modo de acceso: corre en <c>publico</c>, que además de las tres tablas de la vitrina puede
/// hacer <c>SELECT</c> sobre <c>articulos</c> (publicados y activos), <c>articulos_empresas</c>,
/// <c>listas_precio</c>, <c>precios</c>, <c>categorias</c> e <c>imagenes_articulo</c> del tenant que se le pasa
/// (ver <see cref="ConsultaDeCatalogoPublico"/>).
/// </summary>
public interface ILectorDeCatalogoPublico
{
    /// <summary><c>null</c> cuando el tenant no tiene una vitrina publicada.</summary>
    Task<CatalogoPublico?> ObtenerAsync(int idTenant, ConsultaDeCatalogo consulta, CancellationToken ct = default);

    /// <summary>Tipo y marca de carga de la imagen, sin leer la columna <c>contenido</c>. <c>null</c> cuando no
    /// hay vitrina publicada, el artículo no está publicado o disponible para la empresa, o no tiene imagen.</summary>
    Task<VersionDeImagen?> ObtenerVersionDeImagenAsync(int idTenant, int idArticulo, CancellationToken ct = default);

    /// <summary>Los bytes de la imagen. Misma regla que <see cref="ObtenerVersionDeImagenAsync"/>.</summary>
    Task<ImagenServida?> ObtenerImagenAsync(int idTenant, int idArticulo, CancellationToken ct = default);
}
