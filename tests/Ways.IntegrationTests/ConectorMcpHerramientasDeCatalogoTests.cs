using System.Data.Common;
using System.Net;
using System.Text.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;
using Ways.Domain.Articulos;
using Ways.Domain.Catalogos;
using Ways.Domain.Proveedores;
using Ways.Domain.Usuarios;
using Ways.Infrastructure.Multitenancy;
using Ways.Infrastructure.Persistencia;
using static Ways.IntegrationTests.ApoyoDeConectorMcp;

namespace Ways.IntegrationTests;

/// <summary>
/// Las herramientas de escritura del conector MCP (<c>asociar_codigo_de_barras</c> y
/// <c>asociar_codigo_de_proveedor</c>) llamadas por <c>/mcp</c> con un access token real, contra
/// Postgres como <c>ways_app</c>. Cada escritura, o su ausencia, se verifica leyendo la base.
/// </summary>
[Collection("Ways.IntegrationTests secuencial")]
public class ConectorMcpHerramientasDeCatalogoTests(WaysApiFixture fixture) : IClassFixture<WaysApiFixture>
{
    private const string EanValido = "7790895000782";
    private const string EanConDigitoVerificadorErroneo = "7790895000783";
    private const string OtroEanValido = "4006381333931";

    private readonly ApoyoDeConectorMcp _apoyo = new(fixture);

    private sealed record DatosDeTenant(UsuarioDeTenant Usuario, int IdArea, int IdAlicuotaIva, int IdProveedor);

    private sealed class Escenario(
        WebApplicationFactory<Program> host, HttpClient cliente, string token, DatosDeTenant datos) : IAsyncDisposable
    {
        public HttpClient Cliente { get; } = cliente;

        public string Token { get; } = token;

        public DatosDeTenant Datos { get; } = datos;

        public int IdTenant => Datos.Usuario.IdTenant;

        public async ValueTask DisposeAsync()
        {
            Cliente.Dispose();
            await host.DisposeAsync();
        }
    }

    /// <param name="rol">Se fija en la base ANTES de autorizar: el access token lleva el rol que el
    /// usuario tiene al momento de aprobar.</param>
    private async Task<Escenario> PrepararAsync(
        string nombre, RolConocido rol = RolConocido.Admin, Action<IWebHostBuilder>? configurarMas = null)
    {
        var usuario = await _apoyo.SembrarUsuarioDeTenantAsync(nombre);
        var datos = await SembrarCatalogoAsync(usuario, nombre);

        if (rol != RolConocido.Admin)
        {
            await using var db = fixture.CrearContextoDeAplicacion(TenantActualFijo.Plataforma);
            var fila = await db.Usuarios.SingleAsync(u => u.Id == usuario.IdUsuario);
            fila.RolId = (int)rol;
            await db.SaveChangesAsync();
        }

        var host = _apoyo.HostConConector(usuario.Mail, configurarMas: configurarMas);
        var cliente = Cliente(host);
        var tokens = await ObtenerTokensAsync(cliente, usuario.Mail);

        return new Escenario(host, cliente, tokens.GetProperty("access_token").GetString()!, datos);
    }

    private async Task<DatosDeTenant> SembrarCatalogoAsync(UsuarioDeTenant usuario, string nombre)
    {
        await using var db = fixture.CrearContextoDeAplicacion(TenantActualFijo.Plataforma);
        var ahora = DateTimeOffset.UtcNow;

        var area = new Area { IdTenant = usuario.IdTenant, Nombre = $"{nombre}-area", Orden = 1, CreatedAt = ahora, UpdatedAt = ahora };
        db.Areas.Add(area);
        var proveedor = new Proveedor
        {
            IdTenant = usuario.IdTenant,
            RazonSocial = $"Proveedor {nombre}",
            IdCondicionFiscal = await db.CondicionesFiscales.Select(c => c.Id).FirstAsync(),
            CreatedAt = ahora,
            UpdatedAt = ahora
        };
        db.Proveedores.Add(proveedor);
        await db.SaveChangesAsync();

        var idAlicuotaIva = await db.AlicuotasIva.Select(a => a.Id).FirstAsync();

        return new DatosDeTenant(usuario, area.Id, idAlicuotaIva, proveedor.Id);
    }

