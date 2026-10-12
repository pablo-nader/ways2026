using System.Net;
using System.Text.Json;
using Ways.Domain.Articulos;
using Ways.Domain.Common;

namespace Ways.IntegrationTests;

/// <summary>
/// Los combos de la tienda: <c>GET /api/publico/combos</c> y la clave <c>combos</c> del catálogo (solo en la primera
/// página sin búsqueda ni categoría). Cada combo que NO aparece es el fixture de UNA regla; el que aparece lleva cada
/// campo con su verdad. Todo contra Postgres real y la API completa por el host del comercio.
/// </summary>
[Collection("Ways.IntegrationTests secuencial")]
public class CombosPublicosTests(WaysApiFixture fixture) : IClassFixture<WaysApiFixture>
{
    private readonly ApoyoDeCatalogoOnline apoyo = new(fixture);

    private HttpClient ClienteEn(ApoyoDeCatalogoOnline.Tienda tienda) => apoyo.Dominios.ClienteEnHost(tienda.Host);

    private static string[] Propiedades(JsonElement objeto) =>
        [.. objeto.EnumerateObject().Select(p => p.Name).Order(StringComparer.Ordinal)];

    private static (int? IdArticulo, int? IdFamilia, decimal Cantidad) Art(int id, decimal cantidad = 1m) => (id, null, cantidad);

    private async Task<JsonElement> CombosAsync(ApoyoDeCatalogoOnline.Tienda tienda)
    {
        using var cliente = ClienteEn(tienda);
        var respuesta = await cliente.GetAsync(ApoyoDeCatalogoOnline.RutaCombos);

        Assert.Equal(HttpStatusCode.OK, respuesta.StatusCode);

        return await ApoyoDeDominios.LeerJsonAsync(respuesta);
    }

    private static string[] Nombres(JsonElement combos) =>
        [.. combos.EnumerateArray().Select(c => c.GetProperty("nombre").GetString()!)];

    // ---- el contrato ------------------------------------------------------------------------------

    /// <summary>Dos combos con valores distintos en cada campo y en cada componente (dos campos intercambiados no
    /// pasan), ordenados por nombre aunque se hayan sembrado al revés; la cabecera de caché; que no viaja nada más (en
    /// particular el código del combo, que es el atajo de la caja).</summary>
    [Fact]
    public async Task ElEndpointDevuelveCadaCampoConSuVerdadOrdenadoPorNombreYNadaMas()
    {
        var tienda = await apoyo.SembrarTiendaAsync("cmb-contrato");
        var pancho = await apoyo.SembrarVendibleAsync(tienda, "Pancho", 3000m);
        var gaseosa = await apoyo.SembrarVendibleAsync(tienda, "Gaseosa", 2500m);
        var papas = await apoyo.SembrarVendibleAsync(tienda, "Papas", 1800m);
        var cargada = new DateTimeOffset(2026, 10, 10, 12, 0, 0, TimeSpan.Zero);
        await apoyo.SembrarImagenAsync(tienda.IdTenant, gaseosa, ApoyoDeVitrinas.Imagen(ValidadorDeImagen.TipoPng), actualizadoEn: cargada);

        var zeta = await apoyo.SembrarComboAsync(tienda.IdTenant, "Zeta: papas y gaseosa", 3900m, [Art(papas), Art(gaseosa, 2m)]);
        var alfa = await apoyo.SembrarComboAsync(tienda.IdTenant, "Alfa: pancho y papas", 4400m, [Art(pancho, 3m), Art(papas)]);

        using var cliente = ClienteEn(tienda);
        var respuesta = await cliente.GetAsync(ApoyoDeCatalogoOnline.RutaCombos);

        Assert.Equal(HttpStatusCode.OK, respuesta.StatusCode);
        Assert.Equal("public, max-age=60", respuesta.Headers.CacheControl?.ToString());
        var combos = (await ApoyoDeDominios.LeerJsonAsync(respuesta)).EnumerateArray().ToList();

        Assert.Equal(2, combos.Count);
        Assert.All(combos, c => Assert.Equal(["componentes", "id", "nombre", "precio"], Propiedades(c)));

        Assert.Equal(alfa, combos[0].GetProperty("id").GetInt32());
        Assert.Equal("Alfa: pancho y papas", combos[0].GetProperty("nombre").GetString());
        Assert.Equal(4400m, combos[0].GetProperty("precio").GetDecimal());
        var componentesAlfa = combos[0].GetProperty("componentes").EnumerateArray().ToList();
        Assert.All(componentesAlfa, c => Assert.Equal(["cantidad", "idArticulo", "imagenVersion", "nombre"], Propiedades(c)));
        Assert.Equal((pancho, "Pancho", 3m), (componentesAlfa[0].GetProperty("idArticulo").GetInt32(), componentesAlfa[0].GetProperty("nombre").GetString(), componentesAlfa[0].GetProperty("cantidad").GetDecimal()));
        Assert.Equal(JsonValueKind.Null, componentesAlfa[0].GetProperty("imagenVersion").ValueKind);
        Assert.Equal((papas, "Papas", 1m), (componentesAlfa[1].GetProperty("idArticulo").GetInt32(), componentesAlfa[1].GetProperty("nombre").GetString(), componentesAlfa[1].GetProperty("cantidad").GetDecimal()));

        Assert.Equal(zeta, combos[1].GetProperty("id").GetInt32());
        Assert.Equal(3900m, combos[1].GetProperty("precio").GetDecimal());
        var componentesZeta = combos[1].GetProperty("componentes").EnumerateArray().ToList();
        Assert.Equal((papas, "Papas", 1m), (componentesZeta[0].GetProperty("idArticulo").GetInt32(), componentesZeta[0].GetProperty("nombre").GetString(), componentesZeta[0].GetProperty("cantidad").GetDecimal()));
        Assert.Equal((gaseosa, "Gaseosa", 2m), (componentesZeta[1].GetProperty("idArticulo").GetInt32(), componentesZeta[1].GetProperty("nombre").GetString(), componentesZeta[1].GetProperty("cantidad").GetDecimal()));
        Assert.Equal(cargada.ToUnixTimeSeconds(), componentesZeta[1].GetProperty("imagenVersion").GetInt64());

    }

