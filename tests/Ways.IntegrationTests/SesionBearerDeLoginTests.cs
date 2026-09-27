using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Mvc.Testing;
using Ways.Application.Dispositivos;
using Ways.Application.Organizacion;
using Ways.Application.Usuarios;
using Ways.Domain.Organizacion;

namespace Ways.IntegrationTests;

/// <summary>
/// <c>POST /api/auth/login</c> con <c>SolicitarBearer=true</c>: la pantalla de vinculación del POS
/// de escritorio corre en otro origen que la API y no recibe cookies, así que el Admin que vincula
/// el equipo necesita un token bearer. Sin el flag, el login por mail no cambia.
/// Clientes con <c>HandleCookies = false</c> en toda la suite: cualquier autenticación que se vea
/// acá viene del header <c>Authorization</c>, nunca de un cookie jar.
/// </summary>
[Collection("Ways.IntegrationTests secuencial")]
public class SesionBearerDeLoginTests(WaysApiFixture fixture) : IClassFixture<WaysApiFixture>
{
    private static readonly string[] CamposDeUsuarioAutenticado =
        ["id", "idTenant", "mail", "rol", "rolId", "ultimaConexion", "usuario"];

    private HttpClient ClienteSinCookies() =>
        fixture.CreateClient(new WebApplicationFactoryClientOptions { HandleCookies = false });

    private async Task<(string MailAdmin, string PasswordAdmin, int IdPuntoVenta)> AprovisionarTenantAsync(string nombre)
    {
        using var root = fixture.CreateClient();
        var loginRoot = await root.PostAsJsonAsync("/api/auth/login", new SolicitudDeLogin("test@test.com", "root"));
        Assert.Equal(HttpStatusCode.OK, loginRoot.StatusCode);

        var solicitud = new SolicitudDeAprovisionamiento(
            NombreTenant: nombre,
            RazonSocialEmpresa: $"Empresa {nombre}",
            NombrePuntoVenta: "Local 1",
            MailAdmin: $"{nombre.ToLowerInvariant()}-admin@ways.test",
            Modo: ModoPuntoVenta.Escritorio);

        var alta = await root.PostAsJsonAsync("/api/plataforma/tenants", solicitud);
        Assert.Equal(HttpStatusCode.Created, alta.StatusCode);
        var resultado = (await alta.Content.ReadFromJsonAsync<ResultadoAprovisionamiento>())!;

        return (solicitud.MailAdmin, resultado.PasswordTemporal, resultado.IdPuntoVenta);
    }

    private static bool TraeCookieDeSesion(HttpResponseMessage respuesta) =>
        respuesta.Headers.TryGetValues("Set-Cookie", out var valores) &&
        valores.Any(v => v.StartsWith("ways.sesion=", StringComparison.Ordinal));

    private static HttpRequestMessage RequestConBearer(HttpMethod metodo, string ruta, string token, object? cuerpo = null)
    {
        var request = new HttpRequestMessage(metodo, ruta);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        if (cuerpo is not null)
        {
            request.Content = JsonContent.Create(cuerpo);
        }

        return request;
    }

    [Fact]
    public async Task SinSolicitarBearerElLoginDevuelveSoloElUsuarioYLaCookieDeSesion()
    {
        var (mail, password, _) = await AprovisionarTenantAsync(nameof(SinSolicitarBearerElLoginDevuelveSoloElUsuarioYLaCookieDeSesion));
        using var cliente = ClienteSinCookies();

        var respuesta = await cliente.PostAsJsonAsync("/api/auth/login", new SolicitudDeLogin(mail, password));

        Assert.Equal(HttpStatusCode.OK, respuesta.StatusCode);
        Assert.True(TraeCookieDeSesion(respuesta));
        var cuerpo = await respuesta.Content.ReadFromJsonAsync<JsonElement>();
        var campos = cuerpo.EnumerateObject().Select(p => p.Name).Order(StringComparer.Ordinal).ToArray();
        Assert.Equal(CamposDeUsuarioAutenticado, campos);
        Assert.Equal(mail, cuerpo.GetProperty("mail").GetString());
    }

