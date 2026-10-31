using System.Collections.Concurrent;
using System.Net;
using System.Text;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Npgsql;
using Ways.Application.Abstracciones;
using Ways.Application.MercadoPago;
using Ways.Domain.Common;
using Ways.Domain.MercadoPago;
using Ways.Infrastructure.MercadoPago;
using static Ways.IntegrationTests.ApoyoDeMercadoPago;

namespace Ways.IntegrationTests;

/// <summary>
/// El token del vendedor y el cliente base de la API de Mercado Pago contra Postgres real: la lectura de la conexión activa, la
/// renovación previa al uso (con el endpoint de tokens como guion), la política ante una renovación fallida, el 401 que deja la
/// conexión en <c>requiere_reconexion</c> y la ausencia de tokens en los logs.
/// </summary>
[Collection("Ways.IntegrationTests secuencial")]
public class MercadoPagoApiTests(WaysApiFixture fixture) : IClassFixture<WaysApiFixture>
{
    private readonly ApoyoDeMercadoPago _apoyo = new(fixture);

    private sealed record Conectada(Contexto Ctx, FilaDeConexion Fila, string Acceso, string Refresco);

    private sealed class CapturaDeLogs : ILoggerProvider
    {
        private readonly ConcurrentQueue<string> _mensajes = new();

        public IReadOnlyCollection<string> Mensajes => _mensajes;

        public ILogger CreateLogger(string categoria) => new Registro(_mensajes);

        public void Dispose()
        {
        }

        private sealed class Registro(ConcurrentQueue<string> mensajes) : ILogger
        {
            public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

            public bool IsEnabled(LogLevel logLevel) => true;

            public void Log<TState>(
                LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter) =>
                mensajes.Enqueue(formatter(state, exception) + exception);
        }
    }

    private static string Etiqueta() => Guid.NewGuid().ToString("N")[..8];

    private static HttpResponseMessage Json(HttpStatusCode estado, string json) =>
        new(estado) { Content = new StringContent(json, Encoding.UTF8, "application/json") };

    private static bool EsDeTokens(PedidoRegistrado pedido) => pedido.Uri.AbsolutePath.EndsWith("/oauth/token", StringComparison.Ordinal);

    private async Task<Conectada> ConectarAsync(Contexto ctx)
    {
        var etiqueta = Etiqueta();
        var acceso = $"APP_USR-{etiqueta}-acceso-secreto";
        var refresco = $"TG-{etiqueta}-refresco-secreto";
        ctx.MercadoPago.Responder = _ => Exitoso(acceso, refresco, ctx.MercadoPago.IdUsuario);

        var (_, estado) = await IniciarAsync(ctx);
        Assert.Equal(HttpStatusCode.OK, (await CompletarAsync(ctx, estado)).StatusCode);

        var fila = (await _apoyo.FilasAsync(ctx.IdEmpresa)).Last(f => f.Estado == "activa");
        ctx.MercadoPago.Responder = _ => new HttpResponseMessage(HttpStatusCode.ServiceUnavailable);
        return new Conectada(ctx, fila, acceso, refresco);
    }

    /// <summary>El endpoint de tokens responde <paramref name="renovacion"/>; cualquier otra ruta, <paramref name="api"/>.</summary>
    private static void Guionar(
        Contexto ctx, Func<HttpResponseMessage> renovacion, Func<PedidoRegistrado, HttpResponseMessage>? api = null) =>
        ctx.MercadoPago.ResponderPedido = pedido => EsDeTokens(pedido)
            ? renovacion()
            : api?.Invoke(pedido) ?? new HttpResponseMessage(HttpStatusCode.NotFound);

    private async Task VencerEnAsync(Conectada conectada, TimeSpan desdeAhora) =>
        await EjecutarSqlAsync(
            "UPDATE conexiones_mercadopago SET access_token_expira_en = $1 WHERE id_conexion_mercadopago = $2",
            DateTimeOffset.UtcNow + desdeAhora, conectada.Fila.Id);

    private async Task EjecutarSqlAsync(string sql, params object[] parametros)
    {
        await using var conexion = await fixture.AbrirConexionCrudaAsync("plataforma", null);
        await using var comando = conexion.CreateCommand();
        comando.CommandText = sql;
        foreach (var parametro in parametros)
        {
            comando.Parameters.Add(new NpgsqlParameter { Value = parametro });
        }

        await comando.ExecuteNonQueryAsync();
    }

