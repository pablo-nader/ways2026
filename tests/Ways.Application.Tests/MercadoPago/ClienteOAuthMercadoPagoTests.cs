using System.Net;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Ways.Application.MercadoPago;
using Ways.Domain.Common;
using Ways.Infrastructure.MercadoPago;

namespace Ways.Application.Tests.MercadoPago;

public class ClienteOAuthMercadoPagoTests
{
    private const string ClientSecret = "secreto-de-la-aplicacion-que-no-debe-salir";

    private sealed class ManejadorFalso(Func<HttpRequestMessage, string, HttpResponseMessage> responder) : HttpMessageHandler
    {
        public List<(HttpRequestMessage Pedido, string Cuerpo)> Pedidos { get; } = [];

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage pedido, CancellationToken ct)
        {
            var cuerpo = pedido.Content is null ? string.Empty : await pedido.Content.ReadAsStringAsync(ct);
            Pedidos.Add((pedido, cuerpo));
            return responder(pedido, cuerpo);
        }
    }

    private sealed class RegistroDeLogs : ILogger<ClienteOAuthMercadoPago>
    {
        public List<string> Mensajes { get; } = [];

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(
            LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter) =>
            Mensajes.Add(formatter(state, exception) + exception);
    }

    private static HttpResponseMessage Json(HttpStatusCode estado, string json) =>
        new(estado) { Content = new StringContent(json, Encoding.UTF8, "application/json") };

    private static (ClienteOAuthMercadoPago Cliente, ManejadorFalso Manejador, RegistroDeLogs Logs) Crear(
        Func<HttpRequestMessage, string, HttpResponseMessage> responder)
    {
        var manejador = new ManejadorFalso(responder);
        var logs = new RegistroDeLogs();
        var opciones = Options.Create(new OpcionesDeMercadoPago
        {
            ClientId = "1234567890",
            ClientSecret = ClientSecret,
            RedirectUri = "https://ways.test/api/integraciones/mercadopago/callback"
        });
        var http = new HttpClient(manejador) { BaseAddress = new Uri("https://api.mp.test/") };
        return (new ClienteOAuthMercadoPago(http, opciones, logs), manejador, logs);
    }

    private const string RespuestaValida =
        """{"access_token":"APP_USR-acceso","refresh_token":"TG-renovacion","expires_in":15552000,"user_id":987654321,"token_type":"Bearer"}""";

    [Fact]
    public async Task CanjearCodigoEnviaElCuerpoJsonDeAuthorizationCodeYDevuelveLosTokens()
    {
        var (cliente, manejador, _) = Crear((_, _) => Json(HttpStatusCode.OK, RespuestaValida));

        var tokens = await cliente.CanjearCodigoAsync("TG-codigo", "el-verificador", CancellationToken.None);

        Assert.Equal(new TokensDeMercadoPago("APP_USR-acceso", "TG-renovacion", 15552000, 987654321), tokens);

        var (pedido, cuerpo) = Assert.Single(manejador.Pedidos);
        Assert.Equal(HttpMethod.Post, pedido.Method);
        Assert.Equal("https://api.mp.test/oauth/token", pedido.RequestUri!.ToString());
        Assert.Equal("application/json", pedido.Content!.Headers.ContentType!.MediaType);

        using var json = JsonDocument.Parse(cuerpo);
        var raiz = json.RootElement;
        Assert.Equal("authorization_code", raiz.GetProperty("grant_type").GetString());
        Assert.Equal("TG-codigo", raiz.GetProperty("code").GetString());
        Assert.Equal("el-verificador", raiz.GetProperty("code_verifier").GetString());
        Assert.Equal("1234567890", raiz.GetProperty("client_id").GetString());
        Assert.Equal(ClientSecret, raiz.GetProperty("client_secret").GetString());
        Assert.Equal("https://ways.test/api/integraciones/mercadopago/callback", raiz.GetProperty("redirect_uri").GetString());
        Assert.False(raiz.TryGetProperty("refresh_token", out _));
    }

    [Fact]
    public async Task RenovarEnviaElCuerpoJsonDeRefreshTokenSinCodigoNiVerificador()
    {
        var (cliente, manejador, _) = Crear((_, _) => Json(HttpStatusCode.OK, RespuestaValida));

        var tokens = await cliente.RenovarAsync("TG-renovacion-vieja", CancellationToken.None);

        Assert.Equal("TG-renovacion", tokens.RefreshToken);

        using var json = JsonDocument.Parse(Assert.Single(manejador.Pedidos).Cuerpo);
        var raiz = json.RootElement;
        Assert.Equal("refresh_token", raiz.GetProperty("grant_type").GetString());
        Assert.Equal("TG-renovacion-vieja", raiz.GetProperty("refresh_token").GetString());
        Assert.Equal("1234567890", raiz.GetProperty("client_id").GetString());
        Assert.Equal(ClientSecret, raiz.GetProperty("client_secret").GetString());
        Assert.False(raiz.TryGetProperty("code", out _));
        Assert.False(raiz.TryGetProperty("code_verifier", out _));
        Assert.False(raiz.TryGetProperty("redirect_uri", out _));
    }

    [Theory]
    [InlineData(HttpStatusCode.BadRequest)]
    [InlineData(HttpStatusCode.Unauthorized)]
    [InlineData(HttpStatusCode.InternalServerError)]
    public async Task UnaRespuestaNoExitosaEsUnErrorDeIntercambioQueNoDevuelveNiRegistraElCuerpo(HttpStatusCode estado)
    {
        var cuerpoSensible = $$"""{"message":"invalid_grant","detalle":"{{ClientSecret}} TG-codigo"}""";
        var (cliente, _, logs) = Crear((_, _) => Json(estado, cuerpoSensible));

        var error = await Assert.ThrowsAsync<ErrorDominio>(
            () => cliente.CanjearCodigoAsync("TG-codigo", "el-verificador", CancellationToken.None));

        Assert.Equal("mercadopago_intercambio_fallido", error.Codigo);
        Assert.Equal(502, error.EstadoHttp);
        Assert.DoesNotContain(ClientSecret, error.Message);
        Assert.NotEmpty(logs.Mensajes);
        Assert.All(logs.Mensajes, m =>
        {
            Assert.DoesNotContain(ClientSecret, m);
            Assert.DoesNotContain("TG-codigo", m);
            Assert.DoesNotContain("el-verificador", m);
        });
    }

    [Theory]
    [InlineData("""{"access_token":"a","refresh_token":"r","expires_in":100}""")]
    [InlineData("""{"access_token":"","refresh_token":"r","expires_in":100,"user_id":1}""")]
    [InlineData("""{"access_token":"a","refresh_token":"r","expires_in":0,"user_id":1}""")]
    [InlineData("""{"access_token":"a","expires_in":100,"user_id":1}""")]
    [InlineData("no es json")]
    public async Task UnaRespuestaIncompletaOQueNoEsJsonSeTrataComoIntercambioFallido(string cuerpo)
    {
        var (cliente, _, _) = Crear((_, _) => Json(HttpStatusCode.OK, cuerpo));

        var error = await Assert.ThrowsAsync<ErrorDominio>(
            () => cliente.CanjearCodigoAsync("TG-codigo", "el-verificador", CancellationToken.None));

        Assert.Equal("mercadopago_intercambio_fallido", error.Codigo);
    }

    [Theory]
    [InlineData("text/html", "<html>Bad Gateway</html>")]
    [InlineData("application/json; charset=charset-inexistente", "no es json")]
    [InlineData("application/json; charset=charset-inexistente", "")]
    public async Task UnaRespuestaExitosaQueNoSePuedeLeerComoJsonEsIntercambioFallidoYNoUnaExcepcionSinTipar(string tipo, string cuerpo)
    {
        var (cliente, _, logs) = Crear((_, _) =>
        {
            var contenido = new ByteArrayContent(Encoding.UTF8.GetBytes(cuerpo));
            contenido.Headers.TryAddWithoutValidation("Content-Type", tipo);
            return new HttpResponseMessage(HttpStatusCode.OK) { Content = contenido };
        });

        var error = await Assert.ThrowsAsync<ErrorDominio>(
            () => cliente.CanjearCodigoAsync("TG-codigo", "el-verificador", CancellationToken.None));
        var errorDeRenovacion = await Assert.ThrowsAsync<ErrorDominio>(
            () => cliente.RenovarAsync("TG-renovacion", CancellationToken.None));

        Assert.Equal("mercadopago_intercambio_fallido", error.Codigo);
        Assert.Equal("mercadopago_intercambio_fallido", errorDeRenovacion.Codigo);
        Assert.All(logs.Mensajes, m => Assert.DoesNotContain("APP_USR-acceso", m));
    }

    [Fact]
    public async Task UnaFallaDeRedSeTrataComoIntercambioFallido()
    {
        var (cliente, _, _) = Crear((_, _) => throw new HttpRequestException("sin red"));

        var error = await Assert.ThrowsAsync<ErrorDominio>(
            () => cliente.CanjearCodigoAsync("TG-codigo", "el-verificador", CancellationToken.None));

        Assert.Equal("mercadopago_intercambio_fallido", error.Codigo);
    }

    [Fact]
    public void LosTokensNoSalenEnElToString()
    {
        var texto = new TokensDeMercadoPago("APP_USR-acceso", "TG-renovacion", 10, 5).ToString();

        Assert.DoesNotContain("APP_USR-acceso", texto);
        Assert.DoesNotContain("TG-renovacion", texto);
    }

    [Fact]
    public void LaConfiguracionSoloEstaCompletaConClientIdSecretYRedirectUri()
    {
        Assert.False(new OpcionesDeMercadoPago().EstaConfigurado);
        Assert.False(new OpcionesDeMercadoPago { ClientId = "1", ClientSecret = "s", RedirectUri = " " }.EstaConfigurado);
        Assert.False(new OpcionesDeMercadoPago { ClientId = "1", RedirectUri = "https://x" }.EstaConfigurado);
        Assert.True(new OpcionesDeMercadoPago { ClientId = "1", ClientSecret = "s", RedirectUri = "https://x" }.EstaConfigurado);
    }

    [Fact]
    public void LosHostsPorDefectoSonLosDeMercadoPago()
    {
        var opciones = new OpcionesDeMercadoPago();

        Assert.Equal("https://auth.mercadopago.com/authorization", opciones.UrlAutorizacion);
        Assert.Equal("https://api.mercadopago.com", opciones.UrlApi);
    }
}
