using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.DependencyInjection;
using Ways.Application.Ventas;
using Ways.Domain.Articulos;
using Ways.Domain.Organizacion;
using Ways.Domain.Ventas;
using static Ways.IntegrationTests.CombosApoyo;

namespace Ways.IntegrationTests;

/// <summary>
/// "Disponible a la venta" apagado: un artículo de uso interno se compra y se stockea, pero no figura en la instantánea
/// del POS, no se escanea, no sale en la búsqueda de la caja y la venta lo rechaza con 409 <c>articulo_no_vendible</c>.
/// Cada test deja un artículo vendible al lado como control del mismo escenario, para que ninguna exclusión se deba a
/// otra causa.
/// </summary>
[Collection("Ways.IntegrationTests secuencial")]
public class ArticulosNoVendiblesTests(WaysApiFixture fixture) : IClassFixture<WaysApiFixture>
{
    private readonly CombosApoyo apoyo = new(fixture);

    private void VaciarCacheDeInstantaneas() =>
        ((MemoryCache)fixture.Services.GetRequiredService<IMemoryCache>()).Compact(1.0);

    private async Task<JsonElement> InstantaneaAsync(Escenario ctx, string ruta)
    {
        VaciarCacheDeInstantaneas();
        var respuesta = await ctx.Cajero!.GetAsync(ruta);
        var texto = await respuesta.Content.ReadAsStringAsync();
        Assert.True(respuesta.StatusCode == HttpStatusCode.OK, texto);

        return JsonDocument.Parse(texto).RootElement;
    }

    private static List<int> IdsDeArticulos(JsonElement instantanea, string propiedad = "idArticulo") =>
        [.. instantanea.GetProperty("articulos").EnumerateArray().Select(a => a.GetProperty(propiedad).GetInt32())];

    private static List<int> IdsDeCombos(JsonElement instantanea) =>
        instantanea.TryGetProperty("combos", out var combos)
            ? [.. combos.EnumerateArray().Select(c => c.GetProperty("idCombo").GetInt32())]
            : [];

    private async Task MarcarAsync(Escenario ctx, int idArticulo, string codigoInterno, string? codigoBarra = null)
    {
        await using var db = apoyo.Db(ctx.IdTenant);
        var articulo = await db.Articulos.SingleAsync(a => a.Id == idArticulo);
        articulo.CodigoInterno = codigoInterno;
        if (codigoBarra is not null)
        {
            var ahora = DateTimeOffset.UtcNow;
            db.CodigosBarra.Add(new CodigoBarra
            {
                IdTenant = ctx.IdTenant, IdArticulo = idArticulo, Codigo = codigoBarra, Activo = true,
                CreatedAt = ahora, UpdatedAt = ahora
            });
        }

        await db.SaveChangesAsync();
    }

    // ---- instantánea del POS ----------------------------------------------------------------------

    /// <summary>El artículo no vendible no viaja y arrastra a los combos que lo llevan como componente. Un combo con un
    /// componente familia viaja si la familia conserva algún miembro vendible, y no viaja si todos sus miembros son
    /// de uso interno. Mata el filtro de la instantánea; los combos no necesitan su propio filtro.</summary>
    [Fact]
    public async Task LaInstantaneaNoTraeLosArticulosNoVendiblesNiLosCombosQueLosNecesitan()
    {
        var ctx = await apoyo.PrepararAsync(nameof(LaInstantaneaNoTraeLosArticulosNoVendiblesNiLosCombosQueLosNecesitan), ModoPuntoVenta.Escritorio);
        var sano1 = await apoyo.ArticuloAsync(ctx, "sano1", 1000m);
        var sano2 = await apoyo.ArticuloAsync(ctx, "sano2", 1000m);
        var interno = await apoyo.ArticuloAsync(ctx, "interno", 1000m, disponibleVenta: false);
        var familiaInterna = await apoyo.FamiliaAsync(ctx, "SoloInternos");
        var familiaMixta = await apoyo.FamiliaAsync(ctx, "Mixta");
        await apoyo.ArticuloAsync(ctx, "miembro-interno", 1000m, idFamilia: familiaInterna, disponibleVenta: false);
        await apoyo.ArticuloAsync(ctx, "miembro-interno-de-mixta", 1000m, idFamilia: familiaMixta, disponibleVenta: false);
        var miembroSano = await apoyo.ArticuloAsync(ctx, "miembro-sano-de-mixta", 1000m, idFamilia: familiaMixta);

        var conInterno = await apoyo.ComboAsync(ctx, 1500m, [Art(sano1), Art(interno)], nombre: "Con un artículo interno");
        var sano = await apoyo.ComboAsync(ctx, 1500m, [Art(sano1), Art(sano2)], nombre: "Sano");
        var conFamiliaInterna = await apoyo.ComboAsync(ctx, 1500m, [Art(sano1), Fam(familiaInterna)], nombre: "Con familia interna");
        var conFamiliaMixta = await apoyo.ComboAsync(ctx, 1500m, [Art(sano1), Fam(familiaMixta)], nombre: "Con familia mixta");

        var instantanea = await InstantaneaAsync(ctx, "/api/pos/instantanea?version=2");

        var articulos = IdsDeArticulos(instantanea);
        Assert.Contains(sano1, articulos);
        Assert.Contains(sano2, articulos);
        Assert.Contains(miembroSano, articulos);
        Assert.DoesNotContain(interno, articulos);
        Assert.Equal(3, articulos.Count);
        Assert.Equal([sano, conFamiliaMixta], IdsDeCombos(instantanea));
        Assert.DoesNotContain(conInterno, IdsDeCombos(instantanea));
        Assert.DoesNotContain(conFamiliaInterna, IdsDeCombos(instantanea));
    }

