using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Ways.Application.Ofertas;
using Ways.Application.Ventas;
using Ways.Domain.Articulos;
using Ways.Domain.Organizacion;
using static Ways.IntegrationTests.CombosApoyo;

namespace Ways.IntegrationTests;

/// <summary>
/// <c>POST /api/ofertas/resolver-con-combos</c>: la vista previa de lo que <c>POST /api/ventas</c> cobrará cuando el
/// documento tiene combos. La prueba central es la paridad: el neto de cada línea y el total del documento que calcula
/// la vista previa son los que la venta online persiste. Los combos se siembran directo por EF.
/// </summary>
[Collection("Ways.IntegrationTests secuencial")]
public class CombosPrevisualizacionTests(WaysApiFixture fixture) : IClassFixture<WaysApiFixture>
{
    private readonly CombosApoyo apoyo = new(fixture);

    private const string Ruta = "/api/ofertas/resolver-con-combos";

    private static LineaDeResolucion Linea(Escenario ctx, int idArticulo, decimal cantidad, int? idEmpresa = -1) =>
        new(idArticulo, idEmpresa == -1 ? ctx.IdEmpresa : idEmpresa, ctx.IdListaPrecio, cantidad);

    private static async Task<JsonElement> PrevisualizarAsync(
        Escenario ctx, params (int IdArticulo, decimal Cantidad)[] lineas)
    {
        var solicitud = new SolicitudDeResolucion([.. lineas.Select(l => Linea(ctx, l.IdArticulo, l.Cantidad))]);
        var respuesta = await ctx.Admin.PostAsJsonAsync(Ruta, solicitud);
        var cuerpo = await respuesta.Content.ReadAsStringAsync();
        Assert.True(respuesta.StatusCode == HttpStatusCode.OK, cuerpo);

        return JsonDocument.Parse(cuerpo).RootElement;
    }

    /// <summary>Neto de una línea con la fórmula del checkout: bruto − (round(oferta unitaria × unidades fuera de combo) +
    /// parte del combo). Es la única aritmética que un cliente tiene que replicar.</summary>
    private static decimal NetoDe(JsonElement linea, decimal cantidad)
    {
        var precio = linea.GetProperty("precioOriginal").GetDecimal();
        var descuentoUnitario = linea.GetProperty("descuentoUnitario").GetDecimal();
        var unidades = 0m;
        var ahorro = 0m;
        if (linea.TryGetProperty("combo", out var combo))
        {
            unidades = combo.GetProperty("unidadesConsumidas").GetDecimal();
            ahorro = combo.GetProperty("ahorro").GetDecimal();
        }

        var bruto = Math.Round(cantidad * precio, 2, MidpointRounding.AwayFromZero);
        var descuento = Math.Round(descuentoUnitario * (cantidad - unidades), 2, MidpointRounding.AwayFromZero) + ahorro;

        return bruto - descuento;
    }

    [Fact]
    public async Task UnPanchoYUnaCocaConElComboMuestranElPrecioDelComboEnLasLineasYEnLaListaDeAplicados()
    {
        var ctx = await apoyo.PrepararAsync(nameof(UnPanchoYUnaCocaConElComboMuestranElPrecioDelComboEnLasLineasYEnLaListaDeAplicados), ModoPuntoVenta.Web);
        var pancho = await apoyo.ArticuloAsync(ctx, "pancho", 3000m);
        var coca = await apoyo.ArticuloAsync(ctx, "coca", 2500m);
        var combo = await apoyo.ComboAsync(ctx, 5000m, [Art(pancho), Art(coca)], nombre: "Pancho + coca");

        var respuesta = await PrevisualizarAsync(ctx, (pancho, 1m), (coca, 1m));

        var lineas = respuesta.GetProperty("lineas");
        Assert.Equal(2, lineas.GetArrayLength());
        Assert.Equal(pancho, lineas[0].GetProperty("idArticulo").GetInt32());
        Assert.Equal(3000m, lineas[0].GetProperty("precioOriginal").GetDecimal());

        var comboPancho = lineas[0].GetProperty("combo");
        Assert.Equal(1, comboPancho.GetProperty("unidadesConsumidas").GetInt32());
        Assert.Equal(273m, comboPancho.GetProperty("ahorro").GetDecimal());
        Assert.Equal(combo, comboPancho.GetProperty("idCombo").GetInt32());
        Assert.Equal(227m, lineas[1].GetProperty("combo").GetProperty("ahorro").GetDecimal());

        var aplicado = Assert.Single(respuesta.GetProperty("combos").EnumerateArray());
        Assert.Equal(combo, aplicado.GetProperty("idCombo").GetInt32());
        Assert.Equal(1, aplicado.GetProperty("codigo").GetInt32());
        Assert.Equal("Pancho + coca", aplicado.GetProperty("nombre").GetString());
        Assert.Equal(1, aplicado.GetProperty("veces").GetInt32());
        Assert.Equal(500m, aplicado.GetProperty("ahorro").GetDecimal());
    }

