using System.Data.Common;
using System.Globalization;
using System.Net;
using System.Net.Http.Json;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Ways.Application.Reportes;
using Ways.Application.Usuarios;
using Ways.Domain.Usuarios;
using Ways.Domain.Ventas;
using static Ways.IntegrationTests.ApoyoDeConsultasMcp;

namespace Ways.IntegrationTests;

/// <summary>
/// Las consultas por línea del conector MCP: <c>ventas_agrupadas</c> por área, categoría y marca, y
/// <c>ventas_por_articulo</c>, de punta a punta con token OAuth real, Postgres como <c>ways_app</c> (RLS
/// activo) y reloj fijo 2026-10-18T12:00Z (las 09:00 del 18 en Buenos Aires). Todos los importes están
/// calculados a mano sobre el escenario de <see cref="SembrarEscenarioAsync"/>.
/// </summary>
[Collection("Ways.IntegrationTests secuencial")]
public class ConectorMcpVentasPorArticuloTests(WaysApiFixture fixture) : IClassFixture<WaysApiFixture>
{
    private static readonly DateTimeOffset RelojDelMediodia = new(2026, 10, 18, 12, 0, 0, TimeSpan.Zero);
    private static readonly DateTimeOffset HoyAlMediodia = new(2026, 10, 18, 10, 0, 0, TimeSpan.Zero);

    private readonly WaysApiFixture _fixture = fixture;
    private readonly ApoyoDeConsultasMcp _apoyo = new(fixture, new ApoyoDeConectorMcp(fixture));

    private sealed record Escenario(
        TenantDeVentas Tenant, ApoyoDeConectorMcp.UsuarioDeTenant Admin, ApoyoDeConectorMcp.UsuarioDeTenant Supervisor,
        ApoyoDeConectorMcp.UsuarioDeTenant Vendedor, int IdMarcaFresh, int IdArticuloViejo);

    private static string[] Lineas(string texto) => texto.Split(Environment.NewLine);

    /// <summary>
    /// Catálogo: áreas Bebidas y Almacén; categorías Gaseosas, Aguas y Descontinuada; marcas ColaCo, Fresh y
    /// Fresco; proveedores Distribuidora Norte y Mayorista Sur; ningún grupo.
    /// Artículos: Cola 2L (Bebidas, Gaseosas, ColaCo, Norte), Cola 500 (ídem), Agua 1L (Bebidas, Aguas, Fresh,
    /// Sur), Fideos (Almacén, Descontinuada, sin marca, Sur) y Viejo (Almacén, Aguas, Fresh, Sur).
    /// Ventas de hoy (cantidad, total): Cola 2L (2, 600) + Agua 1L (3, 300); Cola 500 (4, 400) + Fideos (1, 150) +
    /// una línea sin artículo (1, 50); Cola 2L (1, 300) + Viejo (2, 200); una NC de Cola 2L (−1, −300). Quedan afuera
    /// una venta anulada de Cola 2L (10, 9999) y una de ayer de Cola 500 (100, 5000). La línea de Fideos se vendió
    /// con el área Bebidas (snapshot) aunque el artículo es de Almacén.
    /// Después de vender: Agua 1L pasa de Aguas a Gaseosas, se da de baja la categoría Descontinuada y el artículo
    /// Viejo, que las líneas siguen referenciando. Total de líneas con artículo: 1.650.
    /// </summary>
    private async Task<Escenario> SembrarEscenarioAsync(string nombre)
    {
        var t = await _apoyo.SembrarTenantAsync(nombre);
        var admin = await _apoyo.SembrarUsuarioAsync(t, RolConocido.Admin);
        var supervisor = await _apoyo.SembrarUsuarioAsync(t, RolConocido.Supervisor);
        var vendedor = await _apoyo.SembrarUsuarioAsync(t, RolConocido.Vendedor);

        var bebidas = await _apoyo.SembrarAreaAsync(t, "Bebidas");
        var almacen = await _apoyo.SembrarAreaAsync(t, "Almacén");
        var gaseosas = await _apoyo.SembrarCategoriaAsync(t, "Gaseosas");
        var aguas = await _apoyo.SembrarCategoriaAsync(t, "Aguas");
        var descontinuada = await _apoyo.SembrarCategoriaAsync(t, "Descontinuada");
        var colaCo = await _apoyo.SembrarMarcaAsync(t, "ColaCo");
        var fresh = await _apoyo.SembrarMarcaAsync(t, "Fresh");
        await _apoyo.SembrarMarcaAsync(t, "Fresco");
        var norte = await _apoyo.SembrarProveedorAsync(t, "Distribuidora Norte");
        var sur = await _apoyo.SembrarProveedorAsync(t, "Mayorista Sur");

        var cola2 = await _apoyo.SembrarArticuloAsync(t, "Cola 2L", "COLA-2L", bebidas, gaseosas, colaCo, norte);
        var cola500 = await _apoyo.SembrarArticuloAsync(t, "Cola 500", "COLA-500", bebidas, gaseosas, colaCo, norte);
        var agua = await _apoyo.SembrarArticuloAsync(t, "Agua 1L", "AGUA-1L", bebidas, aguas, fresh, sur);
        var fideos = await _apoyo.SembrarArticuloAsync(t, "Fideos", "FID-500", almacen, descontinuada, null, sur);
        var viejo = await _apoyo.SembrarArticuloAsync(t, "Viejo", "VIEJO-1", almacen, aguas, fresh, sur);

        var empleado = admin.IdUsuario;
        await _apoyo.SembrarComprobanteConLineasAsync(t, HoyAlMediodia, empleado,
            [new(cola2, "Gaseosa cola 2 litros (foto vieja)", bebidas, 2m, 600m), new(agua, "Agua 1L", bebidas, 3m, 300m)]);
        await _apoyo.SembrarComprobanteConLineasAsync(t, HoyAlMediodia.AddMinutes(5), empleado,
            [new(cola500, "Cola 500", bebidas, 4m, 400m), new(fideos, "Fideos", bebidas, 1m, 150m), new(null, "Servicio", almacen, 1m, 50m)]);
        await _apoyo.SembrarComprobanteConLineasAsync(t, HoyAlMediodia.AddMinutes(10), empleado,
            [new(cola2, "Gaseosa cola 2 litros (foto vieja)", bebidas, 1m, 300m), new(viejo, "Viejo descontinuado", almacen, 2m, 200m)]);
        await _apoyo.SembrarComprobanteConLineasAsync(t, HoyAlMediodia.AddMinutes(15), empleado,
            [new(cola2, "Gaseosa cola 2 litros (foto vieja)", bebidas, -1m, -300m)], esNcx: true);
        await _apoyo.SembrarComprobanteConLineasAsync(t, HoyAlMediodia.AddMinutes(20), empleado,
            [new(cola2, "Gaseosa cola 2 litros (foto vieja)", bebidas, 10m, 9999m)], estado: EstadoComprobante.Anulado);
        await _apoyo.SembrarComprobanteConLineasAsync(t, new(2026, 10, 17, 15, 0, 0, TimeSpan.Zero), empleado,
            [new(cola500, "Cola 500", bebidas, 100m, 5000m)]);

        await _apoyo.CambiarCategoriaDeArticuloAsync(t, agua, gaseosas);
        await _apoyo.DarDeBajaCategoriaAsync(t, descontinuada);
        await _apoyo.DarDeBajaArticuloAsync(t, viejo);
        return new Escenario(t, admin, supervisor, vendedor, fresh, viejo);
    }

