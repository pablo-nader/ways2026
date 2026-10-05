using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using OpenIddict.Abstractions;
using Ways.Api.ConectorMcp;
using Ways.Application.Usuarios;
using Ways.Domain.Usuarios;
using Ways.Infrastructure.Multitenancy;
using static Ways.IntegrationTests.ApoyoDeConectorMcp;

namespace Ways.IntegrationTests;

/// <summary>
/// El conector MCP encendido, contra Postgres real y como <c>ways_app</c> (RLS activo):
/// descubrimiento, autorización con código + PKCE desde la página de consentimiento, canje y
/// refresh de tokens, llamadas a <c>/mcp</c> y la línea de diagnóstico de cada request. Cada prueba
/// arranca su propio host derivado con la lista de mails que necesita.
/// </summary>
[Collection("Ways.IntegrationTests secuencial")]
public class ConectorMcpFlujoTests(WaysApiFixture fixture) : IClassFixture<WaysApiFixture>
{
    private readonly ApoyoDeConectorMcp _apoyo = new(fixture);

    private static string Campo(JsonElement json, string nombre) => json.GetProperty(nombre).GetString()!;

    private static async Task<string?> ErrorOAuthAsync(HttpResponseMessage respuesta) =>
        (await respuesta.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("error").GetString();

    private static Action<IWebHostBuilder> ConCaptura(CapturaDeLogs captura) =>
        builder => builder.ConfigureLogging(logging => logging.AddProvider(captura));

    private async Task DesactivarAsync(int idUsuario)
    {
        await using var db = fixture.CrearContextoDeAplicacion(TenantActualFijo.Plataforma);
        var fila = await db.Usuarios.SingleAsync(u => u.Id == idUsuario);
        fila.Estado = EstadoUsuario.Inactivo;
        await db.SaveChangesAsync();
    }

    [Fact]
    public async Task SinTokenMcpDa401ApuntandoALaMetadataYEsaMetadataCoincideConLaDelServidor()
    {
        await using var host = _apoyo.HostConConector(mailsHabilitados: string.Empty);
        using var cliente = Cliente(host);

        using var pedido = new HttpRequestMessage(HttpMethod.Post, "/mcp")
        {
            Content = JsonContent.Create(new { jsonrpc = "2.0", id = 1, method = "tools/list" })
        };
        pedido.Headers.Accept.ParseAdd("application/json");
        pedido.Headers.Accept.ParseAdd("text/event-stream");
        var respuesta = await cliente.SendAsync(pedido);

        Assert.Equal(HttpStatusCode.Unauthorized, respuesta.StatusCode);
        Assert.Equal(
            $"Bearer resource_metadata=\"{UrlPublica}/.well-known/oauth-protected-resource/mcp\"",
            Assert.Single(respuesta.Headers.WwwAuthenticate).ToString());

        var delRecurso = await cliente.GetFromJsonAsync<JsonElement>("/.well-known/oauth-protected-resource/mcp");
        var delServidor = await cliente.GetFromJsonAsync<JsonElement>("/.well-known/oauth-authorization-server");

        Assert.Equal(RecursoMcp, Campo(delRecurso, "resource"));
        Assert.Equal(UrlPublica + "/", Campo(delServidor, "issuer"));
        Assert.Equal(Campo(delServidor, "issuer"), delRecurso.GetProperty("authorization_servers")[0].GetString());
        Assert.Equal(
            "S256",
            Assert.Single(delServidor.GetProperty("code_challenge_methods_supported").EnumerateArray()).GetString());
        Assert.Contains(
            "none",
            delServidor.GetProperty("token_endpoint_auth_methods_supported").EnumerateArray().Select(m => m.GetString()));
    }

    [Fact]
    public async Task UnUsuarioDeTenantHabilitadoAutorizaYQuienSoyDevuelveSuTenantYNoElDeOtro()
    {
        var otro = await _apoyo.SembrarUsuarioDeTenantAsync("conector-otro");
        var usuario = await _apoyo.SembrarUsuarioDeTenantAsync("conector-propio");
        await using var host = _apoyo.HostConConector($"{otro.Mail};{usuario.Mail}");
        using var cliente = Cliente(host);

        var texto = await QuienSoyAsync(cliente, Campo(await ObtenerTokensAsync(cliente, usuario.Mail), "access_token"));

        Assert.Contains(usuario.Mail, texto);
        Assert.Contains(usuario.NombreTenant, texto);
        Assert.DoesNotContain(otro.NombreTenant, texto);
    }

    /// <summary>Cubre las tres partes de la línea de diagnóstico: los parámetros OAuth (valor de la
    /// lista blanca, solo presencia de los secretos), el error OAuth de un rechazo y el motivo de un
    /// 401 en <c>/mcp</c>; y que ningún log, de ninguna categoría, escribe un secreto del flujo.</summary>
    [Fact]
    public async Task ElLogMuestraLoQuePideElClienteYPorQueSeRechazaSinEscribirSecretos()
    {
        var usuario = await _apoyo.SembrarUsuarioDeTenantAsync("conector-diagnostico");
        var captura = new CapturaDeLogs();
        await using var host = _apoyo.HostConConector(usuario.Mail, configurarMas: ConCaptura(captura));
        using var cliente = Cliente(host);

        var pkce = NuevoPkce();
        var estado = $"estado-{Guid.NewGuid():N}";
        var codigo = CodigoDe(await AprobarAsync(
            cliente, UrlDeAutorizacion(pkce, new Dictionary<string, string?> { ["state"] = estado }), usuario.Mail, Password));
        Assert.NotNull(codigo);
        var canje = await CanjearAsync(cliente, codigo, pkce);
        Assert.Equal(HttpStatusCode.OK, canje.StatusCode);
        var tokens = await canje.Content.ReadFromJsonAsync<JsonElement>();
        var refresco = await RefrescarAsync(cliente, Campo(tokens, "refresh_token"));
        Assert.Equal(HttpStatusCode.OK, refresco.StatusCode);
        var nuevos = await refresco.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(HttpStatusCode.OK, (await LlamarMcpAsync(cliente, Campo(nuevos, "access_token"), "tools/list")).Estado);

        Assert.Equal(HttpStatusCode.BadRequest, (await CanjearAsync(cliente, codigo, pkce)).StatusCode);
        Assert.Equal(
            HttpStatusCode.BadRequest,
            (await cliente.GetAsync(UrlDeAutorizacion(pkce, new Dictionary<string, string?> { ["code_challenge_method"] = "plain" }))).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await LlamarMcpAsync(cliente, accessToken: null, "tools/list")).Estado);
        Assert.Equal(HttpStatusCode.Unauthorized, (await LlamarMcpAsync(cliente, "token-ajeno-al-conector", "tools/list")).Estado);
        await PostearCuerpoCrudoAsync(
            cliente, $"{{\"jsonrpc\":\"2.0\",\"id\":1,\"method\":\"tools/list\",\"params\":{{\"relleno\":\"{new string('x', 1_000_001)}\"}}}}");
        await PostearCuerpoCrudoAsync(cliente, "{esto no es json");

        var lineas = LineasDeDiagnostico(captura);
        Assert.Contains(lineas, l =>
            l.Contains("GET /connect/authorize -> 200") &&
            l.Contains("client_id=\"claude-ways\"") && l.Contains($"redirect_uri=\"{Redireccion}\"") &&
            l.Contains("response_type=\"code\"") && l.Contains("scope=\"ways.mcp offline_access\"") &&
            l.Contains($"resource=\"{RecursoMcp}\"") && l.Contains("code_challenge_method=\"S256\"") &&
            l.Contains("state=true") && l.Contains("code_challenge=true") && l.Contains("code=false"));
        Assert.Contains(lineas, l =>
            l.Contains("POST /connect/token -> 200") && l.Contains("grant_type=\"authorization_code\"") &&
            l.Contains("code=true") && l.Contains("code_verifier=true") && l.Contains("refresh_token=false"));
        Assert.Contains(lineas, l =>
            l.Contains("POST /connect/token -> 200") && l.Contains("grant_type=\"refresh_token\"") &&
            l.Contains("refresh_token=true") && l.Contains("code=false"));
        Assert.Contains(lineas, l => l.Contains("POST /connect/token -> 400") && l.Contains("Error=invalid_grant: "));
        Assert.Contains(lineas, l => l.Contains("GET /connect/authorize -> 400") && l.Contains("Error=invalid_request: "));
        Assert.Contains(lineas, l => l.Contains("POST /mcp -> 401") && l.Contains("Motivo=sin token"));
        Assert.Contains(lineas, l => l.Contains("POST /mcp -> 401") && l.Contains("Motivo=token inválido o vencido"));
        Assert.Contains(lineas, l => l.Contains("JSON-RPC=(cuerpo no inspeccionado)"));
        Assert.Contains(lineas, l => l.Contains("JSON-RPC=(json inválido)"));

        string[] secretos =
        [
            estado, pkce.Desafio, pkce.Verificador, codigo, Password,
            Campo(tokens, "access_token"), Campo(tokens, "refresh_token"),
            Campo(nuevos, "access_token"), Campo(nuevos, "refresh_token")
        ];
        Assert.DoesNotContain(captura.Entradas, entrada => secretos.Any(secreto => entrada.Mensaje.Contains(secreto, StringComparison.Ordinal)));
    }

