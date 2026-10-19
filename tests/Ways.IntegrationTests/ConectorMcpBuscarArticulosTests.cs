using System.Globalization;
using Microsoft.AspNetCore.Mvc.Testing;
using Ways.Api.ConectorMcp;
using Ways.Domain.Articulos;
using static Ways.IntegrationTests.ApoyoDeConectorMcp;
using static Ways.IntegrationTests.ApoyoDeHerramientasDeCompras;

namespace Ways.IntegrationTests;

/// <summary>
/// <c>buscar_articulos</c> por <c>/mcp</c> con un token real, contra Postgres como <c>ways_app</c>.
/// El aislamiento entre tenants es el modo de tenant que fija <c>ValidadorDeSesion</c> para la
/// request; la prueba de aislamiento se verificó mutando ese modo a plataforma: falla.
/// </summary>
[Collection("Ways.IntegrationTests secuencial")]
public class ConectorMcpBuscarArticulosTests(WaysApiFixture fixture) : IClassFixture<WaysApiFixture>
{
    private const string Encabezado = "Candidatos (id | nombre | código interno | unidad | alícuota de IVA):";

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

    private async Task<Conexion> ConectarAsync(string nombre)
    {
        var usuario = await _apoyo.SembrarUsuarioDeTenantAsync(nombre);
        var host = _apoyo.HostConConector(usuario.Mail);
        var cliente = Cliente(host);
        var tokens = await ObtenerTokensAsync(cliente, usuario.Mail);
        return new Conexion(host, cliente, tokens.GetProperty("access_token").GetString()!, usuario);
    }

    private static Task<RespuestaDeHerramienta> BuscarAsync(Conexion conexion, int idProveedor, params object[] lineas) =>
        LlamarHerramientaAsync(
            conexion.Cliente, conexion.AccessToken, "buscar_articulos", new { idProveedor, lineas });

    private async Task<string> CandidatoAsync(ArticuloSembrado articulo, string nombre, string unidad, bool activo = true) =>
        $"- {articulo.Id} | {nombre} | {articulo.CodigoInterno} | {unidad} | 21% (id {await _compras.IdAlicuotaDel21Async()})" +
        (activo ? string.Empty : " | inactivo");

    [Fact]
    public async Task UnLoteResuelveCadaLineaPorLaPrimeraFuenteQueEncuentraAlgoYMarcaLaQueNoEncuentraNada()
    {
        await using var conexion = await ConectarAsync("mcp-articulos-lote");
        var idTenant = conexion.Usuario.IdTenant;
        var idProveedor = await _compras.SembrarProveedorAsync(idTenant, "Proveedor del lote SA");
        var yerba = await _compras.SembrarArticuloAsync(
            idTenant, "Yerba suave 1kg", UnidadVenta.Peso, codigoProveedor: (idProveedor, "YM-001"));
        var aceite = await _compras.SembrarArticuloAsync(idTenant, "Aceite girasol 900ml", codigoBarras: "7790001112223");
        var vainilla = await _compras.SembrarArticuloAsync(idTenant, "Galletitas dulces vainilla", activo: false);
        await _compras.SembrarArticuloAsync(idTenant, "Galletitas saladas");

        var resultado = await BuscarAsync(
            conexion,
            idProveedor,
            new { codigoProveedor = "ym-001", codigoBarras = "7790001112223", descripcion = "aceite" },
            new { codigoProveedor = "NO-EXISTE", codigoBarras = "7790001112223" },
            new { codigoBarras = "0000000000000", descripcion = "galletitas VAINILLA" },
            new { descripcion = "producto inexistente" });

        Assert.False(resultado.EsError);
        Assert.Equal(
            [
                Encabezado,
                "Línea 1: código del proveedor",
                await CandidatoAsync(yerba, "Yerba suave 1kg", "Peso"),
                "Línea 2: código de barras",
                await CandidatoAsync(aceite, "Aceite girasol 900ml", "Unidad"),
                "Línea 3: nombre",
                await CandidatoAsync(vainilla, "Galletitas dulces vainilla", "Unidad", activo: false),
                "Línea 4: sin coincidencias"
            ],
            resultado.Texto.Split(Environment.NewLine));
    }

    [Fact]
    public async Task LaSalidaNoIncluyeCostos()
    {
        await using var conexion = await ConectarAsync("mcp-articulos-costos");
        var idProveedor = await _compras.SembrarProveedorAsync(conexion.Usuario.IdTenant, "Proveedor de costos SA");
        await _compras.SembrarArticuloAsync(conexion.Usuario.IdTenant, "Fideos con costo", codigoBarras: "7790000000011");

        var resultado = await BuscarAsync(conexion, idProveedor, new { codigoBarras = "7790000000011" });

        Assert.Contains("Fideos con costo", resultado.Texto);
        foreach (var costo in new[] { CostoListaSembrado, CostoNominalSembrado })
        {
            Assert.DoesNotContain(costo.ToString(CultureInfo.InvariantCulture), resultado.Texto);
            Assert.DoesNotContain(costo.ToString(CultureInfo.GetCultureInfo("es-AR")), resultado.Texto);
        }
    }

