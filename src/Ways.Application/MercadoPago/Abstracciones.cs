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

    /// <summary>La empresa no tiene una conexión activa con Mercado Pago ni una que haya quedado pidiendo reconexión.</summary>
    public const string NoConectada = "mercadopago_no_conectada";

    /// <summary>La última conexión de la empresa quedó en <c>requiere_reconexion</c>, o Mercado Pago respondió 401 a su token.</summary>
    public const string RequiereReconexion = "mercadopago_requiere_reconexion";

    /// <summary>Mercado Pago no pudo atender el pedido ahora (408, 429, 5xx, tiempo agotado o red) o no hay un token vigente
    /// para usar: reintentar más tarde alcanza, la conexión no se toca.</summary>
    public const string NoDisponible = "mercadopago_no_disponible";

    /// <summary>Mercado Pago rechazó el pedido con un 4xx distinto de 401, 408 y 429. Lo lleva <see cref="ErrorDeApiMercadoPago"/>.</summary>
    public const string SolicitudRechazada = "mercadopago_solicitud_rechazada";

    /// <summary>Mercado Pago respondió 2xx con un cuerpo que no es el JSON esperado.</summary>
    public const string RespuestaInvalida = "mercadopago_respuesta_invalida";

    /// <summary>La sucursal del punto de venta todavía no es un local de la cuenta de Mercado Pago conectada: se registra primero.</summary>
    public const string LocalNoRegistrado = "mercadopago_local_no_registrado";

    /// <summary>El punto de venta todavía no es una caja de la cuenta de Mercado Pago conectada: se registra primero.</summary>
    public const string CajaNoRegistrada = "mercadopago_caja_no_registrada";

    /// <summary>La terminal no está vinculada a la caja del punto de venta (no figura en su listado, o Mercado Pago contesta que no
    /// tiene local y caja): se vincula en la app de Mercado Pago.</summary>
    public const string TerminalNoVinculada = "mercadopago_terminal_no_vinculada";

    /// <summary>Mercado Pago no permite cambiar el modo de ese modelo de terminal (solo Newland N950 y PAX A910).</summary>
    public const string TerminalModeloNoAdmitido = "mercadopago_terminal_modelo_no_admitido";

    /// <summary>La caja ya tiene otra terminal en modo <c>PDV</c> o suspendida: Mercado Pago admite una por caja.</summary>
    public const string CajaConOtraTerminalEnPdv = "mercadopago_caja_con_terminal_en_pdv";
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