    [Fact]
    public async Task UnaLineaSinComboNoLlevaLaClaveCombo()
    {
        var ctx = await apoyo.PrepararAsync(nameof(UnaLineaSinComboNoLlevaLaClaveCombo), ModoPuntoVenta.Web);
        var pancho = await apoyo.ArticuloAsync(ctx, "pancho", 3000m);
        var coca = await apoyo.ArticuloAsync(ctx, "coca", 2500m);
        var papas = await apoyo.ArticuloAsync(ctx, "papas", 1800m);
        await apoyo.ComboAsync(ctx, 5000m, [Art(pancho), Art(coca)]);

        var respuesta = await PrevisualizarAsync(ctx, (pancho, 1m), (coca, 1m), (papas, 1m));

        var lineas = respuesta.GetProperty("lineas");
        Assert.True(lineas[0].TryGetProperty("combo", out _));
        Assert.False(lineas[2].TryGetProperty("combo", out _));
    }

    [Fact]
    public async Task SinCombosLaRespuestaTraeLaListaDeAplicadosVacia()
    {
        var ctx = await apoyo.PrepararAsync(nameof(SinCombosLaRespuestaTraeLaListaDeAplicadosVacia), ModoPuntoVenta.Web);
        var pancho = await apoyo.ArticuloAsync(ctx, "pancho", 3000m);
        var coca = await apoyo.ArticuloAsync(ctx, "coca", 2500m);

        var respuesta = await PrevisualizarAsync(ctx, (pancho, 1m), (coca, 1m));

        Assert.Equal(0, respuesta.GetProperty("combos").GetArrayLength());
        Assert.False(respuesta.GetProperty("lineas")[0].TryGetProperty("combo", out _));
    }

    [Fact]
    public async Task UnComboConUnComponenteArticuloYOtroFamiliaConsumeSoloUnaUnidadDeLaLineaConMasCantidad()
    {
        var ctx = await apoyo.PrepararAsync(nameof(UnComboConUnComponenteArticuloYOtroFamiliaConsumeSoloUnaUnidadDeLaLineaConMasCantidad), ModoPuntoVenta.Web);
        var gaseosas = await apoyo.FamiliaAsync(ctx, "Gaseosas");
        var pancho = await apoyo.ArticuloAsync(ctx, "pancho", 3000m);
        var coca = await apoyo.ArticuloAsync(ctx, "coca", 2500m, idFamilia: gaseosas);
        var combo = await apoyo.ComboAsync(ctx, 5000m, [Art(pancho), Fam(gaseosas)]);

        var respuesta = await PrevisualizarAsync(ctx, (pancho, 2m), (coca, 1m));

        var lineas = respuesta.GetProperty("lineas");
        Assert.Equal(1, lineas[0].GetProperty("combo").GetProperty("unidadesConsumidas").GetInt32());
        Assert.Equal(1, lineas[1].GetProperty("combo").GetProperty("unidadesConsumidas").GetInt32());
        Assert.Equal(combo, lineas[0].GetProperty("combo").GetProperty("idCombo").GetInt32());

        // Un pancho queda fuera del combo a su precio de lista: 6000 + 2500 − 500 de ahorro.
        Assert.Equal(8000m, NetoDe(lineas[0], 2m) + NetoDe(lineas[1], 1m));
        Assert.Equal(1, Assert.Single(respuesta.GetProperty("combos").EnumerateArray()).GetProperty("veces").GetInt32());
    }