    private async Task<(int Id, string Nombre, string CodigoInterno)> SembrarArticuloAsync(DatosDeTenant datos, string nombre)
    {
        await using var db = fixture.CrearContextoDeAplicacion(TenantActualFijo.Plataforma);
        var ahora = DateTimeOffset.UtcNow;

        var articulo = new Articulo
        {
            IdTenant = datos.Usuario.IdTenant,
            CodigoInterno = $"INT-{Guid.NewGuid():N}"[..16],
            Nombre = nombre,
            IdArea = datos.IdArea,
            IdAlicuotaIva = datos.IdAlicuotaIva,
            UnidadVenta = UnidadVenta.Unidad,
            EsProducto = true,
            CreatedAt = ahora,
            UpdatedAt = ahora
        };
        db.Articulos.Add(articulo);
        await db.SaveChangesAsync();

        return (articulo.Id, articulo.Nombre, articulo.CodigoInterno);
    }

    private async Task SembrarCodigoDeBarrasAsync(int idTenant, int idArticulo, string codigo)
    {
        await using var db = fixture.CrearContextoDeAplicacion(TenantActualFijo.Plataforma);
        var ahora = DateTimeOffset.UtcNow;
        db.CodigosBarra.Add(new CodigoBarra
        {
            IdTenant = idTenant, IdArticulo = idArticulo, Codigo = codigo, CreatedAt = ahora, UpdatedAt = ahora
        });
        await db.SaveChangesAsync();
    }

    private async Task SembrarCodigoDeProveedorAsync(int idTenant, int idArticulo, int idProveedor, string codigo)
    {
        await using var db = fixture.CrearContextoDeAplicacion(TenantActualFijo.Plataforma);
        var ahora = DateTimeOffset.UtcNow;
        db.CodigosProveedor.Add(new CodigoProveedor
        {
            IdTenant = idTenant, IdArticulo = idArticulo, IdProveedor = idProveedor, Codigo = codigo,
            CreatedAt = ahora, UpdatedAt = ahora
        });
        await db.SaveChangesAsync();
    }

    private async Task DarDeBajaElArticuloAsync(int idArticulo)
    {
        await using var db = fixture.CrearContextoDeAplicacion(TenantActualFijo.Plataforma);
        var articulo = await db.Articulos.SingleAsync(a => a.Id == idArticulo);
        articulo.DeletedAt = DateTimeOffset.UtcNow;
        await db.SaveChangesAsync();
    }

    private async Task<List<(int IdTenant, string Codigo)>> CodigosDeBarrasEnLaBaseAsync(int idArticulo)
    {
        await using var db = fixture.CrearContextoDeAplicacion(TenantActualFijo.Plataforma);
        var filas = await db.CodigosBarra
            .AsNoTracking()
            .Where(c => c.IdArticulo == idArticulo)
            .OrderBy(c => c.Id)
            .Select(c => new { c.IdTenant, c.Codigo })
            .ToListAsync();

        return [.. filas.Select(f => (f.IdTenant, f.Codigo))];
    }

    private async Task<List<(int IdTenant, int IdProveedor, string Codigo)>> CodigosDeProveedorEnLaBaseAsync(int idArticulo)
    {
        await using var db = fixture.CrearContextoDeAplicacion(TenantActualFijo.Plataforma);
        var filas = await db.CodigosProveedor
            .AsNoTracking()
            .Where(c => c.IdArticulo == idArticulo)
            .OrderBy(c => c.Id)
            .Select(c => new { c.IdTenant, c.IdProveedor, c.Codigo })
            .ToListAsync();

        return [.. filas.Select(f => (f.IdTenant, f.IdProveedor, f.Codigo))];
    }

    private static async Task<(string Texto, bool EsError)> LlamarAsync(
        Escenario e, string herramienta, Dictionary<string, object?> argumentos)
    {
        var (estado, mensaje) = await LlamarMcpAsync(
            e.Cliente, e.Token, "tools/call", new { name = herramienta, arguments = argumentos });
        Assert.Equal(HttpStatusCode.OK, estado);

        var resultado = mensaje!.Value.GetProperty("result");
        var esError = resultado.TryGetProperty("isError", out var marca) && marca.GetBoolean();

        return (resultado.GetProperty("content")[0].GetProperty("text").GetString()!, esError);
    }

    private static Task<(string Texto, bool EsError)> AsociarCodigoDeBarrasAsync(
        Escenario e, int idArticulo, string codigo, bool? confirmar = null)
    {
        var argumentos = new Dictionary<string, object?> { ["id_articulo"] = idArticulo, ["codigo"] = codigo };
        if (confirmar is { } valor)
        {
            argumentos["confirmar_codigo_no_estandar"] = valor;
        }

        return LlamarAsync(e, "asociar_codigo_de_barras", argumentos);
    }

