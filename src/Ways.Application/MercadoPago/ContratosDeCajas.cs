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

    /// <summary>Registrada, pero el punto de venta se editó o cambió de sucursal después de la última sincronización.</summary>
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
/// Cuerpo de <c>POST /pos</c> y <c>PUT /pos/{id}</c> de Mercado Pago. Los nombres salen en snake_case. Forma tomada de la referencia
/// de la API y todavía no ejercitada contra la API real. <c>fixed_amount</c> es siempre <c>false</c>: el importe lo fija cada orden.
/// </summary>
public sealed record SolicitudDeCajaMercadoPago(string Name, string ExternalId, long StoreId, bool FixedAmount);

/// <summary>El QR de una caja tal como lo devuelve Mercado Pago; solo se guarda la imagen.</summary>
public sealed record QrDeCajaMercadoPago(string? Image);

/// <summary>
/// Una caja de Mercado Pago tal como la devuelven el alta, la actualización y la búsqueda; los campos que Ways no usa se ignoran. El QR
/// puede venir como <c>qr</c> o como <c>qr_response</c> (la referencia usa los dos nombres): se acepta cualquiera.
/// </summary>
public sealed record CajaDeMercadoPago(long Id, string? ExternalId, long? StoreId, QrDeCajaMercadoPago? Qr, QrDeCajaMercadoPago? QrResponse)
{
    public string? ImagenDelQr => Normalizar(Qr?.Image) ?? Normalizar(QrResponse?.Image);

    private static string? Normalizar(string? imagen) => string.IsNullOrWhiteSpace(imagen) ? null : imagen.Trim();
}

/// <summary><c>GET /pos?external_id=</c>: <c>{paging, results:[...]}</c>.</summary>
public sealed record BusquedaDeCajasMercadoPago(IReadOnlyList<CajaDeMercadoPago>? Results);