    [Fact]
    public async Task UnComboQueSeCompletaDosVecesLoInformaConSuAhorroTotal()
    {
        var ctx = await apoyo.PrepararAsync(nameof(UnComboQueSeCompletaDosVecesLoInformaConSuAhorroTotal), ModoPuntoVenta.Web);
        var pancho = await apoyo.ArticuloAsync(ctx, "pancho", 3000m);
        var coca = await apoyo.ArticuloAsync(ctx, "coca", 2500m);
        await apoyo.ComboAsync(ctx, 5000m, [Art(pancho), Art(coca)]);

        var respuesta = await PrevisualizarAsync(ctx, (pancho, 2m), (coca, 2m));

        var aplicado = Assert.Single(respuesta.GetProperty("combos").EnumerateArray());
        Assert.Equal(2, aplicado.GetProperty("veces").GetInt32());
        Assert.Equal(1000m, aplicado.GetProperty("ahorro").GetDecimal());
    }

    // ---- Paridad con la venta online ----

    /// <summary>Cláusula: la vista previa y <c>POST /api/ventas</c> comparten el camino de detección. Para cada carrito
    /// el neto por línea y el total del documento que calcula la vista previa son los que la venta persiste, y el combo de
    /// cada línea es el mismo (incluida la regla del menor id cuando dos combos consumen la misma línea). Los carritos
    /// cubren: consumo parcial de una línea, competencia entre combos, familia, oferta en una línea que queda fuera del
    /// combo, artículo pesable sin combo, combo de dos artículos pesables con excedente y el carrito sin combos.</summary>
    [Fact]
    public async Task LaVistaPreviaCoincideConLoQueLaVentaOnlinePersisteEnVariosCarritos()
    {
        var ctx = await apoyo.PrepararAsync(nameof(LaVistaPreviaCoincideConLoQueLaVentaOnlinePersisteEnVariosCarritos), ModoPuntoVenta.Web);
        var gaseosas = await apoyo.FamiliaAsync(ctx, "Gaseosas");
        var pancho = await apoyo.ArticuloAsync(ctx, "pancho", 3000m);
        var coca = await apoyo.ArticuloAsync(ctx, "coca", 2500m, idFamilia: gaseosas);
        var sprite = await apoyo.ArticuloAsync(ctx, "sprite", 2200m, idFamilia: gaseosas);
        var papas = await apoyo.ArticuloAsync(ctx, "papas", 1800m);
        var chicle = await apoyo.ArticuloAsync(ctx, "chicle", 100m);
        var queso = await apoyo.ArticuloAsync(ctx, "queso", 1000m, UnidadVenta.Peso);
        var jamon = await apoyo.ArticuloAsync(ctx, "jamon", 10000m, UnidadVenta.Peso);
        var salame = await apoyo.ArticuloAsync(ctx, "salame", 8000m, UnidadVenta.Peso);
        await apoyo.ComboAsync(ctx, 3000m, [Art(jamon, 0.200m), Art(salame, 0.200m)]);
        await apoyo.OfertaDePrecioAsync(ctx, chicle, 80m);
        var comboPancho = await apoyo.ComboAsync(ctx, 5000m, [Art(pancho), Fam(gaseosas)]);
        var comboPapas = await apoyo.ComboAsync(ctx, 3600m, [Art(papas), Art(coca)]);

        (int, decimal)[][] carritos =
        [
            [(pancho, 2m), (coca, 1m)],
            [(pancho, 1m), (coca, 1m), (papas, 1m)],
            [(pancho, 1m), (sprite, 1m), (coca, 1m), (queso, 2m)],
            [(chicle, 3m), (pancho, 1m), (coca, 1m)],
            [(coca, 2m), (pancho, 1m), (papas, 1m)],
            [(pancho, 1m), (queso, 1m)],
            [(jamon, 0.215m), (salame, 0.400m), (pancho, 1m)],
        ];

        foreach (var carrito in carritos)
        {
            var vista = await PrevisualizarAsync(ctx, carrito);
            var lineasVista = vista.GetProperty("lineas").EnumerateArray().ToList();
            var netos = lineasVista.Select((l, i) => NetoDe(l, carrito[i].Item2)).ToList();

            var solicitud = new SolicitudDeVenta(
                ctx.IdPuntoVenta, null, "TX", null,
                [.. carrito.Select(l => new LineaDeVenta(l.Item1, l.Item2, null))],
                [new PagoDeVenta(ctx.IdMedioEfectivo, netos.Sum(), null, 0m)],
                null, null);
            var respuesta = await ctx.Admin.PostAsJsonAsync("/api/ventas", solicitud);
            var cuerpo = await respuesta.Content.ReadAsStringAsync();
            Assert.True(respuesta.StatusCode == HttpStatusCode.Created, cuerpo);
            var emitido = JsonSerializer.Deserialize<ComprobanteEmitido>(cuerpo, OpcionesJson)!;

            var persistidos = await apoyo.ItemsAsync(ctx, emitido.Id);
            Assert.Equal(netos, persistidos.Select(i => i.Total));
            Assert.Equal(netos.Sum(), emitido.Total);
            Assert.Equal(
                lineasVista.Select(l => l.TryGetProperty("combo", out var c) ? c.GetProperty("idCombo").GetInt32() : (int?)null),
                persistidos.Select(i => i.IdCombo));
        }

        // La competencia del carrito 5: la coca la consumen los dos combos y gana el de menor id.
        var competencia = await PrevisualizarAsync(ctx, (coca, 2m), (pancho, 1m), (papas, 1m));
        Assert.Equal(
            Math.Min(comboPancho, comboPapas),
            competencia.GetProperty("lineas")[0].GetProperty("combo").GetProperty("idCombo").GetInt32());
        Assert.Equal(2, competencia.GetProperty("combos").GetArrayLength());
    }