    private static Task<(string Texto, bool EsError)> AsociarCodigoDeProveedorAsync(
        Escenario e, int idArticulo, int idProveedor, string codigo) =>
        LlamarAsync(
            e,
            "asociar_codigo_de_proveedor",
            new Dictionary<string, object?>
            {
                ["id_articulo"] = idArticulo, ["id_proveedor"] = idProveedor, ["codigo"] = codigo
            });

    [Fact]
    public async Task ToolsListPublicaLasDosHerramientasDeEscrituraConSusAnotacionesYNombresDeParametro()
    {
        await using var e = await PrepararAsync(nameof(ToolsListPublicaLasDosHerramientasDeEscrituraConSusAnotacionesYNombresDeParametro));

        var (estado, mensaje) = await LlamarMcpAsync(e.Cliente, e.Token, "tools/list");
        Assert.Equal(HttpStatusCode.OK, estado);
        var herramientas = mensaje!.Value.GetProperty("result").GetProperty("tools").EnumerateArray()
            .ToDictionary(t => t.GetProperty("name").GetString()!);

        Assert.Contains("quien_soy", herramientas.Keys);
        foreach (var nombre in new[] { "asociar_codigo_de_barras", "asociar_codigo_de_proveedor" })
        {
            var anotaciones = herramientas[nombre].GetProperty("annotations");
            Assert.False(anotaciones.GetProperty("readOnlyHint").GetBoolean());
            Assert.False(anotaciones.GetProperty("destructiveHint").GetBoolean());
            Assert.True(anotaciones.GetProperty("idempotentHint").GetBoolean());
            Assert.False(anotaciones.GetProperty("openWorldHint").GetBoolean());
        }

        string[] ParametrosDe(string nombre, string lista) =>
            [.. herramientas[nombre].GetProperty("inputSchema").GetProperty(lista).EnumerateObject().Select(p => p.Name)];

        Assert.Equal(
            ["codigo", "confirmar_codigo_no_estandar", "id_articulo"],
            ParametrosDe("asociar_codigo_de_barras", "properties").Order());
        Assert.Equal(
            ["codigo", "id_articulo", "id_proveedor"],
            ParametrosDe("asociar_codigo_de_proveedor", "properties").Order());

        string[] Requeridos(string nombre) =>
            [.. herramientas[nombre].GetProperty("inputSchema").GetProperty("required").EnumerateArray().Select(p => p.GetString()!)];

        Assert.Equal(["codigo", "id_articulo"], Requeridos("asociar_codigo_de_barras").Order());
        Assert.Equal(["codigo", "id_articulo", "id_proveedor"], Requeridos("asociar_codigo_de_proveedor").Order());
    }

    [Fact]
    public async Task AsociarUnCodigoDeBarrasLoEscribeEnLaBaseYDevuelveLosCodigosDelArticulo()
    {
        await using var e = await PrepararAsync(nameof(AsociarUnCodigoDeBarrasLoEscribeEnLaBaseYDevuelveLosCodigosDelArticulo));
        var articulo = await SembrarArticuloAsync(e.Datos, "Tornillo hexagonal");
        await SembrarCodigoDeBarrasAsync(e.IdTenant, articulo.Id, OtroEanValido);

        var (texto, esError) = await AsociarCodigoDeBarrasAsync(e, articulo.Id, $"  {EanValido}  ");

        Assert.False(esError, texto);
        Assert.Contains("Se escribió en la base", texto);
        Assert.Contains(articulo.Nombre, texto);
        Assert.Contains(articulo.CodigoInterno, texto);
        Assert.Contains($"id {articulo.Id}", texto);
        Assert.Contains($"{OtroEanValido}, {EanValido}", texto);
        Assert.Equal(
            [(e.IdTenant, OtroEanValido), (e.IdTenant, EanValido)],
            await CodigosDeBarrasEnLaBaseAsync(articulo.Id));
    }

    [Fact]
    public async Task AsociarDeNuevoElMismoCodigoDeBarrasAlMismoArticuloNoEsUnErrorYNoAgregaOtraFila()
    {
        await using var e = await PrepararAsync(nameof(AsociarDeNuevoElMismoCodigoDeBarrasAlMismoArticuloNoEsUnErrorYNoAgregaOtraFila));
        var articulo = await SembrarArticuloAsync(e.Datos, "Clavo");

        var primera = await AsociarCodigoDeBarrasAsync(e, articulo.Id, EanValido);
        var segunda = await AsociarCodigoDeBarrasAsync(e, articulo.Id, EanValido);

        Assert.False(primera.EsError, primera.Texto);
        Assert.False(segunda.EsError, segunda.Texto);
        Assert.Contains("ya estaba asociado", segunda.Texto);
        Assert.Contains("No se cambió nada", segunda.Texto);
        Assert.Equal([(e.IdTenant, EanValido)], await CodigosDeBarrasEnLaBaseAsync(articulo.Id));
    }

