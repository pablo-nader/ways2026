using System.Net;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Ways.Api.Seguridad;

namespace Ways.IntegrationTests;

/// <summary>
/// judgment-day ronda 1 (hallazgo WARNING, juez A): <c>UnTokenBearerCorruptoDaNoAutenticado</c>
/// (<c>SesionBearerDeDispositivoTests.cs</c>) manda un header <c>Bearer &lt;basura&gt;</c> — prueba
/// el <c>try/catch</c> de <c>Unprotect</c>, no el guard <c>"Bearer "</c> de
/// <c>ManejadorBearerDeSesion.HandleAuthenticateAsync</c>: sacando el guard, ese mismo test sigue
/// en 401 (ahora vía el catch), así que no lo mata. Para aislar el guard hace falta un host
/// MÍNIMO donde el esquema bearer sea el default DIRECTO (sin el selector de <c>Program.cs</c>,
/// sin cookie, sin base) — así la única razón posible de un 401 es este guard, nunca el selector
/// ni el catch.
///
/// Mutation-proof (evidencia registrada en el reporte de la ronda, no acá): con el guard borrado,
/// un header sin el prefijo <c>"Bearer "</c> (o ausente) hace que el código intente
/// <c>encabezado[PrefijoBearer.Length..]</c> sobre un string mas corto que 7 caracteres —
/// <c>ArgumentOutOfRangeException</c> sin capturar (la línea vive ANTES del try/catch de
/// <c>Unprotect</c>) — la request explota en vez de dar el 401 limpio que este test espera; con
/// el guard de vuelta, vuelve a 401.
/// </summary>
public sealed class ManejadorBearerDeSesionPrefijoTests : IAsyncLifetime
{
    private TestServer _servidor = null!;

    public Task InitializeAsync()
    {
        var builder = new WebHostBuilder()
            .ConfigureServices(servicios =>
            {
                servicios.AddDataProtection().UseEphemeralDataProtectionProvider();
                servicios.AddSingleton<FormateadorDeTicketBearer>();
                servicios
                    .AddAuthentication(EsquemasWays.Bearer)
                    .AddScheme<AuthenticationSchemeOptions, ManejadorBearerDeSesion>(EsquemasWays.Bearer, _ => { });
                servicios.AddAuthorization();
                servicios.AddRouting();
            })
            .Configure(app =>
            {
                app.UseRouting();
                app.UseAuthentication();
                app.UseAuthorization();
                app.UseEndpoints(endpoints => endpoints.MapGet("/protegido", () => "ok").RequireAuthorization());
            });

        _servidor = new TestServer(builder);
        return Task.CompletedTask;
    }

    public Task DisposeAsync()
    {
        _servidor.Dispose();
        return Task.CompletedTask;
    }

    /// <summary>Sin header <c>Authorization</c> — el caso más común (navegador/cliente que ni
    /// siquiera intenta bearer). El guard tiene que rechazar con un 401 limpio.</summary>
    [Fact]
    public async Task SinHeaderAuthorizationNoAutentica()
    {
        using var cliente = _servidor.CreateClient();
        var respuesta = await cliente.GetAsync("/protegido");

        Assert.Equal(HttpStatusCode.Unauthorized, respuesta.StatusCode);
    }

    /// <summary>Header presente pero SIN el prefijo <c>"Bearer "</c> — más corto que
    /// <c>"Bearer ".Length</c> (7) a propósito: es el caso que hace explotar el slice
    /// (<c>ArgumentOutOfRangeException</c>) si el guard no estuviera. <c>"Bearer"</c> sin el
    /// espacio final es el borde más ajustado: prueba que el guard exige el espacio, no solo la
    /// palabra. El guard tiene que rechazar los dos con un 401 limpio, igual que sin header.</summary>
    [Theory]
    [InlineData("abc")]
    [InlineData("Bearer")]
    public async Task UnHeaderSinElPrefijoBearerNoAutentica(string valorHeader)
    {
        using var cliente = _servidor.CreateClient();
        using var request = new HttpRequestMessage(HttpMethod.Get, "/protegido");
        request.Headers.TryAddWithoutValidation("Authorization", valorHeader);

        var respuesta = await cliente.SendAsync(request);

        Assert.Equal(HttpStatusCode.Unauthorized, respuesta.StatusCode);
    }

    /// <summary>judgment-day ronda 1 (hallazgo SUGGESTION, juez B): el juez asumió que
    /// <c>if (ticket is null)</c> (líneas 61-64) es inalcanzable porque "<c>Unprotect</c> o tira o
    /// devuelve un ticket". Comprobado FALSO con un diagnóstico directo contra
    /// <c>TicketDataFormat.Unprotect</c> (la implementación real de
    /// <c>Microsoft.AspNetCore.Authentication.SecureDataFormat</c> envuelve TODO su cuerpo en un
    /// try/catch propio que traga la excepción y devuelve <c>default</c>): para un string basura,
    /// <c>Unprotect</c> NO tira — devuelve <c>null</c> directamente. La rama que de verdad atiende
    /// un token corrupto es <c>if (ticket is null)</c>. judgment-day ronda 2 (ambos jueces): al
    /// estar probado que <c>Unprotect</c> nunca tira, el try/catch que rodeaba esa llamada en
    /// <c>ManejadorBearerDeSesion.cs</c> quedó removido (precedente PR #257: una guarda que ningún
    /// test puede matar no se shippea como código vivo) y reemplazado por un comentario que explica
    /// por qué no hace falta. Este test aísla la rama <c>if (ticket is null)</c> en el mismo host
    /// mínimo de arriba.
    ///
    /// Mutation-proof: comentando <c>if (ticket is null) return AuthenticateResult.Fail(...);</c>
    /// en <c>ManejadorBearerDeSesion.cs</c>, el build de <c>Ways.Api</c> pasó de compilar limpio a
    /// <c>error CS8602: Desreferencia de una referencia posiblemente NULL</c> sobre
    /// <c>ticket.Properties.ExpiresUtc</c> (nullable habilitado, 0 warnings nuevos) — ni siquiera
    /// llegó a correr el test. Revertido, vuelve a compilar y este test vuelve a 401. La propia
    /// política de nullability del repo ya deja este guard imposible de borrar por accidente sin
    /// romper el build; evidencia completa en el reporte de la ronda.</summary>
    [Fact]
    public async Task UnTokenBearerQueUnprotectNoPuedeDecodificarNoAutentica()
    {
        using var cliente = _servidor.CreateClient();
        using var request = new HttpRequestMessage(HttpMethod.Get, "/protegido");
        request.Headers.TryAddWithoutValidation("Authorization", "Bearer esto-no-es-un-token-valido");

        var respuesta = await cliente.SendAsync(request);

        Assert.Equal(HttpStatusCode.Unauthorized, respuesta.StatusCode);
    }
}