    // ---- qué combos aparecen ----------------------------------------------------------------------

    /// <summary>Cada combo que NO aparece rompe UNA regla y comparte los componentes sanos con el que sí. Los de
    /// <c>combos</c> (fuera de la tienda, inactivo, dado de baja) los decide la policy; la empresa, la familia y las
    /// cuatro condiciones del artículo, la consulta.</summary>
    [Fact]
    public async Task SoloAparecenLosCombosOnlineActivosVivosDeLaEmpresaConTodosSusComponentesVendiblesEnLaTienda()
    {
        var tienda = await apoyo.SembrarTiendaAsync("cmb-matriz");
        var otraEmpresa = await apoyo.Vitrinas.SembrarEmpresaAsync(tienda.IdTenant, "Otra empresa SA");
        var sano1 = await apoyo.SembrarVendibleAsync(tienda, "Sano uno", 100m);
        var sano2 = await apoyo.SembrarVendibleAsync(tienda, "Sano dos", 100m);
        var par = new[] { Art(sano1), Art(sano2) };

        // Los que aparecen.
        await apoyo.SembrarComboAsync(tienda.IdTenant, "01 de todo el tenant", 150m, par);
        await apoyo.SembrarComboAsync(tienda.IdTenant, "02 de esta empresa", 160m, par, idEmpresa: tienda.IdEmpresa);
        var restringido = await apoyo.SembrarArticuloAsync(tienda, "Restringido a esta empresa", idEmpresaDisponible: tienda.IdEmpresa);
        await apoyo.SembrarPrecioAsync(tienda.IdTenant, restringido, tienda.IdListaDefault, 50m);
        await apoyo.SembrarComboAsync(tienda.IdTenant, "03 con un artículo de esta empresa", 120m, [Art(sano1), Art(restringido)]);

        // Los que no: cada uno rompe UNA regla.
        await apoyo.SembrarComboAsync(tienda.IdTenant, "10 fuera de la tienda", 150m, par, disponibleOnline: false);
        await apoyo.SembrarComboAsync(tienda.IdTenant, "11 inactivo", 150m, par, activo: false);
        await apoyo.SembrarComboAsync(tienda.IdTenant, "12 eliminado", 150m, par, eliminado: true);
        await apoyo.SembrarComboAsync(tienda.IdTenant, "13 de otra empresa", 150m, par, idEmpresa: otraEmpresa);
        await apoyo.SembrarComboAsync(tienda.IdTenant, "14 sin componentes", 150m, []);

        var familia = await ApoyoFamiliaAsync(tienda);
        await apoyo.SembrarComboAsync(tienda.IdTenant, "15 con un componente familia", 150m, [Art(sano1), (null, familia, 1m)]);

        var fueraDeLaTienda = await apoyo.SembrarVendibleAsync(tienda, "Artículo fuera de la tienda", 100m, disponibleOnline: false);
        await apoyo.SembrarComboAsync(tienda.IdTenant, "16 con un artículo fuera de la tienda", 150m, [Art(sano1), Art(fueraDeLaTienda)]);

        var deUsoInterno = await apoyo.SembrarVendibleAsync(tienda, "Artículo de uso interno", 100m, disponibleVenta: false);
        await apoyo.SembrarComboAsync(tienda.IdTenant, "17 con un artículo no vendible", 150m, [Art(sano1), Art(deUsoInterno)]);

        var inactivo = await apoyo.SembrarArticuloAsync(tienda, "Artículo inactivo", activo: false);
        await apoyo.SembrarPrecioAsync(tienda.IdTenant, inactivo, tienda.IdListaDefault, 100m);
        await apoyo.SembrarComboAsync(tienda.IdTenant, "18 con un artículo inactivo", 150m, [Art(sano1), Art(inactivo)]);

        var deOtraEmpresa = await apoyo.SembrarArticuloAsync(tienda, "Artículo de otra empresa", idEmpresaDisponible: otraEmpresa);
        await apoyo.SembrarPrecioAsync(tienda.IdTenant, deOtraEmpresa, tienda.IdListaDefault, 100m);
        await apoyo.SembrarComboAsync(tienda.IdTenant, "19 con un artículo de otra empresa", 150m, [Art(sano1), Art(deOtraEmpresa)]);

        var sinPrecio = await apoyo.SembrarArticuloAsync(tienda, "Artículo sin precio");
        await apoyo.SembrarComboAsync(tienda.IdTenant, "20 con un artículo sin precio", 150m, [Art(sano1), Art(sinPrecio)]);

        var ahora = DateTimeOffset.UtcNow;
        var vencido = await apoyo.SembrarArticuloAsync(tienda, "Artículo con precio vencido");
        await apoyo.SembrarPrecioAsync(tienda.IdTenant, vencido, tienda.IdListaDefault, 100m, desde: ahora.AddDays(-30), hasta: ahora.AddDays(-20));
        await apoyo.SembrarComboAsync(tienda.IdTenant, "21 con un artículo de precio vencido", 150m, [Art(sano1), Art(vencido)]);

        var pesable = await apoyo.SembrarVendibleAsync(tienda, "Artículo pesable", 100m, unidadVenta: UnidadVenta.Peso);
        await apoyo.SembrarComboAsync(tienda.IdTenant, "22 con un artículo pesable", 150m, [Art(sano1), Art(pesable)]);

        var eliminado = await apoyo.SembrarArticuloAsync(tienda, "Artículo eliminado", eliminado: true);
        await apoyo.SembrarPrecioAsync(tienda.IdTenant, eliminado, tienda.IdListaDefault, 100m);
        await apoyo.SembrarComboAsync(tienda.IdTenant, "23 con un artículo eliminado", 150m, [Art(sano1), Art(eliminado)]);

        var combos = await CombosAsync(tienda);

        Assert.Equal(["01 de todo el tenant", "02 de esta empresa", "03 con un artículo de esta empresa"], Nombres(combos));
    }

