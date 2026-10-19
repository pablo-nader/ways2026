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
        foreach (var nombre in new[] { "contexto_de_compras", "buscar_proveedor", "buscar_articulos" })
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
    [InlineData("20 12345678 9")]
    [InlineData("20123456789")]
    [InlineData("20.12345678.9")]
    public async Task BuscarProveedorPorCuitComparaSoloLosDigitosYNoVeProveedoresDeOtroTenant(string cuitBuscado)
    {
        await using var conexion = await ConectarAsync("mcp-compras-cuit");
        var idPropio = await _compras.SembrarProveedorAsync(
            conexion.Usuario.IdTenant, "Proveedor Propio SRL", cuit: "20-12345678-9", nombreFantasia: "Propio",
            percibeIibb: true, preciosIncluyenIva: true);
        var otro = await _apoyo.SembrarUsuarioDeTenantAsync("mcp-compras-cuit-ajeno");
        await _compras.SembrarProveedorAsync(otro.IdTenant, "Proveedor Ajeno SRL", cuit: "20123456789");

        var resultado = await LlamarHerramientaAsync(
            conexion.Cliente, conexion.AccessToken, "buscar_proveedor", new { cuit = cuitBuscado });

        Assert.False(resultado.EsError);
        var lineas = resultado.Texto.Split(Environment.NewLine);
        Assert.Equal(2, lineas.Length);
        Assert.Equal(
            $"- {idPropio} | Proveedor Propio SRL | Propio | 20-12345678-9 | sí | sí | no | sí | todas", lineas[1]);
        Assert.DoesNotContain("Ajeno", resultado.Texto);
    }

    [Fact]
    public async Task BuscarProveedorPorNombreEncuentraPorFantasiaSinAcentosYNoVeProveedoresDeOtroTenant()
    {
        await using var conexion = await ConectarAsync("mcp-compras-nombre");
        var (idEmpresa, _) = await _compras.SembrarEmpresaConPuntoDeVentaAsync(
            conexion.Usuario.IdTenant, "Empresa Propia SA", "Local propio");
        var idPropio = await _compras.SembrarProveedorAsync(
            conexion.Usuario.IdTenant, "Razón Propia SA", nombreFantasia: "Distribuidora Ñandú", activo: false,
            percibeIva: true, idEmpresa: idEmpresa);
        var otro = await _apoyo.SembrarUsuarioDeTenantAsync("mcp-compras-nombre-ajeno");
        await _compras.SembrarProveedorAsync(otro.IdTenant, "Razón Ajena SA", nombreFantasia: "Distribuidora Ñandú");

        var resultado = await LlamarHerramientaAsync(
            conexion.Cliente, conexion.AccessToken, "buscar_proveedor", new { nombre = "nandu" });

        Assert.False(resultado.EsError);
        var lineas = resultado.Texto.Split(Environment.NewLine);
        Assert.Equal(2, lineas.Length);
        Assert.Equal(
            $"- {idPropio} | Razón Propia SA | Distribuidora Ñandú | - | no | no | sí | no | {idEmpresa}", lineas[1]);
    }

    [Fact]
    public async Task BuscarProveedorSinCoincidenciasLoDice()
    {
        await using var conexion = await ConectarAsync("mcp-compras-sin-resultado");

        var resultado = await LlamarHerramientaAsync(
            conexion.Cliente, conexion.AccessToken, "buscar_proveedor", new { cuit = "27-99999999-1" });

        Assert.False(resultado.EsError);
        Assert.Equal("No hay proveedores con el CUIT 27999999991.", resultado.Texto);
    }

    [Theory]
    [InlineData("20-1234")]
    [InlineData("CUIT 20123456789")]
    public async Task BuscarProveedorConCuitInvalidoDevuelveErrorDeHerramienta(string cuit)
    {
        await using var conexion = await ConectarAsync("mcp-compras-cuit-invalido");

        var resultado = await LlamarHerramientaAsync(
            conexion.Cliente, conexion.AccessToken, "buscar_proveedor", new { cuit });

        Assert.True(resultado.EsError);
        Assert.StartsWith($"El CUIT \"{cuit}\" no es válido", resultado.Texto, StringComparison.Ordinal);
    }

    [Fact]
    public async Task BuscarProveedorConUnNombreDeUnCaracterDevuelveErrorDeHerramienta()
    {
        await using var conexion = await ConectarAsync("mcp-compras-sin-criterio");

        var resultado = await LlamarHerramientaAsync(
            conexion.Cliente, conexion.AccessToken, "buscar_proveedor", new { nombre = " a " });

        Assert.True(resultado.EsError);
        Assert.Equal("Pasá un cuit o un nombre de al menos 2 caracteres.", resultado.Texto);
    }

    [Fact]
    public async Task BuscarProveedorPorNombreConMasDeDiezCoincidenciasDevuelveDiezYAvisa()
    {
        await using var conexion = await ConectarAsync("mcp-compras-tope");
        for (var i = 0; i <= HerramientasDeCompras.MaximoDeProveedores; i++)
        {
            await _compras.SembrarProveedorAsync(conexion.Usuario.IdTenant, $"Tope {i:00}");
        }

        var resultado = await LlamarHerramientaAsync(
            conexion.Cliente, conexion.AccessToken, "buscar_proveedor", new { nombre = "tope" });

        var lineas = resultado.Texto.Split(Environment.NewLine);
        Assert.Equal(HerramientasDeCompras.MaximoDeProveedores + 2, lineas.Length);
        Assert.EndsWith("| Tope 09 | - | - | sí | no | no | no | todas", lineas[^2], StringComparison.Ordinal);
        Assert.Equal("Hay más de 10 coincidencias: afiná la búsqueda.", lineas[^1]);
    }

    [Theory]
    [InlineData("contexto_de_compras", RolConocido.Supervisor)]
    [InlineData("contexto_de_compras", RolConocido.Vendedor)]
    [InlineData("buscar_proveedor", RolConocido.Supervisor)]
    [InlineData("buscar_proveedor", RolConocido.Vendedor)]
    [InlineData("buscar_articulos", RolConocido.Supervisor)]
    [InlineData("buscar_articulos", RolConocido.Vendedor)]
    public async Task UnUsuarioQueNoEsAdminRecibeUnErrorSinDatos(string herramienta, RolConocido rol)
    {
        await using var conexion = await ConectarAsync($"mcp-compras-rol-{rol}", rol);
        await _compras.SembrarEmpresaConPuntoDeVentaAsync(conexion.Usuario.IdTenant, "Empresa Vedada SA", "Local vedado");
        var idProveedor = await _compras.SembrarProveedorAsync(
            conexion.Usuario.IdTenant, "Proveedor Vedado SA", cuit: "30-11111111-2");
        await _compras.SembrarArticuloAsync(conexion.Usuario.IdTenant, "Artículo vedado", codigoBarras: "7792222222222");

        object argumentos = herramienta switch
        {
            "buscar_proveedor" => new { cuit = "30-11111111-2" },
            "buscar_articulos" => new { idProveedor, lineas = new[] { new { codigoBarras = "7792222222222" } } },
            _ => new { }
        };
        var resultado = await LlamarHerramientaAsync(conexion.Cliente, conexion.AccessToken, herramienta, argumentos);

        Assert.True(resultado.EsError);
        Assert.Equal(PermisoDelConector.MensajeSinPermisoDeCompras, resultado.Texto);
    }
}
