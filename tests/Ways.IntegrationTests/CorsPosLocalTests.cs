using System.Net;

namespace Ways.IntegrationTests;

/// <summary>
/// stage-desktop-pos, slice 3: la política CORS que habilita al shell de escritorio (pagina
/// LOCAL, origen <c>http://tauri.localhost</c> en Windows) a llamar a esta API por red
/// (<c>Program.cs</c>). Corre contra el pipeline real (<see cref="WaysApiFixture"/>, Postgres en
/// contenedor) porque CORS es responsabilidad del middleware, no algo que se pueda probar sin
/// levantar el host.
/// </summary>
[Collection("Ways.IntegrationTests secuencial")]
public class CorsPosLocalTests(WaysApiFixture fixture) : IClassFixture<WaysApiFixture>
{
    private const string OrigenTauri = "http://tauri.localhost";
    private const string OrigenNoConfiado = "http://evil.example";

    private static HttpRequestMessage PreflightHacia(string ruta, string origen) =>
        new(HttpMethod.Options, ruta)
        {
            Headers =
            {
                { "Origin", origen },
                { "Access-Control-Request-Method", "GET" },
            },
        };

    [Fact]
    public async Task El_preflight_desde_el_origen_de_tauri_lo_permite_sin_credentials()
    {
        using var cliente = fixture.CreateClient();
        using var respuesta = await cliente.SendAsync(PreflightHacia("/api/salud", OrigenTauri));

        Assert.True(respuesta.IsSuccessStatusCode, $"preflight inesperado: {respuesta.StatusCode}");
        Assert.Equal(OrigenTauri, Assert.Single(respuesta.Headers.GetValues("Access-Control-Allow-Origin")));
        Assert.False(
            respuesta.Headers.Contains("Access-Control-Allow-Credentials"),
            "AllowCredentials no debería estar habilitado — la sesión bajo Tauri viaja por bearer, nunca por cookie.");
    }

    [Fact]
    public async Task El_preflight_desde_un_origen_no_configurado_no_recibe_los_headers_de_cors()
    {
        using var cliente = fixture.CreateClient();
        using var respuesta = await cliente.SendAsync(PreflightHacia("/api/salud", OrigenNoConfiado));

        // El middleware de CORS no rechaza la request con un error — simplemente omite los
        // headers de Access-Control-*, y es el NAVEGADOR (nunca este test, que no es uno) el que
        // bloquea la respuesta ante su ausencia. Lo único que este test puede probar es que el
        // servidor no le da su sello a un origen que no configuramos.
        Assert.False(respuesta.Headers.Contains("Access-Control-Allow-Origin"));
    }

    [Fact]
    public async Task Una_respuesta_real_al_origen_de_tauri_incluye_allow_origin_pero_nunca_allow_credentials()
    {
        using var cliente = fixture.CreateClient();
        using var request = new HttpRequestMessage(HttpMethod.Get, "/api/salud");
        request.Headers.Add("Origin", OrigenTauri);

        using var respuesta = await cliente.SendAsync(request);

        Assert.Equal(HttpStatusCode.OK, respuesta.StatusCode);
        Assert.Equal(OrigenTauri, Assert.Single(respuesta.Headers.GetValues("Access-Control-Allow-Origin")));
        Assert.False(respuesta.Headers.Contains("Access-Control-Allow-Credentials"));
    }

    [Fact]
    public async Task Una_respuesta_real_a_un_origen_no_configurado_no_incluye_allow_origin()
    {
        using var cliente = fixture.CreateClient();
        using var request = new HttpRequestMessage(HttpMethod.Get, "/api/salud");
        request.Headers.Add("Origin", OrigenNoConfiado);

        using var respuesta = await cliente.SendAsync(request);

        Assert.Equal(HttpStatusCode.OK, respuesta.StatusCode);
        Assert.False(respuesta.Headers.Contains("Access-Control-Allow-Origin"));
    }

    [Fact]
    public async Task Un_401_de_un_endpoint_PROTEGIDO_igual_lleva_el_header_de_cors()
    {
        // Prueba la razón real de "UseCors antes de UseAuthentication/UseAuthorization"
        // (Program.cs): `/api/auth/me` exige sesión (sin AllowAnonymous, fallback policy) — sin
        // ninguna cookie/Authorization en esta request, el pipeline corta con 401 DENTRO de
        // UseAuthorization. Si UseCors estuviera registrado DESPUÉS de UseAuthorization, ese
        // corte nunca llegaría a ejecutarlo y el 401 saldría sin Access-Control-Allow-Origin — el
        // navegador lo vería como un error de red opaco en vez de un 401 legible, y `cliente.ts`
        // (que parsea el cuerpo del 401 para disparar `alPerderLaSesion`) nunca lo vería bajo
        // Tauri. Este es el test que de verdad distingue el orden: un preflight (OPTIONS) a esta
        // misma ruta NO lo prueba, porque `/me` solo mapea GET — la request real (GET, sin
        // credenciales) es la que efectivamente atraviesa la autorización y corta.
        using var cliente = fixture.CreateClient();
        using var request = new HttpRequestMessage(HttpMethod.Get, "/api/auth/me");
        request.Headers.Add("Origin", OrigenTauri);

        using var respuesta = await cliente.SendAsync(request);

        Assert.Equal(HttpStatusCode.Unauthorized, respuesta.StatusCode);
        Assert.Equal(OrigenTauri, Assert.Single(respuesta.Headers.GetValues("Access-Control-Allow-Origin")));
    }

    [Fact]
    public async Task El_metodo_bearer_authorization_esta_permitido_en_el_preflight_del_origen_de_tauri()
    {
        // El cliente de Tauri manda `Authorization: Bearer <token>` (ver `cliente.ts`) — sin
        // `WithHeaders("Authorization", ...)` en la política, este preflight lo rechazaría el
        // NAVEGADOR (nunca este servidor) antes de que la request real saliera.
        using var cliente = fixture.CreateClient();
        using var request = PreflightHacia("/api/salud", OrigenTauri);
        request.Headers.Add("Access-Control-Request-Headers", "Authorization,Content-Type");

        using var respuesta = await cliente.SendAsync(request);

        Assert.True(respuesta.IsSuccessStatusCode, $"preflight inesperado: {respuesta.StatusCode}");
        var headersPermitidos = string.Join(",", respuesta.Headers.GetValues("Access-Control-Allow-Headers"));
        Assert.Contains("Authorization", headersPermitidos, StringComparison.OrdinalIgnoreCase);
    }
}