    [Fact]
    public async Task ElFormatoOriginalDeLaInstantaneaTampocoTraeLosArticulosNoVendibles()
    {
        var ctx = await apoyo.PrepararAsync(nameof(ElFormatoOriginalDeLaInstantaneaTampocoTraeLosArticulosNoVendibles), ModoPuntoVenta.Escritorio);
        var sano = await apoyo.ArticuloAsync(ctx, "sano", 1000m);
        await apoyo.ArticuloAsync(ctx, "interno", 1000m, disponibleVenta: false);

        var original = await InstantaneaAsync(ctx, "/api/pos/instantanea");

        Assert.Equal([sano], IdsDeArticulos(original));
    }

    // ---- escaneo y búsqueda -----------------------------------------------------------------------

    /// <summary>Por código interno y por código de barras: el vendible resuelve y el no vendible da 404, que es lo
    /// mismo que un código que no existe (no se revela que el artículo existe).</summary>
    [Fact]
    public async Task ElEscaneoNoResuelveUnArticuloNoVendiblePorCodigoInternoNiPorCodigoDeBarras()
    {
        var ctx = await apoyo.PrepararAsync(nameof(ElEscaneoNoResuelveUnArticuloNoVendiblePorCodigoInternoNiPorCodigoDeBarras), ModoPuntoVenta.Web);
        var sano = await apoyo.ArticuloAsync(ctx, "sano", 1000m);
        var interno = await apoyo.ArticuloAsync(ctx, "interno", 1000m, disponibleVenta: false);
        await MarcarAsync(ctx, sano, "4821", "7791234500011");
        await MarcarAsync(ctx, interno, "4822", "7791234500028");

        foreach (var entrada in new[] { "4821", "7791234500011" })
        {
            var resuelto = await ctx.Admin.GetFromJsonAsync<ArticuloEscaneado>(
                $"/api/articulos/escaneo?entrada={entrada}", OpcionesJson);
            Assert.Equal(sano, resuelto!.IdArticulo);
        }

        foreach (var entrada in new[] { "4822", "7791234500028" })
        {
            var respuesta = await ctx.Admin.GetAsync($"/api/articulos/escaneo?entrada={entrada}");

            Assert.Equal(HttpStatusCode.NotFound, respuesta.StatusCode);
        }
    }

