using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Ways.Application.Ofertas;
using Ways.Application.Pos;
using Ways.Application.Ventas;
using Ways.Domain.Organizacion;
using static Ways.IntegrationTests.CombosApoyo;

namespace Ways.IntegrationTests;

/// <summary>
/// Ofertas "solo múltiplos" de punta a punta contra Postgres real: el ABM guarda y devuelve el campo, el alta y la
/// edición rechazan el campo sin cantidad mínima, y la venta online, el remito, el presupuesto y la resolución cobran el
/// precio de oferta solo en los grupos completos de <c>cantidad_minima</c> unidades. Artículo a 4000 con "2 x 7500"
/// (precio unitario 3750, cantidad mínima 2): 1 → 4000, 2 → 7500, 3 → 11500, 4 → 15000.
/// </summary>
[Collection("Ways.IntegrationTests secuencial")]
public class OfertasSoloMultiplosTests(WaysApiFixture fixture) : IClassFixture<WaysApiFixture>
{
    private readonly CombosApoyo apoyo = new(fixture);

    private static object CuerpoDeOferta(
        int? idArticulo, bool? soloMultiplos, decimal? cantidadMinima = 2m, string nombre = "2 x 7500",
        int? idFamilia = null) =>
        soloMultiplos is { } valor
            ? new
            {
                nombre, idArticulo, idFamilia, precioUnitario = 3750m, cantidadMinima, soloMultiplos = valor,
                prioridad = 0, acumulable = false, activo = true, idsListas = Array.Empty<int>()
            }
            : new
            {
                nombre, idArticulo, idFamilia, precioUnitario = 3750m, cantidadMinima, prioridad = 0,
                acumulable = false, activo = true, idsListas = Array.Empty<int>()
            };

    private static async Task<int> CrearOfertaAsync(Escenario ctx, object cuerpo)
    {
        var respuesta = await ctx.Admin.PostAsJsonAsync("/api/ofertas", cuerpo);
        var texto = await respuesta.Content.ReadAsStringAsync();
        Assert.True(respuesta.StatusCode == HttpStatusCode.Created, texto);

        return JsonDocument.Parse(texto).RootElement.GetProperty("id").GetInt32();
    }

