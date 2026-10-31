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

    /// <summary>La instantánea offline todavía no representa una oferta "solo múltiplos": queda fuera de la curva de
    /// escalones (el dispositivo cobra lista), mientras que la misma oferta común sí viaja como escalón.</summary>
    [Fact]
    public async Task LaInstantaneaDejaFueraDeLosEscalonesALaOfertaSoloMultiplos()
    {
        var ctx = await apoyo.PrepararAsync(nameof(LaInstantaneaDejaFueraDeLosEscalonesALaOfertaSoloMultiplos), ModoPuntoVenta.Escritorio);
        var estricto = await apoyo.ArticuloAsync(ctx, "estricto", 4000m);
        var comun = await apoyo.ArticuloAsync(ctx, "comun", 4000m);
        await CrearOfertaAsync(ctx, CuerpoDeOferta(estricto, true));
        await CrearOfertaAsync(ctx, CuerpoDeOferta(comun, false, nombre: "Comun"));

        var respuesta = await ctx.Cajero!.GetAsync("/api/pos/instantanea");
        var cuerpo = await respuesta.Content.ReadAsStringAsync();
        Assert.True(respuesta.StatusCode == HttpStatusCode.OK, cuerpo);
        var instantanea = JsonSerializer.Deserialize<InstantaneaLegadaDePos>(cuerpo, OpcionesJson)!;

        var conEstricta = Assert.Single(instantanea.Articulos, a => a.IdArticulo == estricto);
        Assert.Null(conEstricta.Escalones);
        Assert.Equal(4000m, conEstricta.PrecioFinal);
        var conComun = Assert.Single(instantanea.Articulos, a => a.IdArticulo == comun);
        Assert.Equal(2m, Assert.Single(conComun.Escalones!).CantidadDesde);
    }
}