    [Fact]
    public async Task UnCodigoDeBarrasDeOtroArticuloNombraAlDuenoYNoEscribeNada()
    {
        await using var e = await PrepararAsync(nameof(UnCodigoDeBarrasDeOtroArticuloNombraAlDuenoYNoEscribeNada));
        var dueno = await SembrarArticuloAsync(e.Datos, "Arandela dueña");
        var destino = await SembrarArticuloAsync(e.Datos, "Arandela destino");
        await SembrarCodigoDeBarrasAsync(e.IdTenant, dueno.Id, EanValido);

        var (texto, esError) = await AsociarCodigoDeBarrasAsync(e, destino.Id, EanValido);

        Assert.True(esError, texto);
        Assert.Contains(dueno.Nombre, texto);
        Assert.Contains(dueno.CodigoInterno, texto);
        Assert.Contains($"id {dueno.Id}", texto);
        Assert.DoesNotContain("dado de baja", texto);
        Assert.Contains("No se escribió nada", texto);
        Assert.Empty(await CodigosDeBarrasEnLaBaseAsync(destino.Id));
        Assert.Equal([(e.IdTenant, EanValido)], await CodigosDeBarrasEnLaBaseAsync(dueno.Id));
    }

    [Fact]
    public async Task UnCodigoDeBarrasDeUnArticuloDadoDeBajaSigueBloqueadoYElErrorLoIndica()
    {
        await using var e = await PrepararAsync(nameof(UnCodigoDeBarrasDeUnArticuloDadoDeBajaSigueBloqueadoYElErrorLoIndica));
        var dueno = await SembrarArticuloAsync(e.Datos, "Perno retirado");
        var destino = await SembrarArticuloAsync(e.Datos, "Perno vigente");
        await SembrarCodigoDeBarrasAsync(e.IdTenant, dueno.Id, EanValido);
        await DarDeBajaElArticuloAsync(dueno.Id);

        var (texto, esError) = await AsociarCodigoDeBarrasAsync(e, destino.Id, EanValido);

        Assert.True(esError, texto);
        Assert.Contains(dueno.Nombre, texto);
        Assert.Contains("dado de baja", texto);
        Assert.Empty(await CodigosDeBarrasEnLaBaseAsync(destino.Id));
    }

    [Fact]
    public async Task UnCodigoDeBarrasNoEstandarSoloSeEscribeConLaConfirmacionExplicita()
    {
        await using var e = await PrepararAsync(nameof(UnCodigoDeBarrasNoEstandarSoloSeEscribeConLaConfirmacionExplicita));
        var articulo = await SembrarArticuloAsync(e.Datos, "Tuerca");

        var sinConfirmar = await AsociarCodigoDeBarrasAsync(e, articulo.Id, EanConDigitoVerificadorErroneo);
        var confirmandoEnFalse = await AsociarCodigoDeBarrasAsync(e, articulo.Id, EanConDigitoVerificadorErroneo, confirmar: false);

        Assert.True(sinConfirmar.EsError, sinConfirmar.Texto);
        Assert.Contains("dígito verificador", sinConfirmar.Texto);
        Assert.Contains("confirmar_codigo_no_estandar", sinConfirmar.Texto);
        Assert.Contains("No se escribió nada", sinConfirmar.Texto);
        Assert.True(confirmandoEnFalse.EsError, confirmandoEnFalse.Texto);
        Assert.Empty(await CodigosDeBarrasEnLaBaseAsync(articulo.Id));

        var confirmado = await AsociarCodigoDeBarrasAsync(e, articulo.Id, EanConDigitoVerificadorErroneo, confirmar: true);

        Assert.False(confirmado.EsError, confirmado.Texto);
        Assert.Equal([(e.IdTenant, EanConDigitoVerificadorErroneo)], await CodigosDeBarrasEnLaBaseAsync(articulo.Id));
    }

    [Fact]
    public async Task UnCodigoDeBarrasEnBlancoNoEscribeNada()
    {
        await using var e = await PrepararAsync(nameof(UnCodigoDeBarrasEnBlancoNoEscribeNada));
        var articulo = await SembrarArticuloAsync(e.Datos, "Bulón");

        var vacio = await AsociarCodigoDeBarrasAsync(e, articulo.Id, "   ");

        Assert.True(vacio.EsError, vacio.Texto);
        Assert.Contains("vacío", vacio.Texto);
        Assert.Empty(await CodigosDeBarrasEnLaBaseAsync(articulo.Id));
    }