    // ---- Mismas exclusiones que la venta ----

    [Fact]
    public async Task UnArticuloPesableCompletaUnComboYSuLineaInformaLosKilosConsumidos()
    {
        var ctx = await apoyo.PrepararAsync(nameof(UnArticuloPesableCompletaUnComboYSuLineaInformaLosKilosConsumidos), ModoPuntoVenta.Web);
        var pancho = await apoyo.ArticuloAsync(ctx, "pancho", 3000m);
        var queso = await apoyo.ArticuloAsync(ctx, "queso", 1000m, UnidadVenta.Peso);
        var combo = await apoyo.ComboAsync(ctx, 3000m, [Art(pancho), Art(queso, 0.200m)]);

        var respuesta = await PrevisualizarAsync(ctx, (pancho, 1m), (queso, 0.215m));

        var aplicado = Assert.Single(respuesta.GetProperty("combos").EnumerateArray());
        Assert.Equal(combo, aplicado.GetProperty("idCombo").GetInt32());
        Assert.Equal(1, aplicado.GetProperty("veces").GetInt32());
        var delQueso = respuesta.GetProperty("lineas")[1].GetProperty("combo");
        Assert.Equal(0.200m, delQueso.GetProperty("unidadesConsumidas").GetDecimal());
        Assert.Equal(13m, delQueso.GetProperty("ahorro").GetDecimal());
        Assert.Equal(1m, respuesta.GetProperty("lineas")[0].GetProperty("combo").GetProperty("unidadesConsumidas").GetDecimal());
    }

    /// <summary>Las unidades consumidas de un artículo por unidad siguen viajando como un entero sin decimales: un
    /// cliente que las lee como entero no se rompe.</summary>
    [Fact]
    public async Task LasUnidadesConsumidasDeUnArticuloPorUnidadViajanComoUnEnteroSinDecimales()
    {
        var ctx = await apoyo.PrepararAsync(nameof(LasUnidadesConsumidasDeUnArticuloPorUnidadViajanComoUnEnteroSinDecimales), ModoPuntoVenta.Web);
        var pancho = await apoyo.ArticuloAsync(ctx, "pancho", 3000m);
        var coca = await apoyo.ArticuloAsync(ctx, "coca", 2500m);
        await apoyo.ComboAsync(ctx, 5000m, [Art(pancho), Art(coca)]);

        var respuesta = await PrevisualizarAsync(ctx, (pancho, 2m), (coca, 1m));

        var unidades = respuesta.GetProperty("lineas")[0].GetProperty("combo").GetProperty("unidadesConsumidas");
        Assert.Equal("1", unidades.GetRawText());
        Assert.Equal(1, unidades.GetInt32());
    }