    /// <summary>Una devolución necesita el artículo de uso interno: con <c>incluirNoVendibles=true</c> el escaneo lo
    /// resuelve por código interno y por código de barras, y <c>=false</c> sigue siendo 404. Mata el <c>||</c> de las dos
    /// ramas de la consulta del escaneo.</summary>
    [Fact]
    public async Task ElEscaneoConIncluirNoVendiblesResuelveElArticuloDeUsoInterno()
    {
        var ctx = await apoyo.PrepararAsync(nameof(ElEscaneoConIncluirNoVendiblesResuelveElArticuloDeUsoInterno), ModoPuntoVenta.Web);
        var interno = await apoyo.ArticuloAsync(ctx, "interno", 1000m, disponibleVenta: false);
        await MarcarAsync(ctx, interno, "4832", "7791234500035");

        foreach (var entrada in new[] { "4832", "7791234500035" })
        {
            var con = await ctx.Admin.GetFromJsonAsync<ArticuloEscaneado>(
                $"/api/articulos/escaneo?entrada={entrada}&incluirNoVendibles=true", OpcionesJson);
            Assert.Equal(interno, con!.IdArticulo);

            var sin = await ctx.Admin.GetAsync($"/api/articulos/escaneo?entrada={entrada}&incluirNoVendibles=false");
            Assert.Equal(HttpStatusCode.NotFound, sin.StatusCode);
        }
    }

    /// <summary>El parámetro no abre más que la vendibilidad: un artículo inactivo sigue sin resolverse.</summary>
    [Fact]
    public async Task ElEscaneoConIncluirNoVendiblesSigueExigiendoQueElArticuloEsteActivo()
    {
        var ctx = await apoyo.PrepararAsync(nameof(ElEscaneoConIncluirNoVendiblesSigueExigiendoQueElArticuloEsteActivo), ModoPuntoVenta.Web);
        var inactivo = await apoyo.ArticuloAsync(ctx, "inactivo interno", 1000m, disponibleVenta: false);
        await MarcarAsync(ctx, inactivo, "4833");
        await using (var db = apoyo.Db(ctx.IdTenant))
        {
            (await db.Articulos.SingleAsync(a => a.Id == inactivo)).Activo = false;
            await db.SaveChangesAsync();
        }

        var respuesta = await ctx.Admin.GetAsync("/api/articulos/escaneo?entrada=4833&incluirNoVendibles=true");

        Assert.Equal(HttpStatusCode.NotFound, respuesta.StatusCode);
    }

    /// <summary>La búsqueda de la caja pide <c>soloVendibles=true</c> y no ve el artículo; sin el parámetro (compras,
    /// back-office) el listado los trae a todos. Mata las dos mitades: el filtro y que no sea el comportamiento por defecto.</summary>
    [Fact]
    public async Task LaBusquedaDeLaCajaOmiteLosNoVendiblesYElListadoSinElParametroLosTrae()
    {
        var ctx = await apoyo.PrepararAsync(nameof(LaBusquedaDeLaCajaOmiteLosNoVendiblesYElListadoSinElParametroLosTrae), ModoPuntoVenta.Web);
        var sano = await apoyo.ArticuloAsync(ctx, "detergente sano", 1000m);
        var interno = await apoyo.ArticuloAsync(ctx, "detergente interno", 1000m, disponibleVenta: false);

        async Task<List<int>> BuscarAsync(string consulta)
        {
            var pagina = await ctx.Admin.GetFromJsonAsync<JsonElement>($"/api/articulos?busqueda=detergente{consulta}", OpcionesJson);

            return [.. pagina.GetProperty("items").EnumerateArray().Select(a => a.GetProperty("id").GetInt32()).Order()];
        }

        Assert.Equal([sano], await BuscarAsync("&soloVendibles=true"));
        Assert.Equal([sano, interno], await BuscarAsync(string.Empty));
        Assert.Equal([sano, interno], await BuscarAsync("&soloVendibles=false"));
    }

    /// <summary>La búsqueda de una devolución manda <c>soloVendibles=true&amp;incluirNoVendibles=true</c> y ve también el
    /// artículo de uso interno; <c>incluirNoVendibles=false</c> no cambia el filtro. Mata la anulación en el endpoint.</summary>
    [Fact]
    public async Task LaBusquedaConIncluirNoVendiblesVeElArticuloDeUsoInternoAunConSoloVendibles()
    {
        var ctx = await apoyo.PrepararAsync(nameof(LaBusquedaConIncluirNoVendiblesVeElArticuloDeUsoInternoAunConSoloVendibles), ModoPuntoVenta.Web);
        var sano = await apoyo.ArticuloAsync(ctx, "lavandina sana", 1000m);
        var interno = await apoyo.ArticuloAsync(ctx, "lavandina interna", 1000m, disponibleVenta: false);

        async Task<List<int>> BuscarAsync(string consulta)
        {
            var pagina = await ctx.Admin.GetFromJsonAsync<JsonElement>($"/api/articulos?busqueda=lavandina{consulta}", OpcionesJson);

            return [.. pagina.GetProperty("items").EnumerateArray().Select(a => a.GetProperty("id").GetInt32()).Order()];
        }

        Assert.Equal([sano, interno], await BuscarAsync("&soloVendibles=true&incluirNoVendibles=true"));
        Assert.Equal([sano], await BuscarAsync("&soloVendibles=true&incluirNoVendibles=false"));
        Assert.Equal([sano], await BuscarAsync("&soloVendibles=true"));
    }