    [Fact]
    public async Task UnUsuarioSinRolAdminNoEscribeUnCodigoDeBarras()
    {
        await using var e = await PrepararAsync(nameof(UnUsuarioSinRolAdminNoEscribeUnCodigoDeBarras), RolConocido.Vendedor);
        var articulo = await SembrarArticuloAsync(e.Datos, "Remache");

        var (texto, esError) = await AsociarCodigoDeBarrasAsync(e, articulo.Id, EanValido);

        Assert.True(esError, texto);
        Assert.Contains("rol Admin", texto);
        Assert.Contains("No se escribió nada", texto);
        Assert.Empty(await CodigosDeBarrasEnLaBaseAsync(articulo.Id));
    }

    [Fact]
    public async Task UnArticuloDeOtroTenantEsInalcanzableParaElCodigoDeBarras()
    {
        await using var e = await PrepararAsync(nameof(UnArticuloDeOtroTenantEsInalcanzableParaElCodigoDeBarras));
        var ajeno = await _apoyo.SembrarUsuarioDeTenantAsync("conector-ajeno-barras");
        var datosAjenos = await SembrarCatalogoAsync(ajeno, "ajeno-barras");
        var articuloAjeno = await SembrarArticuloAsync(datosAjenos, "Artículo del otro tenant");

        var (texto, esError) = await AsociarCodigoDeBarrasAsync(e, articuloAjeno.Id, EanValido);

        Assert.True(esError, texto);
        Assert.Contains($"No existe el artículo {articuloAjeno.Id}", texto);
        Assert.DoesNotContain(articuloAjeno.Nombre, texto);
        Assert.DoesNotContain(articuloAjeno.CodigoInterno, texto);
        Assert.Empty(await CodigosDeBarrasEnLaBaseAsync(articuloAjeno.Id));
    }

    [Fact]
    public async Task UnCodigoDeBarrasDeOtroTenantNoBloqueaNiSeNombra()
    {
        await using var e = await PrepararAsync(nameof(UnCodigoDeBarrasDeOtroTenantNoBloqueaNiSeNombra));
        var ajeno = await _apoyo.SembrarUsuarioDeTenantAsync("conector-ajeno-codigo");
        var datosAjenos = await SembrarCatalogoAsync(ajeno, "ajeno-codigo");
        var articuloAjeno = await SembrarArticuloAsync(datosAjenos, "Artículo ajeno con el mismo código");
        await SembrarCodigoDeBarrasAsync(ajeno.IdTenant, articuloAjeno.Id, EanValido);
        var propio = await SembrarArticuloAsync(e.Datos, "Artículo propio");

        var (texto, esError) = await AsociarCodigoDeBarrasAsync(e, propio.Id, EanValido);

        Assert.False(esError, texto);
        Assert.DoesNotContain(articuloAjeno.Nombre, texto);
        Assert.Equal([(e.IdTenant, EanValido)], await CodigosDeBarrasEnLaBaseAsync(propio.Id));
        Assert.Equal([(ajeno.IdTenant, EanValido)], await CodigosDeBarrasEnLaBaseAsync(articuloAjeno.Id));
    }

    /// <summary>Las dos llamadas pueden resolverse por el chequeo previo o por el índice único; la
    /// prueba solo afirma lo que vale en ambos casos: una escribe, la otra falla nombrando a la
    /// ganadora, y queda una única fila.</summary>
    [Fact]
    public async Task DosAsociacionesSimultaneasDelMismoCodigoDeBarrasDejanUnSoloDueno()
    {
        await using var e = await PrepararAsync(nameof(DosAsociacionesSimultaneasDelMismoCodigoDeBarrasDejanUnSoloDueno));
        var a = await SembrarArticuloAsync(e.Datos, "Artículo A");
        var b = await SembrarArticuloAsync(e.Datos, "Artículo B");

        var resultados = await Task.WhenAll(
            AsociarCodigoDeBarrasAsync(e, a.Id, EanValido),
            AsociarCodigoDeBarrasAsync(e, b.Id, EanValido));

        var filasA = await CodigosDeBarrasEnLaBaseAsync(a.Id);
        var filasB = await CodigosDeBarrasEnLaBaseAsync(b.Id);
        Assert.Equal(1, filasA.Count + filasB.Count);
        Assert.Single(resultados, r => !r.EsError);
        var fallo = Assert.Single(resultados, r => r.EsError);
        var ganador = filasA.Count == 1 ? a : b;
        Assert.Contains(ganador.Nombre, fallo.Texto);
        Assert.Contains("No se escribió nada", fallo.Texto);
    }