    [Fact]
    public async Task UnaLineaSinPrecioVigenteNoCompletaUnCombo()
    {
        var ctx = await apoyo.PrepararAsync(nameof(UnaLineaSinPrecioVigenteNoCompletaUnCombo), ModoPuntoVenta.Web);
        var pancho = await apoyo.ArticuloAsync(ctx, "pancho", 3000m);
        var coca = await apoyo.ArticuloAsync(ctx, "coca", 2500m, conPrecio: false);
        await apoyo.ComboAsync(ctx, 4000m, [Art(pancho), Art(coca)]);

        var respuesta = await PrevisualizarAsync(ctx, (pancho, 1m), (coca, 1m));

        Assert.Equal(0, respuesta.GetProperty("combos").GetArrayLength());
        Assert.Equal(JsonValueKind.Null, respuesta.GetProperty("lineas")[1].GetProperty("precioOriginal").ValueKind);
    }

    [Fact]
    public async Task UnComboInactivoNoSeAplica()
    {
        var ctx = await apoyo.PrepararAsync(nameof(UnComboInactivoNoSeAplica), ModoPuntoVenta.Web);
        var pancho = await apoyo.ArticuloAsync(ctx, "pancho", 3000m);
        var coca = await apoyo.ArticuloAsync(ctx, "coca", 2500m);
        await apoyo.ComboAsync(ctx, 5000m, [Art(pancho), Art(coca)], activo: false);

        var respuesta = await PrevisualizarAsync(ctx, (pancho, 1m), (coca, 1m));

        Assert.Equal(0, respuesta.GetProperty("combos").GetArrayLength());
    }

    [Fact]
    public async Task UnaSolaUnidadNoDetectaCombos()
    {
        var ctx = await apoyo.PrepararAsync(nameof(UnaSolaUnidadNoDetectaCombos), ModoPuntoVenta.Web);
        var pancho = await apoyo.ArticuloAsync(ctx, "pancho", 3000m);
        var coca = await apoyo.ArticuloAsync(ctx, "coca", 2500m);
        await apoyo.ComboAsync(ctx, 5000m, [Art(pancho), Art(coca)]);

        var respuesta = await PrevisualizarAsync(ctx, (pancho, 1m));

        Assert.Equal(0, respuesta.GetProperty("combos").GetArrayLength());
        Assert.Equal(3000m, respuesta.GetProperty("lineas")[0].GetProperty("precioOriginal").GetDecimal());
    }

    // ---- Empresa ----

    [Fact]
    public async Task SoloAplicanLosCombosDeTodoElTenantOLosDeLaEmpresaDeLasLineas()
    {
        var ctx = await apoyo.PrepararAsync(nameof(SoloAplicanLosCombosDeTodoElTenantOLosDeLaEmpresaDeLasLineas), ModoPuntoVenta.Web);
        var otraEmpresa = await apoyo.OtraEmpresaAsync(ctx);
        var a = await apoyo.ArticuloAsync(ctx, "a", 1000m);
        var b = await apoyo.ArticuloAsync(ctx, "b", 1000m);
        var c = await apoyo.ArticuloAsync(ctx, "c", 1000m);
        var d = await apoyo.ArticuloAsync(ctx, "d", 1000m);
        await apoyo.ComboAsync(ctx, 1500m, [Art(a), Art(b)], idEmpresa: ctx.IdEmpresa);
        await apoyo.ComboAsync(ctx, 1500m, [Art(c), Art(d)], idEmpresa: otraEmpresa);

        var propia = await PrevisualizarAsync(ctx, (a, 1m), (b, 1m));
        var ajena = await PrevisualizarAsync(ctx, (c, 1m), (d, 1m));

        Assert.Equal(1, propia.GetProperty("combos").GetArrayLength());
        Assert.Equal(0, ajena.GetProperty("combos").GetArrayLength());

        // Sin empresa en las líneas solo participan los combos de todo el tenant.
        var solicitud = new SolicitudDeResolucion(
            [Linea(ctx, a, 1m, idEmpresa: null), Linea(ctx, b, 1m, idEmpresa: null)]);
        var sinEmpresa = await ctx.Admin.PostAsJsonAsync(Ruta, solicitud);
        Assert.Equal(HttpStatusCode.OK, sinEmpresa.StatusCode);
        var cuerpo = JsonDocument.Parse(await sinEmpresa.Content.ReadAsStringAsync()).RootElement;
        Assert.Equal(0, cuerpo.GetProperty("combos").GetArrayLength());
    }

