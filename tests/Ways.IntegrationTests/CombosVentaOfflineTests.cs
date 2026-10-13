using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Ways.Application.Ventas;
using Ways.Domain.Articulos;
using Ways.Domain.Organizacion;
using static Ways.IntegrationTests.CombosApoyo;

namespace Ways.IntegrationTests;

/// <summary>
/// Combos en una venta que un dispositivo cobró offline (<c>POST /api/ventas</c> con <c>NumeroPreasignado</c>): el
/// servidor graba literalmente lo que el dispositivo calculó (unidades consumidas, parte del combo como importe de
/// línea y combo), valida su forma y audita —sin bloquear— la diferencia contra su propia detección de combos.
/// </summary>
[Collection("Ways.IntegrationTests secuencial")]
public class CombosVentaOfflineTests(WaysApiFixture fixture) : IClassFixture<WaysApiFixture>
{
    private readonly CombosApoyo apoyo = new(fixture);

    private static LineaDeVenta Offline(
        int idArticulo, decimal cantidad, decimal precio, decimal descuentoUnitario = 0m, decimal? unidades = null,
        decimal? ahorro = null, int? idCombo = null, decimal? ajusteManualPorcentaje = null) =>
        new(idArticulo, cantidad, null, PrecioUnitario: precio, DescuentoUnitario: descuentoUnitario,
            AjusteManualPorcentaje: ajusteManualPorcentaje, UnidadesConsumidasPorCombo: unidades,
            AhorroDeCombo: ahorro, IdCombo: idCombo);

    private static SolicitudDeVenta Venta(
        Escenario ctx, long numero, decimal total, string tipo = "TX", params LineaDeVenta[] lineas) =>
        new(ctx.IdPuntoVenta, null, tipo, null, [.. lineas],
            [new PagoDeVenta(ctx.IdMedioEfectivo, total, null, 0m)], null, null, NumeroPreasignado: numero);

    private static async Task<ComprobanteEmitido> EmitirAsync(Escenario ctx, SolicitudDeVenta solicitud)
    {
        var respuesta = await ctx.Cajero!.PostAsJsonAsync("/api/ventas", solicitud);
        var cuerpo = await respuesta.Content.ReadAsStringAsync();
        Assert.True(respuesta.StatusCode == HttpStatusCode.Created, cuerpo);

        return JsonSerializer.Deserialize<ComprobanteEmitido>(cuerpo, OpcionesJson)!;
    }

    private static async Task<string> CodigoDeRechazoAsync(Escenario ctx, SolicitudDeVenta solicitud)
    {
        var respuesta = await ctx.Cajero!.PostAsJsonAsync("/api/ventas", solicitud);
        Assert.Equal(HttpStatusCode.BadRequest, respuesta.StatusCode);
        var problema = await respuesta.Content.ReadFromJsonAsync<JsonElement>();

        return problema.GetProperty("codigo").GetString()!;
    }

    private async Task<List<JsonElement>> DiscrepanciasAuditadasAsync(Escenario ctx)
    {
        await using var db = apoyo.Db(ctx.IdTenant);
        var filas = await db.Auditoria
            .Where(a => a.IdTenant == ctx.IdTenant && a.Accion == "venta.discrepancia")
            .OrderBy(a => a.Id)
            .ToListAsync();

        return [.. filas.Select(f => JsonDocument.Parse(f.ValorNuevo).RootElement)];
    }

    private async Task<(Escenario Ctx, int Pancho, int Coca, int Combo)> PanchoYCocaAsync(string nombre)
    {
        var ctx = await apoyo.PrepararAsync(nombre, ModoPuntoVenta.Escritorio);
        await ReservarBloqueAsync(ctx);
        var pancho = await apoyo.ArticuloAsync(ctx, "pancho", 3000m);
        var coca = await apoyo.ArticuloAsync(ctx, "coca", 2500m);
        var combo = await apoyo.ComboAsync(ctx, 5000m, [Art(pancho), Art(coca)]);

        return (ctx, pancho, coca, combo);
    }

    // ---- Lo que se graba ----

    [Fact]
    public async Task UnComboBienCalculadoPorElDispositivoSeGrabaTalCualYNoSeAudita()
    {
        var (ctx, pancho, coca, combo) = await PanchoYCocaAsync(nameof(UnComboBienCalculadoPorElDispositivoSeGrabaTalCualYNoSeAudita));

        var emitido = await EmitirAsync(ctx, Venta(ctx, 1, 5000m, "TX",
            Offline(pancho, 1m, 3000m, unidades: 1, ahorro: 273m, idCombo: combo),
            Offline(coca, 1m, 2500m, unidades: 1, ahorro: 227m, idCombo: combo)));

        Assert.Equal(5000m, emitido.Total);
        Assert.Equal([273m, 227m], emitido.Items.Select(i => i.Descuento));
        Assert.Equal([2727m, 2273m], emitido.Items.Select(i => i.Total));
        Assert.All(emitido.Items, i =>
        {
            Assert.Equal(combo, i.IdCombo);
            Assert.False(i.PrecioDiscrepante);
        });

        var persistidos = await apoyo.ItemsAsync(ctx, emitido.Id);
        Assert.Equal([273m, 227m], persistidos.Select(i => i.Descuento));
        Assert.All(persistidos, i => Assert.Equal(combo, i.IdCombo));
        Assert.Empty(await DiscrepanciasAuditadasAsync(ctx));
    }