    /// <summary>
    /// Un combo solo figura si cobra estrictamente menos (redondeado al peso) que sus componentes sueltos al precio de
    /// la tienda, con la cantidad de cada uno y las ofertas online que el catálogo ya aplica: es la condición con la que
    /// el motor de la venta lo aplica, así que uno que no ahorra nunca se aplicaría. Cada combo es el fixture de UN borde:
    /// más barato, igual, más caro, un peso menos, igual tras redondear, con cantidades, y contra una oferta.
    /// </summary>
    [Fact]
    public async Task UnComboSoloFiguraSiCobraMenosQueSusComponentesAlPrecioDeLaTienda()
    {
        var tienda = await apoyo.SembrarTiendaAsync("cmb-ahorro");
        var a = await apoyo.SembrarVendibleAsync(tienda, "A", 100m);
        var b = await apoyo.SembrarVendibleAsync(tienda, "B", 100m);
        var conOferta = await apoyo.SembrarVendibleAsync(tienda, "C con oferta", 100m);
        await apoyo.SembrarOfertaAsync(tienda.IdTenant, "Mitad de precio", idArticulo: conOferta, porcentaje: 50m);

        await apoyo.SembrarComboAsync(tienda.IdTenant, "01 más barato", 150m, [Art(a), Art(b)]);
        await apoyo.SembrarComboAsync(tienda.IdTenant, "02 igual", 200m, [Art(a), Art(b)]);
        await apoyo.SembrarComboAsync(tienda.IdTenant, "03 más caro", 250m, [Art(a), Art(b)]);
        await apoyo.SembrarComboAsync(tienda.IdTenant, "04 un peso menos", 199m, [Art(a), Art(b)]);
        await apoyo.SembrarComboAsync(tienda.IdTenant, "05 se redondea hacia abajo y ahorra", 199.4m, [Art(a), Art(b)]);
        await apoyo.SembrarComboAsync(tienda.IdTenant, "06 se redondea hasta igualar", 199.6m, [Art(a), Art(b)]);
        await apoyo.SembrarComboAsync(tienda.IdTenant, "07 ahorra por la cantidad", 350m, [Art(a, 3m), Art(b)]);
        await apoyo.SembrarComboAsync(tienda.IdTenant, "08 no gana contra la oferta", 160m, [Art(conOferta), Art(b)]);
        await apoyo.SembrarComboAsync(tienda.IdTenant, "09 gana a pesar de la oferta", 140m, [Art(conOferta), Art(b)]);

        var combos = await CombosAsync(tienda);

        Assert.Equal(
            ["01 más barato", "04 un peso menos", "05 se redondea hacia abajo y ahorra", "07 ahorra por la cantidad", "09 gana a pesar de la oferta"],
            Nombres(combos));
    }