    private static (string Acceso, string Refresco) Descifrar(FilaDeConexion f) =>
        new CifradoDeTokensMercadoPago(ConfiguracionDeClaves()).Descifrar(new ConexionMercadoPago
        {
            Id = f.Id,
            IdTenant = f.IdTenant,
            IdEmpresa = f.IdEmpresa,
            IdClaveMaestra = f.IdClaveMaestra,
            AccessTokenCifrado = f.AccessCifrado,
            AccessTokenNonce = f.AccessNonce,
            AccessTokenTag = f.AccessTag,
            RefreshTokenCifrado = f.RefreshCifrado,
            RefreshTokenNonce = f.RefreshNonce,
            RefreshTokenTag = f.RefreshTag
        });

    private static (IServiceScope Alcance, IDisposable Suplantacion) EnElTenant(WebApplicationFactory<Program> host, int idTenant)
    {
        var alcance = host.Services.CreateScope();
        var suplantacion = alcance.ServiceProvider.GetRequiredService<ITenantActual>().Suplantar(idTenant);
        return (alcance, suplantacion);
    }

    private static async Task<TokenDelVendedorMercadoPago> ObtenerAsync(Contexto ctx)
    {
        var (alcance, suplantacion) = EnElTenant(ctx.Host, ctx.IdTenant);
        using (alcance)
        using (suplantacion)
        {
            return await alcance.ServiceProvider.GetRequiredService<IProveedorDeTokenMercadoPago>()
                .ObtenerAccessTokenAsync(ctx.IdEmpresa);
        }
    }

    private static async Task<ErrorDominio> FallaAsync(Contexto ctx) =>
        await Assert.ThrowsAsync<ErrorDominio>(() => ObtenerAsync(ctx));

    private static int Renovaciones(Contexto ctx) =>
        ctx.MercadoPago.Pedidos.Count(p => EsDeTokens(p) && p.Cuerpo.Contains("\"grant_type\":\"refresh_token\""));

    private static void AfirmarRequiereReconexion(FilaDeConexion fila)
    {
        Assert.Equal("requiere_reconexion", fila.Estado);
        Assert.Null(fila.AccessCifrado);
        Assert.Null(fila.AccessNonce);
        Assert.Null(fila.AccessTag);
        Assert.Null(fila.RefreshCifrado);
        Assert.Null(fila.RefreshNonce);
        Assert.Null(fila.RefreshTag);
        Assert.NotNull(fila.FechaRequiereReconexion);
        Assert.Null(fila.FechaDesconexion);
    }

    // ---- Estado de la conexión --------------------------------------------------------------------------------------

    [Fact]
    public async Task SinConexionElProveedorResponde409NoConectada()
    {
        var ctx = await _apoyo.PrepararAsync("mp-api-sin");

        var error = await FallaAsync(ctx);

        Assert.Equal(CodigosDeErrorDeMercadoPago.NoConectada, error.Codigo);
        Assert.Equal(409, error.EstadoHttp);
        Assert.Empty(ctx.MercadoPago.Pedidos);
    }

    [Fact]
    public async Task UnaConexionDesconectadaEsNoConectada()
    {
        var ctx = await _apoyo.PrepararAsync("mp-api-desc");
        await ConectarAsync(ctx);
        Assert.Equal(HttpStatusCode.NoContent, (await ctx.Admin.DeleteAsync(ctx.Ruta)).StatusCode);

        Assert.Equal(CodigosDeErrorDeMercadoPago.NoConectada, (await FallaAsync(ctx)).Codigo);
    }

    [Fact]
    public async Task SiLaUltimaConexionRequiereReconexionElProveedorLoDiceYUnaActivaPosteriorGana()
    {
        var ctx = await _apoyo.PrepararAsync("mp-api-requiere");
        var conectada = await ConectarAsync(ctx);
        await EjecutarSqlAsync(
            "UPDATE conexiones_mercadopago SET estado = 'requiere_reconexion', fecha_requiere_reconexion = now(), " +
            "access_token_cifrado = NULL, access_token_nonce = NULL, access_token_tag = NULL, refresh_token_cifrado = NULL, " +
            "refresh_token_nonce = NULL, refresh_token_tag = NULL WHERE id_conexion_mercadopago = $1",
            conectada.Fila.Id);

        Assert.Equal(CodigosDeErrorDeMercadoPago.RequiereReconexion, (await FallaAsync(ctx)).Codigo);

        var otra = await ConectarAsync(ctx);
        Assert.Equal(otra.Acceso, (await ObtenerAsync(ctx)).AccessToken);
    }