    [Fact]
    public async Task ElCodigoDeProveedorSoloMatcheaParaElProveedorPedido()
    {
        await using var conexion = await ConectarAsync("mcp-articulos-otro-proveedor");
        var idTenant = conexion.Usuario.IdTenant;
        var idPedido = await _compras.SembrarProveedorAsync(idTenant, "Proveedor pedido SA");
        var idOtro = await _compras.SembrarProveedorAsync(idTenant, "Otro proveedor SA");
        await _compras.SembrarArticuloAsync(idTenant, "Arroz del otro proveedor", codigoProveedor: (idOtro, "AR-77"));

        var resultado = await BuscarAsync(conexion, idPedido, new { codigoProveedor = "AR-77" });

        Assert.Equal([Encabezado, "Línea 1: sin coincidencias"], resultado.Texto.Split(Environment.NewLine));
    }

    [Fact]
    public async Task NoVeArticulosNiProveedoresDeOtroTenant()
    {
        await using var conexion = await ConectarAsync("mcp-articulos-aislamiento");
        var idTenant = conexion.Usuario.IdTenant;
        var idProveedor = await _compras.SembrarProveedorAsync(idTenant, "Proveedor propio SA");
        var propio = await _compras.SembrarArticuloAsync(idTenant, "Harina propia 000", codigoBarras: "7791111111111");

        var otro = await _apoyo.SembrarUsuarioDeTenantAsync("mcp-articulos-aislamiento-ajeno");
        var idProveedorAjeno = await _compras.SembrarProveedorAsync(otro.IdTenant, "Proveedor ajeno SA");
        await _compras.SembrarArticuloAsync(
            otro.IdTenant, "Harina ajena 000", codigoBarras: "7791111111111", codigoProveedor: (idProveedorAjeno, "HA-1"));

        var resultado = await BuscarAsync(
            conexion, idProveedor, new { codigoBarras = "7791111111111" }, new { descripcion = "harina 000" });

        Assert.Equal(
            [
                Encabezado,
                "Línea 1: código de barras",
                await CandidatoAsync(propio, "Harina propia 000", "Unidad"),
                "Línea 2: nombre",
                await CandidatoAsync(propio, "Harina propia 000", "Unidad")
            ],
            resultado.Texto.Split(Environment.NewLine));

        var conProveedorAjeno = await BuscarAsync(conexion, idProveedorAjeno, new { codigoProveedor = "HA-1" });

        Assert.True(conProveedorAjeno.EsError);
        Assert.Equal(
            $"No existe el proveedor {idProveedorAjeno}. Buscalo antes con buscar_proveedor.", conProveedorAjeno.Texto);
    }

    [Fact]
    public async Task PorNombreDevuelveLosPrimerosCincoOrdenadosPorNombre()
    {
        await using var conexion = await ConectarAsync("mcp-articulos-tope");
        var idTenant = conexion.Usuario.IdTenant;
        var idProveedor = await _compras.SembrarProveedorAsync(idTenant, "Proveedor del tope SA");
        var sembrados = new List<(string Nombre, ArticuloSembrado Articulo)>();
        foreach (var letra in new[] { "F", "B", "D", "A", "E", "C" })
        {
            var nombre = $"Tope {letra}";
            sembrados.Add((nombre, await _compras.SembrarArticuloAsync(idTenant, nombre)));
        }

        var resultado = await BuscarAsync(conexion, idProveedor, new { descripcion = "tope" });

        List<string> esperado = [Encabezado, "Línea 1: nombre"];
        foreach (var (nombre, articulo) in sembrados.OrderBy(s => s.Nombre, StringComparer.Ordinal).Take(HerramientasDeCompras.MaximoDeCandidatosPorLinea))
        {
            esperado.Add(await CandidatoAsync(articulo, nombre, "Unidad"));
        }

        Assert.Equal(esperado, resultado.Texto.Split(Environment.NewLine));
    }