    private static async Task<string> CodigoAsync(HttpResponseMessage respuesta) =>
        (await respuesta.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("codigo").GetString()!;

    private static async Task<ComprobanteEmitido> VenderAsync(
        Escenario ctx, decimal total, params (int IdArticulo, decimal Cantidad)[] lineas)
    {
        var respuesta = await ctx.Admin.PostAsJsonAsync("/api/ventas", new SolicitudDeVenta(
            ctx.IdPuntoVenta, null, "TX", null,
            [.. lineas.Select(l => new LineaDeVenta(l.IdArticulo, l.Cantidad, null))],
            [new PagoDeVenta(ctx.IdMedioEfectivo, total, null, 0m)],
            null, null));
        var cuerpo = await respuesta.Content.ReadAsStringAsync();
        Assert.True(respuesta.StatusCode == HttpStatusCode.Created, cuerpo);

        return JsonSerializer.Deserialize<ComprobanteEmitido>(cuerpo, OpcionesJson)!;
    }

    private static async Task<JsonElement> ResolverAsync(
        Escenario ctx, string ruta, params (int IdArticulo, decimal Cantidad)[] lineas)
    {
        var solicitud = new SolicitudDeResolucion(
            [.. lineas.Select(l => new LineaDeResolucion(l.IdArticulo, ctx.IdEmpresa, ctx.IdListaPrecio, l.Cantidad))]);
        var respuesta = await ctx.Admin.PostAsJsonAsync(ruta, solicitud);
        var cuerpo = await respuesta.Content.ReadAsStringAsync();
        Assert.True(respuesta.StatusCode == HttpStatusCode.OK, cuerpo);

        return JsonDocument.Parse(cuerpo).RootElement;
    }

    // ---- ABM --------------------------------------------------------------------------------------

    [Fact]
    public async Task ElAltaGuardaSoloMultiplosYElListadoYElDetalleLoDevuelven()
    {
        var ctx = await apoyo.PrepararAsync(nameof(ElAltaGuardaSoloMultiplosYElListadoYElDetalleLoDevuelven), ModoPuntoVenta.Web);
        var articulo = await apoyo.ArticuloAsync(ctx, "articulo", 4000m);

        var estricta = await CrearOfertaAsync(ctx, CuerpoDeOferta(articulo, true, nombre: "Estricta"));
        var sinCampo = await CrearOfertaAsync(ctx, CuerpoDeOferta(articulo, null, nombre: "Sin campo"));

        await using (var db = apoyo.Db(ctx.IdTenant))
        {
            var guardadas = await db.Ofertas.ToDictionaryAsync(o => o.Id, o => o.SoloMultiplos);
            Assert.True(guardadas[estricta]);
            Assert.False(guardadas[sinCampo]);
        }

        var detalle = await ctx.Admin.GetFromJsonAsync<JsonElement>($"/api/ofertas/{estricta}");
        Assert.True(detalle.GetProperty("soloMultiplos").GetBoolean());
        var listado = (await ctx.Admin.GetFromJsonAsync<JsonElement>("/api/ofertas")).EnumerateArray()
            .ToDictionary(o => o.GetProperty("id").GetInt32(), o => o.GetProperty("soloMultiplos").GetBoolean());
        Assert.True(listado[estricta]);
        Assert.False(listado[sinCampo]);
    }

    [Fact]
    public async Task SoloMultiplosSinCantidadMinimaEs400EnElAltaYEnLaEdicion()
    {
        var ctx = await apoyo.PrepararAsync(nameof(SoloMultiplosSinCantidadMinimaEs400EnElAltaYEnLaEdicion), ModoPuntoVenta.Web);
        var articulo = await apoyo.ArticuloAsync(ctx, "articulo", 4000m);
        var existente = await CrearOfertaAsync(ctx, CuerpoDeOferta(articulo, false));

        var alta = await ctx.Admin.PostAsJsonAsync("/api/ofertas", CuerpoDeOferta(articulo, true, cantidadMinima: null));
        var edicion = await ctx.Admin.PutAsJsonAsync(
            $"/api/ofertas/{existente}", CuerpoDeOferta(articulo, true, cantidadMinima: null));

        Assert.Equal(HttpStatusCode.BadRequest, alta.StatusCode);
        Assert.Equal("solo_multiplos_sin_cantidad_minima", await CodigoAsync(alta));
        Assert.Equal(HttpStatusCode.BadRequest, edicion.StatusCode);
        Assert.Equal("solo_multiplos_sin_cantidad_minima", await CodigoAsync(edicion));
        await using var db = apoyo.Db(ctx.IdTenant);
        Assert.Equal(1, await db.Ofertas.CountAsync());
        Assert.False((await db.Ofertas.SingleAsync()).SoloMultiplos);
    }

    /// <summary>update-404-first: un id inexistente o dado de baja da 404 aunque el cuerpo sea el inválido de
    /// <c>solo_multiplos_sin_cantidad_minima</c>.</summary>
    [Fact]
    public async Task EditarUnaOfertaQueNoExisteConSoloMultiplosInvalidoDa404()
    {
        var ctx = await apoyo.PrepararAsync(nameof(EditarUnaOfertaQueNoExisteConSoloMultiplosInvalidoDa404), ModoPuntoVenta.Web);
        var articulo = await apoyo.ArticuloAsync(ctx, "articulo", 4000m);
        var eliminada = await CrearOfertaAsync(ctx, CuerpoDeOferta(articulo, true));
        Assert.Equal(HttpStatusCode.NoContent, (await ctx.Admin.DeleteAsync($"/api/ofertas/{eliminada}")).StatusCode);
        var invalido = CuerpoDeOferta(articulo, true, cantidadMinima: null);

        Assert.Equal(HttpStatusCode.NotFound, (await ctx.Admin.PutAsJsonAsync("/api/ofertas/999999", invalido)).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await ctx.Admin.PutAsJsonAsync($"/api/ofertas/{eliminada}", invalido)).StatusCode);
    }

