using System.Net;
using Microsoft.AspNetCore.Mvc.Testing;
using Ways.Api.ConectorMcp;
using Ways.Domain.Usuarios;
using static Ways.IntegrationTests.ApoyoDeConectorMcp;
using static Ways.IntegrationTests.ApoyoDeHerramientasDeCompras;

namespace Ways.IntegrationTests;

/// <summary>
/// Herramientas de compras del conector MCP llamadas por <c>/mcp</c> con un token real, contra
/// Postgres como <c>ways_app</c> (RLS activo). Las herramientas no agregan un predicado de tenant
/// propio: el aislamiento es el modo de tenant que fija <c>ValidadorDeSesion</c> para la request.
/// Las pruebas de aislamiento se verificaron mutando ese modo a plataforma: fallan.
/// </summary>
[Collection("Ways.IntegrationTests secuencial")]
public class ConectorMcpHerramientasDeComprasTests(WaysApiFixture fixture) : IClassFixture<WaysApiFixture>
{
    private readonly ApoyoDeConectorMcp _apoyo = new(fixture);
    private readonly ApoyoDeHerramientasDeCompras _compras = new(fixture);

    private sealed record Conexion(
        WebApplicationFactory<Program> Host, HttpClient Cliente, string AccessToken, UsuarioDeTenant Usuario)
        : IAsyncDisposable
    {
        public async ValueTask DisposeAsync()
        {
            Cliente.Dispose();
            await Host.DisposeAsync();
        }
    }

    private async Task<Conexion> ConectarAsync(string nombre, RolConocido rol = RolConocido.Admin)
    {
        var usuario = await _apoyo.SembrarUsuarioDeTenantAsync(nombre);
        if (rol != RolConocido.Admin)
        {
            await _compras.CambiarRolAsync(usuario.IdUsuario, rol);
        }

        var host = _apoyo.HostConConector(usuario.Mail);
        var cliente = Cliente(host);
        var tokens = await ObtenerTokensAsync(cliente, usuario.Mail);
        return new Conexion(host, cliente, tokens.GetProperty("access_token").GetString()!, usuario);
    }

    [Fact]
    public async Task ToolsListAnunciaQuienSoyYLasHerramientasDeComprasDeSoloLecturaNoDestructivasIdempotentesYDeMundoCerrado()
    {
        await using var conexion = await ConectarAsync("mcp-compras-anotaciones");

        var (estado, mensaje) = await LlamarMcpAsync(conexion.Cliente, conexion.AccessToken, "tools/list");

        Assert.Equal(HttpStatusCode.OK, estado);
        var herramientas = mensaje!.Value.GetProperty("result").GetProperty("tools").EnumerateArray()
            .ToDictionary(h => h.GetProperty("name").GetString()!);
        Assert.Contains("quien_soy", herramientas.Keys);
        foreach (var nombre in new[] { "contexto_de_compras" })
        {
            var anotaciones = herramientas[nombre].GetProperty("annotations");
            Assert.True(anotaciones.GetProperty("readOnlyHint").GetBoolean());
            Assert.False(anotaciones.GetProperty("destructiveHint").GetBoolean());
            Assert.True(anotaciones.GetProperty("idempotentHint").GetBoolean());
            Assert.False(anotaciones.GetProperty("openWorldHint").GetBoolean());
        }
    }

    [Fact]
    public async Task ContextoDeComprasDevuelveLosPuntosDeVentaYEmpresasDelTenantYSoloLosTiposDeCompra()
    {
        await using var conexion = await ConectarAsync("mcp-compras-contexto");
        var (idEmpresa, idPuntoVenta) = await _compras.SembrarEmpresaConPuntoDeVentaAsync(
            conexion.Usuario.IdTenant, "Empresa Propia SA", "Local propio", percepcionIibb: 3.5m);
        var otro = await _apoyo.SembrarUsuarioDeTenantAsync("mcp-compras-contexto-ajeno");
        await _compras.SembrarEmpresaConPuntoDeVentaAsync(
            otro.IdTenant, "Empresa Ajena SA", "Local ajeno", percepcionIva: 1m);

        var resultado = await LlamarHerramientaAsync(
            conexion.Cliente, conexion.AccessToken, "contexto_de_compras", new { });

        Assert.False(resultado.EsError);
        var lineas = resultado.Texto.Split(Environment.NewLine);
        Assert.Contains("Rol del usuario: Admin", lineas);
        Assert.Contains($"- {idPuntoVenta} | Local propio | {idEmpresa} Empresa Propia SA", lineas);
        Assert.Contains($"- {idEmpresa} | Empresa Propia SA | 3.5 % | sin valor", lineas);
        Assert.Contains(lineas, l => l.EndsWith("| C-FA | Factura A de compra | sí | sí", StringComparison.Ordinal));
        Assert.Contains(lineas, l => l.EndsWith("| C-FB | Factura B de compra | sí | no", StringComparison.Ordinal));
        Assert.Contains(lineas, l => l.EndsWith("| C-RM | Remito / comprobante no fiscal | no | no", StringComparison.Ordinal));
        Assert.Contains(lineas, l => l.EndsWith("| 21% | 21 %", StringComparison.Ordinal));
        Assert.DoesNotContain(lineas, l => l.Contains("| TX | Ticket X", StringComparison.Ordinal));
        Assert.DoesNotContain("Local ajeno", resultado.Texto);
        Assert.DoesNotContain("Empresa Ajena SA", resultado.Texto);
    }

    [Theory]
    [InlineData("contexto_de_compras", RolConocido.Supervisor)]
    [InlineData("contexto_de_compras", RolConocido.Vendedor)]
    public async Task UnUsuarioQueNoEsAdminRecibeUnErrorSinDatos(string herramienta, RolConocido rol)
    {
        await using var conexion = await ConectarAsync($"mcp-compras-rol-{rol}", rol);
        await _compras.SembrarEmpresaConPuntoDeVentaAsync(conexion.Usuario.IdTenant, "Empresa Vedada SA", "Local vedado");

        var resultado = await LlamarHerramientaAsync(conexion.Cliente, conexion.AccessToken, herramienta, new { });

        Assert.True(resultado.EsError);
        Assert.Equal(PermisoDeCompras.MensajeSinPermiso, resultado.Texto);
    }
}