    [Fact]
    public async Task UnaAutorizacionSinScopeIgualDaUnTokenQueSirveEnMcp()
    {
        var usuario = await _apoyo.SembrarUsuarioDeTenantAsync("conector-sin-scope");
        await using var host = _apoyo.HostConConector(usuario.Mail);
        using var cliente = Cliente(host);

        var tokens = await ObtenerTokensAsync(cliente, usuario.Mail, alcances: null);

        Assert.Equal(ConstantesDeMcp.AlcanceMcp, Campo(tokens, "scope"));
        Assert.Equal(HttpStatusCode.OK, (await LlamarMcpAsync(cliente, Campo(tokens, "access_token"), "tools/list")).Estado);
    }

    [Fact]
    public async Task UnaAutorizacionSinResourceDaUnTokenParaElRecursoDeEsteServidor()
    {
        var usuario = await _apoyo.SembrarUsuarioDeTenantAsync("conector-sin-resource");
        await using var host = _apoyo.HostConConector(usuario.Mail);
        using var cliente = Cliente(host);

        var accessToken = Campo(await ObtenerTokensAsync(cliente, usuario.Mail, recurso: null), "access_token");

        Assert.Equal(HttpStatusCode.OK, (await LlamarMcpAsync(cliente, accessToken, "tools/list")).Estado);
    }