    [Fact]
    public async Task ElProveedorSoloVeLasConexionesDeSuTenant()
    {
        var a = await _apoyo.PrepararAsync("mp-api-aislada-a");
        var b = await _apoyo.PrepararAsync("mp-api-aislada-b");
        await ConectarAsync(a);

        var (alcance, suplantacion) = EnElTenant(b.Host, b.IdTenant);
        using (alcance)
        using (suplantacion)
        {
            var error = await Assert.ThrowsAsync<ErrorDominio>(
                () => alcance.ServiceProvider.GetRequiredService<IProveedorDeTokenMercadoPago>().ObtenerAccessTokenAsync(a.IdEmpresa));
            Assert.Equal(CodigosDeErrorDeMercadoPago.NoConectada, error.Codigo);
        }
    }

    [Fact]
    public async Task UnMaterialIlegibleSubeComoErrorTipadoSinLlamarAMercadoPago()
    {
        var ctx = await _apoyo.PrepararAsync("mp-api-ilegible");
        var id = await _apoyo.InsertarAsync(ctx.IdTenant, ctx.IdEmpresa, ctx.IdCuenta, NuevoIdUsuario(), ctx.IdUsuarioAdmin);
        await EjecutarSqlAsync(
            "UPDATE conexiones_mercadopago SET access_token_expira_en = $1 WHERE id_conexion_mercadopago = $2",
            DateTimeOffset.UtcNow.AddDays(60), id);

        var error = await FallaAsync(ctx);

        Assert.Equal("mercadopago_tokens_ilegibles", error.Codigo);
        Assert.Empty(ctx.MercadoPago.Pedidos);
    }

    // ---- Margen y renovación previa ---------------------------------------------------------------------------------

    [Fact]
    public async Task UnTokenFueraDelMargenSeEntregaSinRenovarYConElIdDelVendedor()
    {
        var ctx = await _apoyo.PrepararAsync("mp-api-fuera");
        var conectada = await ConectarAsync(ctx);
        await VencerEnAsync(conectada, TimeSpan.FromDays(2));
        Guionar(ctx, () => Exitoso("no-debe-usarse", "no-debe-usarse", ctx.MercadoPago.IdUsuario));

        var token = await ObtenerAsync(ctx);

        Assert.Equal(conectada.Acceso, token.AccessToken);
        Assert.Equal(ctx.MercadoPago.IdUsuario, token.IdUsuarioMercadoPago);
        Assert.Equal(conectada.Fila.Id, token.IdConexion);
        Assert.Equal(0, Renovaciones(ctx));
    }

    [Fact]
    public async Task UnTokenDentroDelMargenSeRenuevaAntesYSeEntregaElNuevo()
    {
        var ctx = await _apoyo.PrepararAsync("mp-api-margen");
        var conectada = await ConectarAsync(ctx);
        await VencerEnAsync(conectada, TimeSpan.FromHours(12));
        var (acceso2, refresco2) = ($"APP_USR-{Etiqueta()}-nuevo", $"TG-{Etiqueta()}-nuevo");
        Guionar(ctx, () => Exitoso(acceso2, refresco2, ctx.MercadoPago.IdUsuario));

        var token = await ObtenerAsync(ctx);

        Assert.Equal(acceso2, token.AccessToken);
        Assert.Equal(1, Renovaciones(ctx));
        var fila = Assert.Single(await _apoyo.FilasAsync(ctx.IdEmpresa));
        Assert.Equal((acceso2, refresco2), Descifrar(fila));
        Assert.Equal(fila.AccessExpiraEn, token.ExpiraEn);
    }

    [Fact]
    public async Task UnTokenYaVencidoSeRenuevaAntesDeEntregarse()
    {
        var ctx = await _apoyo.PrepararAsync("mp-api-vencido");
        var conectada = await ConectarAsync(ctx);
        await VencerEnAsync(conectada, TimeSpan.FromHours(-3));
        var acceso2 = $"APP_USR-{Etiqueta()}-nuevo";
        Guionar(ctx, () => Exitoso(acceso2, $"TG-{Etiqueta()}-nuevo", ctx.MercadoPago.IdUsuario));

        Assert.Equal(acceso2, (await ObtenerAsync(ctx)).AccessToken);
    }

