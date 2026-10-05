using System.Net;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using ModelContextProtocol.AspNetCore.Authentication;
using OpenIddict.Abstractions;
using OpenIddict.Server.AspNetCore;
using OpenIddict.Validation.AspNetCore;
using Ways.Api.ConectorMcp;

namespace Ways.IntegrationTests;

/// <summary>
/// Los tres estados de arranque del conector MCP que no son "activo": flag apagado, flag encendido
/// con una configuración inválida y una falla al inicializarlo. En los tres la app arranca, responde
/// <c>/api/salud</c> y el conector no deja rutas ni esquemas de autenticación. Con el flag apagado
/// no deja ningún log; en los otros dos, exactamente un error.
/// </summary>
[Collection("Ways.IntegrationTests secuencial")]
public class ConectorMcpArranqueTests(WaysApiFixture fixture) : IClassFixture<WaysApiFixture>
{
    private const string CategoriaDelConector = "Ways.Api.ConectorMcp";

    private static readonly string[] EsquemasDelConector =
    [
        OpenIddictServerAspNetCoreDefaults.AuthenticationScheme,
        OpenIddictValidationAspNetCoreDefaults.AuthenticationScheme,
        McpAuthenticationDefaults.AuthenticationScheme,
        ConstantesDeMcp.EsquemaDeToken
    ];

    private WebApplicationFactory<Program> Host(CapturaDeLogs captura, Action<IWebHostBuilder> configurar) =>
        fixture.WithWebHostBuilder(builder =>
        {
            builder.ConfigureLogging(logging => logging.AddProvider(captura));
            configurar(builder);
        });

    /// <summary>Las rutas del conector responden como cualquier ruta desconocida de este host (404:
    /// el host de pruebas no tiene index.html para el fallback de la SPA), no hay endpoints con esas
    /// rutas, no quedan esquemas de autenticación del conector y <c>/api/salud</c> responde 200.</summary>
    private static async Task AfirmarSinConectorAsync(WebApplicationFactory<Program> host)
    {
        using var cliente = host.CreateClient();

        Assert.Equal(HttpStatusCode.NotFound, (await cliente.PostAsync("/mcp", new StringContent("{}"))).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await cliente.GetAsync("/connect/authorize?client_id=claude-ways")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await cliente.PostAsync("/connect/token", new StringContent(""))).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await cliente.GetAsync("/.well-known/oauth-authorization-server")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await cliente.GetAsync("/.well-known/openid-configuration")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await cliente.GetAsync("/.well-known/oauth-protected-resource/mcp")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await cliente.GetAsync("/api/salud")).StatusCode);

        var rutas = host.Services.GetRequiredService<EndpointDataSource>().Endpoints
            .OfType<RouteEndpoint>()
            .Select(e => e.RoutePattern.RawText ?? string.Empty);
        Assert.DoesNotContain(rutas, ruta =>
            ruta.StartsWith("/mcp", StringComparison.Ordinal) ||
            ruta.StartsWith("/connect", StringComparison.Ordinal) ||
            ruta.StartsWith("/.well-known", StringComparison.Ordinal));

        var esquemas = await host.Services.GetRequiredService<IAuthenticationSchemeProvider>().GetAllSchemesAsync();
        Assert.DoesNotContain(esquemas, esquema => EsquemasDelConector.Contains(esquema.Name));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("false")]
    [InlineData("si")]
    public async Task ConElFlagApagadoOMalEscritoElConectorNoExisteNiLoguea(string? valorDelFlag)
    {
        var captura = new CapturaDeLogs();
        await using var host = Host(captura, builder =>
        {
            if (valorDelFlag is not null)
            {
                builder.UseSetting("Mcp:Habilitado", valorDelFlag);
            }
        });

        await AfirmarSinConectorAsync(host);

        Assert.DoesNotContain(captura.Entradas, entrada =>
            entrada.Categoria.StartsWith(CategoriaDelConector, StringComparison.Ordinal) ||
            entrada.Categoria.StartsWith("OpenIddict", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData("Production", "Mcp:UrlPublica", null, "Mcp:UrlPublica")]
    [InlineData("Development", "Mcp:UrlPublica", "https://conector.ways.test/mcp", "Mcp:UrlPublica")]
    [InlineData("Development", "Mcp:MinutosDeAccessToken", "sesenta", "sección Mcp")]
    public async Task ConElFlagEncendidoYUnaConfiguracionInvalidaElConectorQuedaDeshabilitadoConUnSoloError(
        string entorno, string clave, string? valor, string fragmentoDelMotivo)
    {
        var captura = new CapturaDeLogs();
        await using var host = Host(captura, builder =>
        {
            builder.UseEnvironment(entorno);
            builder.UseSetting("Mcp:Habilitado", "true");
            if (valor is not null)
            {
                builder.UseSetting(clave, valor);
            }
        });

        await AfirmarSinConectorAsync(host);

        var delConector = captura.Entradas
            .Where(e => e.Categoria.StartsWith(CategoriaDelConector, StringComparison.Ordinal))
            .ToList();
        var error = Assert.Single(delConector);
        Assert.Equal(LogLevel.Error, error.Nivel);
        Assert.Contains(fragmentoDelMotivo, error.Mensaje);
    }

    [Fact]
    public async Task SiElConectorFallaAlInicializarseSeDeshabilitaSinAfectarAlRestoDeLaApp()
    {
        var captura = new CapturaDeLogs();
        await using var host = Host(captura, builder =>
        {
            builder.UseSetting("Mcp:Habilitado", "true");
            builder.UseSetting("Mcp:UrlPublica", ApoyoDeConectorMcp.UrlPublica);
            builder.ConfigureServices(servicios => servicios.AddScoped<IOpenIddictApplicationManager>(
                _ => throw new InvalidOperationException("Falla inducida por la prueba.")));
        });

        await AfirmarSinConectorAsync(host);

        var delConector = captura.Entradas
            .Where(e => e.Categoria.StartsWith(CategoriaDelConector, StringComparison.Ordinal))
            .ToList();
        var error = Assert.Single(delConector);
        Assert.Equal(LogLevel.Error, error.Nivel);
        Assert.Contains("no pudo inicializarse", error.Mensaje);
    }
}
