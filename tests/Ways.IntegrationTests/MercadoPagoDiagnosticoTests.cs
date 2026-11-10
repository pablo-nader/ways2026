using System.Net;
using System.Text;
using System.Text.Json;
using Ways.Application.MercadoPago;
using static Ways.IntegrationTests.ApoyoDeMercadoPago;

namespace Ways.IntegrationTests;

/// <summary>
/// El diagnóstico de solo lectura de los pagos recibidos (<c>GET /api/empresas/{id}/mercadopago/diagnostico/pagos-recibidos</c>) contra
/// Postgres real y el manejador falso de Mercado Pago: solo Admin, 404 de la empresa ajena, 409 sin conexión, 400 de las horas, el
/// mapeo saneado y el fallo de los reportes que no esconde los pagos.
/// </summary>
[Collection("Ways.IntegrationTests secuencial")]
public class MercadoPagoDiagnosticoTests(WaysApiFixture fixture) : IClassFixture<WaysApiFixture>
{
    private static readonly DateTimeOffset Ahora = new(2026, 11, 9, 15, 0, 0, TimeSpan.Zero);

    private readonly ApoyoDeMercadoPago _apoyo = new(fixture);

    private const string Pagos =
        """
        {"paging":{"total":3,"limit":100,"offset":0},"results":[
          {"id":555001,"status":"approved","operation_type":"money_transfer","transaction_amount":900.5,
           "payer":{"first_name":"Ana","last_name":"Gómez","email":"ana.gomez@correo.test","identification":{"number":"28999111"}},
           "fee_details":[{"type":"mercadopago_fee","amount":10}]}
        ]}
        """;

    private const string Reportes =
        """[{"file_name":"settlement.csv","begin_date":"2026-11-01","end_date":"2026-11-02","date_created":"2026-11-02","status":"processed"}]""";

    private static HttpResponseMessage Json(HttpStatusCode estado, string json) =>
        new(estado) { Content = new StringContent(json, Encoding.UTF8, "application/json") };

    private static string Ruta(int idEmpresa, string consulta = "") =>
        $"/api/empresas/{idEmpresa}/mercadopago/diagnostico/pagos-recibidos{consulta}";

    private static bool EsDeTokens(PedidoRegistrado pedido) => pedido.Uri.AbsolutePath.EndsWith("/oauth/token", StringComparison.Ordinal);

    private static IReadOnlyList<PedidoRegistrado> LlamadasALaApi(Contexto ctx) => [.. ctx.MercadoPago.Pedidos.Where(p => !EsDeTokens(p))];

    private async Task<Contexto> ConectarAsync(string nombre, Func<PedidoRegistrado, HttpResponseMessage>? api = null)
    {
        var ctx = await _apoyo.PrepararAsync(nombre, reloj: new RelojControlable(Ahora));
        ctx.MercadoPago.Responder = _ => Exitoso(AccessToken, RefreshToken, ctx.MercadoPago.IdUsuario);
        var (_, estado) = await IniciarAsync(ctx);
        Assert.Equal(HttpStatusCode.OK, (await CompletarAsync(ctx, estado)).StatusCode);

        ctx.MercadoPago.ResponderPedido = pedido =>
        {
            if (EsDeTokens(pedido))
            {
                return Exitoso(AccessToken, RefreshToken, ctx.MercadoPago.IdUsuario);
            }

            if (api?.Invoke(pedido) is { } propia)
            {
                return propia;
            }

            return pedido.Uri.AbsolutePath.EndsWith("/payments/search", StringComparison.Ordinal)
                ? Json(HttpStatusCode.OK, Pagos)
                : Json(HttpStatusCode.OK, Reportes);
        };
        return ctx;
    }

    [Fact]
    public async Task ElAdminRecibeLosPagosYLosReportesSaneadosConLaVentanaDeHorasPedida()
    {
        var ctx = await ConectarAsync("mp-diag-ok");

        var respuesta = await ctx.Admin.GetAsync(Ruta(ctx.IdEmpresa, "?horas=6"));

        var cuerpo = await respuesta.Content.ReadAsStringAsync();
        Assert.True(respuesta.StatusCode == HttpStatusCode.OK, $"{respuesta.StatusCode}: {cuerpo}");
        var raiz = JsonDocument.Parse(cuerpo).RootElement;
        Assert.Equal(3, raiz.GetProperty("total").GetInt32());
        Assert.Equal(Ahora, raiz.GetProperty("hasta").GetDateTimeOffset());
        Assert.Equal(Ahora.AddHours(-6), raiz.GetProperty("desde").GetDateTimeOffset());
        var pago = Assert.Single(raiz.GetProperty("pagos").EnumerateArray());
        Assert.Equal(555001, pago.GetProperty("id").GetInt64());
        Assert.Equal("approved", pago.GetProperty("estado").GetString());
        Assert.Equal(900.5m, pago.GetProperty("monto").GetDecimal());
        Assert.Equal("Ana Gómez", pago.GetProperty("nombreDelPagador").GetString());
        Assert.Equal("mercadopago_fee", Assert.Single(pago.GetProperty("comisiones").EnumerateArray()).GetProperty("tipo").GetString());
        Assert.Equal(
            "settlement.csv",
            Assert.Single(raiz.GetProperty("reportes").EnumerateArray()).GetProperty("nombreDeArchivo").GetString());
        Assert.Equal(JsonValueKind.Null, raiz.GetProperty("errorReportes").ValueKind);
        Assert.DoesNotContain("ana.gomez@correo.test", cuerpo);
        Assert.DoesNotContain("28999111", cuerpo);
        Assert.DoesNotContain(AccessToken, cuerpo);

        var llamadas = LlamadasALaApi(ctx);
        Assert.All(llamadas, l => Assert.Equal(HttpMethod.Get, l.Metodo));
        Assert.Equal(["/v1/payments/search", "/v1/account/settlement_report/list"], llamadas.Select(l => l.Uri.AbsolutePath));
        Assert.Contains("begin_date=2026-11-09T09%3A00%3A00.000Z", llamadas[0].Uri.Query);
        Assert.Contains("end_date=2026-11-09T15%3A00%3A00.000Z", llamadas[0].Uri.Query);
    }

