using Ways.Domain.MercadoPago;

namespace Ways.Application.MercadoPago;

public static class CodigosDeErrorDeMercadoPago
{
    /// <summary><see cref="IClienteOAuthMercadoPago.RenovarAsync"/> lo lanza cuando Mercado Pago responde 400 con
    /// <c>invalid_grant</c>: el refresh token de esa conexión no sirve y hay que reconectar. Cualquier otro fallo lleva otro código.</summary>
    public const string RenovacionRechazada = "mercadopago_renovacion_rechazada";

    /// <summary><see cref="IClienteOAuthMercadoPago.RenovarAsync"/> lo lanza ante cualquier otro 4xx (salvo 408 y 429):
    /// <c>invalid_client</c>, 401, 403 o 404 señalan las credenciales o la configuración de la plataforma, no a una conexión.
    /// Los 5xx, el tiempo agotado y la red llevan <c>mercadopago_intercambio_fallido</c> y son transitorios.</summary>
    public const string CredencialesRechazadas = "mercadopago_credenciales_rechazadas";
}

public interface IClienteOAuthMercadoPago
{
    /// <summary>Canjea el código de autorización (válido 10 minutos y de un solo uso).</summary>
    Task<TokensDeMercadoPago> CanjearCodigoAsync(string codigo, string codeVerifier, CancellationToken ct);

    /// <summary>Renueva con el refresh token. Cada renovación devuelve un refresh token NUEVO que reemplaza al anterior.</summary>
    Task<TokensDeMercadoPago> RenovarAsync(string refreshToken, CancellationToken ct);
}

public interface IProtectorDeEstadoOAuth
{
    string Proteger(EstadoOAuth estado);

    /// <summary><c>null</c> si el valor fue alterado, no se puede abrir o ya venció.</summary>
    EstadoOAuth? Desproteger(string valor);
}

public interface IAlmacenDeTokensMercadoPago
{
    /// <summary><c>false</c> si falta la clave maestra actual o su valor no sirve: no se puede cifrar material nuevo.</summary>
    bool PuedeCifrar { get; }

    /// <summary>El AAD de cada blob ata tenant, empresa, conexión y cuál de los dos tokens es.</summary>
    TokensCifrados Cifrar(int idTenant, int idEmpresa, int idConexion, string accessToken, string refreshToken);

    (string AccessToken, string RefreshToken) Descifrar(ConexionMercadoPago conexion);
}