    private async Task<(Microsoft.AspNetCore.Mvc.Testing.WebApplicationFactory<Program> Host, HttpClient Cliente, string Token)> ConectarAsync(
        ApoyoDeConectorMcp.UsuarioDeTenant usuario)
    {
        var host = _apoyo.HostConReloj(RelojDelMediodia, usuario.Mail);
        var cliente = ApoyoDeConectorMcp.Cliente(host);
        return (host, cliente, await AccessTokenAsync(cliente, usuario.Mail));
    }

    /// <summary>Cláusula bajo prueba: el "(sin área)" de <c>AgruparPorClasificacionAsync</c> y la clasificación ACTUAL del
    /// artículo. Fideos se vendió con el área Bebidas pero hoy es de Almacén; Viejo ya no es visible; la NC resta, la
    /// anulada y el día anterior quedan afuera y la línea sin artículo no cuenta.</summary>
    [Fact]
    public async Task VentasAgrupadasPorAreaUsaElAreaActualDelArticuloYAgrupaLoQueNoTieneAreaVisible()
    {
        var e = await SembrarEscenarioAsync(nameof(VentasAgrupadasPorAreaUsaElAreaActualDelArticuloYAgrupaLoQueNoTieneAreaVisible));
        var (host, cliente, token) = await ConectarAsync(e.Supervisor);
        await using var _ = host;
        using var __ = cliente;

        var resultado = await LlamarHerramientaAsync(cliente, token, "ventas_agrupadas", new { agruparPor = "area", periodo = "hoy" });

        Assert.False(resultado.EsError, resultado.Texto);
        var lineas = Lineas(resultado.Texto);
        Assert.Equal(
            [
                "Ventas por área hoy (2026-10-18), zona horaria: America/Argentina/Buenos_Aires",
                lineas[1],
                "área | importe | unidades | % del total",
                "Bebidas | 1.300,00 | 9 | 78,8%",
                "(sin área) | 200,00 | 2 | 12,1%",
                "Almacén | 150,00 | 1 | 9,1%",
                "Total (suma de las líneas con artículo): 1.650,00 en 3 grupos"
            ],
            lineas);
    }

    [Fact]
    public async Task VentasAgrupadasPorGrupoSinGruposAsignadosLoDejaTodoEnSinGrupo()
    {
        var e = await SembrarEscenarioAsync(nameof(VentasAgrupadasPorGrupoSinGruposAsignadosLoDejaTodoEnSinGrupo));
        var (host, cliente, token) = await ConectarAsync(e.Admin);
        await using var _ = host;
        using var __ = cliente;

        var resultado = await LlamarHerramientaAsync(cliente, token, "ventas_agrupadas", new { agruparPor = "grupo", periodo = "hoy" });

        Assert.False(resultado.EsError, resultado.Texto);
        Assert.Equal(
            ["grupo | importe | unidades | % del total", "(sin grupo) | 1.650,00 | 12 | 100,0%",
                "Total (suma de las líneas con artículo): 1.650,00 en 1 grupos"],
            Lineas(resultado.Texto).Skip(2).ToArray());
    }

    /// <summary>Cláusula bajo prueba: el "(sin categoría)" de <c>AgruparPorClasificacionAsync</c>. La categoría
    /// Descontinuada está dada de baja (Fideos) y el artículo Viejo no es visible: ambos caen en la misma fila.
    /// Agua 1L cambió de Aguas a Gaseosas después de vender: sus ventas van a Gaseosas.</summary>
    [Fact]
    public async Task VentasAgrupadasPorCategoriaUsaLaCategoriaActualYTrataUnaFkDeBajaIgualQueNull()
    {
        var e = await SembrarEscenarioAsync(nameof(VentasAgrupadasPorCategoriaUsaLaCategoriaActualYTrataUnaFkDeBajaIgualQueNull));
        var (host, cliente, token) = await ConectarAsync(e.Admin);
        await using var _ = host;
        using var __ = cliente;

        var resultado = await LlamarHerramientaAsync(cliente, token, "ventas_agrupadas", new { agruparPor = "categoria", periodo = "hoy" });

        Assert.False(resultado.EsError, resultado.Texto);
        var lineas = Lineas(resultado.Texto);
        Assert.Equal(
            [
                "Ventas por categoría hoy (2026-10-18), zona horaria: America/Argentina/Buenos_Aires",
                lineas[1],
                "categoría | importe | unidades | % del total",
                "Gaseosas | 1.300,00 | 9 | 78,8%",
                "(sin categoría) | 350,00 | 3 | 21,2%",
                "Total (suma de las líneas con artículo): 1.650,00 en 2 grupos"
            ],
            lineas);
    }

