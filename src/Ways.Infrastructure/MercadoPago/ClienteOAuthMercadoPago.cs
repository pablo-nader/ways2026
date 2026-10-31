using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Ways.Application.MercadoPago;
using Ways.Domain.Common;

namespace Ways.Infrastructure.MercadoPago;

/// <summary>
/// Cliente del endpoint de tokens de Mercado Pago (<c>POST /oauth/token</c>, cuerpo JSON). Ni el client secret ni los tokens
/// salen en un log, una excepción ni un mensaje: ante una respuesta que no es un 2xx válido solo queda registrado el código
/// de estado.
/// </summary>
public sealed class ClienteOAuthMercadoPago(
    HttpClient http, IOptions<OpcionesDeMercadoPago> opciones, ILogger<ClienteOAuthMercadoPago> log)
    : IClienteOAuthMercadoPago
{
    private const string RutaDeTokens = "oauth/token";

    public Task<TokensDeMercadoPago> CanjearCodigoAsync(string codigo, string codeVerifier, CancellationToken ct)
    {
        var config = opciones.Value;
        return PedirAsync(
            new SolicitudDeToken
            {
                ClientId = config.ClientId,
                ClientSecret = config.ClientSecret,
                GrantType = "authorization_code",
                Code = codigo,
                RedirectUri = config.RedirectUri,
                CodeVerifier = codeVerifier
            },
            ct);
    }

    public Task<TokensDeMercadoPago> RenovarAsync(string refreshToken, CancellationToken ct)
    {
        var config = opciones.Value;
        return PedirAsync(
            new SolicitudDeToken
            {
                ClientId = config.ClientId,
                ClientSecret = config.ClientSecret,
                GrantType = "refresh_token",
                RefreshToken = refreshToken
            },
            ct,
            distinguirRechazo: true);
    }

    private async Task<TokensDeMercadoPago> PedirAsync(SolicitudDeToken solicitud, CancellationToken ct, bool distinguirRechazo = false)
    {
        HttpResponseMessage respuesta;
        try
        {
            respuesta = await http.PostAsJsonAsync(RutaDeTokens, solicitud, ct);
        }
        catch (HttpRequestException)
        {
            log.LogWarning("Mercado Pago no respondió al pedir tokens ({GrantType}).", solicitud.GrantType);
            throw Fallido();
        }
        catch (TaskCanceledException) when (!ct.IsCancellationRequested)
        {
            log.LogWarning("Mercado Pago agotó el tiempo al pedir tokens ({GrantType}).", solicitud.GrantType);
            throw Fallido();
        }

        using (respuesta)
        {
            if (!respuesta.IsSuccessStatusCode)
            {
                log.LogWarning(
                    "Mercado Pago rechazó el pedido de tokens ({GrantType}) con el estado {Estado}.",
                    solicitud.GrantType, (int)respuesta.StatusCode);

                if (distinguirRechazo && respuesta.StatusCode is >= HttpStatusCode.BadRequest and < HttpStatusCode.InternalServerError
                    and not HttpStatusCode.RequestTimeout and not HttpStatusCode.TooManyRequests)
                {
                    // Solo un 400 invalid_grant dice que el refresh token de ESA conexión ya no sirve. Cualquier otro 4xx
                    // (invalid_client, 401, 403, 404) apunta a las credenciales o a la configuración de la plataforma y
                    // alcanzaría a todas las conexiones por igual.
                    throw respuesta.StatusCode == HttpStatusCode.BadRequest && await EsInvalidGrantAsync(respuesta, ct)
                        ? new ErrorDominio(CodigosDeErrorDeMercadoPago.RenovacionRechazada, "Mercado Pago rechazó la renovación del token.", 502)
                        : new ErrorDominio(
                            CodigosDeErrorDeMercadoPago.CredencialesRechazadas, "Mercado Pago rechazó las credenciales de la aplicación.", 502);
                }

                throw Fallido();
            }

            try
            {
                var cuerpo = await respuesta.Content.ReadFromJsonAsync<RespuestaDeToken>(ct);
                if (cuerpo is { AccessToken: { Length: > 0 } acceso, RefreshToken: { Length: > 0 } renovacion, ExpiresIn: > 0 and var expira, UserId: > 0 and var usuario })
                {
                    return new TokensDeMercadoPago(acceso, renovacion, expira, usuario);
                }
            }
            catch (JsonException)
            {
                // Se trata igual que una respuesta incompleta, sin volcar el cuerpo.
            }

            log.LogWarning("Mercado Pago devolvió una respuesta de tokens incompleta ({GrantType}).", solicitud.GrantType);
            throw Fallido();
        }
    }

    private static async Task<bool> EsInvalidGrantAsync(HttpResponseMessage respuesta, CancellationToken ct)
    {
        try
        {
            var cuerpo = await respuesta.Content.ReadAsStringAsync(ct);
            using var json = JsonDocument.Parse(cuerpo);
            return json.RootElement.ValueKind == JsonValueKind.Object
                && json.RootElement.TryGetProperty("error", out var error)
                && error.ValueKind == JsonValueKind.String
                && error.GetString() == "invalid_grant";
        }
        catch (JsonException)
        {
            return false;
        }
    }

    private static ErrorDominio Fallido() =>
        new("mercadopago_intercambio_fallido", "No se pudo completar el intercambio con Mercado Pago.", 502);

    private sealed class SolicitudDeToken
    {
        [JsonPropertyName("client_id")]
        public string? ClientId { get; init; }

        [JsonPropertyName("client_secret")]
        public string? ClientSecret { get; init; }

        [JsonPropertyName("grant_type")]
        public required string GrantType { get; init; }

        [JsonPropertyName("code")]
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public string? Code { get; init; }

        [JsonPropertyName("redirect_uri")]
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public string? RedirectUri { get; init; }

        [JsonPropertyName("code_verifier")]
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public string? CodeVerifier { get; init; }

        [JsonPropertyName("refresh_token")]
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public string? RefreshToken { get; init; }
    }

    private sealed class RespuestaDeToken
    {
        [JsonPropertyName("access_token")]
        public string? AccessToken { get; init; }

        [JsonPropertyName("refresh_token")]
        public string? RefreshToken { get; init; }

        [JsonPropertyName("expires_in")]
        public int ExpiresIn { get; init; }

        [JsonPropertyName("user_id")]
        public long UserId { get; init; }
    }
}