    /// <summary>Cargar un combo en el carrito: un componente artículo no vendible queda sin candidatos (el combo se
    /// muestra como no cargable) y los miembros de una familia que no se venden no se ofrecen.</summary>
    [Fact]
    public async Task ElComboCargableNoOfreceArticulosNoVendiblesComoCandidatos()
    {
        var ctx = await apoyo.PrepararAsync(nameof(ElComboCargableNoOfreceArticulosNoVendiblesComoCandidatos), ModoPuntoVenta.Web);
        var sano = await apoyo.ArticuloAsync(ctx, "sano", 1000m);
        var interno = await apoyo.ArticuloAsync(ctx, "interno", 1000m, disponibleVenta: false);
        var familia = await apoyo.FamiliaAsync(ctx, "Mixta");
        var miembroSano = await apoyo.ArticuloAsync(ctx, "miembro-sano", 1000m, idFamilia: familia);
        await apoyo.ArticuloAsync(ctx, "miembro-interno", 1000m, idFamilia: familia, disponibleVenta: false);
        var combo = await apoyo.ComboAsync(ctx, 2500m, [Art(sano), Art(interno), Fam(familia)]);

        var cargable = await ctx.Admin.GetFromJsonAsync<JsonElement>($"/api/combos/{combo}/cargable", OpcionesJson);

        var candidatos = cargable.GetProperty("componentes").EnumerateArray()
            .Select(c => c.GetProperty("candidatos").EnumerateArray().Select(x => x.GetProperty("idArticulo").GetInt32()).ToList())
            .ToList();
        Assert.Equal([sano], candidatos[0]);
        Assert.Empty(candidatos[1]);
        Assert.Equal([miembroSano], candidatos[2]);
    }

    // ---- la venta ---------------------------------------------------------------------------------

    private static SolicitudDeVenta VentaOnline(Escenario ctx, decimal total, params LineaDeVenta[] lineas) =>
        new(ctx.IdPuntoVenta, null, "TX", null, [.. lineas], [new PagoDeVenta(ctx.IdMedioEfectivo, total, null, 0m)], null, null);

    private async Task<int> ComprobantesAsync(Escenario ctx)
    {
        await using var db = apoyo.Db(ctx.IdTenant);

        return await db.ComprobantesVenta.CountAsync();
    }

    /// <summary>Una venta con una línea de un artículo no vendible se rechaza entera con 409, sin emitir ningún
    /// comprobante ni mover stock; la misma venta con el artículo vendible se emite (control).</summary>
    [Fact]
    public async Task EmitirUnaVentaConUnArticuloNoVendibleDa409YNoEmiteNada()
    {
        var ctx = await apoyo.PrepararAsync(nameof(EmitirUnaVentaConUnArticuloNoVendibleDa409YNoEmiteNada), ModoPuntoVenta.Web);
        var sano = await apoyo.ArticuloAsync(ctx, "sano", 100m);
        var interno = await apoyo.ArticuloAsync(ctx, "interno", 200m, disponibleVenta: false);

        var rechazada = await ctx.Admin.PostAsJsonAsync(
            "/api/ventas", VentaOnline(ctx, 300m, new LineaDeVenta(sano, 1m, null), new LineaDeVenta(interno, 1m, null)));

        Assert.Equal(HttpStatusCode.Conflict, rechazada.StatusCode);
        var problema = await rechazada.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("articulo_no_vendible", problema.GetProperty("codigo").GetString());
        Assert.Equal(0, await ComprobantesAsync(ctx));
        await using (var db = apoyo.Db(ctx.IdTenant))
        {
            Assert.Empty(await db.Stock.Where(s => s.IdArticulo == sano || s.IdArticulo == interno).ToListAsync());
        }

        var aceptada = await ctx.Admin.PostAsJsonAsync("/api/ventas", VentaOnline(ctx, 100m, new LineaDeVenta(sano, 1m, null)));

        Assert.Equal(HttpStatusCode.Created, aceptada.StatusCode);
        Assert.Equal(1, await ComprobantesAsync(ctx));
    }