    /// <summary>Cláusula: el descuento de la línea es <c>round(descuento unitario × unidades fuera del combo) + parte del
    /// combo</c> y la línea conserva el <c>id_oferta</c> mientras le queden unidades con oferta.</summary>
    [Fact]
    public async Task UnaLineaConConsumoParcialConservaLaOfertaDeLasUnidadesFueraDelCombo()
    {
        var (ctx, pancho, coca, combo) = await PanchoYCocaAsync(nameof(UnaLineaConConsumoParcialConservaLaOfertaDeLasUnidadesFueraDelCombo));
        var oferta = await apoyo.OfertaDePrecioAsync(ctx, pancho, 2800m);

        var emitido = await EmitirAsync(ctx, Venta(ctx, 1, 7800m, "TX",
            Offline(pancho, 2m, 3000m, descuentoUnitario: 200m, unidades: 1, ahorro: 273m, idCombo: combo),
            Offline(coca, 1m, 2500m, unidades: 1, ahorro: 227m, idCombo: combo)));

        Assert.Equal([473m, 227m], emitido.Items.Select(i => i.Descuento));
        Assert.Equal([5527m, 2273m], emitido.Items.Select(i => i.Total));
        Assert.Equal(oferta, emitido.Items[0].IdOferta);
        Assert.Equal(combo, emitido.Items[0].IdCombo);
        Assert.All(emitido.Items, i => Assert.False(i.PrecioDiscrepante));
        Assert.Empty(await DiscrepanciasAuditadasAsync(ctx));
    }

    /// <summary>Cláusula: una línea con TODAS sus unidades en el combo no recibió oferta, así que no lleva id_oferta
    /// aunque el servidor tenga una para el artículo.</summary>
    [Fact]
    public async Task UnaLineaConTodasSusUnidadesEnElComboNoLlevaIdOferta()
    {
        var (ctx, pancho, coca, combo) = await PanchoYCocaAsync(nameof(UnaLineaConTodasSusUnidadesEnElComboNoLlevaIdOferta));
        await apoyo.OfertaDePrecioAsync(ctx, pancho, 2800m);

        var emitido = await EmitirAsync(ctx, Venta(ctx, 1, 5000m, "TX",
            Offline(pancho, 1m, 3000m, unidades: 1, ahorro: 273m, idCombo: combo),
            Offline(coca, 1m, 2500m, unidades: 1, ahorro: 227m, idCombo: combo)));

        Assert.Null(emitido.Items[0].IdOferta);
        Assert.Empty(await DiscrepanciasAuditadasAsync(ctx));
    }

    // ---- Discrepancias ----

    [Fact]
    public async Task UnaParteDelComboMalCalculadaSeGrabaTalCualYSeAudita()
    {
        var (ctx, pancho, coca, combo) = await PanchoYCocaAsync(nameof(UnaParteDelComboMalCalculadaSeGrabaTalCualYSeAudita));

        var emitido = await EmitirAsync(ctx, Venta(ctx, 1, 5300m, "TX",
            Offline(pancho, 1m, 3000m, unidades: 1, ahorro: 100m, idCombo: combo),
            Offline(coca, 1m, 2500m, unidades: 1, ahorro: 100m, idCombo: combo)));

        Assert.Equal(5300m, emitido.Total);
        Assert.Equal([100m, 100m], emitido.Items.Select(i => i.Descuento));
        Assert.All(emitido.Items, i => Assert.True(i.PrecioDiscrepante));

        var fila = Assert.Single(await DiscrepanciasAuditadasAsync(ctx));
        var item = fila.GetProperty("items")[0];
        Assert.Equal(pancho, item.GetProperty("id_articulo").GetInt32());
        Assert.Equal(100m, item.GetProperty("combo_cobrado").GetProperty("ahorro").GetDecimal());
        Assert.Equal(combo, item.GetProperty("combo_cobrado").GetProperty("id_combo").GetInt32());
        Assert.False(item.GetProperty("combo_cobrado").GetProperty("inexistente").GetBoolean());
        Assert.Equal(273m, item.GetProperty("combo_esperado").GetProperty("ahorro").GetDecimal());
        Assert.Equal(1, item.GetProperty("combo_esperado").GetProperty("unidades").GetInt32());
        Assert.Equal(combo, item.GetProperty("combo_esperado").GetProperty("id_combo").GetInt32());
    }