    /// <summary>Cláusula bajo prueba: el LEFT JOIN a marcas y el "(sin marca)" de <c>AgruparPorClasificacionAsync</c>.</summary>
    [Fact]
    public async Task VentasAgrupadasPorMarcaUsaLaMarcaActualDelArticuloYAgrupaLoQueNoTieneMarcaVisible()
    {
        var e = await SembrarEscenarioAsync(nameof(VentasAgrupadasPorMarcaUsaLaMarcaActualDelArticuloYAgrupaLoQueNoTieneMarcaVisible));
        var (host, cliente, token) = await ConectarAsync(e.Admin);
        await using var _ = host;
        using var __ = cliente;

        var resultado = await LlamarHerramientaAsync(cliente, token, "ventas_agrupadas", new { agruparPor = "marca", periodo = "hoy" });

        Assert.False(resultado.EsError, resultado.Texto);
        var lineas = Lineas(resultado.Texto);
        Assert.Equal(
            [
                "Ventas por marca hoy (2026-10-18), zona horaria: America/Argentina/Buenos_Aires",
                lineas[1],
                "marca | importe | unidades | % del total",
                "ColaCo | 1.000,00 | 6 | 60,6%",
                "(sin marca) | 350,00 | 3 | 21,2%",
                "Fresh | 300,00 | 3 | 18,2%",
                "Total (suma de las líneas con artículo): 1.650,00 en 3 grupos"
            ],
            lineas);
    }

    [Fact]
    public async Task VentasPorArticuloListaPorImporteConElNombreActualYLaDescripcionDeLaLineaSiElArticuloYaNoEsVisible()
    {
        var e = await SembrarEscenarioAsync(nameof(VentasPorArticuloListaPorImporteConElNombreActualYLaDescripcionDeLaLineaSiElArticuloYaNoEsVisible));
        var (host, cliente, token) = await ConectarAsync(e.Supervisor);
        await using var _ = host;
        using var __ = cliente;

        var resultado = await LlamarHerramientaAsync(cliente, token, "ventas_por_articulo", new { periodo = "hoy" });

        Assert.False(resultado.EsError, resultado.Texto);
        var lineas = Lineas(resultado.Texto);
        Assert.Equal(
            [
                "Ventas por artículo hoy (2026-10-18), zona horaria: America/Argentina/Buenos_Aires",
                lineas[1],
                "Orden: importe",
                "código | artículo | cantidad | importe | % del total",
                "COLA-2L | Cola 2L | 2 | 600,00 | 36,4%",
                "COLA-500 | Cola 500 | 4 | 400,00 | 24,2%",
                "AGUA-1L | Agua 1L | 3 | 300,00 | 18,2%",
                "(sin código) | Viejo descontinuado | 2 | 200,00 | 12,1%",
                "FID-500 | Fideos | 1 | 150,00 | 9,1%",
                "Total de los artículos filtrados: 1.650,00 en 5 artículos",
                "Total de todos los artículos vendidos en el período (base del %): 1.650,00"
            ],
            lineas);
    }

    [Fact]
    public async Task VentasPorArticuloOrdenaPorCantidadDistintoQuePorImporteYCortaConElLimite()
    {
        var e = await SembrarEscenarioAsync(nameof(VentasPorArticuloOrdenaPorCantidadDistintoQuePorImporteYCortaConElLimite));
        var (host, cliente, token) = await ConectarAsync(e.Admin);
        await using var _ = host;
        using var __ = cliente;

        var porCantidad = await LlamarHerramientaAsync(cliente, token, "ventas_por_articulo", new { periodo = "hoy", orden = "cantidad" });
        var cortado = await LlamarHerramientaAsync(cliente, token, "ventas_por_articulo", new { periodo = "hoy", limite = 2 });

        // Cola 2L y Viejo empatan en 2 unidades: desempata el importe.
        Assert.Equal(
            ["COLA-500 | Cola 500 | 4 | 400,00 | 24,2%", "AGUA-1L | Agua 1L | 3 | 300,00 | 18,2%",
                "COLA-2L | Cola 2L | 2 | 600,00 | 36,4%", "(sin código) | Viejo descontinuado | 2 | 200,00 | 12,1%",
                "FID-500 | Fideos | 1 | 150,00 | 9,1%"],
            Lineas(porCantidad.Texto).Skip(4).Take(5).ToArray());
        Assert.Contains("Orden: cantidad", Lineas(porCantidad.Texto));

        Assert.Equal(
            ["COLA-2L | Cola 2L | 2 | 600,00 | 36,4%", "COLA-500 | Cola 500 | 4 | 400,00 | 24,2%", "… y 3 más",
                "Total de los artículos filtrados: 1.650,00 en 5 artículos"],
            Lineas(cortado.Texto).Skip(4).Take(4).ToArray());
    }