    [Fact]
    public async Task LaEdicionSinElCampoConservaLoGuardadoYSinCantidadMinimaLaVuelveComun()
    {
        var ctx = await apoyo.PrepararAsync(nameof(LaEdicionSinElCampoConservaLoGuardadoYSinCantidadMinimaLaVuelveComun), ModoPuntoVenta.Web);
        var articulo = await apoyo.ArticuloAsync(ctx, "articulo", 4000m);
        var conserva = await CrearOfertaAsync(ctx, CuerpoDeOferta(articulo, true, nombre: "Conserva"));
        var apaga = await CrearOfertaAsync(ctx, CuerpoDeOferta(articulo, true, nombre: "Apaga"));
        var pierdeMinimo = await CrearOfertaAsync(ctx, CuerpoDeOferta(articulo, true, nombre: "Pierde minimo"));
        var prende = await CrearOfertaAsync(ctx, CuerpoDeOferta(articulo, false, nombre: "Prende"));

        var respuestas = new[]
        {
            await ctx.Admin.PutAsJsonAsync($"/api/ofertas/{conserva}", CuerpoDeOferta(articulo, null, nombre: "Conserva")),
            await ctx.Admin.PutAsJsonAsync($"/api/ofertas/{apaga}", CuerpoDeOferta(articulo, false, nombre: "Apaga")),
            await ctx.Admin.PutAsJsonAsync(
                $"/api/ofertas/{pierdeMinimo}", CuerpoDeOferta(articulo, null, cantidadMinima: null, nombre: "Pierde minimo")),
            await ctx.Admin.PutAsJsonAsync($"/api/ofertas/{prende}", CuerpoDeOferta(articulo, true, nombre: "Prende")),
        };

        Assert.All(respuestas, r => Assert.Equal(HttpStatusCode.OK, r.StatusCode));
        Assert.True((await respuestas[0].Content.ReadFromJsonAsync<JsonElement>()).GetProperty("soloMultiplos").GetBoolean());
        await using var db = apoyo.Db(ctx.IdTenant);
        var guardadas = await db.Ofertas.ToDictionaryAsync(o => o.Id, o => o.SoloMultiplos);
        Assert.True(guardadas[conserva]);
        Assert.False(guardadas[apaga]);
        Assert.False(guardadas[pierdeMinimo]);
        Assert.True(guardadas[prende]);
    }

    // ---- Cobro ------------------------------------------------------------------------------------

    [Fact]
    public async Task LaVentaCobraElPrecioDeOfertaSoloEnLosGruposCompletos()
    {
        var ctx = await apoyo.PrepararAsync(nameof(LaVentaCobraElPrecioDeOfertaSoloEnLosGruposCompletos), ModoPuntoVenta.Web);
        var estricto = await apoyo.ArticuloAsync(ctx, "estricto", 4000m);
        var comun = await apoyo.ArticuloAsync(ctx, "comun", 4000m);
        var idOferta = await CrearOfertaAsync(ctx, CuerpoDeOferta(estricto, true));
        await CrearOfertaAsync(ctx, CuerpoDeOferta(comun, false, nombre: "Comun"));

        foreach (var (cantidad, total) in new[] { (1m, 4000m), (2m, 7500m), (3m, 11500m), (4m, 15000m) })
        {
            var venta = await VenderAsync(ctx, total, (estricto, cantidad));

            Assert.Equal(total, venta.Total);
            var item = Assert.Single(await apoyo.ItemsAsync(ctx, venta.Id));
            Assert.Equal(total, item.Total);
            Assert.Equal(4000m, item.PrecioUnitario);
            Assert.Equal((cantidad * 4000m) - total, item.Descuento);
            Assert.Equal(cantidad >= 2m ? idOferta : null, item.IdOferta);
        }

        Assert.Equal(11250m, (await VenderAsync(ctx, 11250m, (comun, 3m))).Total);
    }

