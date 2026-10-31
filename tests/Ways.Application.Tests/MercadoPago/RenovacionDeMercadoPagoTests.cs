using System.Net;
using System.Text;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Ways.Application.MercadoPago;
using Ways.Domain.Common;
using Ways.Infrastructure.MercadoPago;

namespace Ways.Application.Tests.MercadoPago;

public class RenovacionDeMercadoPagoTests
{
    private static readonly DateTimeOffset Ahora = new(2026, 11, 1, 12, 0, 0, TimeSpan.Zero);

    // ---- Configuración ----------------------------------------------------------------------------------------------

    [Fact]
    public void SinConfigurarLaVentanaEsDeTreintaDiasElIntervaloDeSeisHorasYLaRenovacionEstaPrendida()
    {
        var opciones = new OpcionesDeMercadoPago();

        Assert.Equal(TimeSpan.FromDays(30), opciones.VentanaDeRenovacion);
        Assert.Equal(TimeSpan.FromHours(6), opciones.IntervaloDeRenovacion);
        Assert.True(opciones.RenovacionAutomatica);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-5)]
    public void UnValorNoPositivoVuelveAlPorDefecto(int valor)
    {
        var opciones = new OpcionesDeMercadoPago { DiasDeAnticipacionRenovacion = valor, IntervaloRenovacionHoras = valor };

        Assert.Equal(TimeSpan.FromDays(30), opciones.VentanaDeRenovacion);
        Assert.Equal(TimeSpan.FromHours(6), opciones.IntervaloDeRenovacion);
    }

    [Fact]
    public void LosValoresConfiguradosSeRespetan()
    {
        var opciones = new OpcionesDeMercadoPago { DiasDeAnticipacionRenovacion = 7, IntervaloRenovacionHoras = 2 };

        Assert.Equal(TimeSpan.FromDays(7), opciones.VentanaDeRenovacion);
        Assert.Equal(TimeSpan.FromHours(2), opciones.IntervaloDeRenovacion);
    }

    // ---- Ventana ----------------------------------------------------------------------------------------------------

    [Fact]
    public void ElUmbralEsAhoraMasLaVentana()
    {
        Assert.Equal(Ahora.AddDays(30), ServicioDeRenovacionDeConexionesMercadoPago.UmbralDeRenovacion(Ahora, TimeSpan.FromDays(30)));
    }

    [Theory]
    [InlineData(-1, true)]
    [InlineData(0, true)]
    [InlineData(29, true)]
    [InlineData(30, true)]
    public void UnTokenQueVenceDentroDeLaVentanaOYaVencioLeToca(int diasHastaElVencimiento, bool esperado)
    {
        var umbral = ServicioDeRenovacionDeConexionesMercadoPago.UmbralDeRenovacion(Ahora, TimeSpan.FromDays(30));

        Assert.Equal(esperado, ServicioDeRenovacionDeConexionesMercadoPago.LeToca(Ahora.AddDays(diasHastaElVencimiento), umbral));
    }

    [Fact]
    public void UnTokenQueVenceUnTickDespuesDelUmbralNoLeToca()
    {
        var umbral = ServicioDeRenovacionDeConexionesMercadoPago.UmbralDeRenovacion(Ahora, TimeSpan.FromDays(30));

        Assert.True(ServicioDeRenovacionDeConexionesMercadoPago.LeToca(umbral, umbral));
        Assert.False(ServicioDeRenovacionDeConexionesMercadoPago.LeToca(umbral.AddTicks(1), umbral));
    }

    // ---- Clasificación del rechazo del cliente ----------------------------------------------------------------------

    private sealed class ManejadorFalso(Func<HttpResponseMessage> responder) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage pedido, CancellationToken ct) =>
            Task.FromResult(responder());
    }

    private static ClienteOAuthMercadoPago Cliente(Func<HttpResponseMessage> responder) =>
        new(
            new HttpClient(new ManejadorFalso(responder)) { BaseAddress = new Uri("https://api.mp.test/") },
            Options.Create(new OpcionesDeMercadoPago { ClientId = "1", ClientSecret = "s", RedirectUri = "https://ways.test/cb" }),
            NullLogger<ClienteOAuthMercadoPago>.Instance);

    private static HttpResponseMessage Json(HttpStatusCode estado, string json) =>
        new(estado) { Content = new StringContent(json, Encoding.UTF8, "application/json") };

    [Theory]
    [InlineData(HttpStatusCode.BadRequest)]
    [InlineData(HttpStatusCode.Unauthorized)]
    [InlineData(HttpStatusCode.Forbidden)]
    [InlineData(HttpStatusCode.NotFound)]
    public async Task UnRechazo4xxDeLaRenovacionTieneElCodigoDeRenovacionRechazada(HttpStatusCode estado)
    {
        var cliente = Cliente(() => Json(estado, """{"error":"invalid_grant"}"""));

        var error = await Assert.ThrowsAsync<ErrorDominio>(() => cliente.RenovarAsync("TG-refresh", CancellationToken.None));

        Assert.Equal(CodigosDeErrorDeMercadoPago.RenovacionRechazada, error.Codigo);
    }

    [Theory]
    [InlineData(HttpStatusCode.InternalServerError)]
    [InlineData(HttpStatusCode.BadGateway)]
    [InlineData(HttpStatusCode.ServiceUnavailable)]
    [InlineData(HttpStatusCode.RequestTimeout)]
    [InlineData(HttpStatusCode.TooManyRequests)]
    public async Task UnaFallaTransitoriaDeLaRenovacionNoEsUnRechazo(HttpStatusCode estado)
    {
        var cliente = Cliente(() => Json(estado, "{}"));

        var error = await Assert.ThrowsAsync<ErrorDominio>(() => cliente.RenovarAsync("TG-refresh", CancellationToken.None));

        Assert.Equal("mercadopago_intercambio_fallido", error.Codigo);
    }

    [Fact]
    public async Task UnaFallaDeRedEnLaRenovacionNoEsUnRechazo()
    {
        var cliente = Cliente(() => throw new HttpRequestException("sin red"));

        var error = await Assert.ThrowsAsync<ErrorDominio>(() => cliente.RenovarAsync("TG-refresh", CancellationToken.None));

        Assert.Equal("mercadopago_intercambio_fallido", error.Codigo);
    }

    [Fact]
    public async Task ElCanjeDelCodigoNoDistingueElRechazo()
    {
        var cliente = Cliente(() => Json(HttpStatusCode.BadRequest, "{}"));

        var error = await Assert.ThrowsAsync<ErrorDominio>(
            () => cliente.CanjearCodigoAsync("TG-codigo", "verificador", CancellationToken.None));

        Assert.Equal("mercadopago_intercambio_fallido", error.Codigo);
    }
}