    // Cada caso fija una cláusula: categoria/area/articulo acotan con los criterios del reporte (AcotarComoElReporteAsync);
    // marca, proveedor y texto recortan por los artículos que coinciden. Agua 1L ya es Gaseosas (cambió después de
    // vender). Viejo está dado de baja: los criterios del reporte (categoría y área) lo siguen contando con su
    // clasificación, los propios del conector (marca, proveedor, texto) no lo alcanzan.
    [Theory]
    [InlineData("""{"categoria":"gaseosas"}""", "categoría Gaseosas", "1.300,00", new[] { "COLA-2L", "COLA-500", "AGUA-1L" })]
    [InlineData("""{"categoria":"aguas"}""", "categoría Aguas", "200,00", new[] { "(sin código)" })]
    [InlineData("""{"area":"almacén"}""", "área Almacén", "350,00", new[] { "(sin código)", "FID-500" })]
    [InlineData("""{"articulo":"cola-500"}""", "artículo Cola 500", "400,00", new[] { "COLA-500" })]
    [InlineData("""{"marca":"FRESH"}""", "marca Fresh", "300,00", new[] { "AGUA-1L" })]
    [InlineData("""{"proveedor":"norte"}""", "proveedor Distribuidora Norte", "1.000,00", new[] { "COLA-2L", "COLA-500" })]
    [InlineData("""{"texto":"COLA"}""", "texto 'COLA'", "1.000,00", new[] { "COLA-2L", "COLA-500" })]
    [InlineData("""{"texto":"agua-1l"}""", "texto 'agua-1l'", "300,00", new[] { "AGUA-1L" })]
    [InlineData("""{"texto":"fid"}""", "texto 'fid'", "150,00", new[] { "FID-500" })]
    [InlineData("""{"marca":"colaco","proveedor":"sur"}""", "marca ColaCo, proveedor Mayorista Sur", "0,00", new string[0])]
    // Un filtro del reporte combinado con uno propio: el % sigue siendo sobre los 1.650 sin filtros.
    [InlineData("""{"categoria":"gaseosas","marca":"fresh"}""", "categoría Gaseosas, marca Fresh", "300,00", new[] { "AGUA-1L" })]
    public async Task VentasPorArticuloAcotaLasFilasPeroElPorcentajeSigueSiendoSobreTodoLoVendido(
        string filtrosJson, string filtroEsperado, string importeFiltrado, string[] codigosEsperados)
    {
        var e = await SembrarEscenarioAsync(nameof(VentasPorArticuloAcotaLasFilasPeroElPorcentajeSigueSiendoSobreTodoLoVendido));
        var (host, cliente, token) = await ConectarAsync(e.Admin);
        await using var _ = host;
        using var __ = cliente;
        var argumentos = new Dictionary<string, object?> { ["periodo"] = "hoy" };
        foreach (var propiedad in System.Text.Json.JsonDocument.Parse(filtrosJson).RootElement.EnumerateObject())
        {
            argumentos[propiedad.Name] = propiedad.Value.GetString();
        }

        var resultado = await LlamarHerramientaAsync(cliente, token, "ventas_por_articulo", argumentos);

        Assert.False(resultado.EsError, resultado.Texto);
        var lineas = Lineas(resultado.Texto);
        Assert.Equal(filtroEsperado, lineas[2].Replace("Filtros: ", string.Empty, StringComparison.Ordinal));
        var filas = lineas.Skip(5).TakeWhile(l => !l.StartsWith("Total de los", StringComparison.Ordinal)).ToArray();
        Assert.Equal(codigosEsperados, filas.Select(f => f.Split(" | ")[0]).ToArray());

        Assert.Contains($"Total de los artículos filtrados: {importeFiltrado} en {codigosEsperados.Length} artículos", lineas);
        Assert.Contains("Total de todos los artículos vendidos en el período (base del %): 1.650,00", lineas);

        // El porcentaje de cada fila es importe / 1.650 sin importar el filtro: Cola 2L = 600 => 36,4% y Agua 1L = 300 => 18,2%.
        if (codigosEsperados.Contains("COLA-2L"))
        {
            Assert.Contains("COLA-2L | Cola 2L | 2 | 600,00 | 36,4%", lineas);
        }

        if (codigosEsperados.Contains("AGUA-1L"))
        {
            Assert.Contains("AGUA-1L | Agua 1L | 3 | 300,00 | 18,2%", lineas);
        }
    }

    /// <summary>Cláusula bajo prueba: una sola lectura de las líneas vendidas por empresa aunque haya un filtro del
    /// reporte. El porcentaje sale de esa misma lectura (lo fija
    /// <see cref="VentasPorArticuloAcotaLasFilasPeroElPorcentajeSigueSiendoSobreTodoLoVendido"/>); acá se cuentan las
    /// consultas a <c>items_comprobante_venta</c>, una por empresa del alcance (el escenario tiene una).</summary>
    [Fact]
    public async Task VentasPorArticuloConUnFiltroDelReporteLeeLasLineasUnaSolaVezPorEmpresa()
    {
        var e = await SembrarEscenarioAsync(nameof(VentasPorArticuloConUnFiltroDelReporteLeeLasLineasUnaSolaVezPorEmpresa));
        var contador = new ContadorDeLecturasDeLineas();
        await using var host = _apoyo.HostConRelojEInterceptor(RelojDelMediodia, contador, e.Admin.Mail);
        using var cliente = ApoyoDeConectorMcp.Cliente(host);
        var token = await AccessTokenAsync(cliente, e.Admin.Mail);
        var antes = contador.Lecturas;

        var resultado = await LlamarHerramientaAsync(
            cliente, token, "ventas_por_articulo", new { periodo = "hoy", categoria = "gaseosas" });

        Assert.False(resultado.EsError, resultado.Texto);
        Assert.Contains("Total de todos los artículos vendidos en el período (base del %): 1.650,00", Lineas(resultado.Texto));
        Assert.Equal(1, contador.Lecturas - antes);
    }