    [Fact]
    public async Task SiLaRenovacionPreviaDaInvalidGrantElProveedorPideReconectarYLaFilaLoRefleja()
    {
        var ctx = await _apoyo.PrepararAsync("mp-api-invalid");
        var conectada = await ConectarAsync(ctx);
        await VencerEnAsync(conectada, TimeSpan.FromHours(12));
        Guionar(ctx, () => Json(HttpStatusCode.BadRequest, """{"error":"invalid_grant"}"""));

        var error = await FallaAsync(ctx);

        Assert.Equal(CodigosDeErrorDeMercadoPago.RequiereReconexion, error.Codigo);
        AfirmarRequiereReconexion(Assert.Single(await _apoyo.FilasAsync(ctx.IdEmpresa)));
    }

    [Theory]
    [InlineData(503)]
    [InlineData(429)]
    [InlineData(401)]
    public async Task SiLaRenovacionFallaPeroElTokenTodaviaValeSeEntregaElQueYaEstaGuardado(int estado)
    {
        var ctx = await _apoyo.PrepararAsync("mp-api-falla-vigente");
        var conectada = await ConectarAsync(ctx);
        await VencerEnAsync(conectada, TimeSpan.FromHours(12));
        Guionar(ctx, () => Json((HttpStatusCode)estado, """{"error":"invalid_client"}"""));

        var token = await ObtenerAsync(ctx);

        Assert.Equal(conectada.Acceso, token.AccessToken);
        Assert.Equal(1, Renovaciones(ctx));
        Assert.Equal("activa", Assert.Single(await _apoyo.FilasAsync(ctx.IdEmpresa)).Estado);
    }

    [Theory]
    [InlineData(503)]
    [InlineData(429)]
    [InlineData(401)]
    public async Task SiLaRenovacionFallaYElTokenYaVencioNuncaSeEntregaElVencido(int estado)
    {
        var ctx = await _apoyo.PrepararAsync("mp-api-falla-vencido");
        var conectada = await ConectarAsync(ctx);
        await VencerEnAsync(conectada, TimeSpan.FromHours(-2));
        Guionar(ctx, () => Json((HttpStatusCode)estado, """{"error":"invalid_client"}"""));

        var error = await FallaAsync(ctx);

        Assert.Equal(CodigosDeErrorDeMercadoPago.NoDisponible, error.Codigo);
        Assert.Equal(503, error.EstadoHttp);
        var fila = Assert.Single(await _apoyo.FilasAsync(ctx.IdEmpresa));
        Assert.Equal("activa", fila.Estado);
        Assert.Equal(conectada.Acceso, Descifrar(fila).Acceso);
    }

    [Fact]
    public async Task UnTokenQueVenceEnMenosDeLaHolguraNoSeEntregaSiLaRenovacionFalla()
    {
        var ctx = await _apoyo.PrepararAsync("mp-api-holgura");
        var conectada = await ConectarAsync(ctx);
        await VencerEnAsync(conectada, TimeSpan.FromSeconds(20));
        Guionar(ctx, () => new HttpResponseMessage(HttpStatusCode.ServiceUnavailable));

        Assert.Equal(CodigosDeErrorDeMercadoPago.NoDisponible, (await FallaAsync(ctx)).Codigo);
    }

    // ---- Cliente ----------------------------------------------------------------------------------------------------

    private const string Terminales =
        """{"data":{"terminals":[{"id":"PAX_A910__8701","pos_id":47792476,"store_id":59554,"external_pos_id":"PV1","operating_mode":"PDV"}]},"paging":{"total":1,"offset":0,"limit":50}}""";

    private static async Task<ListadoDeTerminalesMercadoPago> ListarAsync(Contexto ctx, WebApplicationFactory<Program>? host = null)
    {
        var (alcance, suplantacion) = EnElTenant(host ?? ctx.Host, ctx.IdTenant);
        using (alcance)
        using (suplantacion)
        {
            return await alcance.ServiceProvider.GetRequiredService<IClienteApiMercadoPago>()
                .ListarTerminalesAsync(ctx.IdEmpresa, 59554, null);
        }
    }