    /// <summary>Decisión: un documento es de una sola empresa. Líneas de empresas distintas, o con y sin empresa, se
    /// rechazan; el <c>/resolver</c> de siempre las sigue aceptando.</summary>
    [Fact]
    public async Task LineasDeEmpresasDistintasSeRechazan()
    {
        var ctx = await apoyo.PrepararAsync(nameof(LineasDeEmpresasDistintasSeRechazan), ModoPuntoVenta.Web);
        var otraEmpresa = await apoyo.OtraEmpresaAsync(ctx);
        var a = await apoyo.ArticuloAsync(ctx, "a", 1000m);
        var b = await apoyo.ArticuloAsync(ctx, "b", 1000m);

        foreach (var otra in new int?[] { otraEmpresa, null })
        {
            var solicitud = new SolicitudDeResolucion([Linea(ctx, a, 1m), Linea(ctx, b, 1m, otra)]);
            var respuesta = await ctx.Admin.PostAsJsonAsync(Ruta, solicitud);

            Assert.Equal(HttpStatusCode.BadRequest, respuesta.StatusCode);
            var problema = await respuesta.Content.ReadFromJsonAsync<JsonElement>();
            Assert.Equal("empresa_inconsistente", problema.GetProperty("codigo").GetString());
        }

        var heredado = await ctx.Admin.PostAsJsonAsync(
            "/api/ofertas/resolver", new SolicitudDeResolucion([Linea(ctx, a, 1m), Linea(ctx, b, 1m, otraEmpresa)]));
        Assert.Equal(HttpStatusCode.OK, heredado.StatusCode);
    }

    /// <summary>Cláusula: <c>ExigirUnaSolaLista</c>. La venta cotiza todas las líneas con la lista del cliente, así que una
    /// vista previa con listas mezcladas no es el total de ninguna venta: 400.</summary>
    [Fact]
    public async Task LineasConListasDePreciosDistintasSeRechazan()
    {
        var ctx = await apoyo.PrepararAsync(nameof(LineasConListasDePreciosDistintasSeRechazan), ModoPuntoVenta.Web);
        var a = await apoyo.ArticuloAsync(ctx, "a", 1000m);
        var b = await apoyo.ArticuloAsync(ctx, "b", 1000m);
        var mezcla = new SolicitudDeResolucion(
        [
            new LineaDeResolucion(a, ctx.IdEmpresa, ctx.IdListaPrecio, 1m),
            new LineaDeResolucion(b, ctx.IdEmpresa, ctx.IdListaPrecio + 1000, 1m)
        ]);

        var respuesta = await ctx.Admin.PostAsJsonAsync(Ruta, mezcla);

        Assert.Equal(HttpStatusCode.BadRequest, respuesta.StatusCode);
        var problema = await respuesta.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("lista_inconsistente", problema.GetProperty("codigo").GetString());
    }