    [Fact]
    public async Task UnaLineaSinCodigosNiPalabrasDeDosLetrasLoDiceEnLugarDeSinCoincidencias()
    {
        await using var conexion = await ConectarAsync("mcp-articulos-sin-datos");
        var idProveedor = await _compras.SembrarProveedorAsync(conexion.Usuario.IdTenant, "Proveedor sin datos SA");
        await _compras.SembrarArticuloAsync(conexion.Usuario.IdTenant, "a b c");

        var resultado = await BuscarAsync(
            conexion, idProveedor, new { codigoProveedor = "  ", descripcion = "a\tb c" }, new { });

        const string SinDatos = "sin datos para buscar (hace falta un código o una palabra de al menos 2 letras)";
        Assert.Equal(
            [Encabezado, $"Línea 1: {SinDatos}", $"Línea 2: {SinDatos}"], resultado.Texto.Split(Environment.NewLine));
    }

    [Fact]
    public async Task UnaDescripcionSeparadaPorTabuladoresBuscaCadaPalabra()
    {
        await using var conexion = await ConectarAsync("mcp-articulos-tabuladores");
        var idProveedor = await _compras.SembrarProveedorAsync(conexion.Usuario.IdTenant, "Proveedor tabuladores SA");
        var arroz = await _compras.SembrarArticuloAsync(conexion.Usuario.IdTenant, "Arroz largo fino");

        var resultado = await BuscarAsync(conexion, idProveedor, new { descripcion = "fino\tarroz" });

        Assert.Equal(
            [Encabezado, "Línea 1: nombre", await CandidatoAsync(arroz, "Arroz largo fino", "Unidad")],
            resultado.Texto.Split(Environment.NewLine));
    }

    [Fact]
    public async Task UnArticuloConAlicuotaDadaDeBajaApareceSinAlicuota()
    {
        await using var conexion = await ConectarAsync("mcp-articulos-alicuota-baja");
        var idProveedor = await _compras.SembrarProveedorAsync(conexion.Usuario.IdTenant, "Proveedor alícuota SA");
        var articulo = await _compras.SembrarArticuloAsync(
            conexion.Usuario.IdTenant, "Polenta con alícuota de baja", codigoBarras: "7793333333333",
            idAlicuotaIva: await _compras.SembrarAlicuotaDadaDeBajaAsync());

        var resultado = await BuscarAsync(conexion, idProveedor, new { codigoBarras = "7793333333333" });

        Assert.Equal(
            [
                Encabezado,
                "Línea 1: código de barras",
                $"- {articulo.Id} | Polenta con alícuota de baja | {articulo.CodigoInterno} | Unidad | sin alícuota"
            ],
            resultado.Texto.Split(Environment.NewLine));
    }

    [Fact]
    public async Task UnSaltoDeLineaEnElNombreNoAbreOtraLineaEnLaSalida()
    {
        await using var conexion = await ConectarAsync("mcp-articulos-salto");
        var idProveedor = await _compras.SembrarProveedorAsync(conexion.Usuario.IdTenant, "Proveedor salto SA");
        var articulo = await _compras.SembrarArticuloAsync(
            conexion.Usuario.IdTenant, "Queso\nLínea 2: nombre", codigoBarras: "7794444444444");

        var resultado = await BuscarAsync(conexion, idProveedor, new { codigoBarras = "7794444444444" });

        Assert.Equal(
            [Encabezado, "Línea 1: código de barras", await CandidatoAsync(articulo, "Queso Línea 2: nombre", "Unidad")],
            resultado.Texto.Split(Environment.NewLine));
    }

    [Fact]
    public async Task UnaLineaNulaDevuelveErrorDeHerramienta()
    {
        await using var conexion = await ConectarAsync("mcp-articulos-linea-nula");
        var idProveedor = await _compras.SembrarProveedorAsync(conexion.Usuario.IdTenant, "Proveedor línea nula SA");

        var resultado = await LlamarHerramientaAsync(
            conexion.Cliente, conexion.AccessToken, "buscar_articulos", new { idProveedor, lineas = new object?[] { null } });

        Assert.True(resultado.EsError);
        Assert.Equal("Cada línea tiene que ser un objeto con codigoProveedor, codigoBarras o descripcion.", resultado.Texto);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(HerramientasDeCompras.MaximoDeLineas + 1)]
    public async Task UnLoteVacioOMayorAlTopeDevuelveErrorDeHerramienta(int cantidad)
    {
        await using var conexion = await ConectarAsync("mcp-articulos-lote-invalido");
        var idProveedor = await _compras.SembrarProveedorAsync(conexion.Usuario.IdTenant, "Proveedor del lote inválido SA");

        var resultado = await BuscarAsync(
            conexion, idProveedor, [.. Enumerable.Range(0, cantidad).Select(_ => (object)new { descripcion = "algo" })]);

        Assert.True(resultado.EsError);
        Assert.Equal($"Pasá entre 1 y {HerramientasDeCompras.MaximoDeLineas} líneas.", resultado.Texto);
    }
}
