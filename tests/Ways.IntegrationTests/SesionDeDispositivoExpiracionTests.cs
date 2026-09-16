using System.Net;
using System.Net.Http.Json;
using Microsoft.AspNetCore.Mvc.Testing;
using Ways.Api.Seguridad;
using Ways.Application.Dispositivos;
using Ways.Application.Organizacion;
using Ways.Application.Usuarios;
using Ways.Domain.Usuarios;
using Ways.Infrastructure.Multitenancy;
using Ways.Infrastructure.Seguridad;

namespace Ways.IntegrationTests;

/// <summary>
/// stage-desktop-pos: prueba que la expiración deslizante de <c>ways.sesion</c> conserva el
/// SPAN propio del ticket de una sesión de dispositivo (365 días fijados en
/// <c>AuthEndpoints</c>, <c>ExpiresUtc = reloj.Ahora.AddDays(365)</c>) en vez de resetearlo al
/// <c>ExpireTimeSpan</c> global de 1 hora (<c>Program.cs</c>) en cada refresh.
///
/// Hallazgo (lectura de <c>CookieAuthenticationHandler</c>, .NET): <c>RequestRefresh</c> —el
/// método interno que dispara un refresh deslizante— recalcula
/// <c>newTicket.Properties.ExpiresUtc = currentUtc + (expiresUtc_original - issuedUtc_original)</c>,
/// es decir el SPAN del ticket original, nunca <c>Options.ExpireTimeSpan</c>. Ese span solo se
/// deriva del <c>ExpireTimeSpan</c> global cuando <c>AuthenticationProperties.ExpiresUtc</c>
/// llega SIN setear al <c>SignInAsync</c> (que es el camino de <c>/login</c> normal, 1h). Como
/// <c>/login-dispositivo</c> sí lo setea explícito a 365 días, el span de 365 días persiste
/// indefinidamente a través de refreshes sucesivos — NINGÚN cambio de código hizo falta en
/// <c>OnValidatePrincipal</c>/<c>Program.cs</c> para esto.
///
/// La prueba no puede esperar 200 días de verdad: reemplaza el <see cref="TimeProvider"/> que
/// usa el <c>CookieAuthenticationHandler</c> (opción <c>TimeProviderDeAutenticacionDelHost</c> de
/// <see cref="WaysApiFixture"/>) por uno controlable, fuerza un refresh a mitad de camino y
/// confirma que la sesión sigue viva mucho después de lo que el default de 1h permitiría — si
/// alguna vez el span se resetea al global, esta prueba pasa a fallar con 401 en el paso final.
/// </summary>
[Collection("Ways.IntegrationTests secuencial")]
public class SesionDeDispositivoExpiracionTests(WaysApiFixture fixture) : IClassFixture<WaysApiFixture>
{
    private const string PasswordRoot = "root";
    private const string MailRoot = "test@test.com";
    private const string PasswordCajero = "una-contraseña-de-cajero";

    private sealed class RelojControlable(DateTimeOffset inicial) : TimeProvider
    {
        private DateTimeOffset _ahora = inicial;
        public override DateTimeOffset GetUtcNow() => _ahora;
        public void Avanzar(TimeSpan delta) => _ahora = _ahora.Add(delta);
    }