    /// <summary>La regla es del artículo, no de la tienda: volver a prender "disponible a la venta" lo deja vender.</summary>
    [Fact]
    public async Task UnArticuloQueVuelveASerVendibleSeVende()
    {
        var ctx = await apoyo.PrepararAsync(nameof(UnArticuloQueVuelveASerVendibleSeVende), ModoPuntoVenta.Web);
        var articulo = await apoyo.ArticuloAsync(ctx, "intermitente", 100m, disponibleVenta: false);

        var antes = await ctx.Admin.PostAsJsonAsync("/api/ventas", VentaOnline(ctx, 100m, new LineaDeVenta(articulo, 1m, null)));
        Assert.Equal(HttpStatusCode.Conflict, antes.StatusCode);

        var cambio = await ctx.Admin.PutAsJsonAsync(
            $"/api/articulos/{articulo}/disponibilidad", new { disponibleVenta = true, disponibleOnline = true });
        Assert.Equal(HttpStatusCode.OK, cambio.StatusCode);

        var despues = await ctx.Admin.PostAsJsonAsync("/api/ventas", VentaOnline(ctx, 100m, new LineaDeVenta(articulo, 1m, null)));
        Assert.Equal(HttpStatusCode.Created, despues.StatusCode);
    }

    /// <summary>Un presupuesto y un remito comparten la regla (cada uno resuelve su propio documento de venta):
    /// crear el borrador con un artículo no vendible da 409.</summary>
    [Fact]
    public async Task UnPresupuestoYUnRemitoConUnArticuloNoVendibleDan409()
    {
        var ctx = await apoyo.PrepararAsync(nameof(UnPresupuestoYUnRemitoConUnArticuloNoVendibleDan409), ModoPuntoVenta.Web);
        var sano = await apoyo.ArticuloAsync(ctx, "sano", 100m);
        var interno = await apoyo.ArticuloAsync(ctx, "interno", 200m, disponibleVenta: false);

        var presupuesto = await ctx.Admin.PostAsJsonAsync(
            "/api/presupuestos", new SolicitudDePresupuesto(ctx.IdPuntoVenta, null, null, [new LineaDePresupuesto(interno, 1m)]));
        Assert.Equal(HttpStatusCode.Conflict, presupuesto.StatusCode);
        Assert.Equal("articulo_no_vendible", (await presupuesto.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("codigo").GetString());

        var remito = await ctx.Admin.PostAsJsonAsync(
            "/api/remitos", new SolicitudDeRemito(ctx.IdPuntoVenta, null, null, null, [new LineaDeRemito(interno, 1m, null)]));
        Assert.Equal(HttpStatusCode.Conflict, remito.StatusCode);
        Assert.Equal("articulo_no_vendible", (await remito.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("codigo").GetString());

        var presupuestoSano = await ctx.Admin.PostAsJsonAsync(
            "/api/presupuestos", new SolicitudDePresupuesto(ctx.IdPuntoVenta, null, null, [new LineaDePresupuesto(sano, 1m)]));
        Assert.Equal(HttpStatusCode.Created, presupuestoSano.StatusCode);
    }

    private async Task CambiarVendibleAsync(Escenario ctx, int idArticulo, bool vendible)
    {
        await using var db = apoyo.Db(ctx.IdTenant);
        (await db.Articulos.SingleAsync(a => a.Id == idArticulo)).DisponibleVenta = vendible;
        await db.SaveChangesAsync();
    }

    private static string? CodigoDeError(JsonElement problema) => problema.GetProperty("codigo").GetString();

    /// <summary>Un borrador anterior a la regla no la esquiva: el presupuesto enviado con un artículo que después pasó
    /// a uso interno no se convierte en venta (409 con el nombre del artículo, sin comprobante), y vuelve a convertirse
    /// cuando el artículo vuelve a venderse, que descarta cualquier otra causa del rechazo. Mata la excepción
    /// <c>presupuestoOrigen is null</c> de la regla en <c>EmitirAsync</c>.</summary>
    [Fact]
    public async Task ConvertirUnPresupuestoViejoConUnArticuloQueYaNoSeVendeDa409()
    {
        var ctx = await apoyo.PrepararAsync(nameof(ConvertirUnPresupuestoViejoConUnArticuloQueYaNoSeVendeDa409), ModoPuntoVenta.Web);
        var articulo = await apoyo.ArticuloAsync(ctx, "se volvera interno", 100m);
        var creado = await ctx.Admin.PostAsJsonAsync(
            "/api/presupuestos", new SolicitudDePresupuesto(ctx.IdPuntoVenta, null, null, [new LineaDePresupuesto(articulo, 1m)]));
        Assert.Equal(HttpStatusCode.Created, creado.StatusCode);
        var borrador = (await creado.Content.ReadFromJsonAsync<PresupuestoDetalle>(OpcionesJson))!;
        var enviado = await ctx.Admin.PostAsJsonAsync(
            $"/api/presupuestos/{borrador.Id}/enviar", new SolicitudDeEnvio(DateOnly.FromDateTime(DateTime.UtcNow.AddDays(30))));
        Assert.Equal(HttpStatusCode.OK, enviado.StatusCode);
        var presupuesto = (await enviado.Content.ReadFromJsonAsync<PresupuestoDetalle>(OpcionesJson))!;
        await CambiarVendibleAsync(ctx, articulo, vendible: false);

        SolicitudDeVenta Conversion() => new(
            ctx.IdPuntoVenta, null, "TX", null, null, [new PagoDeVenta(ctx.IdMedioEfectivo, presupuesto.Total, null, 0m)],
            null, null, IdPresupuestoOrigen: presupuesto.Id);

        var rechazada = await ctx.Admin.PostAsJsonAsync("/api/ventas", Conversion());

        Assert.Equal(HttpStatusCode.Conflict, rechazada.StatusCode);
        var problema = await rechazada.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("articulo_no_vendible", CodigoDeError(problema));
        Assert.Contains("se volvera interno", problema.GetProperty("title").GetString());
        Assert.Equal(0, await ComprobantesAsync(ctx));

        await CambiarVendibleAsync(ctx, articulo, vendible: true);
        var aceptada = await ctx.Admin.PostAsJsonAsync("/api/ventas", Conversion());

        Assert.Equal(HttpStatusCode.Created, aceptada.StatusCode);
    }

    private static async Task<RemitoDetalle> CrearRemitoAsync(Escenario ctx, int idArticulo)
    {
        var creado = await ctx.Admin.PostAsJsonAsync(
            "/api/remitos", new SolicitudDeRemito(ctx.IdPuntoVenta, null, null, null, [new LineaDeRemito(idArticulo, 1m, null)]));
        Assert.Equal(HttpStatusCode.Created, creado.StatusCode);

        return (await creado.Content.ReadFromJsonAsync<RemitoDetalle>(OpcionesJson))!;
    }

    /// <summary>Un remito en borrador anterior a la regla no sale: emitirlo con un artículo que pasó a uso interno da
    /// 409, no gasta número ni mueve stock y el remito sigue en borrador; con el artículo vendible otra vez emite. Mata
    /// la regla agregada en <c>ServicioDeRemitos.EmitirAsync</c>.</summary>
    [Fact]
    public async Task EmitirUnRemitoViejoConUnArticuloQueYaNoSeVendeDa409YNoMueveNada()
    {
        var ctx = await apoyo.PrepararAsync(nameof(EmitirUnRemitoViejoConUnArticuloQueYaNoSeVendeDa409YNoMueveNada), ModoPuntoVenta.Web);
        var articulo = await apoyo.ArticuloAsync(ctx, "remito interno", 100m);
        var borrador = await CrearRemitoAsync(ctx, articulo);
        await CambiarVendibleAsync(ctx, articulo, vendible: false);

        var rechazado = await ctx.Admin.PostAsync($"/api/remitos/{borrador.Id}/emitir", content: null);

        Assert.Equal(HttpStatusCode.Conflict, rechazado.StatusCode);
        Assert.Equal("articulo_no_vendible", CodigoDeError(await rechazado.Content.ReadFromJsonAsync<JsonElement>()));
        await using (var db = apoyo.Db(ctx.IdTenant))
        {
            var remito = await db.Remitos.SingleAsync(r => r.Id == borrador.Id);
            Assert.Equal((EstadoRemito.Borrador, null), (remito.Estado, remito.Numero));
            Assert.Empty(await db.Stock.Where(s => s.IdArticulo == articulo).ToListAsync());
        }

        await CambiarVendibleAsync(ctx, articulo, vendible: true);
        var emitido = await ctx.Admin.PostAsync($"/api/remitos/{borrador.Id}/emitir", content: null);

        Assert.Equal(HttpStatusCode.OK, emitido.StatusCode);
    }

    /// <summary>Facturar un remito ya emitido cuyo artículo pasó a uso interno da 409 y el remito sigue emitido y sin
    /// ligar; con el artículo vendible otra vez se factura. Mata la regla agregada en
    /// <c>ServicioDeFacturacionDeRemitos.FacturarAsync</c>.</summary>
    [Fact]
    public async Task FacturarUnRemitoConUnArticuloQueYaNoSeVendeDa409()
    {
        var ctx = await apoyo.PrepararAsync(nameof(FacturarUnRemitoConUnArticuloQueYaNoSeVendeDa409), ModoPuntoVenta.Web);
        var articulo = await apoyo.ArticuloAsync(ctx, "remito a facturar", 100m);
        var borrador = await CrearRemitoAsync(ctx, articulo);
        var emitido = await ctx.Admin.PostAsync($"/api/remitos/{borrador.Id}/emitir", content: null);
        Assert.Equal(HttpStatusCode.OK, emitido.StatusCode);
        var remito = (await emitido.Content.ReadFromJsonAsync<RemitoDetalle>(OpcionesJson))!;
        await CambiarVendibleAsync(ctx, articulo, vendible: false);

        SolicitudDeFacturacionDeRemitos Facturacion() => new(
            ctx.IdPuntoVenta, [remito.Id], [new PagoDeVenta(ctx.IdMedioEfectivo, remito.Total, null, 0m)], null);

        var rechazada = await ctx.Admin.PostAsJsonAsync("/api/remitos/facturacion", Facturacion());

        Assert.Equal(HttpStatusCode.Conflict, rechazada.StatusCode);
        Assert.Equal("articulo_no_vendible", CodigoDeError(await rechazada.Content.ReadFromJsonAsync<JsonElement>()));
        await using (var db = apoyo.Db(ctx.IdTenant))
        {
            var guardado = await db.Remitos.SingleAsync(r => r.Id == remito.Id);
            Assert.Equal((EstadoRemito.Emitido, null), (guardado.Estado, guardado.IdComprobanteVenta));
        }

        await CambiarVendibleAsync(ctx, articulo, vendible: true);
        var aceptada = await ctx.Admin.PostAsJsonAsync("/api/remitos/facturacion", Facturacion());

        Assert.Equal(HttpStatusCode.Created, aceptada.StatusCode);
    }

    /// <summary>Lo ya cobrado offline no se reescribe: la venta de un dispositivo con número preasignado se acepta aunque
    /// el artículo se haya marcado de uso interno después de que el dispositivo sincronizó. Es la excepción documentada
    /// de la regla, igual que la de las cantidades por unidad de venta.</summary>
    [Fact]
    public async Task LaVentaOfflineYaCobradaSeAceptaAunqueElArticuloHayaDejadoDeSerVendible()
    {
        var ctx = await apoyo.PrepararAsync(nameof(LaVentaOfflineYaCobradaSeAceptaAunqueElArticuloHayaDejadoDeSerVendible), ModoPuntoVenta.Escritorio);
        var articulo = await apoyo.ArticuloAsync(ctx, "se volvio interno", 100m, disponibleVenta: false);
        await ReservarBloqueAsync(ctx);

        var venta = new SolicitudDeVenta(
            ctx.IdPuntoVenta, null, "TX", null,
            [new LineaDeVenta(articulo, 1m, null, PrecioUnitario: 100m, DescuentoUnitario: 0m)],
            [new PagoDeVenta(ctx.IdMedioEfectivo, 100m, null, 0m)], null, null, NumeroPreasignado: 1);

        var respuesta = await ctx.Cajero!.PostAsJsonAsync("/api/ventas", venta);

        Assert.Equal(HttpStatusCode.Created, respuesta.StatusCode);
    }
}
