using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Ways.Application.Ventas;
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
        int idArticulo, decimal cantidad, decimal precio, decimal descuentoUnitario = 0m, int? unidades = null,
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
            Offline(pancho, 1m, 3000m, unidades: 1, ahorro: 272.73m, idCombo: combo),
            Offline(coca, 1m, 2500m, unidades: 1, ahorro: 227.27m, idCombo: combo)));

        Assert.Equal(5000m, emitido.Total);
        Assert.Equal([272.73m, 227.27m], emitido.Items.Select(i => i.Descuento));
        Assert.Equal([2727.27m, 2272.73m], emitido.Items.Select(i => i.Total));
        Assert.All(emitido.Items, i =>
        {
            Assert.Equal(combo, i.IdCombo);
            Assert.False(i.PrecioDiscrepante);
        });

        var persistidos = await apoyo.ItemsAsync(ctx, emitido.Id);
        Assert.Equal([272.73m, 227.27m], persistidos.Select(i => i.Descuento));
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
            Offline(pancho, 2m, 3000m, descuentoUnitario: 200m, unidades: 1, ahorro: 272.73m, idCombo: combo),
            Offline(coca, 1m, 2500m, unidades: 1, ahorro: 227.27m, idCombo: combo)));

        Assert.Equal([472.73m, 227.27m], emitido.Items.Select(i => i.Descuento));
        Assert.Equal([5527.27m, 2272.73m], emitido.Items.Select(i => i.Total));
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
            Offline(pancho, 1m, 3000m, unidades: 1, ahorro: 272.73m, idCombo: combo),
            Offline(coca, 1m, 2500m, unidades: 1, ahorro: 227.27m, idCombo: combo)));

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
        Assert.Equal(272.73m, item.GetProperty("combo_esperado").GetProperty("ahorro").GetDecimal());
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
    /// dispositivo bajó su instantánea) se graba y se audita.</summary>
    [Fact]
    public async Task UnComboQueElServidorNoDetectaSeGrabaConSuIdYSeAudita()
    {
        var (ctx, pancho, coca, _) = await PanchoYCocaAsync(nameof(UnComboQueElServidorNoDetectaSeGrabaConSuIdYSeAudita));
        var dadoDeBaja = await apoyo.ComboAsync(ctx, 4000m, [Art(pancho), Art(coca)], dadoDeBaja: true);

        var emitido = await EmitirAsync(ctx, Venta(ctx, 1, 4000m, "TX",
            Offline(pancho, 1m, 3000m, unidades: 1, ahorro: 1090.91m, idCombo: dadoDeBaja),
            Offline(coca, 1m, 2500m, unidades: 1, ahorro: 409.09m, idCombo: dadoDeBaja)));

        // La baja es lógica: la fila sigue, así que la venta que ya ocurrió conserva su combo.
        Assert.All(emitido.Items, i => Assert.Equal(dadoDeBaja, i.IdCombo));
        Assert.Equal(4000m, emitido.Total);
        Assert.Single(await DiscrepanciasAuditadasAsync(ctx));
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
            Offline(pancho, 1m, 3000m, unidades: 1, ahorro: 272.73m, idCombo: idInexistente),
            Offline(coca, 1m, 2500m, unidades: 1, ahorro: 227.27m, idCombo: combo)));

        Assert.Equal(5000m, emitido.Total);
        Assert.Equal([272.73m, 227.27m], emitido.Items.Select(i => i.Descuento));
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
            Offline(pancho, 1m, 3000m, unidades: 1, ahorro: 272.73m, idCombo: comboAjeno),
            Offline(coca, 1m, 2500m, unidades: 1, ahorro: 227.27m, idCombo: comboAjeno)));

        Assert.All(emitido.Items, i => Assert.Null(i.IdCombo));
        Assert.Single(await DiscrepanciasAuditadasAsync(ctx));
    }

    [Fact]
    public async Task ElAjusteManualSeAplicaSobreElNetoConElComboYNoEsUnaDiscrepancia()
    {
        var (ctx, pancho, coca, combo) = await PanchoYCocaAsync(nameof(ElAjusteManualSeAplicaSobreElNetoConElComboYNoEsUnaDiscrepancia));

        // −10 % sobre 2272,73 son 227,27 menos: la coca queda en 2045,46.
        var emitido = await EmitirAsync(ctx, Venta(ctx, 1, 4772.73m, "TX",
            Offline(pancho, 1m, 3000m, unidades: 1, ahorro: 272.73m, idCombo: combo),
            Offline(coca, 1m, 2500m, unidades: 1, ahorro: 227.27m, idCombo: combo, ajusteManualPorcentaje: -10m)));

        Assert.Equal(2045.46m, emitido.Items[1].Total);
        Assert.All(emitido.Items, i => Assert.False(i.PrecioDiscrepante));
        Assert.Empty(await DiscrepanciasAuditadasAsync(ctx));
    }

    // ---- Validación de la forma ----

    private static LineaDeVenta PanchoOffline(
        int idArticulo, int? unidades, decimal? ahorro, int? idCombo, decimal cantidad = 1m) =>
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
    [InlineData(0, 0, true)]         // unidades por debajo de 1
    [InlineData(2, 100, true)]       // unidades por encima de la cantidad (1)
    [InlineData(1, -1, true)]        // parte negativa
    [InlineData(1, 3000.01, true)]   // parte por encima del bruto de las unidades consumidas
    [InlineData(1, 100.005, true)]   // más de dos decimales
    [InlineData(1, 100, false)]      // id de combo no positivo
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

    [Fact]
    public async Task UnaFraccionDeUnidadesNoLlegaAlServicio()
    {
        var (ctx, pancho, _, combo) = await PanchoYCocaAsync(nameof(UnaFraccionDeUnidadesNoLlegaAlServicio));

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

        Assert.Equal(HttpStatusCode.BadRequest, respuesta.StatusCode);
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
            Offline(pancho, 1m, 3000m, unidades: 1, ahorro: 272.73m, idCombo: combo),
            Offline(coca, 1m, 2500m, unidades: 1, ahorro: 227.27m, idCombo: combo)));
        var reenvio = await EmitirAsync(ctx, Venta(ctx, 1, 5000m, "TX",
            Offline(pancho, 1m, 3000m, unidades: 1, ahorro: 300m, idCombo: combo),
            Offline(coca, 1m, 2500m, unidades: 1, ahorro: 200m, idCombo: combo)));

        Assert.Equal(primera.Id, reenvio.Id);
        Assert.Equal([272.73m, 227.27m], reenvio.Items.Select(i => i.Descuento));
        Assert.Equal([272.73m, 227.27m], (await apoyo.ItemsAsync(ctx, primera.Id)).Select(i => i.Descuento));
    }
}
