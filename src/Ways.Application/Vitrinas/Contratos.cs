namespace Ways.Application.Vitrinas;

/// <summary>
/// Datos de la vitrina de una empresa. Reemplaza los cinco campos: un campo de texto ausente queda sin
/// valor, y <paramref name="Publicada"/> y <paramref name="Tema"/> son obligatorios porque omitirlos
/// despublicaría o re-temaría la landing sin que nadie lo haya pedido. El logo viaja aparte.
/// <paramref name="IdListaPrecio"/> es la lista de precios del catálogo de la tienda: ausente (o <c>null</c>)
/// es "la lista default del tenant", así que reemplaza el valor guardado como los demás campos. Una lista
/// que falta, está inactiva, está dada de baja, es de otro tenant o es de otra empresa es 400
/// <c>lista_invalida</c>.
/// </summary>
public record SolicitudDeVitrina(
    bool? Publicada, string? Tagline, string? Descripcion, string? ColorAcento, string? Tema,
    int? IdListaPrecio = null);

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
    long? LogoVersion,
    int? IdListaPrecio = null);
