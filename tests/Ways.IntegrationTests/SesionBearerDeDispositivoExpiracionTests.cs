using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Ways.Application.Dispositivos;
using Ways.Application.Organizacion;
using Ways.Application.Usuarios;
using Ways.Domain.Organizacion;
using Ways.Domain.Usuarios;
using Ways.Infrastructure.Multitenancy;
using Ways.Infrastructure.Seguridad;

namespace Ways.IntegrationTests;

/// <summary>
/// stage-desktop-pos, slice bearer: el token bearer vence FIJO a los 365 días
/// (<c>AuthEndpoints</c>), SIN el refresh deslizante que sí tiene la cookie de sesión
/// (<see cref="SesionDeDispositivoExpiracionTests"/>) — adelantar el reloj más allá de los 365
/// días tiene que cortar la sesión, porque ningún mecanismo extiende el vencimiento del lado
/// del bearer (a diferencia de la cookie, que se refresca sola en cada request dentro de su
/// ventana).
///
/// Clase PROPIA, deliberadamente separada de <see cref="SesionBearerDeDispositivoTests"/> —
/// mismo motivo exacto que separa a <see cref="SesionDeDispositivoExpiracionTests"/> del resto
/// de <c>DispositivosTests</c> (ver su doc-comment): <c>WaysApiFixture.ConRelojDeAutenticacionEnElHost</c>
/// solo tiene efecto si se instala ANTES de la primera resolución de las opciones del esquema
/// (<c>IOptionsMonitor&lt;AuthenticationSchemeOptions&gt;</c> las cachea tras la primera
/// request) — un test de reloj controlado compartiendo clase/host con otros tests bearer que ya
/// hicieron requests reales (reloj real) dejaría esa caché fijada al reloj real, sin importar
/// qué <see cref="TimeProvider"/> se instale después. <see cref="IClassFixture{TFixture}"/> le
/// da a esta clase su PROPIO <see cref="WaysApiFixture"/> (contenedor y host nuevos), así que su
/// única request bearer es la primera del host — sin ninguna caché previa que envenenar.
/// </summary>
[Collection("Ways.IntegrationTests secuencial")]
public class SesionBearerDeDispositivoExpiracionTests(WaysApiFixture fixture) : IClassFixture<WaysApiFixture>
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

    private static HttpRequestMessage RequestConBearer(HttpMethod metodo, string ruta, string token)
    {
        var request = new HttpRequestMessage(metodo, ruta);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        return request;
    }

    [Fact]
    public async Task ElTokenBearerSigueVigenteAntesDeLos365DiasYVenceDespues()
    {
        var reloj = new RelojControlable(DateTimeOffset.UtcNow);

        // El reloj se instala ANTES de la primera request real del host — mismo cuidado que
        // SesionDeDispositivoExpiracionTests (ver el doc-comment de la clase).
        using var _reloj = fixture.ConRelojDeAutenticacionEnElHost(reloj);

        using var root = fixture.CreateClient();
        var loginRoot = await root.PostAsJsonAsync("/api/auth/login", new SolicitudDeLogin(MailRoot, PasswordRoot));
        Assert.Equal(HttpStatusCode.OK, loginRoot.StatusCode);

        var solicitud = new SolicitudDeAprovisionamiento(
            NombreTenant: nameof(ElTokenBearerSigueVigenteAntesDeLos365DiasYVenceDespues),
            RazonSocialEmpresa: "Empresa de prueba",
            NombrePuntoVenta: "Local 1",
            MailAdmin: "expiracion-bearer-admin@ways.test",
            Modo: ModoPuntoVenta.Escritorio);
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
        var vinculado = (await altaDispositivo.Content.ReadFromJsonAsync<DispositivoVinculado>())!;

        var hasheador = new HasheadorPbkdf2();
        await using (var db = fixture.CrearContextoDeAplicacion(TenantActualFijo.Plataforma))
        {
            var ahora = DateTimeOffset.UtcNow;
            db.Usuarios.Add(new Usuario
            {
                IdTenant = resultado.IdTenant,
                NombreUsuario = "cajero1",
                Mail = "cajero1-expiracion-bearer@ways.test",
                RolId = (int)RolConocido.Vendedor,
                PasswordHash = hasheador.Hashear(PasswordCajero),
                PasswordAlgoritmo = hasheador.Algoritmo,
                PasswordActualizadoEl = ahora,
                CreatedAt = ahora,
                UpdatedAt = ahora
            });
            await db.SaveChangesAsync();
        }

        using var cajero = fixture.CreateClient();
        using var loginRequest = new HttpRequestMessage(HttpMethod.Post, "/api/auth/login-dispositivo")
        {
            Content = JsonContent.Create(new SolicitudDeLoginDeDispositivo("cajero1", PasswordCajero, SolicitarBearer: true))
        };
        loginRequest.Headers.Add("Authorization", $"Dispositivo {vinculado.Secreto}");
        var loginCajero = await cajero.SendAsync(loginRequest);
        Assert.Equal(HttpStatusCode.OK, loginCajero.StatusCode);

        var cuerpo = await loginCajero.Content.ReadFromJsonAsync<JsonElement>();
        var token = cuerpo.GetProperty("token").GetString();
        Assert.False(string.IsNullOrEmpty(token));

        // 1) A los 364 días todavía es válido.
        reloj.Avanzar(TimeSpan.FromDays(364));
        var antesDeVencer = await cajero.SendAsync(RequestConBearer(HttpMethod.Get, "/api/auth/me", token!));
        Assert.Equal(HttpStatusCode.OK, antesDeVencer.StatusCode);

        // 2) Pasados los 365 días completos desde la emisión, el MISMO token deja de ser válido
        // — nada lo refrescó, porque el bearer no tiene expiración deslizante.
        reloj.Avanzar(TimeSpan.FromDays(2));
        var despuesDeVencer = await cajero.SendAsync(RequestConBearer(HttpMethod.Get, "/api/auth/me", token!));
        Assert.Equal(HttpStatusCode.Unauthorized, despuesDeVencer.StatusCode);
    }
}