    /// <summary>Con una lista derivada el costo de los componentes es el derivado (100 + 10% = 110 cada uno): un combo
    /// a 205 ahorra contra 220 aunque no contra los 200 de la lista base.</summary>
    [Fact]
    public async Task ConUnaListaDerivadaElAhorroSeMideContraElPrecioDerivado()
    {
        var tienda = await apoyo.SembrarTiendaAsync("cmb-ahorro-derivada");
        var derivada = await apoyo.SembrarListaAsync(
            tienda.IdTenant, "General +10%", modo: Ways.Domain.Catalogos.ModoLista.Derivada, idListaBase: tienda.IdListaDefault, porcentaje: 10m);
        await apoyo.FijarListaDeLaVitrinaAsync(tienda.IdEmpresa, derivada);
        var a = await apoyo.SembrarVendibleAsync(tienda, "A", 100m);
        var b = await apoyo.SembrarVendibleAsync(tienda, "B", 100m);
        await apoyo.SembrarComboAsync(tienda.IdTenant, "01 ahorra contra el derivado", 205m, [Art(a), Art(b)]);
        await apoyo.SembrarComboAsync(tienda.IdTenant, "02 no ahorra contra el derivado", 220m, [Art(a), Art(b)]);

        Assert.Equal(["01 ahorra contra el derivado"], Nombres(await CombosAsync(tienda)));
    }

    /// <summary>Un combo de otro comercio no figura aunque comparta nombre y artículos con uno propio.</summary>
    [Fact]
    public async Task UnComboDeOtroComercioNoFigura()
    {
        var tienda = await apoyo.SembrarTiendaAsync("cmb-tenant");
        var ajena = await apoyo.SembrarTiendaAsync("cmb-tenant-ajena");
        var propios = new[] { await apoyo.SembrarVendibleAsync(tienda, "A", 10m), await apoyo.SembrarVendibleAsync(tienda, "B", 10m) };
        var ajenos = new[] { await apoyo.SembrarVendibleAsync(ajena, "A", 10m), await apoyo.SembrarVendibleAsync(ajena, "B", 10m) };
        await apoyo.SembrarComboAsync(tienda.IdTenant, "Propio", 15m, [Art(propios[0]), Art(propios[1])]);
        await apoyo.SembrarComboAsync(ajena.IdTenant, "Ajeno", 15m, [Art(ajenos[0]), Art(ajenos[1])]);

        Assert.Equal(["Propio"], Nombres(await CombosAsync(tienda)));
        Assert.Equal(["Ajeno"], Nombres(await CombosAsync(ajena)));
    }