    /// <summary>Documenta el requisito del contrato (doc 10, "Vista previa del carrito"): la vista previa usa la empresa que
    /// ENVÍA el cliente y la venta la de su punto de venta. Un combo propio de la empresa no aparece en la vista previa si
    /// el cliente omite <c>idEmpresa</c>, aunque la venta SÍ lo aplica: sin la empresa del punto de venta en cada línea, la
    /// vista previa no es el total de la venta.</summary>
    [Fact]
    public async Task SiElClienteOmiteLaEmpresaLaVistaPreviaNoMuestraUnComboPropioQueLaVentaSiAplica()
    {
        var ctx = await apoyo.PrepararAsync(nameof(SiElClienteOmiteLaEmpresaLaVistaPreviaNoMuestraUnComboPropioQueLaVentaSiAplica), ModoPuntoVenta.Web);
        var a = await apoyo.ArticuloAsync(ctx, "a", 1000m);
        var b = await apoyo.ArticuloAsync(ctx, "b", 1000m);
        var combo = await apoyo.ComboAsync(ctx, 1500m, [Art(a), Art(b)], idEmpresa: ctx.IdEmpresa);

        var conEmpresa = await PrevisualizarAsync(ctx, (a, 1m), (b, 1m));
        var sinEmpresaRespuesta = await ctx.Admin.PostAsJsonAsync(
            Ruta, new SolicitudDeResolucion([Linea(ctx, a, 1m, idEmpresa: null), Linea(ctx, b, 1m, idEmpresa: null)]));
        Assert.Equal(HttpStatusCode.OK, sinEmpresaRespuesta.StatusCode);
        var sinEmpresa = JsonDocument.Parse(await sinEmpresaRespuesta.Content.ReadAsStringAsync()).RootElement;

        Assert.Equal(combo, Assert.Single(conEmpresa.GetProperty("combos").EnumerateArray()).GetProperty("idCombo").GetInt32());
        Assert.Equal(0, sinEmpresa.GetProperty("combos").GetArrayLength());

        var venta = await ctx.Admin.PostAsJsonAsync("/api/ventas", new SolicitudDeVenta(
            ctx.IdPuntoVenta, null, "TX", null, [new LineaDeVenta(a, 1m, null), new LineaDeVenta(b, 1m, null)],
            [new PagoDeVenta(ctx.IdMedioEfectivo, 1500m, null, 0m)], null, null));
        var cuerpo = await venta.Content.ReadAsStringAsync();
        Assert.True(venta.StatusCode == HttpStatusCode.Created, cuerpo);
        var emitido = JsonSerializer.Deserialize<ComprobanteEmitido>(cuerpo, OpcionesJson)!;
        Assert.Equal(1500m, emitido.Total);
        Assert.All(emitido.Items, i => Assert.Equal(combo, i.IdCombo));
    }

    /// <summary>Cláusula: la ruta queda en el allowlist de <c>SuperficieDeAutorizacionTests</c> (sin
    /// <c>GestionDeCatalogo</c>), así que un cajero Vendedor de dispositivo tiene que poder llamarla y recibir el combo.</summary>
    [Fact]
    public async Task UnVendedorPrevisualizaElCarritoConCombos()
    {
        var ctx = await apoyo.PrepararAsync(nameof(UnVendedorPrevisualizaElCarritoConCombos), ModoPuntoVenta.Escritorio);
        var pancho = await apoyo.ArticuloAsync(ctx, "pancho", 3000m);
        var coca = await apoyo.ArticuloAsync(ctx, "coca", 2500m);
        var combo = await apoyo.ComboAsync(ctx, 5000m, [Art(pancho), Art(coca)]);

        var respuesta = await ctx.Cajero!.PostAsJsonAsync(
            Ruta, new SolicitudDeResolucion([Linea(ctx, pancho, 1m), Linea(ctx, coca, 1m)]));

        Assert.Equal(HttpStatusCode.OK, respuesta.StatusCode);
        var cuerpo = JsonDocument.Parse(await respuesta.Content.ReadAsStringAsync()).RootElement;
        Assert.Equal(combo, Assert.Single(cuerpo.GetProperty("combos").EnumerateArray()).GetProperty("idCombo").GetInt32());
    }

    // ---- PorLinea y la ruta de siempre ----