    [Fact]
    public async Task LaSesionDeUnCajeroSigueVigenteMuchoMasAlladeUnaHoraTrasElRefreshDeslizante()
    {
        var reloj = new RelojControlable(DateTimeOffset.UtcNow);

        // El reloj se instala ANTES de la primera request del host (primer CreateClient real más
        // abajo, dentro de los helpers): así la primera resolución de CookieAuthenticationOptions
        // ya lo toma, sin depender de si IOptionsMonitor cachea la instancia entre requests.
        using var _reloj = fixture.ConRelojDeAutenticacionEnElHost(reloj);

        using var root = fixture.CreateClient();
        var loginRoot = await root.PostAsJsonAsync("/api/auth/login", new SolicitudDeLogin(MailRoot, PasswordRoot));
        Assert.Equal(HttpStatusCode.OK, loginRoot.StatusCode);

        var solicitud = new SolicitudDeAprovisionamiento(
            NombreTenant: nameof(LaSesionDeUnCajeroSigueVigenteMuchoMasAlladeUnaHoraTrasElRefreshDeslizante),
            RazonSocialEmpresa: "Empresa de prueba",
            NombrePuntoVenta: "Local 1",
            MailAdmin: "expiracion-admin@ways.test");
        var alta = await root.PostAsJsonAsync("/api/plataforma/tenants", solicitud);
        Assert.Equal(HttpStatusCode.Created, alta.StatusCode);
        var resultado = (await alta.Content.ReadFromJsonAsync<ResultadoAprovisionamiento>())!;

        using var admin = fixture.CreateClient();
        var loginAdmin = await admin.PostAsJsonAsync(
            "/api/auth/login", new SolicitudDeLogin(solicitud.MailAdmin, resultado.PasswordTemporal));
        Assert.Equal(HttpStatusCode.OK, loginAdmin.StatusCode);

        var altaDispositivo = await admin.PostAsJsonAsync(
            "/api/dispositivos", new AltaDispositivo(resultado.IdPuntoVenta, "Caja 1"));
        Assert.Equal(HttpStatusCode.Created, altaDispositivo.StatusCode);
        var cookieDispositivo = ExtraerCookieDeDispositivo(altaDispositivo);

        var hasheador = new HasheadorPbkdf2();
        await using (var db = fixture.CrearContextoDeAplicacion(TenantActualFijo.Plataforma))
        {
            var ahora = DateTimeOffset.UtcNow;
            db.Usuarios.Add(new Usuario
            {
                IdTenant = resultado.IdTenant,
                NombreUsuario = "cajero1",
                Mail = "cajero1-expiracion@ways.test",
                RolId = (int)RolConocido.Vendedor,
                PasswordHash = hasheador.Hashear(PasswordCajero),
                PasswordAlgoritmo = hasheador.Algoritmo,
                PasswordActualizadoEl = ahora,
                CreatedAt = ahora,
                UpdatedAt = ahora
            });
            await db.SaveChangesAsync();
        }

        // HandleCookies = false a propósito, y CADA request arma su propio header "Cookie" a
        // mano (nunca un default a nivel cliente): un default de cliente MÁS un header puesto a
        // mano en el mensaje terminaría mandando dos líneas "Cookie:" — evita cualquier ambigüedad
        // sobre cómo las combina el servidor.
        using var cajero = fixture.CreateClient(new WebApplicationFactoryClientOptions { HandleCookies = false });

        using var loginRequest = new HttpRequestMessage(HttpMethod.Post, "/api/auth/login-dispositivo")
        {
            Content = JsonContent.Create(new SolicitudDeLoginDeDispositivo("cajero1", PasswordCajero))
        };
        loginRequest.Headers.Add("Cookie", $"{CookiesWays.Dispositivo}={cookieDispositivo}");
        var loginCajero = await cajero.SendAsync(loginRequest);
        Assert.Equal(HttpStatusCode.OK, loginCajero.StatusCode);

        var cookieSesion = ExtraerCookie(loginCajero, "ways.sesion")
            ?? throw new InvalidOperationException("El login de dispositivo no emitió ways.sesion.");

        // 1) Avanza 200 días (pasó la mitad de la ventana de 365 días: dispara el refresh
        // deslizante en el próximo request) y confirma que la sesión sigue viva.
        reloj.Avanzar(TimeSpan.FromDays(200));

        using var request1 = NuevoRequestConCookieDeSesion(cookieSesion);
        var respuesta1 = await cajero.SendAsync(request1);
        Assert.Equal(HttpStatusCode.OK, respuesta1.StatusCode);

        // El refresh deslizante reemite la cookie con un IssuedUtc nuevo (t=200d) — si el span
        // se hubiese reseteado al ExpireTimeSpan global (1h), la sesión ya habría vencido a las
        // 2 horas de acá en más. Si el span propio del ticket (365 días) se conserva, sigue
        // vigente por casi un año más.
        var cookieSesionRefrescada = ExtraerCookie(respuesta1, "ways.sesion") ?? cookieSesion;

        // 2) Avanza 2 horas más (t=200d+2h): más de 1h desde el refresh de arriba, pero muy
        // lejos de los 365 días del span propio.
        reloj.Avanzar(TimeSpan.FromHours(2));

        using var request2 = NuevoRequestConCookieDeSesion(cookieSesionRefrescada);
        var respuesta2 = await cajero.SendAsync(request2);

        Assert.Equal(HttpStatusCode.OK, respuesta2.StatusCode);
    }

    private static HttpRequestMessage NuevoRequestConCookieDeSesion(string cookieSesion)
    {
        var request = new HttpRequestMessage(HttpMethod.Get, "/api/auth/me");
        request.Headers.Add("Cookie", $"ways.sesion={cookieSesion}");
        return request;
    }

    private static string ExtraerCookieDeDispositivo(HttpResponseMessage respuesta) =>
        ExtraerCookie(respuesta, CookiesWays.Dispositivo)
            ?? throw new InvalidOperationException("La respuesta no trajo la cookie de dispositivo.");

    private static string? ExtraerCookie(HttpResponseMessage respuesta, string nombre)
    {
        if (!respuesta.Headers.TryGetValues("Set-Cookie", out var valores))
        {
            return null;
        }

        var prefijo = $"{nombre}=";
        var setCookie = valores.FirstOrDefault(v => v.StartsWith(prefijo, StringComparison.Ordinal));
        if (setCookie is null)
        {
            return null;
        }

        var valor = setCookie[prefijo.Length..];
        return valor[..valor.IndexOf(';')];
    }
}