    /// <summary>Cláusula bajo prueba: el filtro de categoría incluye sus subcategorías. Cola 500 pasa a Light, hija de
    /// Gaseosas: sigue contando en Gaseosas y es lo único de Light.</summary>
    [Theory]
    [InlineData("gaseosas", "categoría Gaseosas", "1.300,00", new[] { "COLA-2L", "COLA-500", "AGUA-1L" })]
    [InlineData("light", "categoría Light", "400,00", new[] { "COLA-500" })]
    public async Task VentasPorArticuloFiltraUnaCategoriaConSusSubcategorias(
        string categoria, string filtroEsperado, string importeFiltrado, string[] codigosEsperados)
    {
        var e = await SembrarEscenarioAsync(nameof(VentasPorArticuloFiltraUnaCategoriaConSusSubcategorias));
        var light = await _apoyo.SembrarCategoriaAsync(e.Tenant, "Light");
        await _apoyo.AsignarCategoriaPadreAsync(e.Tenant, light, await _apoyo.IdDeCategoriaAsync(e.Tenant, "Gaseosas"));
        await _apoyo.CambiarCategoriaDeArticuloAsync(e.Tenant, await _apoyo.IdDeArticuloAsync(e.Tenant, "COLA-500"), light);
        var (host, cliente, token) = await ConectarAsync(e.Admin);
        await using var _ = host;
        using var __ = cliente;

        var resultado = await LlamarHerramientaAsync(cliente, token, "ventas_por_articulo", new { periodo = "hoy", categoria });

        Assert.False(resultado.EsError, resultado.Texto);
        var lineas = Lineas(resultado.Texto);
        Assert.Equal(filtroEsperado, lineas[2].Replace("Filtros: ", string.Empty, StringComparison.Ordinal));
        var filas = lineas.Skip(5).TakeWhile(l => !l.StartsWith("Total de los", StringComparison.Ordinal)).ToArray();
        Assert.Equal(codigosEsperados, filas.Select(f => f.Split(" | ")[0]).ToArray());
        Assert.Contains($"Total de los artículos filtrados: {importeFiltrado} en {codigosEsperados.Length} artículos", lineas);
        Assert.Contains("Total de todos los artículos vendidos en el período (base del %): 1.650,00", lineas);
    }

    /// <summary>Cláusula bajo prueba: el filtro de grupo (el escenario no asigna grupos). Cola 500 pasa a Combos.</summary>
    [Fact]
    public async Task VentasPorArticuloFiltraPorElGrupoActualDelArticulo()
    {
        var e = await SembrarEscenarioAsync(nameof(VentasPorArticuloFiltraPorElGrupoActualDelArticulo));
        await _apoyo.SembrarGrupoDeArticuloAsync(e.Tenant, "Combos", await _apoyo.IdDeArticuloAsync(e.Tenant, "COLA-500"));
        var (host, cliente, token) = await ConectarAsync(e.Admin);
        await using var _ = host;
        using var __ = cliente;

        var resultado = await LlamarHerramientaAsync(cliente, token, "ventas_por_articulo", new { periodo = "hoy", grupo = "combos" });

        Assert.False(resultado.EsError, resultado.Texto);
        var lineas = Lineas(resultado.Texto);
        Assert.Equal("grupo Combos", lineas[2].Replace("Filtros: ", string.Empty, StringComparison.Ordinal));
        Assert.Contains("COLA-500 | Cola 500 | 4 | 400,00 | 24,2%", lineas);
        Assert.Contains("Total de los artículos filtrados: 400,00 en 1 artículos", lineas);
    }

    /// <summary>Cuenta las consultas que leen líneas de comprobantes de venta.</summary>
    private sealed class ContadorDeLecturasDeLineas : DbCommandInterceptor
    {
        private int _lecturas;

        public int Lecturas => Volatile.Read(ref _lecturas);

        public override ValueTask<InterceptionResult<DbDataReader>> ReaderExecutingAsync(
            DbCommand command, CommandEventData eventData, InterceptionResult<DbDataReader> result,
            CancellationToken cancellationToken = default)
        {
            if (command.CommandText.Contains("FROM items_comprobante_venta", StringComparison.Ordinal))
            {
                Interlocked.Increment(ref _lecturas);
            }

            return base.ReaderExecutingAsync(command, eventData, result, cancellationToken);
        }
    }

    /// <summary>Cláusulas bajo prueba, una por fila: los dos brazos (nombre y código interno) del "contiene" de
    /// <c>texto</c> y del paso "contiene" de la búsqueda de <c>articulo</c>, todos con
    /// <c>BusquedaSinAcentos.Coincide</c>. "FIDÉOS" y "fidéo" coinciden solo con el nombre Fideos; "fíd-5" solo con
    /// el código FID-500. Ninguno coincide con un paso exacto, así que solo el "contiene" sin acentos los encuentra.</summary>
    [Theory]
    [InlineData("texto", "FIDÉOS", "texto 'FIDÉOS'")]
    [InlineData("texto", "fíd-5", "texto 'fíd-5'")]
    [InlineData("articulo", "fidéo", "artículo Fideos")]
    [InlineData("articulo", "fíd-5", "artículo Fideos")]
    public async Task VentasPorArticuloBuscaElTextoYElArticuloSinDistinguirAcentos(string filtro, string valor, string filtroEsperado)
    {
        var e = await SembrarEscenarioAsync(nameof(VentasPorArticuloBuscaElTextoYElArticuloSinDistinguirAcentos));
        var (host, cliente, token) = await ConectarAsync(e.Admin);
        await using var _ = host;
        using var __ = cliente;

        var resultado = await LlamarHerramientaAsync(
            cliente, token, "ventas_por_articulo", new Dictionary<string, object?> { ["periodo"] = "hoy", [filtro] = valor });

        Assert.False(resultado.EsError, resultado.Texto);
        var lineas = Lineas(resultado.Texto);
        Assert.Equal(filtroEsperado, lineas[2].Replace("Filtros: ", string.Empty, StringComparison.Ordinal));
        Assert.Contains("FID-500 | Fideos | 1 | 150,00 | 9,1%", lineas);
        Assert.Contains("Total de los artículos filtrados: 150,00 en 1 artículos", lineas);
    }