    /// <summary>La lista de la tienda es la de la vitrina: un artículo con precio solo en la lista default no cuenta
    /// si la vitrina usa otra.</summary>
    [Fact]
    public async Task ElPrecioDeLosComponentesSeBuscaEnLaListaDeLaVitrina()
    {
        var tienda = await apoyo.SembrarTiendaAsync("cmb-lista");
        var otraLista = await apoyo.SembrarListaAsync(tienda.IdTenant, "Mayorista");
        await apoyo.FijarListaDeLaVitrinaAsync(tienda.IdEmpresa, otraLista);
        var enAmbas = await apoyo.SembrarVendibleAsync(tienda, "En ambas", 100m);
        await apoyo.SembrarPrecioAsync(tienda.IdTenant, enAmbas, otraLista, 90m);
        var soloDefault = await apoyo.SembrarVendibleAsync(tienda, "Solo en la default", 100m);
        var soloMayorista = await apoyo.SembrarArticuloAsync(tienda, "Solo en la mayorista");
        await apoyo.SembrarPrecioAsync(tienda.IdTenant, soloMayorista, otraLista, 80m);
        await apoyo.SembrarComboAsync(tienda.IdTenant, "Con la mayorista", 150m, [Art(enAmbas), Art(soloMayorista)]);
        await apoyo.SembrarComboAsync(tienda.IdTenant, "Con la default", 150m, [Art(enAmbas), Art(soloDefault)]);

        Assert.Equal(["Con la mayorista"], Nombres(await CombosAsync(tienda)));
    }

    /// <summary>Con una lista derivada, el precio existe si existe en la base fija de la que sale.</summary>
    [Fact]
    public async Task ConUnaListaDerivadaElPrecioSeBuscaEnSuBase()
    {
        var tienda = await apoyo.SembrarTiendaAsync("cmb-derivada");
        var derivada = await apoyo.SembrarListaAsync(
            tienda.IdTenant, "General +10%", modo: Ways.Domain.Catalogos.ModoLista.Derivada, idListaBase: tienda.IdListaDefault, porcentaje: 10m);
        await apoyo.FijarListaDeLaVitrinaAsync(tienda.IdEmpresa, derivada);
        var a = await apoyo.SembrarVendibleAsync(tienda, "A", 100m);
        var b = await apoyo.SembrarVendibleAsync(tienda, "B", 100m);
        await apoyo.SembrarComboAsync(tienda.IdTenant, "Derivado", 150m, [Art(a), Art(b)]);

        Assert.Equal(["Derivado"], Nombres(await CombosAsync(tienda)));
    }

    // ---- sin vitrina ------------------------------------------------------------------------------

    [Fact]
    public async Task SinVitrinaPublicadaLosCombosDan404VitrinaNoPublicadaSinCache()
    {
        var sinVitrina = await apoyo.SembrarTiendaAsync("cmb-sin-vitrina", vitrinaPublicada: false);
        var sinPublicar = await apoyo.SembrarTiendaAsync("cmb-sin-publicar", vitrinaPublicada: false);
        await apoyo.Vitrinas.SembrarVitrinaAsync(sinPublicar.IdTenant, sinPublicar.IdEmpresa, publicada: false);

        foreach (var tienda in new[] { sinVitrina, sinPublicar })
        {
            var a = await apoyo.SembrarVendibleAsync(tienda, "A", 10m);
            var b = await apoyo.SembrarVendibleAsync(tienda, "B", 10m);
            await apoyo.SembrarComboAsync(tienda.IdTenant, "Combo", 15m, [Art(a), Art(b)]);

            using var cliente = ClienteEn(tienda);
            var respuesta = await cliente.GetAsync(ApoyoDeCatalogoOnline.RutaCombos);

            Assert.Equal(HttpStatusCode.NotFound, respuesta.StatusCode);
            Assert.Equal("vitrina_no_publicada", await ApoyoDeDominios.CodigoDeErrorAsync(respuesta));
            Assert.NotEqual("public, max-age=60", respuesta.Headers.CacheControl?.ToString());
        }
    }

    [Fact]
    public async Task EnElHostDeLaPlataformaNoHayCombos()
    {
        using var cliente = fixture.CreateClient();

        var respuesta = await cliente.GetAsync(ApoyoDeCatalogoOnline.RutaCombos);

        Assert.Equal(HttpStatusCode.NotFound, respuesta.StatusCode);
        Assert.Equal("vitrina_no_publicada", await ApoyoDeDominios.CodigoDeErrorAsync(respuesta));
    }

    // ---- en el catálogo ---------------------------------------------------------------------------

