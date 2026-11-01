namespace Ways.Application.MercadoPago;

public sealed record SolicitudDeInicioDeConexion(int IdCuentaTesoreria);

public sealed record InicioDeConexion(string UrlAutorizacion);

/// <summary>Lo que el navegador recibió de Mercado Pago en el fragmento de la URL de retorno; ambos campos son obligatorios.</summary>
public sealed record SolicitudDeCompletarConexion(string Code, string State);

/// <summary>Estado de la conexión de una empresa. Nunca lleva tokens.</summary>
public sealed record EstadoDeConexionMercadoPago(
    bool Conectada,
    string? Estado,
    int? IdCuentaTesoreria,
    string? NombreCuenta,
    long? IdUsuarioMercadoPago,
    DateTimeOffset? FechaConexion,
    DateTimeOffset? AccessTokenExpiraEn);

/// <summary>Lo que viaja en el <c>state</c> de OAuth, protegido y con vencimiento.</summary>
public sealed record EstadoOAuth(
    int IdTenant,
    int IdEmpresa,
    int IdCuentaTesoreria,
    int IdUsuario,
    string CodeVerifier,
    string Origen);

public enum ResultadoDeConexion
{
    Conectado,
    Cancelado,
    Error,
    CuentaEnUso,
    YaConectada
}

/// <summary>Respuesta del endpoint de tokens de Mercado Pago. Los tokens no salen en <see cref="ToString"/>.</summary>
public sealed record TokensDeMercadoPago(string AccessToken, string RefreshToken, int ExpiraEnSegundos, long IdUsuario)
{
    public override string ToString() => $"TokensDeMercadoPago {{ IdUsuario = {IdUsuario}, tokens = [redactados] }}";
}

/// <summary>Tokens cifrados listos para persistir.</summary>
public sealed record TokensCifrados(
    byte[] AccessTokenCifrado,
    byte[] AccessTokenNonce,
    byte[] AccessTokenTag,
    byte[] RefreshTokenCifrado,
    byte[] RefreshTokenNonce,
    byte[] RefreshTokenTag,
    string IdClaveMaestra);