    /// <summary>Cláusulas bajo prueba: el Y entre los criterios del reporte en <c>AcotarComoElReporteAsync</c>. Con
    /// artículo y categoría, solo el artículo pedido (lo lleva la restricción a los ids, no la consulta de
    /// clasificación); con área y categoría, solo lo que cumple las dos: Agua 1L ya es Gaseosas y Viejo es de Almacén,
    /// así que Bebidas y Aguas no deja nada.</summary>
    [Theory]
    [InlineData("""{"articulo":"cola-500","categoria":"gaseosas"}""", "categoría Gaseosas, artículo Cola 500", "400,00", new[] { "COLA-500" })]
    [InlineData("""{"area":"bebidas","categoria":"aguas"}""", "área Bebidas, categoría Aguas", "0,00", new string[0])]
    public async Task VentasPorArticuloCombinaLosFiltrosDelReporteConY(
        string filtrosJson, string filtroEsperado, string importeFiltrado, string[] codigosEsperados)
    {
        var e = await SembrarEscenarioAsync(nameof(VentasPorArticuloCombinaLosFiltrosDelReporteConY));
        var (host, cliente, token) = await ConectarAsync(e.Admin);
        await using var _ = host;
        using var __ = cliente;
        var argumentos = new Dictionary<string, object?> { ["periodo"] = "hoy" };
        foreach (var propiedad in System.Text.Json.JsonDocument.Parse(filtrosJson).RootElement.EnumerateObject())
        {
            argumentos[propiedad.Name] = propiedad.Value.GetString();
        }

        var resultado = await LlamarHerramientaAsync(cliente, token, "ventas_por_articulo", argumentos);

        Assert.False(resultado.EsError, resultado.Texto);
        var lineas = Lineas(resultado.Texto);
        Assert.Equal(filtroEsperado, lineas[2].Replace("Filtros: ", string.Empty, StringComparison.Ordinal));
        var filas = lineas.Skip(5).TakeWhile(l => !l.StartsWith("Total de los", StringComparison.Ordinal)).ToArray();
        Assert.Equal(codigosEsperados, filas.Select(f => f.Split(" | ")[0]).ToArray());
        Assert.Contains($"Total de los artículos filtrados: {importeFiltrado} en {codigosEsperados.Length} artículos", lineas);
        Assert.Contains("Total de todos los artículos vendidos en el período (base del %): 1.650,00", lineas);
    }

    [Theory]
    [InlineData("""{"periodo":"hoy","categoria":"inexistente"}""", new[] { "No encontré la categoría 'inexistente'" })]
    [InlineData("""{"periodo":"hoy","categoria":"descontinuada"}""", new[] { "No encontré la categoría 'descontinuada'" })]
    [InlineData("""{"periodo":"hoy","proveedor":"zzz"}""", new[] { "No encontré el proveedor 'zzz'" })]
    [InlineData("""{"periodo":"hoy","area":"zzz"}""", new[] { "No encontré el área 'zzz'" })]
    [InlineData("""{"periodo":"hoy","grupo":"zzz"}""", new[] { "No encontré el grupo 'zzz'" })]
    [InlineData("""{"periodo":"hoy","articulo":"zzz"}""", new[] { "No encontré el artículo 'zzz'" })]
    [InlineData("""{"periodo":"hoy","articulo":"cola"}""", new[] { "'cola' es ambiguo para el artículo", "| COLA-2L | Cola 2L", "| COLA-500 | Cola 500" })]
    [InlineData("""{"periodo":"hoy","marca":"fres"}""", new[] { "'fres' es ambiguo para la marca", "Fresh (id ", "Fresco (id " })]
    [InlineData("""{"periodo":"hoy","orden":"precio"}""", new[] { "'orden' tiene que ser importe o cantidad" })]
    [InlineData("""{"periodo":"hoy","limite":51}""", new[] { "entre 1 y 50" })]
    public async Task UnFiltroQueNoCoincideOCoincideConVariosDevuelveUnErrorQueLoNombra(string argumentosJson, string[] fragmentos)
    {
        var e = await SembrarEscenarioAsync(nameof(UnFiltroQueNoCoincideOCoincideConVariosDevuelveUnErrorQueLoNombra));
        var (host, cliente, token) = await ConectarAsync(e.Admin);
        await using var _ = host;
        using var __ = cliente;
        var argumentos = System.Text.Json.JsonSerializer.Deserialize<System.Text.Json.JsonElement>(argumentosJson);

        var resultado = await LlamarHerramientaAsync(cliente, token, "ventas_por_articulo", argumentos);

        Assert.True(resultado.EsError, resultado.Texto);
        foreach (var fragmento in fragmentos)
        {
            Assert.Contains(fragmento, resultado.Texto);
        }
    }

    [Fact]
    public async Task SoloSupervisorYAdminVenVentasPorArticulo()
    {
        var e = await SembrarEscenarioAsync(nameof(SoloSupervisorYAdminVenVentasPorArticulo));
        await using var host = _apoyo.HostConReloj(RelojDelMediodia, e.Vendedor.Mail, e.Supervisor.Mail, e.Admin.Mail);
        using var cliente = ApoyoDeConectorMcp.Cliente(host);

        var delVendedor = await LlamarHerramientaAsync(
            cliente, await AccessTokenAsync(cliente, e.Vendedor.Mail), "ventas_por_articulo", new { periodo = "hoy" });
        var delSupervisor = await LlamarHerramientaAsync(
            cliente, await AccessTokenAsync(cliente, e.Supervisor.Mail), "ventas_por_articulo", new { periodo = "hoy" });
        var delAdmin = await LlamarHerramientaAsync(
            cliente, await AccessTokenAsync(cliente, e.Admin.Mail), "ventas_por_articulo", new { periodo = "hoy" });

        Assert.False(delVendedor.EsError);
        Assert.Equal("Tu rol (Vendedor) no puede ver reportes de ventas: requiere Supervisor o Admin.", delVendedor.Texto);
        foreach (var conDatos in new[] { delSupervisor, delAdmin })
        {
            Assert.False(conDatos.EsError, conDatos.Texto);
            Assert.Contains("COLA-2L | Cola 2L | 2 | 600,00 | 36,4%", Lineas(conDatos.Texto));
        }
    }