    [Fact]
    public async Task ListarTerminalesLlamaAMercadoPagoConElTokenDescifradoDeLaConexion()
    {
        var ctx = await _apoyo.PrepararAsync("mp-api-terminales");
        var conectada = await ConectarAsync(ctx);
        Guionar(ctx, () => new HttpResponseMessage(HttpStatusCode.ServiceUnavailable), _ => Json(HttpStatusCode.OK, Terminales));

        var listado = await ListarAsync(ctx);

        var terminal = Assert.Single(listado.Terminales);
        Assert.Equal(new TerminalMercadoPago("PAX_A910__8701", 47792476, 59554, "PV1", "PDV"), terminal);
        var pedido = Assert.Single(ctx.MercadoPago.Pedidos, p => !EsDeTokens(p) && p.Uri.AbsolutePath.Contains("terminals"));
        Assert.Equal(HttpMethod.Get, pedido.Metodo);
        Assert.Equal("/terminals/v1/list", pedido.Uri.AbsolutePath);
        Assert.Equal("?store_id=59554", pedido.Uri.Query);
        Assert.Equal($"Bearer {conectada.Acceso}", pedido.Autorizacion);
    }

    [Fact]
    public async Task UnRechazo401PasaLaConexionARequiereReconexionBorraElMaterialYLasLlamadasSiguientesNoSalen()
    {
        var ctx = await _apoyo.PrepararAsync("mp-api-401");
        var conectada = await ConectarAsync(ctx);
        Guionar(ctx, () => new HttpResponseMessage(HttpStatusCode.ServiceUnavailable), _ => Json(HttpStatusCode.Unauthorized, """{"message":"invalid token"}"""));

        var error = await Assert.ThrowsAsync<ErrorDominio>(() => ListarAsync(ctx));

        Assert.Equal(CodigosDeErrorDeMercadoPago.RequiereReconexion, error.Codigo);
        AfirmarRequiereReconexion(Assert.Single(await _apoyo.FilasAsync(ctx.IdEmpresa)));
        var llamadas = ctx.MercadoPago.Pedidos.Count;

        var otra = await Assert.ThrowsAsync<ErrorDominio>(() => ListarAsync(ctx));

        Assert.Equal(CodigosDeErrorDeMercadoPago.RequiereReconexion, otra.Codigo);
        Assert.Equal(llamadas, ctx.MercadoPago.Pedidos.Count);
        Assert.Equal(conectada.Fila.Id, Assert.Single(await _apoyo.FilasAsync(ctx.IdEmpresa)).Id);
    }

    [Fact]
    public async Task UnRechazoDeUnaVersionViejaDelTokenNoPisaUnaConexionCuyoVencimientoYaCambio()
    {
        var ctx = await _apoyo.PrepararAsync("mp-api-401-viejo");
        await ConectarAsync(ctx);
        var tokenViejo = await ObtenerAsync(ctx);
        // Una renovación concurrente cambia el vencimiento: se la simula corriéndolo un día.
        await EjecutarSqlAsync(
            "UPDATE conexiones_mercadopago SET access_token_expira_en = access_token_expira_en + interval '1 day' " +
            "WHERE id_conexion_mercadopago = $1",
            tokenViejo.IdConexion);
        var antes = Assert.Single(await _apoyo.FilasAsync(ctx.IdEmpresa));

        var (alcance, suplantacion) = EnElTenant(ctx.Host, ctx.IdTenant);
        using (alcance)
        using (suplantacion)
        {
            var cambio = await alcance.ServiceProvider.GetRequiredService<IProveedorDeTokenMercadoPago>()
                .MarcarTokenRechazadoAsync(tokenViejo);
            Assert.False(cambio);
        }

        var despues = Assert.Single(await _apoyo.FilasAsync(ctx.IdEmpresa));
        Assert.Equal("activa", despues.Estado);
        Assert.Equal(antes.AccessCifrado, despues.AccessCifrado);
        Assert.Equal(antes.RefreshCifrado, despues.RefreshCifrado);
        Assert.Null(despues.FechaRequiereReconexion);
    }

    [Fact]
    public async Task UnRechazoNoTocaLaConexionDeOtraEmpresaConElMismoVencimiento()
    {
        var ctx = await _apoyo.PrepararAsync("mp-api-401-id");
        await ConectarAsync(ctx);
        var token = await ObtenerAsync(ctx);
        var (idEmpresa2, idCuenta2) = await _apoyo.CrearOtraEmpresaConCuentaAsync(ctx, "Otra empresa");
        var idOtra = await _apoyo.InsertarAsync(ctx.IdTenant, idEmpresa2, idCuenta2, NuevoIdUsuario(), ctx.IdUsuarioAdmin);
        await EjecutarSqlAsync(
            "UPDATE conexiones_mercadopago SET access_token_expira_en = $1 WHERE id_conexion_mercadopago = $2", token.ExpiraEn, idOtra);

        var (alcance, suplantacion) = EnElTenant(ctx.Host, ctx.IdTenant);
        using (alcance)
        using (suplantacion)
        {
            Assert.True(await alcance.ServiceProvider.GetRequiredService<IProveedorDeTokenMercadoPago>().MarcarTokenRechazadoAsync(token));
        }

        var otra = Assert.Single(await _apoyo.FilasAsync(idEmpresa2));
        Assert.Equal("activa", otra.Estado);
        Assert.NotNull(otra.AccessCifrado);
        Assert.Equal("requiere_reconexion", Assert.Single(await _apoyo.FilasAsync(ctx.IdEmpresa)).Estado);
    }