    [Fact]
    public async Task SinHorasLaVentanaEsDeVeinticuatro()
    {
        var ctx = await ConectarAsync("mp-diag-defecto");

        var raiz = JsonDocument.Parse(await (await ctx.Admin.GetAsync(Ruta(ctx.IdEmpresa))).Content.ReadAsStringAsync()).RootElement;

        Assert.Equal(Ahora.AddHours(-24), raiz.GetProperty("desde").GetDateTimeOffset());
    }

    [Fact]
    public async Task UnFalloDeLosReportesDejaLosPagosYMarcaElCodigoDelError()
    {
        var ctx = await ConectarAsync(
            "mp-diag-reportes-falla",
            p => p.Uri.AbsolutePath.EndsWith("/settlement_report/list", StringComparison.Ordinal)
                ? Json(HttpStatusCode.Forbidden, """{"message":"forbidden"}""")
                : null);

        var respuesta = await ctx.Admin.GetAsync(Ruta(ctx.IdEmpresa));

        Assert.Equal(HttpStatusCode.OK, respuesta.StatusCode);
        var raiz = JsonDocument.Parse(await respuesta.Content.ReadAsStringAsync()).RootElement;
        Assert.Single(raiz.GetProperty("pagos").EnumerateArray());
        Assert.Equal(JsonValueKind.Null, raiz.GetProperty("reportes").ValueKind);
        Assert.Equal(CodigosDeErrorDeMercadoPago.SolicitudRechazada, raiz.GetProperty("errorReportes").GetString());
    }

    [Fact]
    public async Task UnFalloDeLosPagosSePropagaConElErrorDeMercadoPago()
    {
        var ctx = await ConectarAsync(
            "mp-diag-pagos-falla",
            p => p.Uri.AbsolutePath.EndsWith("/payments/search", StringComparison.Ordinal)
                ? Json(HttpStatusCode.BadRequest, """{"message":"mal"}""")
                : null);

        var respuesta = await ctx.Admin.GetAsync(Ruta(ctx.IdEmpresa));

        Assert.Equal(HttpStatusCode.BadGateway, respuesta.StatusCode);
        Assert.Equal(CodigosDeErrorDeMercadoPago.SolicitudRechazada, await CodigoAsync(respuesta));
    }

    [Fact]
    public async Task SoloElAdminPuedeLeerElDiagnostico()
    {
        var ctx = await ConectarAsync("mp-diag-roles");

        Assert.Equal(HttpStatusCode.Forbidden, (await ctx.Supervisor.GetAsync(Ruta(ctx.IdEmpresa))).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await ctx.Vendedor.GetAsync(Ruta(ctx.IdEmpresa))).StatusCode);
        using var anonimo = ctx.Host.CreateClient();
        Assert.Equal(HttpStatusCode.Unauthorized, (await anonimo.GetAsync(Ruta(ctx.IdEmpresa))).StatusCode);
        Assert.Empty(LlamadasALaApi(ctx));
    }

    [Fact]
    public async Task LaEmpresaDeOtroTenantDa404SinLlamarAMercadoPago()
    {
        var ctx = await ConectarAsync("mp-diag-aislamiento-a");
        var otro = await ConectarAsync("mp-diag-aislamiento-b");

        var respuesta = await ctx.Admin.GetAsync(Ruta(otro.IdEmpresa));

        Assert.Equal(HttpStatusCode.NotFound, respuesta.StatusCode);
        Assert.Empty(LlamadasALaApi(ctx));
        Assert.Empty(LlamadasALaApi(otro));
    }

    [Fact]
    public async Task SinConexionActivaDa409NoConectadaSinLlamarALaApi()
    {
        var ctx = await _apoyo.PrepararAsync("mp-diag-sin-conexion");

        var respuesta = await ctx.Admin.GetAsync(Ruta(ctx.IdEmpresa));

        Assert.Equal(HttpStatusCode.Conflict, respuesta.StatusCode);
        Assert.Equal(CodigosDeErrorDeMercadoPago.NoConectada, await CodigoAsync(respuesta));
        Assert.Empty(LlamadasALaApi(ctx));
    }

    [Theory]
    [InlineData("?horas=0")]
    [InlineData("?horas=169")]
    [InlineData("?horas=-3")]
    public async Task LasHorasFueraDeRangoDan400SinLlamarALaApi(string consulta)
    {
        var ctx = await ConectarAsync("mp-diag-horas");

        var respuesta = await ctx.Admin.GetAsync(Ruta(ctx.IdEmpresa, consulta));

        Assert.Equal(HttpStatusCode.BadRequest, respuesta.StatusCode);
        Assert.Equal(ServicioDeDiagnosticoMercadoPago.CodigoDeHorasInvalidas, await CodigoAsync(respuesta));
        Assert.Empty(LlamadasALaApi(ctx));
    }
}