    /// <summary>Un dispositivo que no detectó el combo (versión vieja, instantánea vieja) cobró a lista: el servidor sí
    /// lo detecta, así que la diferencia se audita; lo cobrado se graba igual.</summary>
    [Fact]
    public async Task UnComboQueElDispositivoNoDetectoSeAuditaYSeGrabaLoCobrado()
    {
        var (ctx, pancho, coca, combo) = await PanchoYCocaAsync(nameof(UnComboQueElDispositivoNoDetectoSeAuditaYSeGrabaLoCobrado));

        var emitido = await EmitirAsync(ctx, Venta(ctx, 1, 5500m, "TX",
            Offline(pancho, 1m, 3000m), Offline(coca, 1m, 2500m)));

        Assert.Equal(5500m, emitido.Total);
        Assert.All(emitido.Items, i =>
        {
            Assert.Null(i.IdCombo);
            Assert.True(i.PrecioDiscrepante);
        });

        var item = Assert.Single(await DiscrepanciasAuditadasAsync(ctx)).GetProperty("items")[0];
        Assert.Equal(JsonValueKind.Null, item.GetProperty("combo_cobrado").ValueKind);
        Assert.Equal(combo, item.GetProperty("combo_esperado").GetProperty("id_combo").GetInt32());
    }

    /// <summary>Un combo que el dispositivo calculó y el servidor no detecta (el combo se desactivó después de que el
    /// dispositivo bajó su instantánea) se graba y se audita, y <c>combo_esperado</c> es <c>null</c> (el servidor no
    /// detectó ninguno). El único combo del tenant es el dado de baja: con uno activo sobre los mismos artículos el
    /// servidor sí detectaría algo.</summary>
    [Fact]
    public async Task UnComboQueElServidorNoDetectaSeGrabaConSuIdYSeAudita()
    {
        var ctx = await apoyo.PrepararAsync(nameof(UnComboQueElServidorNoDetectaSeGrabaConSuIdYSeAudita), ModoPuntoVenta.Escritorio);
        await ReservarBloqueAsync(ctx);
        var pancho = await apoyo.ArticuloAsync(ctx, "pancho", 3000m);
        var coca = await apoyo.ArticuloAsync(ctx, "coca", 2500m);
        var dadoDeBaja = await apoyo.ComboAsync(ctx, 4000m, [Art(pancho), Art(coca)], dadoDeBaja: true);

        var emitido = await EmitirAsync(ctx, Venta(ctx, 1, 4000m, "TX",
            Offline(pancho, 1m, 3000m, unidades: 1, ahorro: 1090.91m, idCombo: dadoDeBaja),
            Offline(coca, 1m, 2500m, unidades: 1, ahorro: 409.09m, idCombo: dadoDeBaja)));

        // La baja es lógica: la fila sigue, así que la venta que ya ocurrió conserva su combo.
        Assert.All(emitido.Items, i => Assert.Equal(dadoDeBaja, i.IdCombo));
        Assert.Equal(4000m, emitido.Total);

        var fila = Assert.Single(await DiscrepanciasAuditadasAsync(ctx));
        var item = fila.GetProperty("items")[0];
        Assert.Equal(dadoDeBaja, item.GetProperty("combo_cobrado").GetProperty("id_combo").GetInt32());
        Assert.False(item.GetProperty("combo_cobrado").GetProperty("inexistente").GetBoolean());
        Assert.Equal(JsonValueKind.Null, item.GetProperty("combo_esperado").ValueKind);
    }

    /// <summary>Decisión: un id de combo que el tenant nunca tuvo no se puede grabar (la FK lo rechazaría y la venta
    /// quedaría varada). Se conservan los importes, <c>id_combo</c> queda en null y se audita, aunque los importes
    /// coincidan con los que el servidor espera.</summary>
    [Fact]
    public async Task UnIdDeComboInexistenteConservaLosImportesDejaIdComboNuloYSeAudita()
    {
        var (ctx, pancho, coca, combo) = await PanchoYCocaAsync(nameof(UnIdDeComboInexistenteConservaLosImportesDejaIdComboNuloYSeAudita));
        const int idInexistente = 987_654;

        var emitido = await EmitirAsync(ctx, Venta(ctx, 1, 5000m, "TX",
            Offline(pancho, 1m, 3000m, unidades: 1, ahorro: 273m, idCombo: idInexistente),
            Offline(coca, 1m, 2500m, unidades: 1, ahorro: 227m, idCombo: combo)));

        Assert.Equal(5000m, emitido.Total);
        Assert.Equal([273m, 227m], emitido.Items.Select(i => i.Descuento));
        Assert.Null(emitido.Items[0].IdCombo);
        Assert.Equal(combo, emitido.Items[1].IdCombo);

        var fila = Assert.Single(await DiscrepanciasAuditadasAsync(ctx));
        var auditados = fila.GetProperty("items");
        var item = Assert.Single(auditados.EnumerateArray());
        Assert.Equal(pancho, item.GetProperty("id_articulo").GetInt32());
        Assert.True(item.GetProperty("combo_cobrado").GetProperty("inexistente").GetBoolean());
        Assert.Equal(idInexistente, item.GetProperty("combo_cobrado").GetProperty("id_combo").GetInt32());
    }