    [Fact]
    public async Task AsociarUnCodigoDeProveedorLoEscribeEnLaBaseYNombraAlProveedor()
    {
        await using var e = await PrepararAsync(nameof(AsociarUnCodigoDeProveedorLoEscribeEnLaBaseYNombraAlProveedor));
        var articulo = await SembrarArticuloAsync(e.Datos, "Cable 2,5 mm");
        await SembrarCodigoDeProveedorAsync(e.IdTenant, articulo.Id, e.Datos.IdProveedor, "VIEJO-1");

        var (texto, esError) = await AsociarCodigoDeProveedorAsync(e, articulo.Id, e.Datos.IdProveedor, "  CAB-25  ");

        Assert.False(esError, texto);
        Assert.Contains("Se escribió en la base", texto);
        Assert.Contains(articulo.Nombre, texto);
        Assert.Contains(articulo.CodigoInterno, texto);
        Assert.Contains($"Proveedor {nameof(AsociarUnCodigoDeProveedorLoEscribeEnLaBaseYNombraAlProveedor)}", texto);
        Assert.Contains("VIEJO-1, CAB-25", texto);
        Assert.Equal(
            [(e.IdTenant, e.Datos.IdProveedor, "VIEJO-1"), (e.IdTenant, e.Datos.IdProveedor, "CAB-25")],
            await CodigosDeProveedorEnLaBaseAsync(articulo.Id));
    }

    [Fact]
    public async Task AsociarDeNuevoElMismoCodigoDeProveedorNoEsUnErrorYNoAgregaOtraFila()
    {
        await using var e = await PrepararAsync(nameof(AsociarDeNuevoElMismoCodigoDeProveedorNoEsUnErrorYNoAgregaOtraFila));
        var articulo = await SembrarArticuloAsync(e.Datos, "Interruptor");

        var primera = await AsociarCodigoDeProveedorAsync(e, articulo.Id, e.Datos.IdProveedor, "INT-7");
        var segunda = await AsociarCodigoDeProveedorAsync(e, articulo.Id, e.Datos.IdProveedor, "INT-7");

        Assert.False(primera.EsError, primera.Texto);
        Assert.False(segunda.EsError, segunda.Texto);
        Assert.Contains("ya estaba asociado", segunda.Texto);
        Assert.Contains("No se cambió nada", segunda.Texto);
        Assert.Equal([(e.IdTenant, e.Datos.IdProveedor, "INT-7")], await CodigosDeProveedorEnLaBaseAsync(articulo.Id));
    }

    [Fact]
    public async Task UnCodigoDeProveedorDeOtroArticuloNombraAlDuenoYNoEscribeNada()
    {
        await using var e = await PrepararAsync(nameof(UnCodigoDeProveedorDeOtroArticuloNombraAlDuenoYNoEscribeNada));
        var dueno = await SembrarArticuloAsync(e.Datos, "Cinta dueña");
        var destino = await SembrarArticuloAsync(e.Datos, "Cinta destino");
        await SembrarCodigoDeProveedorAsync(e.IdTenant, dueno.Id, e.Datos.IdProveedor, "CIN-1");

        var (texto, esError) = await AsociarCodigoDeProveedorAsync(e, destino.Id, e.Datos.IdProveedor, "CIN-1");

        Assert.True(esError, texto);
        Assert.Contains(dueno.Nombre, texto);
        Assert.Contains(dueno.CodigoInterno, texto);
        Assert.Contains($"id {dueno.Id}", texto);
        Assert.Contains("No se escribió nada", texto);
        Assert.Empty(await CodigosDeProveedorEnLaBaseAsync(destino.Id));
        Assert.Equal([(e.IdTenant, e.Datos.IdProveedor, "CIN-1")], await CodigosDeProveedorEnLaBaseAsync(dueno.Id));
    }

    [Fact]
    public async Task UnCodigoDeProveedorMasLargoQueElMaximoNoEscribeNada()
    {
        await using var e = await PrepararAsync(nameof(UnCodigoDeProveedorMasLargoQueElMaximoNoEscribeNada));
        var articulo = await SembrarArticuloAsync(e.Datos, "Pegamento");

        var (texto, esError) = await AsociarCodigoDeProveedorAsync(
            e, articulo.Id, e.Datos.IdProveedor, new string('x', ReglaDeCodigoProveedor.LongitudMaxima + 1));

        Assert.True(esError, texto);
        Assert.Contains("El código no es válido", texto);
        Assert.Contains("No se escribió nada", texto);
        Assert.Empty(await CodigosDeProveedorEnLaBaseAsync(articulo.Id));
    }

