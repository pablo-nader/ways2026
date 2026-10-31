using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Logging;
using Ways.Application.MercadoPago;
using Ways.Domain.Common;

namespace Ways.Infrastructure.MercadoPago;

/// <summary>
/// Base de las llamadas a la API de Mercado Pago como el vendedor de la empresa. El token sale de
/// <see cref="IProveedorDeTokenMercadoPago"/> en cada llamada y nunca se guarda: ni él ni el cuerpo de una respuesta salen en un log o
/// una excepción (el mensaje de error de Mercado Pago se sanea y se acota antes de viajar en <see cref="ErrorDeApiMercadoPago"/>).
/// Los cuerpos viajan en snake_case y sin propiedades nulas, que es lo que espera Mercado Pago.
/// </summary>
public sealed class ClienteApiMercadoPago(
    HttpClient http, IProveedorDeTokenMercadoPago tokens, ILogger<ClienteApiMercadoPago> log) : IClienteApiMercadoPago
{
    public const string EncabezadoDeIdempotencia = "X-Idempotency-Key";

    private const int LargoMaximoDelDetalle = 200;

    public static readonly JsonSerializerOptions OpcionesJson = new(JsonSerializerDefaults.Web)
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        DictionaryKeyPolicy = JsonNamingPolicy.SnakeCaseLower,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        NumberHandling = JsonNumberHandling.AllowReadingFromString
    };

    public async Task<TRespuesta> EnviarAsync<TRespuesta>(
        int idEmpresa, HttpMethod metodo, string ruta, object? cuerpo = null, string? claveIdempotencia = null,
        CancellationToken ct = default)
    {
        var token = await tokens.ObtenerAccessTokenAsync(idEmpresa, ct);

        using var pedido = new HttpRequestMessage(metodo, ruta.TrimStart('/'));
        pedido.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token.AccessToken);
        if (!string.IsNullOrWhiteSpace(claveIdempotencia))
        {
            pedido.Headers.TryAddWithoutValidation(EncabezadoDeIdempotencia, claveIdempotencia);
        }

        if (cuerpo is not null)
        {
            pedido.Content = new StringContent(
                JsonSerializer.Serialize(cuerpo, cuerpo.GetType(), OpcionesJson), Encoding.UTF8, "application/json");
        }

        var rutaSinConsulta = ruta.Split('?')[0];
        HttpResponseMessage respuesta;
        try
        {
            respuesta = await http.SendAsync(pedido, ct);
        }
        catch (HttpRequestException)
        {
            log.LogWarning("Mercado Pago no respondió a {Metodo} {Ruta}.", metodo.Method, rutaSinConsulta);
            throw NoDisponible();
        }
        catch (TaskCanceledException) when (!ct.IsCancellationRequested)
        {
            log.LogWarning("Mercado Pago agotó el tiempo en {Metodo} {Ruta}.", metodo.Method, rutaSinConsulta);
            throw NoDisponible();
        }

        using (respuesta)
        {
            var estado = (int)respuesta.StatusCode;

            if (respuesta.IsSuccessStatusCode)
            {
                return await LeerAsync<TRespuesta>(respuesta, metodo, rutaSinConsulta, ct);
            }

            log.LogWarning("Mercado Pago respondió {Estado} a {Metodo} {Ruta}.", estado, metodo.Method, rutaSinConsulta);

            if (respuesta.StatusCode == HttpStatusCode.Unauthorized)
            {
                // El token se creía vigente: lo revocó el vendedor. La marca va guardada por la versión del token usado.
                await tokens.MarcarTokenRechazadoAsync(token, CancellationToken.None);
                throw new ErrorDominio(
                    CodigosDeErrorDeMercadoPago.RequiereReconexion,
                    "Mercado Pago rechazó las credenciales de esta empresa: hay que reconectarla.",
                    409);
            }

            if (estado is >= 400 and < 500 and not 408 and not 429)
            {
                var (codigo, mensaje) = await LeerErrorAsync(respuesta, token.AccessToken, ct);
                throw new ErrorDeApiMercadoPago(estado, codigo, mensaje);
            }

            throw NoDisponible();
        }
    }

    public async Task<ListadoDeTerminalesMercadoPago> ListarTerminalesAsync(
        int idEmpresa, long? idStore = null, long? idPos = null, CancellationToken ct = default)
    {
        var consulta = new List<string>();
        if (idStore is { } store)
        {
            consulta.Add($"store_id={store.ToString(CultureInfo.InvariantCulture)}");
        }

        if (idPos is { } pos)
        {
            consulta.Add($"pos_id={pos.ToString(CultureInfo.InvariantCulture)}");
        }

        var ruta = consulta.Count == 0 ? "terminals/v1/list" : $"terminals/v1/list?{string.Join('&', consulta)}";
        var respuesta = await EnviarAsync<RespuestaDeTerminales>(idEmpresa, HttpMethod.Get, ruta, ct: ct);

        var terminales = (respuesta.Data?.Terminals ?? [])
            .Where(t => !string.IsNullOrWhiteSpace(t.Id))
            .Select(t => new TerminalMercadoPago(t.Id!, t.PosId, t.StoreId, t.ExternalPosId, t.OperatingMode))
            .ToList();

        return new ListadoDeTerminalesMercadoPago(
            terminales, respuesta.Paging?.Total ?? terminales.Count, respuesta.Paging?.Offset ?? 0, respuesta.Paging?.Limit ?? 0);
    }

    private async Task<TRespuesta> LeerAsync<TRespuesta>(
        HttpResponseMessage respuesta, HttpMethod metodo, string ruta, CancellationToken ct)
    {
        try
        {
            var texto = await respuesta.Content.ReadAsStringAsync(ct);
            var resultado = JsonSerializer.Deserialize<TRespuesta>(texto, OpcionesJson);
            if (resultado is not null)
            {
                return resultado;
            }
        }
        catch (Exception error) when (error is JsonException or NotSupportedException)
        {
            // Se trata igual que un cuerpo vacío o nulo, sin volcar el contenido.
        }
        catch (TaskCanceledException) when (!ct.IsCancellationRequested)
        {
            log.LogWarning("Mercado Pago agotó el tiempo leyendo la respuesta de {Metodo} {Ruta}.", metodo.Method, ruta);
            throw NoDisponible();
        }
        catch (HttpRequestException)
        {
            log.LogWarning("Se cortó la respuesta de Mercado Pago a {Metodo} {Ruta}.", metodo.Method, ruta);
            throw NoDisponible();
        }

        log.LogWarning("Mercado Pago devolvió un 2xx con un cuerpo inesperado a {Metodo} {Ruta}.", metodo.Method, ruta);
        throw new ErrorDominio(
            CodigosDeErrorDeMercadoPago.RespuestaInvalida, "Mercado Pago respondió con un formato inesperado.", 502);
    }

    /// <summary>Código y mensaje del error de Mercado Pago, tolerando sus tres formas (<c>error</c>/<c>message</c>, <c>errors[]</c> y
    /// <c>cause[]</c>). Cualquier cuerpo que no se entienda deja ambos en <c>null</c>.</summary>
    private static async Task<(string? Codigo, string? Mensaje)> LeerErrorAsync(
        HttpResponseMessage respuesta, string token, CancellationToken ct)
    {
        try
        {
            using var json = JsonDocument.Parse(await respuesta.Content.ReadAsStringAsync(ct));
            var raiz = json.RootElement;
            if (raiz.ValueKind != JsonValueKind.Object)
            {
                return (null, null);
            }

            var codigo = Texto(raiz, "error") ?? Texto(raiz, "code");
            var mensaje = Texto(raiz, "message");

            foreach (var lista in new[] { "errors", "cause" })
            {
                if (raiz.TryGetProperty(lista, out var items) && items is { ValueKind: JsonValueKind.Array }
                    && items.EnumerateArray().FirstOrDefault() is { ValueKind: JsonValueKind.Object } primero)
                {
                    codigo ??= Texto(primero, "code");
                    mensaje ??= Texto(primero, "detail") ?? Texto(primero, "description") ?? Texto(primero, "message");
                }
            }

            return (Sanear(codigo, token), Sanear(mensaje, token));
        }
        catch (JsonException)
        {
            return (null, null);
        }
    }

    private static string? Texto(JsonElement objeto, string propiedad) =>
        objeto.TryGetProperty(propiedad, out var valor) && valor.ValueKind is JsonValueKind.String or JsonValueKind.Number
            ? valor.ToString()
            : null;

    private static string? Sanear(string? texto, string token)
    {
        if (string.IsNullOrWhiteSpace(texto))
        {
            return null;
        }

        var limpio = new string(texto.Where(c => !char.IsControl(c)).ToArray()).Replace(token, "***", StringComparison.Ordinal).Trim();
        return limpio.Length > LargoMaximoDelDetalle ? limpio[..LargoMaximoDelDetalle] : limpio;
    }

    private static ErrorDominio NoDisponible() =>
        new(CodigosDeErrorDeMercadoPago.NoDisponible, "Mercado Pago no está disponible en este momento. Probá de nuevo más tarde.", 503);

    private sealed class RespuestaDeTerminales
    {
        public DatosDeTerminales? Data { get; init; }

        public PaginacionDeTerminales? Paging { get; init; }
    }

    private sealed class DatosDeTerminales
    {
        public List<TerminalCruda>? Terminals { get; init; }
    }

    private sealed class PaginacionDeTerminales
    {
        public int Total { get; init; }

        public int Offset { get; init; }

        public int Limit { get; init; }
    }

    private sealed class TerminalCruda
    {
        public string? Id { get; init; }

        public long? PosId { get; init; }

        public long? StoreId { get; init; }

        public string? ExternalPosId { get; init; }

        public string? OperatingMode { get; init; }
    }
}