    [Fact]
    public async Task UnComboDeOtroTenantTambienEsInexistente()
    {
        var (ctx, pancho, coca, _) = await PanchoYCocaAsync(nameof(UnComboDeOtroTenantTambienEsInexistente) + "A");
        var (ajeno, panchoAjeno, cocaAjena, comboAjeno) = await PanchoYCocaAsync(nameof(UnComboDeOtroTenantTambienEsInexistente) + "B");
        Assert.NotEqual(ctx.IdTenant, ajeno.IdTenant);
        Assert.NotEqual(0, panchoAjeno + cocaAjena);

        var emitido = await EmitirAsync(ctx, Venta(ctx, 1, 5000m, "TX",
            Offline(pancho, 1m, 3000m, unidades: 1, ahorro: 273m, idCombo: comboAjeno),
            Offline(coca, 1m, 2500m, unidades: 1, ahorro: 227m, idCombo: comboAjeno)));

        Assert.All(emitido.Items, i => Assert.Null(i.IdCombo));
        Assert.Single(await DiscrepanciasAuditadasAsync(ctx));
    }

    [Fact]
    public async Task ElAjusteManualSeAplicaSobreElNetoConElComboYNoEsUnaDiscrepancia()
    {
        var (ctx, pancho, coca, combo) = await PanchoYCocaAsync(nameof(ElAjusteManualSeAplicaSobreElNetoConElComboYNoEsUnaDiscrepancia));

        // −10 % sobre 2273 son 227 menos: la coca queda en 2046.
        var emitido = await EmitirAsync(ctx, Venta(ctx, 1, 4773m, "TX",
            Offline(pancho, 1m, 3000m, unidades: 1, ahorro: 273m, idCombo: combo),
            Offline(coca, 1m, 2500m, unidades: 1, ahorro: 227m, idCombo: combo, ajusteManualPorcentaje: -10m)));

        Assert.Equal(2046m, emitido.Items[1].Total);
        Assert.All(emitido.Items, i => Assert.False(i.PrecioDiscrepante));
        Assert.Empty(await DiscrepanciasAuditadasAsync(ctx));
    }

    /// <summary>Un dispositivo que parte el combo con centavos (272,50 y 227,50, suman 500) graba lo que mandó: cada
    /// línea redondea su neto al peso por su cuenta (2727,50 sube a 2728, 2272,50 a 2273) y el total es 5001, un peso
    /// más que el precio del combo. La línea se audita como discrepante contra lo que el servidor detecta (273 y 227).</summary>
    [Fact]
    public async Task UnaParteDeComboConCentavosDelDispositivoRedondeaPorLineaYSumaUnPesoDeMas()
    {
        var (ctx, pancho, coca, combo) = await PanchoYCocaAsync(nameof(UnaParteDeComboConCentavosDelDispositivoRedondeaPorLineaYSumaUnPesoDeMas));

        var emitido = await EmitirAsync(ctx, Venta(ctx, 1, 5001m, "TX",
            Offline(pancho, 1m, 3000m, unidades: 1, ahorro: 272.50m, idCombo: combo),
            Offline(coca, 1m, 2500m, unidades: 1, ahorro: 227.50m, idCombo: combo)));

        Assert.Equal(5001m, emitido.Total);
        Assert.Equal([272m, 227m], emitido.Items.Select(i => i.Descuento));
        Assert.True(emitido.Items[0].PrecioDiscrepante);
        Assert.False(emitido.Items[1].PrecioDiscrepante);
        Assert.Single(await DiscrepanciasAuditadasAsync(ctx));
    }

    /// <summary>Con el mismo reparto con centavos, el pago de 5000 que el dispositivo cobró queda un peso por debajo del
    /// total de 5001 del servidor y la venta se acepta porque ese peso entra en la tolerancia de pago por defecto (10): lo
    /// que sostiene este caso es la tolerancia, no una regla de combos.</summary>
    [Fact]
    public async Task UnaParteDeComboConCentavosCobradaAlPrecioDelComboEntraEnLaToleranciaDePago()
    {
        var (ctx, pancho, coca, combo) = await PanchoYCocaAsync(nameof(UnaParteDeComboConCentavosCobradaAlPrecioDelComboEntraEnLaToleranciaDePago));

        var emitido = await EmitirAsync(ctx, Venta(ctx, 1, 5000m, "TX",
            Offline(pancho, 1m, 3000m, unidades: 1, ahorro: 272.50m, idCombo: combo),
            Offline(coca, 1m, 2500m, unidades: 1, ahorro: 227.50m, idCombo: combo)));

        Assert.Equal(5001m, emitido.Total);
    }

