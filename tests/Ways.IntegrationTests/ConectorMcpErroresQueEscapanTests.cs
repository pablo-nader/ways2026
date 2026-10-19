using System.Net;
using System.Text.Json;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using ModelContextProtocol;
using ModelContextProtocol.Server;
using static Ways.IntegrationTests.ApoyoDeConectorMcp;

namespace Ways.IntegrationTests;

/// <summary>
/// Qué hace el SDK con una excepción que escapa de una herramienta, sin pasar por
/// <see cref="Ways.Api.ConectorMcp.ResultadoDeHerramienta"/>: las dos llegan como resultado con <c>isError</c>. Una
/// <see cref="McpException"/> llega como <c>An error occurred invoking '&lt;herramienta&gt;': &lt;mensaje&gt;</c>; una
/// <see cref="InvalidOperationException"/>, como <c>An error occurred invoking '&lt;herramienta&gt;'.</c>, sin su
/// mensaje. Las dos herramientas de prueba se registran solo en este host; producción no las conoce.
/// </summary>
[Collection("Ways.IntegrationTests secuencial")]
public class ConectorMcpErroresQueEscapanTests(WaysApiFixture fixture) : IClassFixture<WaysApiFixture>
{
    private const string ConMcpException = "prueba_lanza_mcp_exception";
    private const string ConExcepcionComun = "prueba_lanza_excepcion_comun";
    private const string MensajeDeLaMcpException = "mensaje-de-la-mcp-exception";
    private const string MensajeDeLaExcepcionComun = "mensaje-de-la-excepcion-comun";

    private readonly ApoyoDeConectorMcp _apoyo = new(fixture);

    private static void RegistrarHerramientasDePrueba(IServiceCollection servicios)
    {
        Func<string> lanzaMcpException = () => throw new McpException(MensajeDeLaMcpException);
        Func<string> lanzaExcepcionComun = () => throw new InvalidOperationException(MensajeDeLaExcepcionComun);
        servicios.AddSingleton(McpServerTool.Create(lanzaMcpException, new() { Name = ConMcpException }));
        servicios.AddSingleton(McpServerTool.Create(lanzaExcepcionComun, new() { Name = ConExcepcionComun }));
    }

    private static async Task<JsonElement> LlamarAsync(HttpClient cliente, string token, string herramienta)
    {
        var (estado, mensaje) = await LlamarMcpAsync(
            cliente, token, "tools/call", new { name = herramienta, arguments = new { } });

        Assert.Equal(HttpStatusCode.OK, estado);
        return mensaje!.Value;
    }

    [Fact]
    public async Task ElSdkDevuelveElMensajeDeUnaMcpExceptionYTapaElDeUnaInvalidOperationException()
    {
        var usuario = await _apoyo.SembrarUsuarioDeTenantAsync("mcp-errores-que-escapan");
        await using var host = _apoyo.HostConConector(
            usuario.Mail, configurarMas: b => b.ConfigureTestServices(RegistrarHerramientasDePrueba));
        using var cliente = Cliente(host);
        var token = (await ObtenerTokensAsync(cliente, usuario.Mail)).GetProperty("access_token").GetString()!;

        var deLaMcpException = await LlamarAsync(cliente, token, ConMcpException);
        var deLaComun = await LlamarAsync(cliente, token, ConExcepcionComun);

        Assert.Equal(
            ($"An error occurred invoking '{ConMcpException}': {MensajeDeLaMcpException}", true),
            Resultado(deLaMcpException));
        Assert.Equal(($"An error occurred invoking '{ConExcepcionComun}'.", true), Resultado(deLaComun));
    }

    private static (string Texto, bool EsError) Resultado(JsonElement mensaje)
    {
        var resultado = mensaje.GetProperty("result");
        return (
            Assert.Single(resultado.GetProperty("content").EnumerateArray()).GetProperty("text").GetString()!,
            resultado.GetProperty("isError").GetBoolean());
    }
}