    [Fact]
    public async Task PorLineaNuncaDevuelveCombos()
    {
        var ctx = await apoyo.PrepararAsync(nameof(PorLineaNuncaDevuelveCombos), ModoPuntoVenta.Web);
        var pancho = await apoyo.ArticuloAsync(ctx, "pancho", 3000m);
        var coca = await apoyo.ArticuloAsync(ctx, "coca", 2500m);
        await apoyo.ComboAsync(ctx, 5000m, [Art(pancho), Art(coca)]);

        var solicitud = new SolicitudDeResolucion(
            [Linea(ctx, pancho, 1m), Linea(ctx, coca, 1m)], Agregacion: AgregacionDeResolucion.PorLinea);
        var respuesta = await ctx.Admin.PostAsJsonAsync(Ruta, solicitud);
        Assert.Equal(HttpStatusCode.OK, respuesta.StatusCode);
        var cuerpo = JsonDocument.Parse(await respuesta.Content.ReadAsStringAsync()).RootElement;

        Assert.Equal(2, cuerpo.GetProperty("lineas").GetArrayLength());
        Assert.All(cuerpo.GetProperty("lineas").EnumerateArray(), l => Assert.False(l.TryGetProperty("combo", out _)));
        Assert.Equal(0, cuerpo.GetProperty("combos").GetArrayLength());
    }

    [Fact]
    public async Task UnaAgregacionInvalidaSeRechaza()
    {
        var ctx = await apoyo.PrepararAsync(nameof(UnaAgregacionInvalidaSeRechaza), ModoPuntoVenta.Web);
        var pancho = await apoyo.ArticuloAsync(ctx, "pancho", 3000m);

        var respuesta = await ctx.Admin.PostAsJsonAsync(
            Ruta, new SolicitudDeResolucion([Linea(ctx, pancho, 1m)], Agregacion: (AgregacionDeResolucion)7));

        Assert.Equal(HttpStatusCode.BadRequest, respuesta.StatusCode);
        var problema = await respuesta.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("agregacion_invalida", problema.GetProperty("codigo").GetString());
    }

    [Fact]
    public async Task ElResolverDeSiempreSigueDevolviendoUnArregloSinLaClaveCombo()
    {
        var ctx = await apoyo.PrepararAsync(nameof(ElResolverDeSiempreSigueDevolviendoUnArregloSinLaClaveCombo), ModoPuntoVenta.Web);
        var pancho = await apoyo.ArticuloAsync(ctx, "pancho", 3000m);
        var coca = await apoyo.ArticuloAsync(ctx, "coca", 2500m);
        await apoyo.ComboAsync(ctx, 5000m, [Art(pancho), Art(coca)]);

        var respuesta = await ctx.Admin.PostAsJsonAsync(
            "/api/ofertas/resolver", new SolicitudDeResolucion([Linea(ctx, pancho, 1m), Linea(ctx, coca, 1m)]));
        Assert.Equal(HttpStatusCode.OK, respuesta.StatusCode);
        var cuerpo = JsonDocument.Parse(await respuesta.Content.ReadAsStringAsync()).RootElement;

        Assert.Equal(JsonValueKind.Array, cuerpo.ValueKind);
        Assert.All(cuerpo.EnumerateArray(), l => Assert.False(l.TryGetProperty("combo", out _)));
        Assert.Equal(3000m, cuerpo[0].GetProperty("precioOriginal").GetDecimal());
    }

    [Fact]
    public async Task UnLoteVacioDevuelveUnaRespuestaVacia()
    {
        var ctx = await apoyo.PrepararAsync(nameof(UnLoteVacioDevuelveUnaRespuestaVacia), ModoPuntoVenta.Web);

        var respuesta = await ctx.Admin.PostAsJsonAsync(Ruta, new SolicitudDeResolucion([]));

        Assert.Equal(HttpStatusCode.OK, respuesta.StatusCode);
        var cuerpo = JsonDocument.Parse(await respuesta.Content.ReadAsStringAsync()).RootElement;
        Assert.Equal(0, cuerpo.GetProperty("lineas").GetArrayLength());
        Assert.Equal(0, cuerpo.GetProperty("combos").GetArrayLength());
    }

    [Fact]
    public async Task SinLineasSeRechazaComoEnElResolverDeSiempre()
    {
        var ctx = await apoyo.PrepararAsync(nameof(SinLineasSeRechazaComoEnElResolverDeSiempre), ModoPuntoVenta.Web);

        var respuesta = await ctx.Admin.PostAsJsonAsync(Ruta, new SolicitudDeResolucion(null));

        Assert.Equal(HttpStatusCode.BadRequest, respuesta.StatusCode);
        var problema = await respuesta.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("lineas_requeridas", problema.GetProperty("codigo").GetString());
    }
}