    /// <summary>Entre no acumulables de la misma prioridad gana la que más descuento de línea da: a 3 unidades la común de
    /// 3800 (600 contra 500), a 4 la "solo múltiplos" (1000 contra 800). La instantánea lleva los escalones de los dos
    /// lados del cruce.</summary>
    [Fact]
    public async Task ConUnaComunEnCompetenciaGanaLaQueMasDescuentoDeLineaDaEnLaVentaYEnLaInstantanea()
    {
        async Task<(Escenario Ctx, int Articulo, int IdEstricta, int IdComun)> PrepararAsync(ModoPuntoVenta modo)
        {
            var ctx = await apoyo.PrepararAsync(
                $"{nameof(ConUnaComunEnCompetenciaGanaLaQueMasDescuentoDeLineaDaEnLaVentaYEnLaInstantanea)}-{modo}", modo);
            var articulo = await apoyo.ArticuloAsync(ctx, "articulo", 4000m);
            var idEstricta = await CrearOfertaAsync(ctx, CuerpoDeOferta(articulo, true));
            var idComun = await CrearOfertaAsync(ctx, new
            {
                nombre = "Comun", idArticulo = articulo, precioUnitario = 3800m, prioridad = 0, acumulable = false,
                activo = true, idsListas = Array.Empty<int>()
            });
            return (ctx, articulo, idEstricta, idComun);
        }

        var web = await PrepararAsync(ModoPuntoVenta.Web);
        foreach (var (cantidad, total, idOferta) in new[] { (3m, 11400m, web.IdComun), (4m, 15000m, web.IdEstricta) })
        {
            var venta = await VenderAsync(web.Ctx, total, (web.Articulo, cantidad));
            var item = Assert.Single(await apoyo.ItemsAsync(web.Ctx, venta.Id));
            Assert.Equal((total, (int?)idOferta), (item.Total, item.IdOferta));
        }

        var escritorio = await PrepararAsync(ModoPuntoVenta.Escritorio);
        var v2 = JsonSerializer.Deserialize<InstantaneaDePos>(
            await InstantaneaAsync(escritorio.Ctx, "/api/pos/instantanea?version=2"), OpcionesJson)!;
        var precio = Assert.Single(Assert.Single(v2.Articulos, a => a.IdArticulo == escritorio.Articulo).PreciosPorLista);
        Assert.Equal(
            [(2m, 4000m, (int?)escritorio.IdEstricta), (2.501m, 3800m, null), (4m, 4000m, escritorio.IdEstricta)],
            precio.Escalones!.Take(3).Select(e => (e.CantidadDesde, e.PrecioFinal, e.AplicadasCompletas?.Single().IdOferta)));
    }

    [Fact]
    public async Task LaOfertaDeFamiliaCuentaLosGruposSobreElDocumentoYElRestoCaeEnLaUltimaLinea()
    {
        var ctx = await apoyo.PrepararAsync(nameof(LaOfertaDeFamiliaCuentaLosGruposSobreElDocumentoYElRestoCaeEnLaUltimaLinea), ModoPuntoVenta.Web);
        var familia = await apoyo.FamiliaAsync(ctx, "Gaseosas");
        var coca = await apoyo.ArticuloAsync(ctx, "coca", 4000m, idFamilia: familia);
        var sprite = await apoyo.ArticuloAsync(ctx, "sprite", 4000m, idFamilia: familia);
        await CrearOfertaAsync(ctx, CuerpoDeOferta(null, true, idFamilia: familia));

        // 1 + 2 = 3 unidades de la familia: un grupo (coca y una sprite), la otra sprite paga lista.
        var venta = await VenderAsync(ctx, 11500m, (coca, 1m), (sprite, 2m));

        Assert.Equal(11500m, venta.Total);
        Assert.Equal([3750m, 7750m], (await apoyo.ItemsAsync(ctx, venta.Id)).Select(i => i.Total));

        var resolucion = await ResolverAsync(ctx, "/api/ofertas/resolver", (coca, 1m), (sprite, 2m));
        Assert.Equal(
            [250m, 250m],
            resolucion.EnumerateArray().Select(l => l.GetProperty("descuentoDeLinea").GetDecimal()));
    }