    /// <summary>Los combos van en el catálogo solo en la primera página sin búsqueda ni categoría: con la página 2, con
    /// <c>buscar</c> o con <c>idCategoria</c> (aunque sea inexistente) la clave viaja vacía. Con la página 0, que se
    /// acota a 1, sí viajan; también con una búsqueda que son solo caracteres de control o espacios, que el catálogo
    /// normaliza a "sin búsqueda" y por lo tanto no filtra.</summary>
    [Fact]
    public async Task ElCatalogoLlevaLosCombosSoloEnLaPrimeraPaginaSinFiltros()
    {
        var tienda = await apoyo.SembrarTiendaAsync("cmb-catalogo");
        var bebidas = await apoyo.SembrarCategoriaAsync(tienda.IdTenant, "Bebidas");
        var a = await apoyo.SembrarVendibleAsync(tienda, "Agua", 10m, idCategoria: bebidas);
        var b = await apoyo.SembrarVendibleAsync(tienda, "Brea", 10m);
        await apoyo.SembrarVendibleAsync(tienda, "Cola", 10m);
        await apoyo.SembrarComboAsync(tienda.IdTenant, "Agua y brea", 15m, [Art(a, 2m), Art(b)]);

        using var cliente = ClienteEn(tienda);

        var primera = await ApoyoDeCatalogoOnline.CatalogoAsync(cliente, "?tamano=2");
        var combo = Assert.Single(primera.GetProperty("combos").EnumerateArray());
        Assert.Equal("Agua y brea", combo.GetProperty("nombre").GetString());
        Assert.Equal(15m, combo.GetProperty("precio").GetDecimal());
        Assert.Equal(2, combo.GetProperty("componentes").GetArrayLength());

        foreach (var consulta in new[]
        {
            "?tamano=2&pagina=2",
            "?buscar=agua",
            $"?idCategoria={bebidas}",
            "?idCategoria=987654321"
        })
        {
            var filtrada = await ApoyoDeCatalogoOnline.CatalogoAsync(cliente, consulta);

            Assert.Equal(0, filtrada.GetProperty("combos").GetArrayLength());
        }

        foreach (var consulta in new[] { "?pagina=1", "?pagina=0", "?pagina=-3", "?buscar=", "?buscar=%01", "?buscar=%01%02%20" })
        {
            var sinFiltro = await ApoyoDeCatalogoOnline.CatalogoAsync(cliente, consulta);

            Assert.Single(sinFiltro.GetProperty("combos").EnumerateArray());
        }
    }

    /// <summary>Lo que el catálogo trae en <c>combos</c> es lo que devuelve el endpoint, campo por campo.</summary>
    [Fact]
    public async Task ElCatalogoYElEndpointDanLosMismosCombos()
    {
        var tienda = await apoyo.SembrarTiendaAsync("cmb-iguales");
        var a = await apoyo.SembrarVendibleAsync(tienda, "Aa", 10m);
        var b = await apoyo.SembrarVendibleAsync(tienda, "Bb", 20m);
        await apoyo.SembrarImagenAsync(tienda.IdTenant, b, ApoyoDeVitrinas.Imagen(ValidadorDeImagen.TipoPng));
        await apoyo.SembrarComboAsync(tienda.IdTenant, "Segundo", 25m, [Art(b, 3m)]);
        await apoyo.SembrarComboAsync(tienda.IdTenant, "Primero", 28m, [Art(a), Art(b)]);

        using var cliente = ClienteEn(tienda);
        var delCatalogo = (await ApoyoDeCatalogoOnline.CatalogoAsync(cliente)).GetProperty("combos").GetRawText();
        var delEndpoint = (await CombosAsync(tienda)).GetRawText();

        Assert.Equal(delEndpoint, delCatalogo);
        Assert.Equal(["Primero", "Segundo"], Nombres(await CombosAsync(tienda)));
    }

    private async Task<int> ApoyoFamiliaAsync(ApoyoDeCatalogoOnline.Tienda tienda)
    {
        var ahora = DateTimeOffset.UtcNow;
        await using var db = fixture.CrearContextoDeAplicacion(Ways.Infrastructure.Multitenancy.TenantActualFijo.Plataforma);
        var familia = new Ways.Domain.Articulos.Familia
        {
            IdTenant = tienda.IdTenant,
            Nombre = $"Familia {Guid.NewGuid():N}",
            CreatedAt = ahora,
            UpdatedAt = ahora
        };
        db.Familias.Add(familia);
        await db.SaveChangesAsync();

        return familia.Id;
    }
}
