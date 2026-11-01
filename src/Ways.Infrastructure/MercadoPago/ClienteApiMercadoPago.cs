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

    /// <summary>Tope de bytes que se leen del cuerpo de un error de Mercado Pago: más allá no hay nada que interpretar.</summary>
    public const int LargoMaximoDelCuerpoDeError = 16 * 1024;

    /// <summary>Plazo para leer el cuerpo de una respuesta una vez recibidos los encabezados: <see cref="HttpClient.Timeout"/> solo
    /// cubre hasta ahí, porque el cuerpo se lee en streaming.</summary>
    public TimeSpan PlazoDeLecturaDelCuerpo { get; init; } = TimeSpan.FromSeconds(15);

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
        var rutaSinConsulta = ruta.Split('?')[0];
        var reintentado = false;

        while (true)
        {
            TokenDelVendedorMercadoPago? tokenParaReintentar = null;

            using (var pedido = ArmarPedido(token, metodo, ruta, cuerpo, claveIdempotencia))
            {
                HttpResponseMessage respuesta;
                try
                {
                    respuesta = await http.SendAsync(pedido, HttpCompletionOption.ResponseHeadersRead, ct);
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
                        tokenParaReintentar = await ResolverRechazoAsync(token, idEmpresa, reintentado, ct);
                    }
                    else if (estado is >= 400 and < 500 and not 408 and not 429)
                    {
                        var (codigo, mensaje) = await LeerErrorAsync(respuesta, token.AccessToken, metodo, rutaSinConsulta, ct);
                        throw new ErrorDeApiMercadoPago(estado, codigo, mensaje);
                    }
                    else
                    {
                        throw NoDisponible();
                    }
                }
            }

            token = tokenParaReintentar!;
            reintentado = true;
        }
    }

    private static HttpRequestMessage ArmarPedido(
        TokenDelVendedorMercadoPago token, HttpMethod metodo, string ruta, object? cuerpo, string? claveIdempotencia)
    {
        var pedido = new HttpRequestMessage(metodo, ruta.TrimStart('/'));
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

        return pedido;
    }

    /// <summary>
    /// Mercado Pago respondió 401. La marca va guardada por la versión del token usado: si cambió una fila, el token era el vigente y
    /// lo revocó el vendedor. Si no cambió ninguna, una renovación ya rotó el token entre la lectura y este 401 (o la conexión dejó de
    /// estar activa): se relee, y solo si sigue activa con un vencimiento distinto del usado se devuelve el token nuevo para reintentar
    /// el pedido, una única vez. Toda otra salida lanza <see cref="CodigosDeErrorDeMercadoPago.RequiereReconexion"/>.
    /// </summary>
    private async Task<TokenDelVendedorMercadoPago> ResolverRechazoAsync(
        TokenDelVendedorMercadoPago token, int idEmpresa, bool yaReintentado, CancellationToken ct)
    {
        var cambioLaFila = false;
        try
        {
            cambioLaFila = await tokens.MarcarTokenRechazadoAsync(token, CancellationToken.None);
        }
        catch (Exception error)
        {
            log.LogError(
                error, "No se pudo dejar la conexión {IdConexion} de Mercado Pago en requiere_reconexion tras un 401.",
                token.IdConexion);
            throw RequiereReconexion();
        }

        if (cambioLaFila || yaReintentado)
        {
            throw RequiereReconexion();
        }

        TokenDelVendedorMercadoPago renovado;
        try
        {
            renovado = await tokens.ObtenerAccessTokenAsync(idEmpresa, ct);
        }
        catch (ErrorDominio error) when (error.Codigo is CodigosDeErrorDeMercadoPago.RequiereReconexion or CodigosDeErrorDeMercadoPago.NoConectada)
        {
            throw RequiereReconexion();
        }

        if (renovado.IdConexion != token.IdConexion || renovado.ExpiraEn == token.ExpiraEn)
        {
            throw RequiereReconexion();
        }

        log.LogInformation(
            "El 401 de Mercado Pago correspondía a un token ya renovado de la conexión {IdConexion}: se reintenta una vez.",
            token.IdConexion);
        return renovado;
    }

    private static ErrorDominio RequiereReconexion() =>
        new(
            CodigosDeErrorDeMercadoPago.RequiereReconexion,
            "Mercado Pago rechazó las credenciales de esta empresa: hay que reconectarla.",
            409);

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
            using var limite = CancellationTokenSource.CreateLinkedTokenSource(ct);
            limite.CancelAfter(PlazoDeLecturaDelCuerpo);
            var texto = await respuesta.Content.ReadAsStringAsync(limite.Token);
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
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            log.LogWarning("Mercado Pago agotó el tiempo leyendo la respuesta de {Metodo} {Ruta}.", metodo.Method, ruta);
            throw NoDisponible();
        }
        catch (Exception error) when (error is HttpRequestException or IOException)
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
    private async Task<(string? Codigo, string? Mensaje)> LeerErrorAsync(
        HttpResponseMessage respuesta, string token, HttpMethod metodo, string ruta, CancellationToken ct)
    {
        try
        {
            using var limite = CancellationTokenSource.CreateLinkedTokenSource(ct);
            limite.CancelAfter(PlazoDeLecturaDelCuerpo);
            using var json = JsonDocument.Parse(await LeerCuerpoAcotadoAsync(respuesta.Content, limite.Token));
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
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            log.LogWarning("Mercado Pago agotó el tiempo leyendo el error de {Metodo} {Ruta}.", metodo.Method, ruta);
            throw NoDisponible();
        }
        catch (Exception error) when (error is HttpRequestException or IOException)
        {
            log.LogWarning("Se cortó el error de Mercado Pago a {Metodo} {Ruta}.", metodo.Method, ruta);
            throw NoDisponible();
        }
    }

    /// <summary>Lee a lo sumo <see cref="LargoMaximoDelCuerpoDeError"/> bytes: un cuerpo más largo queda truncado y se trata como ilegible.</summary>
    private static async Task<byte[]> LeerCuerpoAcotadoAsync(HttpContent contenido, CancellationToken ct)
    {
        await using var flujo = await contenido.ReadAsStreamAsync(ct);
        var buffer = new byte[LargoMaximoDelCuerpoDeError];
        var leidos = 0;
        while (leidos < buffer.Length)
        {
            var n = await flujo.ReadAsync(buffer.AsMemory(leidos), ct);
            if (n == 0)
            {
                break;
            }

            leidos += n;
        }

        return buffer[..leidos];
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