    // ---- Validación de la forma ----

    private static LineaDeVenta PanchoOffline(
        int idArticulo, decimal? unidades, decimal? ahorro, int? idCombo, decimal cantidad = 1m) =>
        Offline(idArticulo, cantidad, 3000m, unidades: unidades, ahorro: ahorro, idCombo: idCombo);

    [Fact]
    public async Task LosDatosDeComboSinPrecioOfflineSeRechazan()
    {
        var (ctx, pancho, _, combo) = await PanchoYCocaAsync(nameof(LosDatosDeComboSinPrecioOfflineSeRechazan));

        var sinPrecio = new LineaDeVenta(
            pancho, 1m, null, UnidadesConsumidasPorCombo: 1, AhorroDeCombo: 100m, IdCombo: combo);

        Assert.Equal(
            "combo_offline_no_admitido",
            await CodigoDeRechazoAsync(ctx, Venta(ctx, 1, 3000m, "TX", sinPrecio)));
    }

    [Theory]
    [InlineData(false, true, true)]
    [InlineData(true, false, true)]
    [InlineData(true, true, false)]
    [InlineData(true, false, false)]
    public async Task LosTresDatosDeComboVanJuntosOnoVan(bool unidades, bool ahorro, bool idCombo)
    {
        var (ctx, pancho, _, combo) = await PanchoYCocaAsync(nameof(LosTresDatosDeComboVanJuntosOnoVan));

        var linea = PanchoOffline(pancho, unidades ? 1 : null, ahorro ? 100m : null, idCombo ? combo : null);

        Assert.Equal("combo_offline_incompleto", await CodigoDeRechazoAsync(ctx, Venta(ctx, 1, 3000m, "TX", linea)));
    }

    [Theory]
    [InlineData(0, 0, true)]         // cero unidades
    [InlineData(2, 100, true)]       // unidades por encima de la cantidad (1)
    [InlineData(1, -1, true)]        // parte negativa
    [InlineData(1, 3000.01, true)]   // parte por encima del bruto de las unidades consumidas
    [InlineData(1, 100.005, true)]   // más de dos decimales
    [InlineData(1, 100, false)]      // id de combo no positivo
    [InlineData(int.MaxValue, 100, true)] // unidades enormes
    public async Task UnDatoDeComboFueraDeRangoSeRechaza(int unidades, double ahorro, bool conIdValido)
    {
        var (ctx, pancho, _, combo) = await PanchoYCocaAsync(nameof(UnDatoDeComboFueraDeRangoSeRechaza));

        var linea = PanchoOffline(pancho, unidades, (decimal)ahorro, conIdValido ? combo : 0);

        Assert.Equal("combo_offline_invalido", await CodigoDeRechazoAsync(ctx, Venta(ctx, 1, 3000m, "TX", linea)));
    }

    [Fact]
    public async Task ElLimiteSuperiorDeLasUnidadesYDeLaParteSeAdmite()
    {
        var (ctx, pancho, _, combo) = await PanchoYCocaAsync(nameof(ElLimiteSuperiorDeLasUnidadesYDeLaParteSeAdmite));

        // Todas las unidades en el combo y la parte igual al bruto consumido: la línea queda gratis.
        var emitido = await EmitirAsync(ctx, Venta(ctx, 1, 0m, "TX", PanchoOffline(pancho, 2, 6000m, combo, cantidad: 2m)));

        Assert.Equal(0m, emitido.Total);
        Assert.Equal(6000m, Assert.Single(emitido.Items).Descuento);
    }

    /// <summary>Cláusula: la unidad de venta del artículo no restringe las unidades de combo de una venta offline.
    /// 1,5 unidades de un artículo por unidad están dentro de la cantidad de la línea (2) y de los tres decimales: la
    /// venta, que el dispositivo ya cobró, se registra.</summary>
    [Fact]
    public async Task UnaFraccionDeUnidadesDeUnArticuloPorUnidadSeAdmiteEnUnaVentaOffline()
    {
        var (ctx, pancho, _, combo) = await PanchoYCocaAsync(nameof(UnaFraccionDeUnidadesDeUnArticuloPorUnidadSeAdmiteEnUnaVentaOffline));

        var cuerpo = new
        {
            idPuntoVenta = ctx.IdPuntoVenta,
            codigoTipoComprobante = "TX",
            numeroPreasignado = 1,
            lineas = new[]
            {
                new
                {
                    idArticulo = pancho, cantidad = 2, precioUnitario = 3000, descuentoUnitario = 0,
                    unidadesConsumidasPorCombo = 1.5, ahorroDeCombo = 100, idCombo = combo
                }
            },
            pagos = new[] { new { idMedioPago = ctx.IdMedioEfectivo, importe = 6000, vuelto = 0 } }
        };

        var respuesta = await ctx.Cajero!.PostAsJsonAsync("/api/ventas", cuerpo);

        Assert.Equal(HttpStatusCode.Created, respuesta.StatusCode);
    }

