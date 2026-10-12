using System.Text.Json.Serialization;
using Ways.Domain.Articulos;

namespace Ways.Application.CatalogoOnline;

/// <summary>Cuerpo de <c>PUT /api/articulos/{id}/disponibilidad</c>. Los dos son obligatorios (<c>400
/// disponible_venta_requerido</c> / <c>400 disponible_online_requerido</c>): omitir uno lo dejaría en <c>false</c> sin
/// que nadie lo haya pedido y sacaría el artículo de la venta o de la tienda.</summary>
public record SolicitudDeDisponibilidad(bool? DisponibleVenta, bool? DisponibleOnline);

public record DisponibilidadDeArticulo(int IdArticulo, bool DisponibleVenta, bool DisponibleOnline);

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

/// <summary>Un artículo del catálogo público. <paramref name="Precio"/> es el que paga el cliente: el de la lista
/// de la tienda con la oferta online aplicada, si la hay. <paramref name="PrecioOriginal"/> y <paramref name="Oferta"/>
/// (el nombre de la oferta de mayor prioridad que lo rebaja) solo viajan cuando una oferta disponible online lo baja
/// de verdad; sin ella las dos claves faltan en el JSON. <paramref name="ImagenVersion"/> son los segundos Unix de la
/// última carga de su imagen; el catálogo la agrega a la URL de la imagen para romper su caché.</summary>
public record ArticuloPublico(
    int Id,
    string Nombre,
    string? Descripcion,
    int? IdCategoria,
    decimal Precio,
    UnidadVenta UnidadVenta,
    bool TieneImagen,
    long? ImagenVersion,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    decimal? PrecioOriginal = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    string? Oferta = null);

/// <summary>Un componente de un combo público: siempre un artículo de la tienda, con la cantidad que lleva el combo.
/// <paramref name="ImagenVersion"/> es el mismo contador que el de <see cref="ArticuloPublico"/> (<c>null</c> sin imagen).</summary>
public record ComponenteDeComboPublico(int IdArticulo, string Nombre, decimal Cantidad, long? ImagenVersion);

/// <summary>Un combo de la tienda: <paramref name="Precio"/> es el precio fijo del conjunto. No tiene imagen propia;
/// la tarjeta muestra las de sus componentes. No lleva el código del combo: es el atajo de la caja y la tienda no lo
/// muestra.</summary>
public record ComboPublico(
    int Id, string Nombre, decimal Precio, IReadOnlyList<ComponenteDeComboPublico> Componentes);

/// <summary><paramref name="Combos"/> solo se llena en la primera página sin búsqueda ni categoría (<c>pagina = 1</c>,
/// sin <c>buscar</c> y sin <c>idCategoria</c>); en cualquier otra consulta es una lista vacía, para que el visitante
/// que filtra o pagina no vea los combos repetidos. <c>GET /api/publico/combos</c> los devuelve siempre.</summary>
public record CatalogoPublico(
    IReadOnlyList<CategoriaPublica> Categorias,
    IReadOnlyList<ArticuloPublico> Articulos,
    int Total,
    int Pagina,
    int Tamano,
    IReadOnlyList<ComboPublico>? Combos = null)
{
    public IReadOnlyList<ComboPublico> Combos { get; init; } = Combos ?? [];
}

public record ConsultaDeCatalogo(string? Buscar, int? IdCategoria, int Pagina, int Tamano);
