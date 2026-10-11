namespace Ways.Application.Vitrinas;

/// <summary>Un punto de venta activo de la empresa, tal como lo muestra la landing.</summary>
public record SucursalPublica(
    string Nombre,
    string? Domicilio,
    string? Horario,
    string? Whatsapp,
    string? Instagram,
    string? Facebook,
    string? Web);

/// <summary>
/// La vitrina publicada de un comercio, sin sesión. <paramref name="LogoVersion"/> son los segundos
/// Unix de la última carga del logo: la landing la agrega a la URL del logo para romper su caché.
/// </summary>
public record VitrinaPublica(
    string Nombre,
    string? Tagline,
    string? Descripcion,
    string? ColorAcento,
    string Tema,
    bool TieneLogo,
    long? LogoVersion,
    IReadOnlyList<SucursalPublica> Sucursales);

/// <summary>
/// Cuál es el logo y de qué carga, sin sus bytes: alcanza para armar el ETag y contestar una condicional
/// sin leer la columna <c>logo</c>.
/// </summary>
public record VersionDeLogo(string ContentType, DateTimeOffset ActualizadoEn);

/// <summary>
/// Los bytes del logo con su tipo y la marca de su carga. Los sirven el endpoint público y la vista previa
/// del admin; solo se leen cuando la condicional no alcanza para contestar.
/// </summary>
public record LogoPublico(byte[] Contenido, string ContentType, DateTimeOffset ActualizadoEn);