    private async Task<(Escenario Ctx, int Jamon, int Queso, int Combo)> JamonYQuesoAsync(string nombre)
    {
        var ctx = await apoyo.PrepararAsync(nombre, ModoPuntoVenta.Escritorio);
        await ReservarBloqueAsync(ctx);
        var jamon = await apoyo.ArticuloAsync(ctx, "jamon", 10000m, Ways.Domain.Articulos.UnidadVenta.Peso);
        var queso = await apoyo.ArticuloAsync(ctx, "queso", 8000m, Ways.Domain.Articulos.UnidadVenta.Peso);
        var combo = await apoyo.ComboAsync(ctx, 3000m, [Art(jamon, 0.200m), Art(queso, 0.200m)]);

        return (ctx, jamon, queso, combo);
    }

    /// <summary>El combo de dos artículos pesables que calculó el dispositivo se graba tal cual: 200 g de cada uno en
    /// el combo y los 15 g que sobran del jamón a precio de lista. Coincide con la detección del servidor, así que no
    /// se audita ninguna discrepancia.</summary>
    [Fact]
    public async Task UnComboDeArticulosPesablesCalculadoPorElDispositivoSeGrabaTalCualYNoSeAudita()
    {
        var (ctx, jamon, queso, combo) = await JamonYQuesoAsync(
            nameof(UnComboDeArticulosPesablesCalculadoPorElDispositivoSeGrabaTalCualYNoSeAudita));

        var emitido = await EmitirAsync(ctx, Venta(ctx, 1, 3150m, "TX",
            Offline(jamon, 0.215m, 10000m, unidades: 0.200m, ahorro: 333m, idCombo: combo),
            Offline(queso, 0.200m, 8000m, unidades: 0.200m, ahorro: 267m, idCombo: combo)));

        Assert.Equal(3150m, emitido.Total);
        Assert.Equal([333m, 267m], emitido.Items.Select(i => i.Descuento));
        Assert.Equal([1817m, 1333m], emitido.Items.Select(i => i.Total));
        Assert.All(emitido.Items, i =>
        {
            Assert.Equal(combo, i.IdCombo);
            Assert.False(i.PrecioDiscrepante);
        });

        var persistidos = await apoyo.ItemsAsync(ctx, emitido.Id);
        Assert.Equal([(0.215m, 333m, 1817m), (0.200m, 267m, 1333m)], persistidos.Select(i => (i.Cantidad, i.Descuento, i.Total)));
        Assert.All(persistidos, i => Assert.Equal(combo, i.IdCombo));
        Assert.Empty(await DiscrepanciasAuditadasAsync(ctx));
    }

    /// <summary>Cláusula: el ahorro acumulado de una línea pesable nunca supera el bruto de lo que consumió. Tres
    /// aplicaciones de 0,250 kg a 9,99 redondean 2,4975 a 2,50 cada una (7,50 en total) y la parte de cobro de la
    /// línea es cero; el motor acota el ahorro a 7,49 y la venta offline correcta no se rechaza.</summary>
    [Fact]
    public async Task UnaVentaOfflineConElAhorroAcotadoDeUnaLineaPesableSeAdmite()
    {
        var ctx = await apoyo.PrepararAsync(nameof(UnaVentaOfflineConElAhorroAcotadoDeUnaLineaPesableSeAdmite), ModoPuntoVenta.Escritorio);
        await ReservarBloqueAsync(ctx);
        var jamon = await apoyo.ArticuloAsync(ctx, "jamon", 9.99m, Ways.Domain.Articulos.UnidadVenta.Peso);
        var coca = await apoyo.ArticuloAsync(ctx, "coca", 500m);
        var combo = await apoyo.ComboAsync(ctx, 100m, [Art(jamon, 0.250m), Art(coca)]);

        var emitido = await EmitirAsync(ctx, Venta(ctx, 1, 300m, "TX",
            Offline(jamon, 0.750m, 9.99m, unidades: 0.750m, ahorro: 7.49m, idCombo: combo),
            Offline(coca, 3m, 500m, unidades: 3m, ahorro: 1200m, idCombo: combo)));

        Assert.Equal(300m, emitido.Total);
        Assert.Equal([0m, 300m], emitido.Items.Select(i => i.Total));
        Assert.Empty(await DiscrepanciasAuditadasAsync(ctx));
    }

