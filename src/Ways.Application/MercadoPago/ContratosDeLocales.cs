namespace Ways.Application.MercadoPago;

/// <summary>Cómo está una sucursal frente a Mercado Pago, para la cuenta de Mercado Pago actualmente conectada a su empresa.</summary>
public static class EstadosDeLocalMercadoPago
{
    /// <summary>La empresa no tiene una conexión activa con Mercado Pago.</summary>
    public const string SinConexion = "sin_conexion";

    /// <summary>La sucursal todavía no se registró como local en la cuenta conectada.</summary>
    public const string NoRegistrada = "no_registrada";

    public const string Registrada = "registrada";

    /// <summary>Registrada, pero la sucursal se editó después de la última sincronización.</summary>
    public const string Desactualizada = "desactualizada";
}

/// <summary>
/// Estado del local de Mercado Pago de una sucursal. <see cref="CamposFaltantes"/> son los datos que la sucursal todavía no tiene
/// para registrarse (vacío si está completa). Nunca lleva tokens.
/// </summary>
public sealed record EstadoDeLocalMercadoPago(
    string Estado,
    long? IdStoreMercadoPago,
    string? ExternalId,
    DateTimeOffset? FechaRegistro,
    DateTimeOffset? FechaSincronizacion,
    IReadOnlyList<string> CamposFaltantes);

/// <summary>
/// Ubicación de un local en el cuerpo que espera Mercado Pago (<c>POST|PUT /users/{user_id}/stores</c>). Los nombres salen en
/// snake_case. Forma tomada de la referencia de la API y todavía no ejercitada contra la API real.
/// </summary>
public sealed record UbicacionDeLocalMercadoPago(
    string StreetNumber,
    string StreetName,
    string CityName,
    string StateName,
    decimal Latitude,
    decimal Longitude,
    string? Reference);

public sealed record SolicitudDeLocalMercadoPago(string Name, string ExternalId, UbicacionDeLocalMercadoPago Location);

/// <summary>Un local de Mercado Pago tal como lo devuelven el alta y la búsqueda; los campos que Ways no usa se ignoran.</summary>
public sealed record LocalDeMercadoPago(long Id, string? ExternalId);

/// <summary><c>GET /users/{user_id}/stores/search</c>: <c>{paging, results:[...]}</c>.</summary>
public sealed record BusquedaDeLocalesMercadoPago(IReadOnlyList<LocalDeMercadoPago>? Results);
