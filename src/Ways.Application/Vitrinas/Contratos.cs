namespace Ways.Application.Vitrinas;

/// <summary>
/// Datos de la vitrina de una empresa. Reemplaza los cinco campos: un campo de texto ausente queda sin
/// valor, y <paramref name="Publicada"/> y <paramref name="Tema"/> son obligatorios porque omitirlos
/// despublicaría o re-temaría la landing sin que nadie lo haya pedido. El logo viaja aparte.
/// </summary>
public record SolicitudDeVitrina(
    bool? Publicada, string? Tagline, string? Descripcion, string? ColorAcento, string? Tema);

/// <summary>
/// Lo que ve el admin de la vitrina de una empresa. <paramref name="Existe"/> es <c>false</c> cuando
/// todavía no se guardó nada: el resto es la forma por defecto, sin publicar.
/// </summary>
public record VitrinaAdmin(
    bool Existe,
    int IdEmpresa,
    bool Publicada,
    string? Tagline,
    string? Descripcion,
    string? ColorAcento,
    string Tema,
    bool TieneLogo,
    long? LogoVersion);