    [Fact]
    public async Task UnMailFueraDeLaListaNoRecibeCodigoYVeElMismoErrorQueConUnaPasswordIncorrecta()
    {
        var habilitado = await _apoyo.SembrarUsuarioDeTenantAsync("conector-habilitado");
        var fueraDeLaLista = await _apoyo.SembrarUsuarioDeTenantAsync("conector-fuera");
        await using var host = _apoyo.HostConConector(habilitado.Mail);
        using var cliente = Cliente(host);

        var conPasswordIncorrecta = await AprobarAsync(
            cliente, UrlDeAutorizacion(NuevoPkce()), habilitado.Mail, "otra-contraseña");
        var fuera = await AprobarAsync(cliente, UrlDeAutorizacion(NuevoPkce()), fueraDeLaLista.Mail, Password);

        Assert.Equal(HttpStatusCode.OK, fuera.StatusCode);
        Assert.Null(CodigoDe(fuera));
        var errorConPasswordIncorrecta = MensajeDeError(await conPasswordIncorrecta.Content.ReadAsStringAsync());
        Assert.NotNull(errorConPasswordIncorrecta);
        Assert.Equal(errorConPasswordIncorrecta, MensajeDeError(await fuera.Content.ReadAsStringAsync()));
    }

    [Fact]
    public async Task LosIntentosConPasswordIncorrectaDeUnMailFueraDeLaListaNoBloqueanLaCuenta()
    {
        var fueraDeLaLista = await _apoyo.SembrarUsuarioDeTenantAsync("conector-sin-bloqueo");
        await using var host = _apoyo.HostConConector("otro@ways.test");
        using var cliente = Cliente(host);

        for (var intento = 0; intento < PoliticaDeRoles.UmbralBloqueoPorIntentosFallidos; intento++)
        {
            var respuesta = await AprobarAsync(
                cliente, UrlDeAutorizacion(NuevoPkce()), fueraDeLaLista.Mail, "contraseña-incorrecta");
            Assert.Null(CodigoDe(respuesta));
        }

        await using (var db = fixture.CrearContextoDeAplicacion(TenantActualFijo.Plataforma))
        {
            var fila = await db.Usuarios.AsNoTracking().SingleAsync(u => u.Id == fueraDeLaLista.IdUsuario);
            Assert.Equal(0, fila.IntentosFallidos);
            Assert.Equal(EstadoUsuario.Activo, fila.Estado);
        }

        var login = await cliente.PostAsJsonAsync("/api/auth/login", new SolicitudDeLogin(fueraDeLaLista.Mail, Password));
        Assert.Equal(HttpStatusCode.OK, login.StatusCode);
    }