    [Fact]
    public async Task LaResolucionInformaElDescuentoDeLineaSoloEnLasLineasConUnaOfertaSoloMultiplos()
    {
        var ctx = await apoyo.PrepararAsync(nameof(LaResolucionInformaElDescuentoDeLineaSoloEnLasLineasConUnaOfertaSoloMultiplos), ModoPuntoVenta.Web);
        var estricto = await apoyo.ArticuloAsync(ctx, "estricto", 4000m);
        var comun = await apoyo.ArticuloAsync(ctx, "comun", 4000m);
        await CrearOfertaAsync(ctx, CuerpoDeOferta(estricto, true));
        await CrearOfertaAsync(ctx, CuerpoDeOferta(comun, false, nombre: "Comun"));

        var lineas = (await ResolverAsync(ctx, "/api/ofertas/resolver", (estricto, 3m), (comun, 3m))).EnumerateArray().ToList();

        Assert.Equal(500m, lineas[0].GetProperty("descuentoDeLinea").GetDecimal());
        Assert.Equal(250m, lineas[0].GetProperty("descuentoUnitario").GetDecimal());
        var aplicada = Assert.Single(lineas[0].GetProperty("aplicadas").EnumerateArray());
        Assert.Equal(2m, aplicada.GetProperty("multiploDe").GetDecimal());
        Assert.False(lineas[1].TryGetProperty("descuentoDeLinea", out _));
        Assert.False(Assert.Single(lineas[1].GetProperty("aplicadas").EnumerateArray()).TryGetProperty("multiploDe", out _));
    }

    /// <summary>Un combo consume una de las 4 unidades: los grupos se cuentan sobre las 3 restantes (un grupo de 2), no
    /// sobre las 4. La vista previa con combos y la venta dan lo mismo.</summary>
    [Fact]
    public async Task ConUnComboLosGruposSeCuentanSobreLasUnidadesQueElComboNoConsumio()
    {
        var ctx = await apoyo.PrepararAsync(nameof(ConUnComboLosGruposSeCuentanSobreLasUnidadesQueElComboNoConsumio), ModoPuntoVenta.Web);
        var pancho = await apoyo.ArticuloAsync(ctx, "pancho", 4000m);
        var coca = await apoyo.ArticuloAsync(ctx, "coca", 1000m);
        await CrearOfertaAsync(ctx, CuerpoDeOferta(pancho, true));
        await apoyo.ComboAsync(ctx, 4000m, [Art(pancho), Art(coca)]);

        var vista = await ResolverAsync(ctx, "/api/ofertas/resolver-con-combos", (pancho, 4m), (coca, 1m));
        var lineaPancho = vista.GetProperty("lineas")[0];
        Assert.Equal(1m, lineaPancho.GetProperty("combo").GetProperty("unidadesConsumidas").GetDecimal());
        Assert.Equal(500m, lineaPancho.GetProperty("descuentoDeLinea").GetDecimal());

        // Pancho: (3 × 4000 − 500) + 3200 de su parte del combo; coca: 800 de su parte.
        var venta = await VenderAsync(ctx, 15500m, (pancho, 4m), (coca, 1m));
        Assert.Equal([14700m, 800m], (await apoyo.ItemsAsync(ctx, venta.Id)).Select(i => i.Total));
    }

    [Fact]
    public async Task ElRemitoYElPresupuestoCobranSoloLosGruposCompletos()
    {
        var ctx = await apoyo.PrepararAsync(nameof(ElRemitoYElPresupuestoCobranSoloLosGruposCompletos), ModoPuntoVenta.Web);
        var estricto = await apoyo.ArticuloAsync(ctx, "estricto", 4000m);
        await CrearOfertaAsync(ctx, CuerpoDeOferta(estricto, true));

        var remito = await ctx.Admin.PostAsJsonAsync(
            "/api/remitos", new SolicitudDeRemito(ctx.IdPuntoVenta, null, null, null, [new LineaDeRemito(estricto, 3m, null)]));
        var presupuesto = await ctx.Admin.PostAsJsonAsync(
            "/api/presupuestos", new SolicitudDePresupuesto(ctx.IdPuntoVenta, null, null, [new LineaDePresupuesto(estricto, 3m)]));

        var textoRemito = await remito.Content.ReadAsStringAsync();
        var textoPresupuesto = await presupuesto.Content.ReadAsStringAsync();
        Assert.True(remito.StatusCode == HttpStatusCode.Created, textoRemito);
        Assert.True(presupuesto.StatusCode == HttpStatusCode.Created, textoPresupuesto);
        Assert.Equal(11500m, JsonSerializer.Deserialize<RemitoDetalle>(textoRemito, OpcionesJson)!.Total);
        Assert.Equal(11500m, JsonSerializer.Deserialize<PresupuestoDetalle>(textoPresupuesto, OpcionesJson)!.Total);
    }