    [Fact]
    public async Task UnRechazoNoReviveUnaConexionQueSeDesconectoDespuesDeLeerElToken()
    {
        var ctx = await _apoyo.PrepararAsync("mp-api-401-desconectada");
        await ConectarAsync(ctx);
        var token = await ObtenerAsync(ctx);
        Assert.Equal(HttpStatusCode.NoContent, (await ctx.Admin.DeleteAsync(ctx.Ruta)).StatusCode);

        var (alcance, suplantacion) = EnElTenant(ctx.Host, ctx.IdTenant);
        using (alcance)
        using (suplantacion)
        {
            Assert.False(await alcance.ServiceProvider.GetRequiredService<IProveedorDeTokenMercadoPago>().MarcarTokenRechazadoAsync(token));
        }

        var fila = Assert.Single(await _apoyo.FilasAsync(ctx.IdEmpresa));
        Assert.Equal("desconectada", fila.Estado);
        Assert.Null(fila.FechaRequiereReconexion);
    }

    [Theory]
    [InlineData(500)]
    [InlineData(429)]
    [InlineData(400)]
    [InlineData(404)]
    public async Task UnaFallaDistintaDe401NoTocaLaConexion(int estado)
    {
        var ctx = await _apoyo.PrepararAsync("mp-api-otras");
        var conectada = await ConectarAsync(ctx);
        Guionar(ctx, () => new HttpResponseMessage(HttpStatusCode.ServiceUnavailable), _ => Json((HttpStatusCode)estado, """{"message":"x"}"""));

        var error = await Assert.ThrowsAnyAsync<ErrorDominio>(() => ListarAsync(ctx));

        Assert.Equal(estado is 400 or 404 ? CodigosDeErrorDeMercadoPago.SolicitudRechazada : CodigosDeErrorDeMercadoPago.NoDisponible, error.Codigo);
        var fila = Assert.Single(await _apoyo.FilasAsync(ctx.IdEmpresa));
        Assert.Equal("activa", fila.Estado);
        Assert.Equal(conectada.Acceso, Descifrar(fila).Acceso);
    }

    [Fact]
    public async Task NingunTokenApareceEnLosLogsDeLaRenovacionNiDelClienteNiDeSusFallas()
    {
        var ctx = await _apoyo.PrepararAsync("mp-api-logs");
        var conectada = await ConectarAsync(ctx);
        var captura = new CapturaDeLogs();
        var host = ctx.Host.WithWebHostBuilder(b => b.ConfigureServices(s => s.AddLogging(l => l.AddProvider(captura))));
        var (acceso2, refresco2) = ($"APP_USR-{Etiqueta()}-nuevo", $"TG-{Etiqueta()}-nuevo");

        await VencerEnAsync(conectada, TimeSpan.FromHours(12));
        Guionar(ctx, () => Exitoso(acceso2, refresco2, ctx.MercadoPago.IdUsuario), _ => Json(HttpStatusCode.OK, Terminales));
        await ListarAsync(ctx, host);

        Guionar(ctx, () => new HttpResponseMessage(HttpStatusCode.ServiceUnavailable), _ => Json(HttpStatusCode.BadRequest, $$"""{"message":"token {{acceso2}}"}"""));
        await Assert.ThrowsAsync<ErrorDeApiMercadoPago>(() => ListarAsync(ctx, host));

        Guionar(ctx, () => new HttpResponseMessage(HttpStatusCode.ServiceUnavailable), _ => Json(HttpStatusCode.Unauthorized, "{}"));
        await Assert.ThrowsAsync<ErrorDominio>(() => ListarAsync(ctx, host));

        Assert.NotEmpty(captura.Mensajes);
        foreach (var secreto in new[] { conectada.Acceso, conectada.Refresco, acceso2, refresco2, ClientSecret })
        {
            Assert.All(captura.Mensajes, m => Assert.DoesNotContain(secreto, m));
        }
    }
}
