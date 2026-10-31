using Ways.Domain.MercadoPago;

namespace Ways.Application.MercadoPago;

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