    // ---- Instantánea y venta offline ---------------------------------------------------------------

    private static async Task<string> InstantaneaAsync(Escenario ctx, string ruta)
    {
        var respuesta = await ctx.Cajero!.GetAsync(ruta);
        var cuerpo = await respuesta.Content.ReadAsStringAsync();
        Assert.True(respuesta.StatusCode == HttpStatusCode.OK, cuerpo);

        return cuerpo;
    }

    /// <summary>El escalón de la oferta "solo múltiplos" viaja con la resolución entera en <c>aplicadasCompletas</c> y
    /// sus campos de siempre a precio de lista, en los dos formatos: un POS anterior cobra lista en esas unidades, nunca
    /// menos que el servidor. La oferta común no cambia.</summary>
    [Fact]
    public async Task LaInstantaneaLlevaLaOfertaSoloMultiplosAparteYLosCamposDeSiempreAPrecioDeLista()
    {
        var ctx = await apoyo.PrepararAsync(nameof(LaInstantaneaLlevaLaOfertaSoloMultiplosAparteYLosCamposDeSiempreAPrecioDeLista), ModoPuntoVenta.Escritorio);
        var estricto = await apoyo.ArticuloAsync(ctx, "estricto", 4000m);
        var comun = await apoyo.ArticuloAsync(ctx, "comun", 4000m);
        var idEstricta = await CrearOfertaAsync(ctx, CuerpoDeOferta(estricto, true));
        await CrearOfertaAsync(ctx, CuerpoDeOferta(comun, false, nombre: "Comun"));

        var v2 = JsonSerializer.Deserialize<InstantaneaDePos>(await InstantaneaAsync(ctx, "/api/pos/instantanea?version=2"), OpcionesJson)!;
        var precio = Assert.Single(Assert.Single(v2.Articulos, a => a.IdArticulo == estricto).PreciosPorLista);
        var escalon = Assert.Single(precio.Escalones!);
        Assert.Equal((2m, 4000m, 0m), (escalon.CantidadDesde, escalon.PrecioFinal, escalon.DescuentoUnitario));
        Assert.Empty(escalon.Aplicadas);
        var completa = Assert.Single(escalon.AplicadasCompletas!);
        Assert.Equal((idEstricta, 250m, 2m), (completa.IdOferta, completa.DescuentoUnitario, completa.MultiploDe));
        Assert.Null(precio.AplicadasCompletas);
        var precioComun = Assert.Single(Assert.Single(v2.Articulos, a => a.IdArticulo == comun).PreciosPorLista);
        Assert.Null(Assert.Single(precioComun.Escalones!).AplicadasCompletas);

        var v1 = JsonSerializer.Deserialize<InstantaneaLegadaDePos>(await InstantaneaAsync(ctx, "/api/pos/instantanea"), OpcionesJson)!;
        var legado = Assert.Single(Assert.Single(v1.Articulos, a => a.IdArticulo == estricto).Escalones!);
        Assert.Equal((4000m, 0m), (legado.PrecioFinal, legado.DescuentoUnitario));
        Assert.Equal(3750m, Assert.Single(Assert.Single(v1.Articulos, a => a.IdArticulo == comun).Escalones!).PrecioFinal);
    }

    private static LineaDeVenta Offline(
        int idArticulo, decimal cantidad, decimal precio, decimal? descuentoDeLinea = null, decimal? descuentoUnitario = null,
        decimal? unidades = null, decimal? ahorro = null, int? idCombo = null) =>
        new(idArticulo, cantidad, null, PrecioUnitario: precio, DescuentoUnitario: descuentoUnitario,
            UnidadesConsumidasPorCombo: unidades, AhorroDeCombo: ahorro, IdCombo: idCombo,
            DescuentoDeOfertaDeLinea: descuentoDeLinea);