    [Fact]
    public async Task UnProveedorQueNoExisteNoEscribeUnCodigoDeProveedor()
    {
        await using var e = await PrepararAsync(nameof(UnProveedorQueNoExisteNoEscribeUnCodigoDeProveedor));
        var articulo = await SembrarArticuloAsync(e.Datos, "Silicona");
        var idInexistente = e.Datos.IdProveedor + 1_000_000;

        var (texto, esError) = await AsociarCodigoDeProveedorAsync(e, articulo.Id, idInexistente, "SIL-1");

        Assert.True(esError, texto);
        Assert.Contains($"No existe el proveedor {idInexistente}", texto);
        Assert.Empty(await CodigosDeProveedorEnLaBaseAsync(articulo.Id));
    }

    [Fact]
    public async Task UnUsuarioSinRolAdminNoEscribeUnCodigoDeProveedor()
    {
        await using var e = await PrepararAsync(nameof(UnUsuarioSinRolAdminNoEscribeUnCodigoDeProveedor), RolConocido.Vendedor);
        var articulo = await SembrarArticuloAsync(e.Datos, "Lija");

        var (texto, esError) = await AsociarCodigoDeProveedorAsync(e, articulo.Id, e.Datos.IdProveedor, "LIJ-1");

        Assert.True(esError, texto);
        Assert.Contains("rol Admin", texto);
        Assert.Contains("No se escribió nada", texto);
        Assert.Empty(await CodigosDeProveedorEnLaBaseAsync(articulo.Id));
    }

    [Fact]
    public async Task UnArticuloOUnProveedorDeOtroTenantSonInalcanzablesParaElCodigoDeProveedor()
    {
        await using var e = await PrepararAsync(nameof(UnArticuloOUnProveedorDeOtroTenantSonInalcanzablesParaElCodigoDeProveedor));
        var ajeno = await _apoyo.SembrarUsuarioDeTenantAsync("conector-ajeno-proveedor");
        var datosAjenos = await SembrarCatalogoAsync(ajeno, "ajeno-proveedor");
        var articuloAjeno = await SembrarArticuloAsync(datosAjenos, "Artículo del otro tenant");
        var propio = await SembrarArticuloAsync(e.Datos, "Artículo propio");

        var conArticuloAjeno = await AsociarCodigoDeProveedorAsync(e, articuloAjeno.Id, e.Datos.IdProveedor, "AJE-1");
        var conProveedorAjeno = await AsociarCodigoDeProveedorAsync(e, propio.Id, datosAjenos.IdProveedor, "AJE-2");

        Assert.True(conArticuloAjeno.EsError, conArticuloAjeno.Texto);
        Assert.Contains($"No existe el artículo {articuloAjeno.Id}", conArticuloAjeno.Texto);
        Assert.DoesNotContain(articuloAjeno.Nombre, conArticuloAjeno.Texto);
        Assert.True(conProveedorAjeno.EsError, conProveedorAjeno.Texto);
        Assert.Contains($"No existe el proveedor {datosAjenos.IdProveedor}", conProveedorAjeno.Texto);
        Assert.Empty(await CodigosDeProveedorEnLaBaseAsync(articuloAjeno.Id));
        Assert.Empty(await CodigosDeProveedorEnLaBaseAsync(propio.Id));
    }

    /// <summary>Igual que la de códigos de barras: afirma solo lo que vale tanto si resuelve el
    /// chequeo previo como si resuelve el índice único.</summary>
    [Fact]
    public async Task DosAsociacionesSimultaneasDelMismoCodigoDeProveedorDejanUnSoloDueno()
    {
        await using var e = await PrepararAsync(nameof(DosAsociacionesSimultaneasDelMismoCodigoDeProveedorDejanUnSoloDueno));
        var a = await SembrarArticuloAsync(e.Datos, "Artículo A");
        var b = await SembrarArticuloAsync(e.Datos, "Artículo B");

        var resultados = await Task.WhenAll(
            AsociarCodigoDeProveedorAsync(e, a.Id, e.Datos.IdProveedor, "CARR-1"),
            AsociarCodigoDeProveedorAsync(e, b.Id, e.Datos.IdProveedor, "CARR-1"));

        var filasA = await CodigosDeProveedorEnLaBaseAsync(a.Id);
        var filasB = await CodigosDeProveedorEnLaBaseAsync(b.Id);
        Assert.Equal(1, filasA.Count + filasB.Count);
        Assert.Single(resultados, r => !r.EsError);
        var fallo = Assert.Single(resultados, r => r.EsError);
        var ganador = filasA.Count == 1 ? a : b;
        Assert.Contains(ganador.Nombre, fallo.Texto);
        Assert.Contains("No se escribió nada", fallo.Texto);
    }

