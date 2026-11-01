using Ways.Domain.Common;

namespace Ways.Application.MercadoPago;

/// <summary>El token del vendedor listo para usar. <see cref="ExpiraEn"/> identifica la versión del token: una renovación la cambia.</summary>
public sealed record TokenDelVendedorMercadoPago(int IdConexion, long IdUsuarioMercadoPago, string AccessToken, DateTimeOffset ExpiraEn)
{
    public override string ToString() => $"TokenDelVendedorMercadoPago {{ IdConexion = {IdConexion}, IdUsuarioMercadoPago = {IdUsuarioMercadoPago} }}";
}

public interface IProveedorDeTokenMercadoPago
{
    /// <summary>
    /// Devuelve un access token vigente de la conexión activa de la empresa, renovándolo antes si le queda menos que el margen. Corre
    /// con el contexto del tenant actual. Nunca devuelve un token vencido ni lo guarda entre llamadas.
    /// Lanza <see cref="ErrorDominio"/> con <see cref="CodigosDeErrorDeMercadoPago.NoConectada"/>,
    /// <see cref="CodigosDeErrorDeMercadoPago.RequiereReconexion"/> o <see cref="CodigosDeErrorDeMercadoPago.NoDisponible"/>.
    /// </summary>
    Task<TokenDelVendedorMercadoPago> ObtenerAccessTokenAsync(int idEmpresa, CancellationToken ct = default);

    /// <summary>Mercado Pago respondió 401 a este token: pasa la conexión a <c>requiere_reconexion</c> y borra su material, siempre que
    /// siga siendo la misma versión del token (una renovación concurrente la deja intacta). Devuelve si cambió una fila.</summary>
    Task<bool> MarcarTokenRechazadoAsync(TokenDelVendedorMercadoPago token, CancellationToken ct = default);
}

public sealed record TerminalMercadoPago(string Id, long? IdPos, long? IdStore, string? IdPosExterno, string? ModoOperativo);

public sealed record ListadoDeTerminalesMercadoPago(IReadOnlyList<TerminalMercadoPago> Terminales, int Total, int Offset, int Limite);

/// <summary>Base de las llamadas a la API de Mercado Pago como el vendedor de la empresa.</summary>
public interface IClienteApiMercadoPago
{
    /// <summary>
    /// Envía el pedido con el token del vendedor. <paramref name="cuerpo"/> se serializa en JSON con snake_case y sin nulos.
    /// Un 2xx se deserializa a <typeparamref name="TRespuesta"/>; un cuerpo que no lo es lanza
    /// <see cref="CodigosDeErrorDeMercadoPago.RespuestaInvalida"/>. Un 401 pasa la conexión a <c>requiere_reconexion</c>; otro 4xx lanza
    /// <see cref="ErrorDeApiMercadoPago"/>; 408, 429, 5xx, tiempo agotado y red lanzan <see cref="CodigosDeErrorDeMercadoPago.NoDisponible"/>.
    /// </summary>
    Task<TRespuesta> EnviarAsync<TRespuesta>(
        int idEmpresa, HttpMethod metodo, string ruta, object? cuerpo = null, string? claveIdempotencia = null,
        CancellationToken ct = default);

    /// <summary><c>GET /terminals/v1/list</c>: las terminales Point del vendedor, filtradas opcionalmente por store y por POS.</summary>
    Task<ListadoDeTerminalesMercadoPago> ListarTerminalesAsync(
        int idEmpresa, long? idStore = null, long? idPos = null, CancellationToken ct = default);
}

/// <summary>
/// Mercado Pago rechazó el pedido con un 4xx que no es 401, 408 ni 429. Lleva el estado, el código y el mensaje de Mercado Pago ya
/// saneados (sin tokens, sin caracteres de control, acotados); el mensaje de la excepción es genérico.
/// </summary>
public sealed class ErrorDeApiMercadoPago(int estadoMercadoPago, string? codigoMercadoPago, string? mensajeMercadoPago)
    : ErrorDominio(CodigosDeErrorDeMercadoPago.SolicitudRechazada, "Mercado Pago rechazó la solicitud.", 502)
{
    public int EstadoMercadoPago { get; } = estadoMercadoPago;

    public string? CodigoMercadoPago { get; } = codigoMercadoPago;

    public string? MensajeMercadoPago { get; } = mensajeMercadoPago;
}
