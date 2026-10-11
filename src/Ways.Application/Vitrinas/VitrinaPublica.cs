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

public record LogoPublico(byte[] Contenido, string ContentType, DateTimeOffset ActualizadoEn);
