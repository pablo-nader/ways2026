namespace Ways.Domain.Common;

/// <summary>Los textos de los dos rechazos de una imagen: cada recurso (el logo de la vitrina, la imagen de
/// un artículo) conserva sus propios códigos de error.</summary>
public sealed record ErroresDeImagen(
    string CodigoDeTamano, string MensajeDeTamano, string CodigoDeTipo, string MensajeDeTipo);

/// <summary>
/// Validación de una imagen subida: tamaño máximo y tipo por los primeros bytes (PNG, JPEG o WebP). Es la
/// regla única del logo de la vitrina y de la imagen de los artículos; los CHECK de las dos tablas repiten
/// el tamaño y los tipos. SVG queda afuera a propósito: un SVG puede llevar scripts y estas imágenes se
/// sirven desde el mismo origen que la aplicación.
/// </summary>
public static class ValidadorDeImagen
{
    public const int TamanoMaximo = 1024 * 1024;

    public const string TipoPng = "image/png";
    public const string TipoJpeg = "image/jpeg";
    public const string TipoWebp = "image/webp";

    private static readonly byte[] FirmaPng = [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A];
    private static readonly byte[] FirmaJpeg = [0xFF, 0xD8, 0xFF];
    private static readonly byte[] FirmaRiff = "RIFF"u8.ToArray();
    private static readonly byte[] FirmaWebp = "WEBP"u8.ToArray();

    public static bool EsTipoAdmitido(string? tipo) => tipo is TipoPng or TipoJpeg or TipoWebp;

    /// <summary>El tipo de imagen que dice el contenido, por sus primeros bytes. <c>null</c> para todo lo
    /// demás.</summary>
    public static string? DetectarTipo(ReadOnlySpan<byte> contenido)
    {
        if (contenido.StartsWith(FirmaPng))
        {
            return TipoPng;
        }

        if (contenido.StartsWith(FirmaJpeg))
        {
            return TipoJpeg;
        }

        // RIFF <tamaño de 4 bytes> WEBP
        if (contenido.Length >= 12 && contenido.StartsWith(FirmaRiff) && contenido[8..12].SequenceEqual(FirmaWebp))
        {
            return TipoWebp;
        }

        return null;
    }

    /// <summary>
    /// Valida una imagen y devuelve su tipo. El tamaño se mira primero (413); después el contenido tiene que
    /// ser PNG, JPEG o WebP y coincidir con el tipo que declaró el cliente, si declaró alguno (415).
    /// </summary>
    public static string Validar(ReadOnlySpan<byte> contenido, string? tipoDeclarado, ErroresDeImagen errores)
    {
        if (contenido.Length > TamanoMaximo)
        {
            throw new ErrorDominio(errores.CodigoDeTamano, errores.MensajeDeTamano, 413);
        }

        var tipo = DetectarTipo(contenido);

        if (tipo is null || tipoDeclarado is not null && !string.Equals(tipo, tipoDeclarado, StringComparison.Ordinal))
        {
            throw new ErrorDominio(errores.CodigoDeTipo, errores.MensajeDeTipo, 415);
        }

        return tipo;
    }
}
