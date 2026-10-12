using Ways.Domain.Articulos;

namespace Ways.Application.CatalogoOnline;

/// <summary>Cuerpo de <c>PUT /api/articulos/{id}/web</c>. <paramref name="Publicado"/> es obligatorio: omitirlo
/// despublicaría el artículo sin que nadie lo haya pedido.</summary>
public record SolicitudDePublicacionWeb(bool? Publicado);

public record PublicacionWeb(int IdArticulo, bool Publicado);

/// <summary>Lo que el admin ve de la imagen de un artículo después de escribirla. <paramref name="ImagenVersion"/>
/// son los segundos Unix de la última carga.</summary>
public record ImagenDeArticuloAdmin(int IdArticulo, bool TieneImagen, long? ImagenVersion);

/// <summary>Tipo y marca de carga de una imagen, sin sus bytes: alcanza para armar el ETag y contestar una
/// condicional sin leer la columna <c>contenido</c>.</summary>
public record VersionDeImagen(string ContentType, DateTimeOffset ActualizadoEn);

/// <summary>Los bytes de una imagen con su tipo y la marca de su carga; solo se leen cuando la condicional no
/// alcanza para contestar.</summary>
public record ImagenServida(byte[] Contenido, string ContentType, DateTimeOffset ActualizadoEn);

/// <summary>Una categoría con artículos publicados que coinciden con la búsqueda. <paramref name="Cantidad"/> no
/// depende del filtro de categoría que esté activo: los chips conservan sus cuentas.</summary>
public record CategoriaPublica(int Id, string Nombre, int Cantidad);

/// <summary>Un artículo del catálogo público. <paramref name="Precio"/> es el de la lista de la tienda, ya
/// resuelto. <paramref name="ImagenVersion"/> son los segundos Unix de la última carga de su imagen; el
/// catálogo la agrega a la URL de la imagen para romper su caché.</summary>
public record ArticuloPublico(
    int Id,
    string Nombre,
    string? Descripcion,
    int? IdCategoria,
    decimal Precio,
    UnidadVenta UnidadVenta,
    bool TieneImagen,
    long? ImagenVersion);

public record CatalogoPublico(
    IReadOnlyList<CategoriaPublica> Categorias, IReadOnlyList<ArticuloPublico> Articulos, int Total, int Pagina, int Tamano);

public record ConsultaDeCatalogo(string? Buscar, int? IdCategoria, int Pagina, int Tamano);