    private static SolicitudDeVenta VentaOffline(Escenario ctx, long numero, decimal total, params LineaDeVenta[] lineas) =>
        new(ctx.IdPuntoVenta, null, "TX", null, [.. lineas],
            [new PagoDeVenta(ctx.IdMedioEfectivo, total, null, 0m)], null, null, NumeroPreasignado: numero);

    private static async Task<ComprobanteEmitido> EmitirOfflineAsync(Escenario ctx, SolicitudDeVenta solicitud)
    {
        var respuesta = await ctx.Cajero!.PostAsJsonAsync("/api/ventas", solicitud);
        var cuerpo = await respuesta.Content.ReadAsStringAsync();
        Assert.True(respuesta.StatusCode == HttpStatusCode.Created, cuerpo);

        return JsonSerializer.Deserialize<ComprobanteEmitido>(cuerpo, OpcionesJson)!;
    }

    private async Task<List<JsonElement>> DiscrepanciasAsync(Escenario ctx)
    {
        await using var db = apoyo.Db(ctx.IdTenant);
        var filas = await db.Auditoria
            .Where(a => a.IdTenant == ctx.IdTenant && a.Accion == "venta.discrepancia")
            .OrderBy(a => a.Id)
            .ToListAsync();

        return [.. filas.Select(f => JsonDocument.Parse(f.ValorNuevo).RootElement)];
    }

    /// <summary>Cláusula: la discrepancia de una línea offline compara contra el descuento de LÍNEA que el servidor
    /// resuelve para la oferta "solo múltiplos". El dispositivo que cobra 1 → 4000, 2 → 7500, 3 → 11500 y 4 → 15000 con
    /// <c>descuentoDeOfertaDeLinea</c> no se audita; el que cobró la oferta en las 3 unidades (11250) y el POS anterior que
    /// cobró lista (12000) sí, y en los dos casos se graba lo cobrado.</summary>
    [Fact]
    public async Task UnaVentaOfflineSoloMultiplosBienCobradaNoSeAuditaYUnaMalCobradaSi()
    {
        var ctx = await apoyo.PrepararAsync(nameof(UnaVentaOfflineSoloMultiplosBienCobradaNoSeAuditaYUnaMalCobradaSi), ModoPuntoVenta.Escritorio);
        await ReservarBloqueAsync(ctx);
        var estricto = await apoyo.ArticuloAsync(ctx, "estricto", 4000m);
        await CrearOfertaAsync(ctx, CuerpoDeOferta(estricto, true));

        var numero = 1L;
        foreach (var (cantidad, descuento, total) in new[] { (1m, 0m, 4000m), (2m, 500m, 7500m), (3m, 500m, 11500m), (4m, 1000m, 15000m) })
        {
            var emitido = await EmitirOfflineAsync(ctx, VentaOffline(ctx, numero++, total, Offline(estricto, cantidad, 4000m, descuento)));

            Assert.Equal(total, emitido.Total);
            Assert.False(Assert.Single(emitido.Items).PrecioDiscrepante);
        }

        Assert.Empty(await DiscrepanciasAsync(ctx));

        var todasConOferta = await EmitirOfflineAsync(ctx, VentaOffline(ctx, numero++, 11250m, Offline(estricto, 3m, 4000m, descuentoUnitario: 250m)));
        var aLista = await EmitirOfflineAsync(ctx, VentaOffline(ctx, numero, 12000m, Offline(estricto, 3m, 4000m, descuentoUnitario: 0m)));

        Assert.Equal(11250m, todasConOferta.Total);
        Assert.True(Assert.Single(todasConOferta.Items).PrecioDiscrepante);
        Assert.Equal(12000m, aLista.Total);
        Assert.True(Assert.Single(aLista.Items).PrecioDiscrepante);
        Assert.Equal(2, (await DiscrepanciasAsync(ctx)).Count);
    }