    /// <summary>Cláusula: las unidades de combo llevan a lo sumo tres decimales; con cuatro la venta se
    /// rechaza aunque estén dentro de la cantidad de la línea.</summary>
    [Fact]
    public async Task LasUnidadesDeComboConMasDeTresDecimalesSeRechazan()
    {
        var (ctx, jamon, _, combo) = await JamonYQuesoAsync(nameof(LasUnidadesDeComboConMasDeTresDecimalesSeRechazan));

        var linea = Offline(jamon, 0.215m, 10000m, unidades: 0.2001m, ahorro: 333m, idCombo: combo);

        Assert.Equal("combo_offline_invalido", await CodigoDeRechazoAsync(ctx, Venta(ctx, 1, 1817m, "TX", linea)));
    }

    /// <summary>Cláusula: la validación de las unidades de combo de una línea offline no mira la unidad de venta ACTUAL
    /// del artículo. El dispositivo ya cobró e imprimió con la unidad de su instantánea; si un administrador pasa el
    /// artículo a importe antes de que la venta suba, la venta se registra igual y no queda varada en la cola. La línea
    /// trae precio offline y no importe (con importe y sin precio la rechaza antes <c>combo_offline_no_admitido</c>,
    /// probado en <c>VentaPorImporteTests</c>).</summary>
    [Fact]
    public async Task UnaVentaOfflineConComboSeRegistraAunqueElArticuloHayaPasadoAImporte()
    {
        var (ctx, pancho, coca, combo) = await PanchoYCocaAsync(
            nameof(UnaVentaOfflineConComboSeRegistraAunqueElArticuloHayaPasadoAImporte));

        SolicitudDeVenta Solicitud(long numero) => Venta(ctx, numero, 5000m, "TX",
            Offline(pancho, 1m, 3000m, unidades: 1, ahorro: 273m, idCombo: combo),
            Offline(coca, 1m, 2500m, unidades: 1, ahorro: 227m, idCombo: combo));

        await EmitirAsync(ctx, Solicitud(1));

        await using (var db = apoyo.Db(ctx.IdTenant))
        {
            await db.Articulos.Where(a => a.Id == pancho).ExecuteUpdateAsync(s => s
                .SetProperty(a => a.UnidadVenta, UnidadVenta.Importe)
                .SetProperty(a => a.AcumulaEnVenta, false));
        }

        var emitido = await EmitirAsync(ctx, Solicitud(2));

        Assert.Equal(5000m, emitido.Total);
        Assert.Equal([273m, 227m], emitido.Items.Select(i => i.Descuento));
        Assert.All(emitido.Items, i => Assert.Equal(combo, i.IdCombo));
    }

    /// <summary>Cláusula: igual que con importe, un artículo pesable que pasó a unidad antes de que suba la venta
    /// offline conserva sus unidades de combo fraccionarias: la venta ya está cobrada y se registra.</summary>
    [Fact]
    public async Task UnaVentaOfflineConUnidadesDeComboFraccionariasSeRegistraAunqueElArticuloHayaPasadoAUnidad()
    {
        var (ctx, jamon, queso, combo) = await JamonYQuesoAsync(
            nameof(UnaVentaOfflineConUnidadesDeComboFraccionariasSeRegistraAunqueElArticuloHayaPasadoAUnidad));

        await using (var db = apoyo.Db(ctx.IdTenant))
        {
            await db.Articulos.Where(a => a.Id == jamon).ExecuteUpdateAsync(s => s
                .SetProperty(a => a.UnidadVenta, UnidadVenta.Unidad));
        }

        var emitido = await EmitirAsync(ctx, Venta(ctx, 1, 3150m, "TX",
            Offline(jamon, 0.215m, 10000m, unidades: 0.200m, ahorro: 333m, idCombo: combo),
            Offline(queso, 0.200m, 8000m, unidades: 0.200m, ahorro: 267m, idCombo: combo)));

        Assert.Equal(3150m, emitido.Total);
        Assert.Equal([333m, 267m], emitido.Items.Select(i => i.Descuento));
        Assert.All(emitido.Items, i => Assert.Equal(combo, i.IdCombo));
    }

    /// <summary>Cláusula: las unidades de combo de una línea offline tienen que ser mayores a cero y no superar la
    /// cantidad de la línea (1 en este caso).</summary>
    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    [InlineData(2)]
    public async Task LasUnidadesDeComboFueraDeRangoSeRechazan(int unidades)
    {
        var (ctx, pancho, _, combo) = await PanchoYCocaAsync($"{nameof(LasUnidadesDeComboFueraDeRangoSeRechazan)}{unidades}");

        var linea = Offline(pancho, 1m, 3000m, unidades: unidades, ahorro: 0m, idCombo: combo);

        Assert.Equal("combo_offline_invalido", await CodigoDeRechazoAsync(ctx, Venta(ctx, 1, 3000m, "TX", linea)));
    }

