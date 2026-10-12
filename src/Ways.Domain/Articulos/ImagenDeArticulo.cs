using Ways.Domain.Common;

namespace Ways.Domain.Articulos;

/// <summary>
/// La imagen de un artículo en el catálogo público: una por artículo vivo, guardada en la base como el logo
/// de la vitrina (PNG, JPEG o WebP, hasta 1 MB). El tipo lo decide el contenido, no el encabezado que mandó
/// el cliente.
/// </summary>
public class ImagenDeArticulo : EntidadTenant
{
    public const int TamanoMaximo = ValidadorDeImagen.TamanoMaximo;

    private static readonly ErroresDeImagen Errores = new(
        "imagen_demasiado_grande",
        "La imagen no puede pesar más de 1 MB.",
        "imagen_no_admitida",
        "La imagen tiene que ser PNG, JPEG o WebP.");

    public int Id { get; set; }

    public int IdArticulo { get; set; }

    public required byte[] Contenido { get; set; }

    public required string ContentType { get; set; }

    /// <summary>Origen del ETag y de la versión que el catálogo agrega a la URL de la imagen.</summary>
    public DateTimeOffset ActualizadoEn { get; set; }

    /// <summary>Segundos Unix de la última carga: la versión que el catálogo agrega a la URL de la imagen.</summary>
    public static long Version(DateTimeOffset actualizadoEn) => actualizadoEn.ToUnixTimeSeconds();

    /// <summary>Valida la imagen y devuelve su tipo: 413 <c>imagen_demasiado_grande</c> por encima de 1 MB, 415
    /// <c>imagen_no_admitida</c> si no es PNG, JPEG o WebP o no coincide con el tipo declarado.</summary>
    public static string Validar(ReadOnlySpan<byte> contenido, string? tipoDeclarado) =>
        ValidadorDeImagen.Validar(contenido, tipoDeclarado, Errores);
}