    /// <summary>Otro tenant con ventas enormes de artículos, áreas y marcas con los mismos nombres: nada de eso
    /// aparece en las consultas por artículo. El aislamiento viene por construcción del reporte reutilizado (filtro de
    /// EF, puntos de venta de la empresa y RLS): este caso no prueba una cláusula propia, y como <c>ways_app</c> el RLS
    /// ya oculta esas filas, tampoco aísla el filtro de EF por separado.</summary>
    [Fact]
    public async Task LasConsultasPorLineaNuncaMuestranLasLineasDeOtroTenant()
    {
        var a = await SembrarEscenarioAsync(nameof(LasConsultasPorLineaNuncaMuestranLasLineasDeOtroTenant) + "-a");
        var b = await _apoyo.SembrarTenantAsync(nameof(LasConsultasPorLineaNuncaMuestranLasLineasDeOtroTenant) + "-b");
        var adminB = await _apoyo.SembrarUsuarioAsync(b, RolConocido.Admin);
        var areaB = await _apoyo.SembrarAreaAsync(b, "Bebidas");
        var categoriaB = await _apoyo.SembrarCategoriaAsync(b, "Gaseosas");
        var marcaB = await _apoyo.SembrarMarcaAsync(b, "ColaCo");
        var articuloB = await _apoyo.SembrarArticuloAsync(b, "Cola 2L", "COLA-2L", areaB, categoriaB, marcaB);
        await _apoyo.SembrarComprobanteConLineasAsync(
            b, HoyAlMediodia, adminB.IdUsuario, [new(articuloB, "Cola 2L", areaB, 1000m, 123456m)]);

        var (host, cliente, token) = await ConectarAsync(a.Admin);
        await using var _ = host;
        using var __ = cliente;

        var porArticulo = await LlamarHerramientaAsync(cliente, token, "ventas_por_articulo", new { periodo = "hoy" });
        var porCategoria = await LlamarHerramientaAsync(cliente, token, "ventas_agrupadas", new { agruparPor = "categoria", periodo = "hoy" });

        Assert.Contains("COLA-2L | Cola 2L | 2 | 600,00 | 36,4%", Lineas(porArticulo.Texto));
        Assert.Contains("Total de los artículos filtrados: 1.650,00 en 5 artículos", Lineas(porArticulo.Texto));
        Assert.Contains("Gaseosas | 1.300,00 | 9 | 78,8%", Lineas(porCategoria.Texto));
        Assert.DoesNotContain("123.456", porArticulo.Texto);
        Assert.DoesNotContain("123.456", porCategoria.Texto);
    }

    /// <summary>Cláusula bajo prueba: el agrupamiento por ID de la clasificación en <c>AgruparPorClasificacionAsync</c>.
    /// Dos categorías llamadas "Varios", una compartida y otra propia de la empresa (el índice único de nombre es por
    /// alcance, no por padre), son dos filas; la etiqueta lleva el id solo porque comparten nombre y "Solo" no lo lleva.</summary>
    [Fact]
    public async Task DosCategoriasConElMismoNombreEnAlcancesDistintosSiguenSiendoDosFilas()
    {
        var t = await _apoyo.SembrarTenantAsync(nameof(DosCategoriasConElMismoNombreEnAlcancesDistintosSiguenSiendoDosFilas));
        var supervisor = await _apoyo.SembrarUsuarioAsync(t, RolConocido.Supervisor);
        var area = await _apoyo.SembrarAreaAsync(t, "Bebidas");
        var variosUno = await _apoyo.SembrarCategoriaAsync(t, "Varios");
        var variosDos = await _apoyo.SembrarCategoriaAsync(t, "Varios", t.IdEmpresa);
        var solo = await _apoyo.SembrarCategoriaAsync(t, "Solo");
        var articuloUno = await _apoyo.SembrarArticuloAsync(t, "Uno", "UNO", area, variosUno);
        var articuloDos = await _apoyo.SembrarArticuloAsync(t, "Dos", "DOS", area, variosDos);
        var articuloSolo = await _apoyo.SembrarArticuloAsync(t, "Tres", "TRES", area, solo);
        await _apoyo.SembrarComprobanteConLineasAsync(t, HoyAlMediodia, supervisor.IdUsuario,
            [new(articuloUno, "Uno", area, 1m, 100m), new(articuloDos, "Dos", area, 2m, 200m), new(articuloSolo, "Tres", area, 1m, 50m)]);
        await using var host = _apoyo.HostConReloj(RelojDelMediodia, supervisor.Mail);
        using var cliente = ApoyoDeConectorMcp.Cliente(host);

        var resultado = await LlamarHerramientaAsync(
            cliente, await AccessTokenAsync(cliente, supervisor.Mail), "ventas_agrupadas", new { agruparPor = "categoria", periodo = "hoy" });

        Assert.False(resultado.EsError, resultado.Texto);
        Assert.Equal(
            [
                "categoría | importe | unidades | % del total",
                $"Varios (id {variosDos}) | 200,00 | 2 | 57,1%",
                $"Varios (id {variosUno}) | 100,00 | 1 | 28,6%",
                "Solo | 50,00 | 1 | 14,3%",
                "Total (suma de las líneas con artículo): 350,00 en 3 grupos"
            ],
            Lineas(resultado.Texto).Skip(2).ToArray());
    }

    private sealed record FiltroDeParidad(
        string Nombre, Dictionary<string, object?> Herramienta, int? IdArea, int? IdGrupo, int? IdCategoria, int? IdArticulo);

    /// <summary>Cantidad e importe como texto invariante sin ceros de más: el reporte trae la escala de la columna
    /// (2,000) y la herramienta la de su formato.</summary>
    private sealed record FilaDeParidad(string Codigo, string Articulo, string Cantidad, string Importe)
    {
        public FilaDeParidad(string codigo, string articulo, decimal cantidad, decimal importe)
            : this(codigo, articulo, Normal(cantidad), Normal(importe))
        {
        }

        private static string Normal(decimal valor) => valor.ToString("0.####", CultureInfo.InvariantCulture);
    }

    private static readonly NumberFormatInfo FormatoDeLaHerramienta = new()
    {
        NumberDecimalSeparator = ",",
        NumberGroupSeparator = "."
    };

    private static decimal Numero(string texto) =>
        decimal.Parse(texto.TrimEnd('%'), NumberStyles.Number, FormatoDeLaHerramienta);

