namespace Ways.Application.MercadoPago;

/// <summary>Cómo está un punto de venta frente a Mercado Pago, para la cuenta de Mercado Pago actualmente conectada a su empresa.</summary>
public static class EstadosDeCajaMercadoPago
{
    /// <summary>La empresa no tiene una conexión activa con Mercado Pago.</summary>
    public const string SinConexion = "sin_conexion";

    /// <summary>La sucursal del punto de venta todavía no se registró como local en la cuenta conectada: hay que hacerlo antes.</summary>
    public const string LocalNoRegistrado = "local_no_registrado";

    /// <summary>La sucursal ya es un local, pero el punto de venta todavía no se registró como caja.</summary>
    public const string NoRegistrada = "no_registrada";

    public const string Registrada = "registrada";

    /// <summary>Registrada, pero el punto de venta se editó o cambió de sucursal después de la última sincronización, o el local de su sucursal se volvió a registrar con otro id de Mercado Pago.</summary>
    public const string Desactualizada = "desactualizada";
}

/// <summary>
/// Estado de la caja de Mercado Pago de un punto de venta. <see cref="IdStoreMercadoPago"/> es el local del que cuelga hoy la caja en
/// Mercado Pago (el de la sucursal que tenía el punto de venta en la última sincronización). Nunca lleva tokens.
/// </summary>
public sealed record EstadoDeCajaMercadoPago(
    string Estado,
    long? IdPosMercadoPago,
    long? IdStoreMercadoPago,
    string? ExternalId,
    string? UrlQr,
    DateTimeOffset? FechaRegistro,
    DateTimeOffset? FechaSincronizacion);

/// <summary>
/// Cuerpo de <c>POST /v2/pos</c> y <c>PATCH /v2/pos/{id}</c> de Mercado Pago. Los nombres salen en snake_case. <c>store_id</c> viaja
/// como cadena de dígitos, como lo documenta la referencia. No lleva <c>fixed_amount</c>: la API no lo admite; el importe lo fija cada orden.
/// </summary>
public sealed record SolicitudDeCajaMercadoPago(string Name, string ExternalId, string StoreId, ConfiguracionDeCajaMercadoPago Config);

/// <summary><c>config</c> de la caja: solo el QR.</summary>
public sealed record ConfiguracionDeCajaMercadoPago(ConfiguracionDeQrMercadoPago Qr)
{
    /// <summary>QR dinámico por orden (<c>pdv</c>), el modo que usa el cobro con QR de Ways.</summary>
    public static ConfiguracionDeCajaMercadoPago ModoPdv { get; } = new(new ConfiguracionDeQrMercadoPago(ModoOperativoPdv));

    public const string ModoOperativoPdv = "pdv";
}

public sealed record ConfiguracionDeQrMercadoPago(string OperatingMode);

/// <summary>El QR de una caja tal como lo devuelve Mercado Pago (<c>qr_response</c>); solo se guarda la imagen.</summary>
public sealed record QrDeCajaMercadoPago(string? Image);

/// <summary>
/// Una caja de Mercado Pago tal como la devuelven el alta, la actualización y la búsqueda; los campos que Ways no usa se ignoran. El QR
/// viene en <c>qr_response.image</c>.
/// </summary>
public sealed record CajaDeMercadoPago(long Id, string? ExternalId, long? StoreId, QrDeCajaMercadoPago? QrResponse)
{
    public string? ImagenDelQr => string.IsNullOrWhiteSpace(QrResponse?.Image) ? null : QrResponse.Image.Trim();
}

/// <summary><c>GET /v2/pos?external_id=</c>: <c>{paging, data:[...]}</c>.</summary>
public sealed record BusquedaDeCajasMercadoPago(IReadOnlyList<CajaDeMercadoPago>? Data);
