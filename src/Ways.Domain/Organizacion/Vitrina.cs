using Ways.Domain.Common;

namespace Ways.Domain.Organizacion;

/// <summary>
/// La identidad pública de una empresa: logo, slogan, descripción y color de acento. Una por
/// empresa; a lo sumo una publicada por tenant, y solo esa es la que muestra la landing del
/// dominio del comercio.
/// </summary>
public class Vitrina : EntidadTenant
{
    public const int LargoMaximoDelTagline = 120;
    public const int LargoMaximoDeLaDescripcion = 2000;
    public const int TamanoMaximoDelLogo = ValidadorDeImagen.TamanoMaximo;

    public const string TemaClaro = "claro";
    public const string TemaOscuro = "oscuro";

    public const string TipoPng = ValidadorDeImagen.TipoPng;
    public const string TipoJpeg = ValidadorDeImagen.TipoJpeg;
    public const string TipoWebp = ValidadorDeImagen.TipoWebp;

    private static readonly ErroresDeImagen ErroresDelLogo = new(
        "logo_demasiado_grande",
        "El logo no puede pesar más de 1 MB.",
        "logo_no_admitido",
        "El logo tiene que ser una imagen PNG, JPEG o WebP.");

    public int Id { get; set; }

    public int IdEmpresa { get; set; }

    public bool Publicada { get; set; }

    public string? Tagline { get; set; }

    public string? Descripcion { get; set; }

    /// <summary><c>#rrggbb</c> en minúscula; <c>null</c> = el naranja de Ways.</summary>
    public string? ColorAcento { get; set; }

    public string Tema { get; set; } = TemaOscuro;

    /// <summary>La lista de precios que usa el catálogo de la tienda; <c>null</c> = la lista default del
    /// tenant. FK compuesta <c>(id_lista_precio, id_tenant)</c>.</summary>
    public int? IdListaPrecio { get; set; }

    public byte[]? Logo { get; set; }

    /// <summary>Se guarda siempre junto con <see cref="Logo"/>: lo decidió el contenido, no el
    /// encabezado que mandó el cliente.</summary>
    public string? LogoContentType { get; set; }

    /// <summary>Origen del ETag del logo y de la versión que la landing agrega a su URL.</summary>
    public DateTimeOffset? LogoActualizadoEn { get; set; }

    public static bool EsTipoDeLogoAdmitido(string? tipo) => ValidadorDeImagen.EsTipoAdmitido(tipo);

    /// <summary>Segundos Unix de la última carga del logo: la versión que la landing agrega a su URL.</summary>
    public static long? VersionDelLogo(DateTimeOffset? actualizadoEn) => actualizadoEn?.ToUnixTimeSeconds();

    /// <summary>Recorta los espacios; un texto vacío es "sin texto". Más largo que
    /// <paramref name="largoMaximo"/> es 400 <c>texto_demasiado_largo</c>.</summary>
    public static string? NormalizarTexto(string? crudo, int largoMaximo, string campo)
    {
        var texto = crudo?.Trim();

        if (string.IsNullOrEmpty(texto))
        {
            return null;
        }

        if (texto.Length > largoMaximo)
        {
            throw new ErrorDominio(
                "texto_demasiado_largo",
                $"{campo} no puede superar los {largoMaximo} caracteres.",
                400);
        }

        return texto;
    }

    /// <summary>Acepta <c>#rrggbb</c> en cualquier caja y lo devuelve en minúscula, que es lo que
    /// exige el CHECK de la base. Vacío es "sin color": la landing usa el de la marca.</summary>
    public static string? NormalizarColor(string? crudo)
    {
        var color = crudo?.Trim();

        if (string.IsNullOrEmpty(color))
        {
            return null;
        }

        if (color.Length != 7 || color[0] != '#' || !color.Skip(1).All(char.IsAsciiHexDigit))
        {
            throw new ErrorDominio(
                "color_invalido",
                "El color de acento tiene que ser un valor hexadecimal con la forma #rrggbb.",
                400);
        }

        return color.ToLowerInvariant();
    }

    /// <summary>El tema es obligatorio y es <c>claro</c> u <c>oscuro</c>.</summary>
    public static string NormalizarTema(string? crudo)
    {
        var tema = crudo?.Trim().ToLowerInvariant();

        if (tema is not (TemaClaro or TemaOscuro))
        {
            throw new ErrorDominio(
                "tema_invalido",
                $"El tema tiene que ser {TemaClaro} u {TemaOscuro}.",
                400);
        }

        return tema;
    }

    /// <summary>
    /// El tipo de imagen que dice el contenido, por sus primeros bytes: PNG, JPEG o WebP.
    /// <c>null</c> para todo lo demás. SVG queda afuera a propósito: un SVG puede llevar scripts y
    /// el logo se sirve desde el mismo origen que la aplicación.
    /// </summary>
    public static string? DetectarTipoDeLogo(ReadOnlySpan<byte> contenido) => ValidadorDeImagen.DetectarTipo(contenido);

    /// <summary>
    /// Valida un logo y devuelve su tipo. El tamaño se mira primero (413
    /// <c>logo_demasiado_grande</c>); después el contenido tiene que ser PNG, JPEG o WebP y coincidir
    /// con el tipo que declaró el cliente, si declaró alguno (415 <c>logo_no_admitido</c>).
    /// </summary>
    public static string ValidarLogo(ReadOnlySpan<byte> contenido, string? tipoDeclarado) =>
        ValidadorDeImagen.Validar(contenido, tipoDeclarado, ErroresDelLogo);
}
