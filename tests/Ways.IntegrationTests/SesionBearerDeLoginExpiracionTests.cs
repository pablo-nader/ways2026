using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Mvc.Testing;
using Ways.Application.Organizacion;
using Ways.Application.Usuarios;
using Ways.Domain.Organizacion;

namespace Ways.IntegrationTests;

/// <summary>
/// El bearer de <c>POST /api/auth/login</c> con <c>SolicitarBearer=true</c> vence FIJO a los 15
/// minutos (<c>AuthEndpoints.VigenciaDelBearerDeLogin</c>), y <c>ManejadorBearerDeSesion</c> hace
/// cumplir ese <c>ExpiresUtc</c> contra el reloj del esquema.
///
/// Clase propia por el mismo motivo que <see cref="SesionBearerDeDispositivoExpiracionTests"/>:
/// <c>WaysApiFixture.ConRelojDeAutenticacionEnElHost</c> solo tiene efecto si se instala antes de
/// la primera request del host.
/// </summary>
[Collection("Ways.IntegrationTests secuencial")]
public class SesionBearerDeLoginExpiracionTests(WaysApiFixture fixture) : IClassFixture<WaysApiFixture>
{
    private sealed class RelojControlable(DateTimeOffset inicial) : TimeProvider
    {
        private DateTimeOffset _ahora = inicial;
        public override DateTimeOffset GetUtcNow() => _ahora;
        public void Avanzar(TimeSpan delta) => _ahora = _ahora.Add(delta);
    }

    private static HttpRequestMessage RequestConBearer(string ruta, string token)
    {
        var request = new HttpRequestMessage(HttpMethod.Get, ruta);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        return request;
    }

    [Fact]
    public async Task ElBearerDelLoginPorMailSigueVigenteALos14MinutosYVenceALos16()
    {
        var reloj = new RelojControlable(DateTimeOffset.UtcNow);
        using var _reloj = fixture.ConRelojDeAutenticacionEnElHost(reloj);

        using var root = fixture.CreateClient();
        var loginRoot = await root.PostAsJsonAsync("/api/auth/login", new SolicitudDeLogin("test@test.com", "root"));
        Assert.Equal(HttpStatusCode.OK, loginRoot.StatusCode);

        var solicitud = new SolicitudDeAprovisionamiento(
            NombreTenant: nameof(ElBearerDelLoginPorMailSigueVigenteALos14MinutosYVenceALos16),
            RazonSocialEmpresa: "Empresa de prueba",
            NombrePuntoVenta: "Local 1",
            MailAdmin: "expiracion-login-bearer-admin@ways.test",
            Modo: ModoPuntoVenta.Escritorio);
        var alta = await root.PostAsJsonAsync("/api/plataforma/tenants", solicitud);
        Assert.Equal(HttpStatusCode.Created, alta.StatusCode);
        var resultado = (await alta.Content.ReadFromJsonAsync<ResultadoAprovisionamiento>())!;

        using var admin = fixture.CreateClient(new WebApplicationFactoryClientOptions { HandleCookies = false });
        var login = await admin.PostAsJsonAsync(
            "/api/auth/login",
            new SolicitudDeLogin(solicitud.MailAdmin, resultado.PasswordTemporal, SolicitarBearer: true));
        Assert.Equal(HttpStatusCode.OK, login.StatusCode);
        var token = (await login.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("token").GetString()!;

        // /api/puntos-venta y no /api/auth/me: el endpoint no repite ningún chequeo de sesión
        // propio, así que el 401 solo puede venir del esquema bearer.
        reloj.Avanzar(TimeSpan.FromMinutes(14));
        var antesDeVencer = await admin.SendAsync(RequestConBearer("/api/puntos-venta", token));
        Assert.Equal(HttpStatusCode.OK, antesDeVencer.StatusCode);

        reloj.Avanzar(TimeSpan.FromMinutes(2));
        var despuesDeVencer = await admin.SendAsync(RequestConBearer("/api/puntos-venta", token));
        Assert.Equal(HttpStatusCode.Unauthorized, despuesDeVencer.StatusCode);
    }
}