    /// <summary>Cláusula: el bruto de las unidades consumidas se calcula DESPUÉS de acotar las unidades a 1..cantidad. Con
    /// <c>int.MaxValue</c> unidades y un precio grande la multiplicación desborda el decimal: calculada antes de las
    /// guardas, esa línea terminaba en 500 en vez de en el 400 de contrato.</summary>
    [Fact]
    public async Task UnasUnidadesDeComboEnormesSeRechazanSinDesbordarElBruto()
    {
        var (ctx, pancho, _, combo) = await PanchoYCocaAsync(nameof(UnasUnidadesDeComboEnormesSeRechazanSinDesbordarElBruto));

        var linea = Offline(pancho, 1m, 100_000_000_000_000_000_000m, unidades: int.MaxValue, ahorro: 0m, idCombo: combo);

        Assert.Equal("combo_offline_invalido", await CodigoDeRechazoAsync(ctx, Venta(ctx, 1, 0m, "TX", linea)));
    }

    [Fact]
    public async Task UnaNotaDeCreditoNoAdmiteDatosDeCombo()
    {
        var (ctx, pancho, _, combo) = await PanchoYCocaAsync(nameof(UnaNotaDeCreditoNoAdmiteDatosDeCombo));

        var codigo = await CodigoDeRechazoAsync(
            ctx, Venta(ctx, 1, 0m, "NCX", PanchoOffline(pancho, 1, 100m, combo)));

        Assert.Equal("combo_offline_no_admitido", codigo);
    }

    // ---- Reenvío idempotente ----

    [Fact]
    public async Task ReenviarLaMismaVentaConComboDevuelveElMismoComprobanteSinDuplicarLaAuditoria()
    {
        var (ctx, pancho, coca, combo) = await PanchoYCocaAsync(nameof(ReenviarLaMismaVentaConComboDevuelveElMismoComprobanteSinDuplicarLaAuditoria));

        SolicitudDeVenta Solicitud() => Venta(ctx, 1, 5300m, "TX",
            Offline(pancho, 1m, 3000m, unidades: 1, ahorro: 100m, idCombo: combo),
            Offline(coca, 1m, 2500m, unidades: 1, ahorro: 100m, idCombo: combo));

        var primera = await EmitirAsync(ctx, Solicitud());
        var segunda = await EmitirAsync(ctx, Solicitud());

        Assert.Equal(primera.Id, segunda.Id);
        Assert.Equal(primera.Total, segunda.Total);
        Assert.Single(await DiscrepanciasAuditadasAsync(ctx));

        await using var db = apoyo.Db(ctx.IdTenant);
        Assert.Equal(1, await db.ComprobantesVenta.CountAsync(c => c.IdPuntoVenta == ctx.IdPuntoVenta && c.Numero == 1));
    }

    /// <summary>Decisión: los tres datos de combo NO forman parte del "mismo contenido" del reenvío bajo un número
    /// pre-asignado, igual que el precio y el descuento de la línea: son dinero que calculó el dispositivo, no la identidad
    /// de lo vendido (artículo, cantidad, ajuste manual, cliente, asociado, pagos). Un reenvío con otra parte de combo
    /// devuelve el comprobante ya emitido, que conserva lo que se grabó la primera vez.</summary>
    [Fact]
    public async Task UnReenvioConOtraParteDeComboDevuelveElComprobanteOriginalSinPisarlo()
    {
        var (ctx, pancho, coca, combo) = await PanchoYCocaAsync(nameof(UnReenvioConOtraParteDeComboDevuelveElComprobanteOriginalSinPisarlo));

        var primera = await EmitirAsync(ctx, Venta(ctx, 1, 5000m, "TX",
            Offline(pancho, 1m, 3000m, unidades: 1, ahorro: 273m, idCombo: combo),
            Offline(coca, 1m, 2500m, unidades: 1, ahorro: 227m, idCombo: combo)));
        var reenvio = await EmitirAsync(ctx, Venta(ctx, 1, 5000m, "TX",
            Offline(pancho, 1m, 3000m, unidades: 1, ahorro: 300m, idCombo: combo),
            Offline(coca, 1m, 2500m, unidades: 1, ahorro: 200m, idCombo: combo)));

        Assert.Equal(primera.Id, reenvio.Id);
        Assert.Equal([273m, 227m], reenvio.Items.Select(i => i.Descuento));
        Assert.Equal([273m, 227m], (await apoyo.ItemsAsync(ctx, primera.Id)).Select(i => i.Descuento));
    }
}