    /// <summary>Paridad del acotado en memoria de <c>ventas_por_articulo</c> (<c>AcotarComoElReporteAsync</c>) con el
    /// reporte web <c>GET /api/reportes/ventas/por-articulo</c>, que filtra en SQL: para cada filtro del reporte
    /// (área, grupo, categoría con una subcategoría, artículo) y tres combinaciones, la herramienta devuelve las
    /// mismas filas que el endpoint con los mismos ids, con el código, el nombre, la cantidad y el importe de cada una,
    /// y el mismo total. El escenario suma un grupo (Combos, solo Cola 500) y una subcategoría de Gaseosas (Light,
    /// solo Cola 500); Viejo, dado de baja, sigue contando en Almacén en los dos lados.</summary>
    [Fact]
    public async Task VentasPorArticuloAcotaComoElReporteWebParaCadaFiltroDelReporte()
    {
        var e = await SembrarEscenarioAsync(nameof(VentasPorArticuloAcotaComoElReporteWebParaCadaFiltroDelReporte));
        var cola500 = await _apoyo.IdDeArticuloAsync(e.Tenant, "COLA-500");
        var cola2 = await _apoyo.IdDeArticuloAsync(e.Tenant, "COLA-2L");
        var combos = await _apoyo.SembrarGrupoDeArticuloAsync(e.Tenant, "Combos", cola500);
        var gaseosas = await _apoyo.IdDeCategoriaAsync(e.Tenant, "Gaseosas");
        var light = await _apoyo.SembrarCategoriaAsync(e.Tenant, "Light");
        await _apoyo.AsignarCategoriaPadreAsync(e.Tenant, light, gaseosas);
        await _apoyo.CambiarCategoriaDeArticuloAsync(e.Tenant, cola500, light);
        var aguas = await _apoyo.IdDeCategoriaAsync(e.Tenant, "Aguas");
        var bebidas = await _apoyo.IdDeAreaAsync(e.Tenant, "Bebidas");
        var almacen = await _apoyo.IdDeAreaAsync(e.Tenant, "Almacén");

        static Dictionary<string, object?> Argumentos(params (string Nombre, string Valor)[] filtros)
        {
            var argumentos = new Dictionary<string, object?> { ["desde"] = "2026-10-18", ["hasta"] = "2026-10-18" };
            foreach (var (nombre, valor) in filtros)
            {
                argumentos[nombre] = valor;
            }

            return argumentos;
        }

        FiltroDeParidad[] matriz =
        [
            new("sin filtros", Argumentos(), null, null, null, null),
            new("área", Argumentos(("area", "almacén")), almacen, null, null, null),
            new("grupo", Argumentos(("grupo", "combos")), null, combos, null, null),
            new("categoría con subcategoría", Argumentos(("categoria", "gaseosas")), null, null, gaseosas, null),
            new("subcategoría", Argumentos(("categoria", "light")), null, null, light, null),
            new("artículo", Argumentos(("articulo", "cola-2l")), null, null, null, cola2),
            new("área y categoría", Argumentos(("area", "bebidas"), ("categoria", "gaseosas")), bebidas, null, gaseosas, null),
            new("grupo y categoría", Argumentos(("grupo", "combos"), ("categoria", "light")), null, combos, light, null),
            new("artículo y categoría", Argumentos(("articulo", "cola-2l"), ("categoria", "aguas")), null, null, aguas, cola2)
        ];

        var (host, cliente, token) = await ConectarAsync(e.Admin);
        await using var _ = host;
        using var __ = cliente;
        using var web = _fixture.CreateClient();
        var login = await web.PostAsJsonAsync("/api/auth/login", new SolicitudDeLogin(e.Admin.Mail, ApoyoDeConectorMcp.Password));
        Assert.Equal(HttpStatusCode.OK, login.StatusCode);

        var filasNoVacias = 0;
        foreach (var filtro in matriz)
        {
            var reporte = await web.GetFromJsonAsync<VentasPorArticulo>(
                $"/api/reportes/ventas/por-articulo?idEmpresa={e.Tenant.IdEmpresa}&desde=2026-10-18&hasta=2026-10-18" +
                (filtro.IdArea is { } a ? $"&idArea={a}" : string.Empty) +
                (filtro.IdGrupo is { } g ? $"&idGrupo={g}" : string.Empty) +
                (filtro.IdCategoria is { } c ? $"&idCategoria={c}" : string.Empty) +
                (filtro.IdArticulo is { } ar ? $"&idArticulo={ar}" : string.Empty));
            var resultado = await LlamarHerramientaAsync(cliente, token, "ventas_por_articulo", filtro.Herramienta);

            Assert.False(resultado.EsError, $"{filtro.Nombre}: {resultado.Texto}");
            var lineas = Lineas(resultado.Texto);
            var encabezado = Array.IndexOf(lineas, "código | artículo | cantidad | importe | % del total");
            var deLaHerramienta = lineas.Skip(encabezado + 1)
                .TakeWhile(l => !l.StartsWith("Total de los", StringComparison.Ordinal))
                .Select(l => l.Split(" | "))
                .Select(c => $"{filtro.Nombre}: {new FilaDeParidad(c[0], c[1], Numero(c[2]), Numero(c[3]))}")
                .Order(StringComparer.Ordinal)
                .ToList();
            var delReporte = reporte!.Articulos
                .Select(f => $"{filtro.Nombre}: {new FilaDeParidad(f.Codigo ?? "(sin código)", f.Descripcion, f.Cantidad, f.Total)}")
                .Order(StringComparer.Ordinal)
                .ToList();

            Assert.True(encabezado > 0, filtro.Nombre);
            Assert.Equal(delReporte, deLaHerramienta);
            Assert.Contains(
                $"Total de los artículos filtrados: {reporte.Articulos.Sum(f => f.Total).ToString("N2", FormatoDeLaHerramienta)} " +
                $"en {reporte.Articulos.Count} artículos",
                lineas);
            filasNoVacias += delReporte.Count > 0 ? 1 : 0;
        }

        Assert.Equal(matriz.Length - 1, filasNoVacias);
    }
}