    /// <summary>Prueba la rama de <c>SolicitarBearer</c> que no firma la cookie: sin
    /// <c>Set-Cookie</c> de <c>ways.sesion</c> en la respuesta.</summary>
    [Fact]
    public async Task ConSolicitarBearerElLoginDevuelveUnTokenDe15MinutosYNoEmiteCookieDeSesion()
    {
        var (mail, password, _) = await AprovisionarTenantAsync(nameof(ConSolicitarBearerElLoginDevuelveUnTokenDe15MinutosYNoEmiteCookieDeSesion));
        using var cliente = ClienteSinCookies();
        var antes = DateTimeOffset.UtcNow;

        var respuesta = await cliente.PostAsJsonAsync(
            "/api/auth/login", new SolicitudDeLogin(mail, password, SolicitarBearer: true));

        Assert.Equal(HttpStatusCode.OK, respuesta.StatusCode);
        Assert.False(TraeCookieDeSesion(respuesta));
        var cuerpo = await respuesta.Content.ReadFromJsonAsync<JsonElement>();
        Assert.False(string.IsNullOrEmpty(cuerpo.GetProperty("token").GetString()));
        Assert.Equal(mail, cuerpo.GetProperty("usuario").GetProperty("mail").GetString());
        var expiraEl = cuerpo.GetProperty("expiraEl").GetDateTimeOffset();
        Assert.InRange(expiraEl, antes.AddMinutes(15), DateTimeOffset.UtcNow.AddMinutes(15));
    }

    /// <summary>El bearer del Admin (sin claim de dispositivo) autentica los dos endpoints que
    /// la vinculación necesita: listar puntos de venta y dar de alta el dispositivo.</summary>
    [Fact]
    public async Task ElBearerDelLoginPorMailAutenticaListarPuntosDeVentaYVincularUnDispositivo()
    {
        var (mail, password, idPuntoVenta) = await AprovisionarTenantAsync(nameof(ElBearerDelLoginPorMailAutenticaListarPuntosDeVentaYVincularUnDispositivo));
        using var cliente = ClienteSinCookies();

        var login = await cliente.PostAsJsonAsync(
            "/api/auth/login", new SolicitudDeLogin(mail, password, SolicitarBearer: true));
        Assert.Equal(HttpStatusCode.OK, login.StatusCode);
        var token = (await login.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("token").GetString()!;

        var sinToken = await cliente.GetAsync("/api/puntos-venta");
        Assert.Equal(HttpStatusCode.Unauthorized, sinToken.StatusCode);

        var puntosVenta = await cliente.SendAsync(RequestConBearer(HttpMethod.Get, "/api/puntos-venta", token));
        Assert.Equal(HttpStatusCode.OK, puntosVenta.StatusCode);
        var ids = (await puntosVenta.Content.ReadFromJsonAsync<JsonElement>())
            .EnumerateArray().Select(p => p.GetProperty("id").GetInt32()).ToArray();
        Assert.Equal([idPuntoVenta], ids);

        var alta = await cliente.SendAsync(RequestConBearer(
            HttpMethod.Post, "/api/dispositivos", token, new AltaDispositivo(idPuntoVenta, "Caja 1")));
        Assert.Equal(HttpStatusCode.Created, alta.StatusCode);
        var vinculado = (await alta.Content.ReadFromJsonAsync<DispositivoVinculado>())!;
        Assert.Equal(idPuntoVenta, vinculado.Datos.IdPuntoVenta);
        Assert.False(string.IsNullOrEmpty(vinculado.Secreto));
    }

    [Fact]
    public async Task ConSolicitarBearerUnaPasswordIncorrectaFallaIgualQueSinElFlag()
    {
        var (mail, _, _) = await AprovisionarTenantAsync(nameof(ConSolicitarBearerUnaPasswordIncorrectaFallaIgualQueSinElFlag));
        using var cliente = ClienteSinCookies();

        var sinFlag = await cliente.PostAsJsonAsync("/api/auth/login", new SolicitudDeLogin(mail, "incorrecta"));
        var conFlag = await cliente.PostAsJsonAsync(
            "/api/auth/login", new SolicitudDeLogin(mail, "incorrecta", SolicitarBearer: true));

        Assert.Equal(HttpStatusCode.Unauthorized, sinFlag.StatusCode);
        Assert.Equal(sinFlag.StatusCode, conFlag.StatusCode);
        var cuerpoSinFlag = await sinFlag.Content.ReadFromJsonAsync<JsonElement>();
        var cuerpoConFlag = await conFlag.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("credenciales_invalidas", cuerpoConFlag.GetProperty("codigo").GetString());
        Assert.Equal(cuerpoSinFlag.GetProperty("codigo").GetString(), cuerpoConFlag.GetProperty("codigo").GetString());
        Assert.Equal(cuerpoSinFlag.GetProperty("title").GetString(), cuerpoConFlag.GetProperty("title").GetString());
        Assert.False(cuerpoConFlag.TryGetProperty("token", out _));
        Assert.False(TraeCookieDeSesion(conFlag));
    }
}