    /// <summary>Cláusula bajo prueba: la rama de carrera de <c>asociar_codigo_de_barras</c> que no encuentra al dueño.
    /// El interceptor hace que el INSERT choque con <c>ux_codigos_barra_codigo_tenant</c> sin que exista ninguna fila
    /// que lo tenga, así que la segunda búsqueda del dueño vuelve vacía. El texto sale de la clasificación de la API,
    /// no del mensaje de EF.</summary>
    [Fact]
    public async Task UnaCarreraDeCodigoDeBarrasSinDuenoVisibleDiceQuePasoSinElMensajeDeLaBase()
    {
        var interceptor = new InterceptorQueSimulaUnaViolacionUnica("codigos_barra", "ux_codigos_barra_codigo_tenant");
        await using var e = await PrepararAsync(
            nameof(UnaCarreraDeCodigoDeBarrasSinDuenoVisibleDiceQuePasoSinElMensajeDeLaBase),
            configurarMas: builder => builder.ConfigureServices(servicios =>
                servicios.AddDbContext<WaysDbContext>((_, opciones) => opciones.AddInterceptors(interceptor))));
        var articulo = await SembrarArticuloAsync(e.Datos, "Artículo de la carrera");

        var resultado = await AsociarCodigoDeBarrasAsync(e, articulo.Id, EanValido);

        Assert.True(resultado.EsError, resultado.Texto);
        Assert.Equal(
            $"No se pudo asociar el código {EanValido}. Ya existe ese código de barras en este tenant. " +
            "No se pudo identificar a qué artículo pertenece. No se escribió nada.",
            resultado.Texto);
        Assert.Equal(1, interceptor.Disparos);
        Assert.Empty(await CodigosDeBarrasEnLaBaseAsync(articulo.Id));
    }

    /// <summary>La misma rama en <c>asociar_codigo_de_proveedor</c>, contra
    /// <c>ux_codigos_proveedor_proveedor_codigo</c>.</summary>
    [Fact]
    public async Task UnaCarreraDeCodigoDeProveedorSinDuenoVisibleDiceQuePasoSinElMensajeDeLaBase()
    {
        var interceptor = new InterceptorQueSimulaUnaViolacionUnica("codigos_proveedor", "ux_codigos_proveedor_proveedor_codigo");
        await using var e = await PrepararAsync(
            nameof(UnaCarreraDeCodigoDeProveedorSinDuenoVisibleDiceQuePasoSinElMensajeDeLaBase),
            configurarMas: builder => builder.ConfigureServices(servicios =>
                servicios.AddDbContext<WaysDbContext>((_, opciones) => opciones.AddInterceptors(interceptor))));
        var articulo = await SembrarArticuloAsync(e.Datos, "Artículo de la carrera");

        var resultado = await AsociarCodigoDeProveedorAsync(e, articulo.Id, e.Datos.IdProveedor, "CARR-2");

        Assert.True(resultado.EsError, resultado.Texto);
        Assert.Equal(
            "No se pudo asociar el código CARR-2. Ya existe ese código de proveedor para ese proveedor en este tenant. " +
            "No se pudo identificar a qué artículo pertenece. No se escribió nada.",
            resultado.Texto);
        Assert.Equal(1, interceptor.Disparos);
        Assert.Empty(await CodigosDeProveedorEnLaBaseAsync(articulo.Id));
    }

    /// <summary>Rompe el primer INSERT sobre <c>tabla</c> con un 23505 de <c>indice</c>, como lo haría otra request
    /// que comiteó el mismo código entre el chequeo y el alta, pero sin dejar ninguna fila.</summary>
    private sealed class InterceptorQueSimulaUnaViolacionUnica(string tabla, string indice) : DbCommandInterceptor
    {
        private int _disparos;

        public int Disparos => Volatile.Read(ref _disparos);

        public override ValueTask<InterceptionResult<DbDataReader>> ReaderExecutingAsync(
            DbCommand command, CommandEventData eventData, InterceptionResult<DbDataReader> result,
            CancellationToken cancellationToken = default)
        {
            if (command.CommandText.Contains($"INSERT INTO {tabla} (", StringComparison.Ordinal)
                && Interlocked.Increment(ref _disparos) == 1)
            {
                throw new PostgresException(
                    "falla inyectada por la prueba", "ERROR", "ERROR", PostgresErrorCodes.UniqueViolation, constraintName: indice);
            }

            return base.ReaderExecutingAsync(command, eventData, result, cancellationToken);
        }
    }
}