    /// <summary>Familia y combos offline, como los cobra el servidor online: los grupos de familia se cuentan sobre el
    /// documento con el resto en la última línea, y la unidad que consumió un combo no cuenta para los grupos.</summary>
    [Fact]
    public async Task LaVentaOfflineDeFamiliaYConComboCobraComoElServidorYNoSeAudita()
    {
        var ctx = await apoyo.PrepararAsync(nameof(LaVentaOfflineDeFamiliaYConComboCobraComoElServidorYNoSeAudita), ModoPuntoVenta.Escritorio);
        await ReservarBloqueAsync(ctx);
        var familia = await apoyo.FamiliaAsync(ctx, "Gaseosas");
        var coca = await apoyo.ArticuloAsync(ctx, "coca", 4000m, idFamilia: familia);
        var sprite = await apoyo.ArticuloAsync(ctx, "sprite", 4000m, idFamilia: familia);
        await CrearOfertaAsync(ctx, CuerpoDeOferta(null, true, idFamilia: familia));
        var pancho = await apoyo.ArticuloAsync(ctx, "pancho", 4000m);
        var agua = await apoyo.ArticuloAsync(ctx, "agua", 1000m);
        await CrearOfertaAsync(ctx, CuerpoDeOferta(pancho, true, nombre: "Pancho 2 x 7500"));
        var combo = await apoyo.ComboAsync(ctx, 4000m, [Art(pancho), Art(agua)]);

        var deFamilia = await EmitirOfflineAsync(ctx, VentaOffline(ctx, 1, 11500m,
            Offline(coca, 1m, 4000m, 250m), Offline(sprite, 2m, 4000m, 250m)));
        var conCombo = await EmitirOfflineAsync(ctx, VentaOffline(ctx, 2, 15500m,
            Offline(pancho, 4m, 4000m, 500m, unidades: 1m, ahorro: 800m, idCombo: combo),
            Offline(agua, 1m, 1000m, unidades: 1m, ahorro: 200m, idCombo: combo)));

        Assert.Equal([3750m, 7750m], deFamilia.Items.Select(i => i.Total));
        Assert.Equal([14700m, 800m], conCombo.Items.Select(i => i.Total));
        Assert.All(deFamilia.Items.Concat(conCombo.Items), i => Assert.False(i.PrecioDiscrepante));
        Assert.Empty(await DiscrepanciasAsync(ctx));
    }

    [Fact]
    public async Task ElDescuentoDeOfertaDeLineaOfflineFueraDeSuContratoEs400()
    {
        var ctx = await apoyo.PrepararAsync(nameof(ElDescuentoDeOfertaDeLineaOfflineFueraDeSuContratoEs400), ModoPuntoVenta.Escritorio);
        await ReservarBloqueAsync(ctx);
        var estricto = await apoyo.ArticuloAsync(ctx, "estricto", 4000m);

        async Task<string> RechazoAsync(SolicitudDeVenta solicitud)
        {
            var respuesta = await ctx.Cajero!.PostAsJsonAsync("/api/ventas", solicitud);
            Assert.Equal(HttpStatusCode.BadRequest, respuesta.StatusCode);
            return await CodigoAsync(respuesta);
        }

        Assert.Equal("precio_offline_incompleto", await RechazoAsync(VentaOffline(ctx, 1, 11500m,
            new LineaDeVenta(estricto, 3m, null, DescuentoDeOfertaDeLinea: 500m))));
        Assert.Equal("precio_offline_invalido", await RechazoAsync(VentaOffline(ctx, 1, 11500m,
            Offline(estricto, 3m, 4000m, 500m, descuentoUnitario: 0m))));
        Assert.Equal("precio_offline_invalido", await RechazoAsync(VentaOffline(ctx, 1, 0m,
            Offline(estricto, 3m, 4000m, 12000.01m))));
        Assert.Equal("precio_offline_invalido", await RechazoAsync(VentaOffline(ctx, 1, 12000m,
            Offline(estricto, 3m, 4000m, -1m))));
        await using var db = apoyo.Db(ctx.IdTenant);
        Assert.Equal(0, await db.ComprobantesVenta.CountAsync());
    }
}