    [Fact]
    public async Task ElUsuarioDePlataformaNoRecibeCodigoAunqueEsteEnLaLista()
    {
        await using var host = _apoyo.HostConConector(MailRoot);
        using var cliente = Cliente(host);

        var respuesta = await AprobarAsync(cliente, UrlDeAutorizacion(NuevoPkce()), MailRoot, PasswordRoot);

        Assert.Null(CodigoDe(respuesta));
        Assert.EndsWith(
            EndpointsDeAutorizacion.MensajeDeCredencialesInvalidas,
            MensajeDeError(await respuesta.Content.ReadAsStringAsync()));
    }

    [Theory]
    [InlineData("sin-pkce")]
    [InlineData("pkce-plain")]
    [InlineData("redirect-no-registrada")]
    public async Task UnaSolicitudSinPkceConPkcePlainOConRedirectUriNoRegistradaSeRechazaSinMostrarLaPagina(string caso)
    {
        await using var host = _apoyo.HostConConector(MailRoot);
        using var cliente = Cliente(host);
        var pkce = NuevoPkce();
        var cambios = caso switch
        {
            "sin-pkce" => new Dictionary<string, string?> { ["code_challenge"] = null, ["code_challenge_method"] = null },
            "pkce-plain" => new Dictionary<string, string?> { ["code_challenge"] = pkce.Verificador, ["code_challenge_method"] = "plain" },
            _ => new Dictionary<string, string?> { ["redirect_uri"] = "https://atacante.test/callback" }
        };

        var respuesta = await cliente.GetAsync(UrlDeAutorizacion(pkce, cambios));

        Assert.Equal(HttpStatusCode.BadRequest, respuesta.StatusCode);
        Assert.Null(respuesta.Headers.Location);
        Assert.DoesNotContain("<form", await respuesta.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task UnaAprobacionSinTokenAntiforgeryNoRecibeCodigoYConElTokenSi()
    {
        var usuario = await _apoyo.SembrarUsuarioDeTenantAsync("conector-antiforgery");
        await using var host = _apoyo.HostConConector(usuario.Mail);
        using var cliente = Cliente(host);

        var sinToken = await AprobarAsync(
            cliente, UrlDeAutorizacion(NuevoPkce()), usuario.Mail, Password, conAntiforgery: false);
        var conToken = await AprobarAsync(cliente, UrlDeAutorizacion(NuevoPkce()), usuario.Mail, Password);

        Assert.Equal(HttpStatusCode.BadRequest, sinToken.StatusCode);
        Assert.Null(CodigoDe(sinToken));
        Assert.NotNull(CodigoDe(conToken));
    }

    [Fact]
    public async Task UnaAccionDeFormularioDesconocidaNoAutorizaAunqueLasCredencialesSeanValidas()
    {
        var usuario = await _apoyo.SembrarUsuarioDeTenantAsync("conector-accion");
        await using var host = _apoyo.HostConConector(usuario.Mail);
        using var cliente = Cliente(host);

        var pagina = await cliente.GetAsync(UrlDeAutorizacion(NuevoPkce()));
        var campos = CamposOcultos(await pagina.Content.ReadAsStringAsync());
        campos.AddRange([new("mail", usuario.Mail), new("password", Password), new("accion", "otra")]);

        var respuesta = await cliente.PostAsync("/connect/authorize", new FormUrlEncodedContent(campos));

        Assert.Equal(HttpStatusCode.OK, respuesta.StatusCode);
        Assert.Null(CodigoDe(respuesta));
    }

    [Fact]
    public async Task ElTokenMcpNoSirveEnLaApiYLaSesionDeWaysNoSirveEnMcp()
    {
        var usuario = await _apoyo.SembrarUsuarioDeTenantAsync("conector-aislamiento");
        await using var host = _apoyo.HostConConector(usuario.Mail);
        using var cliente = Cliente(host);
        var accessToken = Campo(await ObtenerTokensAsync(cliente, usuario.Mail), "access_token");

        using (var meConTokenMcp = new HttpRequestMessage(HttpMethod.Get, "/api/auth/me"))
        {
            meConTokenMcp.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);
            Assert.Equal(HttpStatusCode.Unauthorized, (await cliente.SendAsync(meConTokenMcp)).StatusCode);
        }

        var login = await cliente.PostAsJsonAsync(
            "/api/auth/login", new SolicitudDeLogin(usuario.Mail, Password, SolicitarBearer: true));
        var bearerDeWays = Campo(await login.Content.ReadFromJsonAsync<JsonElement>(), "token");
        using (var meConBearerDeWays = new HttpRequestMessage(HttpMethod.Get, "/api/auth/me"))
        {
            meConBearerDeWays.Headers.Authorization = new AuthenticationHeaderValue("Bearer", bearerDeWays);
            Assert.Equal(HttpStatusCode.OK, (await cliente.SendAsync(meConBearerDeWays)).StatusCode);
        }

        Assert.Equal(HttpStatusCode.Unauthorized, (await LlamarMcpAsync(cliente, bearerDeWays, "tools/list")).Estado);

        using var conCookie = Cliente(host);
        var loginConCookie = await conCookie.PostAsJsonAsync("/api/auth/login", new SolicitudDeLogin(usuario.Mail, Password));
        Assert.Equal(HttpStatusCode.OK, loginConCookie.StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await conCookie.GetAsync("/api/auth/me")).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await LlamarMcpAsync(conCookie, accessToken: null, "tools/list")).Estado);
    }

    [Fact]
    public async Task ElRefreshRotaYReusarElAnteriorDaInvalidGrantYCortaElAccessTokenDeLaCadena()
    {
        var usuario = await _apoyo.SembrarUsuarioDeTenantAsync("conector-refresh");
        await using var host = _apoyo.HostConConector(usuario.Mail, toleranciaDeReuso: 0);
        using var cliente = Cliente(host);
        var primeros = await ObtenerTokensAsync(cliente, usuario.Mail);
        var refreshAnterior = Campo(primeros, "refresh_token");

        var refresco = await RefrescarAsync(cliente, refreshAnterior);
        Assert.Equal(HttpStatusCode.OK, refresco.StatusCode);
        var nuevos = await refresco.Content.ReadFromJsonAsync<JsonElement>();
        var accessNuevo = Campo(nuevos, "access_token");
        Assert.NotEqual(Campo(primeros, "access_token"), accessNuevo);
        Assert.NotEqual(refreshAnterior, Campo(nuevos, "refresh_token"));
        Assert.Equal(HttpStatusCode.OK, (await LlamarMcpAsync(cliente, accessNuevo, "tools/list")).Estado);

        var reuso = await RefrescarAsync(cliente, refreshAnterior);

        Assert.Equal(HttpStatusCode.BadRequest, reuso.StatusCode);
        Assert.Equal("invalid_grant", await ErrorOAuthAsync(reuso));
        Assert.Equal(HttpStatusCode.Unauthorized, (await LlamarMcpAsync(cliente, accessNuevo, "tools/list")).Estado);
    }

    [Fact]
    public async Task SinConfigurarLaToleranciaReusarEnseguidaElRefreshAnteriorTodaviaFunciona()
    {
        var usuario = await _apoyo.SembrarUsuarioDeTenantAsync("conector-tolerancia");
        await using var host = _apoyo.HostConConector(usuario.Mail, toleranciaDeReuso: null);
        using var cliente = Cliente(host);
        var refreshAnterior = Campo(await ObtenerTokensAsync(cliente, usuario.Mail), "refresh_token");

        Assert.Equal(HttpStatusCode.OK, (await RefrescarAsync(cliente, refreshAnterior)).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await RefrescarAsync(cliente, refreshAnterior)).StatusCode);
    }

    [Fact]
    public async Task RevocarLaAutorizacionDejaSinEfectoSusAccessTokens()
    {
        var usuario = await _apoyo.SembrarUsuarioDeTenantAsync("conector-revocacion");
        await using var host = _apoyo.HostConConector(usuario.Mail);
        using var cliente = Cliente(host);
        var accessToken = Campo(await ObtenerTokensAsync(cliente, usuario.Mail), "access_token");
        Assert.Equal(HttpStatusCode.OK, (await LlamarMcpAsync(cliente, accessToken, "tools/list")).Estado);

        await using (var alcance = host.Services.CreateAsyncScope())
        {
            var autorizaciones = alcance.ServiceProvider.GetRequiredService<IOpenIddictAuthorizationManager>();
            var revocadas = 0;
            await foreach (var autorizacion in autorizaciones.FindBySubjectAsync(
                usuario.IdUsuario.ToString(CultureInfo.InvariantCulture)))
            {
                Assert.True(await autorizaciones.TryRevokeAsync(autorizacion));
                revocadas++;
            }

            Assert.Equal(1, revocadas);
        }

        Assert.Equal(HttpStatusCode.Unauthorized, (await LlamarMcpAsync(cliente, accessToken, "tools/list")).Estado);
    }

    [Fact]
    public async Task UnUsuarioDesactivadoDespuesDeAutorizarRecibe401EnMcpConElMotivoEnElLog()
    {
        var usuario = await _apoyo.SembrarUsuarioDeTenantAsync("conector-desactivado");
        var captura = new CapturaDeLogs();
        await using var host = _apoyo.HostConConector(usuario.Mail, configurarMas: ConCaptura(captura));
        using var cliente = Cliente(host);
        var accessToken = Campo(await ObtenerTokensAsync(cliente, usuario.Mail), "access_token");
        Assert.Equal(HttpStatusCode.OK, (await LlamarMcpAsync(cliente, accessToken, "tools/list")).Estado);

        await DesactivarAsync(usuario.IdUsuario);

        Assert.Equal(HttpStatusCode.Unauthorized, (await LlamarMcpAsync(cliente, accessToken, "tools/list")).Estado);
        Assert.Contains(LineasDeDiagnostico(captura), l => l.Contains("POST /mcp -> 401") && l.Contains("Motivo=sesión revocada"));
    }

    [Fact]
    public async Task UnUsuarioDesactivadoNoPuedeRefrescarElToken()
    {
        var usuario = await _apoyo.SembrarUsuarioDeTenantAsync("conector-desactivado-refresh");
        await using var host = _apoyo.HostConConector(usuario.Mail);
        using var cliente = Cliente(host);
        var refreshToken = Campo(await ObtenerTokensAsync(cliente, usuario.Mail), "refresh_token");

        await DesactivarAsync(usuario.IdUsuario);
        var refresco = await RefrescarAsync(cliente, refreshToken);

        Assert.Equal(HttpStatusCode.BadRequest, refresco.StatusCode);
        Assert.Equal("invalid_grant", await ErrorOAuthAsync(refresco));
    }

    [Fact]
    public async Task SiElMailDejaDeEstarEnLaListaAntesDelCanjeElCanjeDaInvalidGrant()
    {
        var usuario = await _apoyo.SembrarUsuarioDeTenantAsync("conector-recheck");
        await using var host = _apoyo.HostConConector(usuario.Mail);
        using var cliente = Cliente(host);
        var pkce = NuevoPkce();
        var codigo = CodigoDe(await AprobarAsync(cliente, UrlDeAutorizacion(pkce), usuario.Mail, Password));
        Assert.NotNull(codigo);

        await using (var db = fixture.CrearContextoDeAplicacion(TenantActualFijo.Plataforma))
        {
            var fila = await db.Usuarios.SingleAsync(u => u.Id == usuario.IdUsuario);
            fila.Mail = $"fuera-{fila.Mail}";
            await db.SaveChangesAsync();
        }

        var canje = await CanjearAsync(cliente, codigo, pkce);

        Assert.Equal(HttpStatusCode.BadRequest, canje.StatusCode);
        Assert.Equal("invalid_grant", await ErrorOAuthAsync(canje));
    }

    /// <summary>Todo token nace con el alcance del conector, pero un refresh que manda <c>scope</c>
    /// hace que OpenIddict acote el access token nuevo: ese es el único camino a un token sin
    /// <c>ways.mcp</c>.</summary>
    [Fact]
    public async Task UnTokenAcotadoSinElAlcanceWaysMcpNoSirveEnMcpYElLogDiceElMotivo()
    {
        var usuario = await _apoyo.SembrarUsuarioDeTenantAsync("conector-alcance");
        var captura = new CapturaDeLogs();
        await using var host = _apoyo.HostConConector(usuario.Mail, configurarMas: ConCaptura(captura));
        using var cliente = Cliente(host);
        var tokens = await ObtenerTokensAsync(cliente, usuario.Mail);

        var refresco = await RefrescarAsync(cliente, Campo(tokens, "refresh_token"), alcances: "offline_access");
        Assert.Equal(HttpStatusCode.OK, refresco.StatusCode);
        var acotado = await refresco.Content.ReadFromJsonAsync<JsonElement>();

        Assert.Equal(HttpStatusCode.Unauthorized, (await LlamarMcpAsync(cliente, Campo(acotado, "access_token"), "tools/list")).Estado);
        Assert.Equal(HttpStatusCode.OK, (await LlamarMcpAsync(cliente, Campo(tokens, "access_token"), "tools/list")).Estado);
        Assert.Contains(LineasDeDiagnostico(captura), l => l.Contains("POST /mcp -> 401") && l.Contains("Motivo=token sin el alcance ways.mcp"));
    }

    /// <summary>Sin URL pública (Development) se registran los recursos de las dos URLs locales: un
    /// token pedido para la segunda tiene el mismo emisor, así que solo el chequeo de audiencia del
    /// conector lo puede rechazar en la primera.</summary>
    [Fact]
    public async Task UnTokenEmitidoParaOtroRecursoRegistradoNoSirveEnEsteMcpYElLogDiceElMotivo()
    {
        const string RecursoLocal = "http://localhost:5080/mcp";
        const string OtroRecurso = "http://localhost:5081/mcp";
        var usuario = await _apoyo.SembrarUsuarioDeTenantAsync("conector-audiencia");
        var captura = new CapturaDeLogs();
        await using var host = fixture.WithWebHostBuilder(builder =>
        {
            builder.UseSetting("Mcp:Habilitado", "true");
            builder.UseSetting("Mcp:MailsHabilitados", usuario.Mail);
            builder.UseSetting("urls", "http://localhost:5080;http://localhost:5081");
            ConCaptura(captura)(builder);
        });
        using var cliente = host.CreateClient(new WebApplicationFactoryClientOptions
        {
            BaseAddress = new Uri("http://localhost:5080"),
            AllowAutoRedirect = false
        });

        var paraOtro = Campo(await ObtenerTokensAsync(cliente, usuario.Mail, OtroRecurso), "access_token");
        var paraEste = Campo(await ObtenerTokensAsync(cliente, usuario.Mail, RecursoLocal), "access_token");

        Assert.Equal(HttpStatusCode.Unauthorized, (await LlamarMcpAsync(cliente, paraOtro, "tools/list")).Estado);
        Assert.Equal(HttpStatusCode.OK, (await LlamarMcpAsync(cliente, paraEste, "tools/list")).Estado);
        Assert.Contains(LineasDeDiagnostico(captura), l => l.Contains("POST /mcp -> 401") && l.Contains("Motivo=token emitido para otro recurso"));
    }

    /// <summary>Con el conector activo, un endpoint JSON existente sigue rechazando un Content-Type que no es
    /// JSON con 415 y el código estable, con las mismas aserciones que <see cref="SolicitudesMalFormadasTests"/>:
    /// la política de ruteo por Content-Type que quita <c>Program.cs</c> sigue ausente. Si estuviera, el
    /// endpoint se descartaría y la ruta de respaldo respondería 404.</summary>
    [Fact]
    public async Task ConElConectorActivoUnContentTypeQueNoEsJsonEnUnEndpointExistenteSigueDando415()
    {
        await using var host = _apoyo.HostConConector(MailRoot);
        using var cliente = Cliente(host);

        foreach (var contentType in new[] { "text/plain", "application/x-www-form-urlencoded", null })
        {
            var respuesta = await cliente.PostAsync(
                "/api/auth/login",
                SolicitudesMalFormadasTests.CuerpoCon(SolicitudesMalFormadasTests.LoginDeRoot(), contentType));

            Assert.Equal(HttpStatusCode.UnsupportedMediaType, respuesta.StatusCode);
            Assert.Equal("application/problem+json", respuesta.Content.Headers.ContentType?.MediaType);
            Assert.Equal("solicitud_invalida", await SolicitudesMalFormadasTests.CodigoSinDetallesInternosAsync(respuesta));
        }
    }

    /// <summary>El POST a <c>/mcp</c> no tiene binding que rechace el tipo de contenido: con un token válido y
    /// un cuerpo JSON-RPC correcto, un Content-Type que no es JSON da el mismo 415 y código que el binding de
    /// los demás endpoints. Sin token sigue dando 401, como en <see cref="SolicitudesMalFormadasTests"/>.</summary>
    [Fact]
    public async Task UnPostAMcpConUnContentTypeQueNoEsJsonDa415SolicitudInvalidaYSinTokenDa401()
    {
        var usuario = await _apoyo.SembrarUsuarioDeTenantAsync("conector-415");
        await using var host = _apoyo.HostConConector(usuario.Mail);
        using var cliente = Cliente(host);
        var accessToken = Campo(await ObtenerTokensAsync(cliente, usuario.Mail), "access_token");

        async Task<HttpResponseMessage> PostearAsync(string? token, string? contentType)
        {
            using var pedido = new HttpRequestMessage(HttpMethod.Post, "/mcp")
            {
                Content = SolicitudesMalFormadasTests.CuerpoCon(
                    "{\"jsonrpc\":\"2.0\",\"id\":1,\"method\":\"tools/list\"}", contentType)
            };
            pedido.Headers.Accept.ParseAdd("application/json");
            pedido.Headers.Accept.ParseAdd("text/event-stream");
            if (token is not null)
            {
                pedido.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
            }

            return await cliente.SendAsync(pedido);
        }

        foreach (var contentType in new[] { "text/plain", "application/x-www-form-urlencoded", null })
        {
            var respuesta = await PostearAsync(accessToken, contentType);

            Assert.Equal(HttpStatusCode.UnsupportedMediaType, respuesta.StatusCode);
            Assert.Equal("application/problem+json", respuesta.Content.Headers.ContentType?.MediaType);
            Assert.Equal("solicitud_invalida", await SolicitudesMalFormadasTests.CodigoSinDetallesInternosAsync(respuesta));
        }

        Assert.Equal(HttpStatusCode.Unauthorized, (await PostearAsync(null, "text/plain")).StatusCode);
    }

    /// <summary>Los mismos casos que <see cref="RutaDeRespaldoTests"/>, con el conector activo y el index de la
    /// SPA presente (sin él, la ruta de respaldo responde 404 también fuera de <c>/api</c>).</summary>
    [Fact]
    public async Task ConElConectorActivoUnaRutaApiInexistenteSigueDando404AunqueLaSpaEsteServida()
    {
        var raizWeb = Directory.CreateTempSubdirectory("ways-conector-mcp-");
        try
        {
            await File.WriteAllTextAsync(Path.Combine(raizWeb.FullName, "index.html"), "<!doctype html><title>spa</title>");
            await using var host = _apoyo.HostConConector(MailRoot, configurarMas: builder => builder.UseWebRoot(raizWeb.FullName));
            using var cliente = Cliente(host);

            Assert.Equal(HttpStatusCode.OK, (await cliente.GetAsync("/ventas/123")).StatusCode);

            foreach (var (metodo, contentType) in new (string Metodo, string? ContentType)[]
                     { ("GET", null), ("POST", "application/json"), ("POST", "text/plain") })
            {
                using var solicitud = new HttpRequestMessage(new HttpMethod(metodo), "/api/no-existe");
                if (contentType is not null)
                {
                    solicitud.Content = SolicitudesMalFormadasTests.CuerpoCon("{}", contentType);
                }

                Assert.Equal(HttpStatusCode.NotFound, (await cliente.SendAsync(solicitud)).StatusCode);
            }
        }
        finally
        {
            raizWeb.Delete(recursive: true);
        }
    }

    [Fact]
    public async Task ConLaSpaServidaGetYDeleteEnMcpDan405YUnaRutaWellKnownDesconocidaDa404()
    {
        var raizWeb = Directory.CreateTempSubdirectory("ways-conector-mcp-");
        try
        {
            await File.WriteAllTextAsync(Path.Combine(raizWeb.FullName, "index.html"), "<!doctype html><title>spa</title>");
            var usuario = await _apoyo.SembrarUsuarioDeTenantAsync("conector-spa");
            await using var host = _apoyo.HostConConector(
                usuario.Mail, configurarMas: builder => builder.UseSetting("webroot", raizWeb.FullName));
            using var cliente = Cliente(host);

            Assert.Equal(HttpStatusCode.OK, (await cliente.GetAsync("/una-pantalla-de-la-spa")).StatusCode);
            Assert.Equal(HttpStatusCode.NotFound, (await cliente.GetAsync("/.well-known/no-existe")).StatusCode);
            Assert.Equal(HttpStatusCode.Unauthorized, (await cliente.GetAsync("/mcp")).StatusCode);

            var accessToken = Campo(await ObtenerTokensAsync(cliente, usuario.Mail), "access_token");
            foreach (var metodo in new[] { HttpMethod.Get, HttpMethod.Delete })
            {
                using var solicitud = new HttpRequestMessage(metodo, "/mcp");
                solicitud.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);
                var respuesta = await cliente.SendAsync(solicitud);

                Assert.Equal(HttpStatusCode.MethodNotAllowed, respuesta.StatusCode);
                Assert.Equal("POST", Assert.Single(respuesta.Content.Headers.Allow));
            }
        }
        finally
        {
            raizWeb.Delete(recursive: true);
        }
    }
}
