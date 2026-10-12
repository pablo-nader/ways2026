using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Ways.Domain.Organizacion;
using static Ways.IntegrationTests.CombosApoyo;

namespace Ways.IntegrationTests;

/// <summary>
/// "Disponible online" de ofertas y combos se escribe por su POST y su PUT de siempre (un solo destino por campo): el
/// alta lo toma como <c>true</c> si falta, la edición lo deja como estaba si falta (un cliente anterior al campo no saca
/// nada de la tienda), y el listado y el detalle lo devuelven. Cada test siembra dos filas con valores distintos para que un cruce
/// entre ellas o un <c>Where</c> perdido no pasen.
/// </summary>
[Collection("Ways.IntegrationTests secuencial")]
public class OfertasYCombosDisponibleOnlineTests(WaysApiFixture fixture) : IClassFixture<WaysApiFixture>
{
    private readonly CombosApoyo apoyo = new(fixture);

    private static StringContent Json(object cuerpo) =>
        new(JsonSerializer.Serialize(cuerpo), System.Text.Encoding.UTF8, "application/json");

    private static StringContent Json(string cuerpo) => new(cuerpo, System.Text.Encoding.UTF8, "application/json");

    private static async Task<string> CodigoAsync(HttpResponseMessage respuesta) =>
        (await respuesta.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("codigo").GetString()!;

    // ---- ofertas ----------------------------------------------------------------------------------

    private static object CuerpoDeOferta(int idArticulo, string nombre, bool? disponibleOnline, decimal porcentaje = 10m) =>
        disponibleOnline is { } valor
            ? new { nombre, idArticulo, porcentaje, prioridad = 0, acumulable = false, activo = true, disponibleOnline = valor, idsListas = Array.Empty<int>() }
            : new { nombre, idArticulo, porcentaje, prioridad = 0, acumulable = false, activo = true, idsListas = Array.Empty<int>() };

    [Fact]
    public async Task LaOfertaNaceDisponibleOnlineSiElAltaNoDiceNadaYGuardaLoQueSeLePide()
    {
        var ctx = await apoyo.PrepararAsync(nameof(LaOfertaNaceDisponibleOnlineSiElAltaNoDiceNadaYGuardaLoQueSeLePide), ModoPuntoVenta.Web);
        var articulo = await apoyo.ArticuloAsync(ctx, "articulo", 100m);

        var sinCampo = await ctx.Admin.PostAsJsonAsync("/api/ofertas", CuerpoDeOferta(articulo, "Sin campo", null));
        var apagada = await ctx.Admin.PostAsJsonAsync("/api/ofertas", CuerpoDeOferta(articulo, "Apagada", false));
        var prendida = await ctx.Admin.PostAsJsonAsync("/api/ofertas", CuerpoDeOferta(articulo, "Prendida", true));

        Assert.Equal(HttpStatusCode.Created, sinCampo.StatusCode);
        Assert.Equal(HttpStatusCode.Created, apagada.StatusCode);
        Assert.Equal(HttpStatusCode.Created, prendida.StatusCode);
        var idSinCampo = (await sinCampo.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetInt32();
        var jsonApagada = await apagada.Content.ReadFromJsonAsync<JsonElement>();
        var idApagada = jsonApagada.GetProperty("id").GetInt32();
        Assert.False(jsonApagada.GetProperty("disponibleOnline").GetBoolean());

        await using var db = apoyo.Db(ctx.IdTenant);
        var guardadas = await db.Ofertas.ToDictionaryAsync(o => o.Nombre, o => o.DisponibleOnline);
        Assert.True(guardadas["Sin campo"]);
        Assert.False(guardadas["Apagada"]);
        Assert.True(guardadas["Prendida"]);

        // El detalle y el listado leen lo guardado, con valores distintos por oferta.
        var detalle = await ctx.Admin.GetFromJsonAsync<JsonElement>($"/api/ofertas/{idApagada}");
        Assert.False(detalle.GetProperty("disponibleOnline").GetBoolean());
        var listado = (await ctx.Admin.GetFromJsonAsync<JsonElement>("/api/ofertas")).EnumerateArray()
            .ToDictionary(o => o.GetProperty("id").GetInt32(), o => o.GetProperty("disponibleOnline").GetBoolean());
        Assert.False(listado[idApagada]);
        Assert.True(listado[idSinCampo]);
    }

    [Fact]
    public async Task EditarUnaOfertaCambiaSuDisponibilidadOnlineSinTocarLaDeLaVecina()
    {
        var ctx = await apoyo.PrepararAsync(nameof(EditarUnaOfertaCambiaSuDisponibilidadOnlineSinTocarLaDeLaVecina), ModoPuntoVenta.Web);
        var articulo = await apoyo.ArticuloAsync(ctx, "articulo", 100m);
        var editada = (await (await ctx.Admin.PostAsJsonAsync("/api/ofertas", CuerpoDeOferta(articulo, "Editada", true))).Content
            .ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetInt32();
        var vecina = (await (await ctx.Admin.PostAsJsonAsync("/api/ofertas", CuerpoDeOferta(articulo, "Vecina", true))).Content
            .ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetInt32();

        var apagar = await ctx.Admin.PutAsJsonAsync($"/api/ofertas/{editada}", CuerpoDeOferta(articulo, "Editada", false, porcentaje: 20m));

        Assert.Equal(HttpStatusCode.OK, apagar.StatusCode);
        Assert.False((await apagar.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("disponibleOnline").GetBoolean());
        await using (var db = apoyo.Db(ctx.IdTenant))
        {
            Assert.False((await db.Ofertas.SingleAsync(o => o.Id == editada)).DisponibleOnline);
            Assert.True((await db.Ofertas.SingleAsync(o => o.Id == vecina)).DisponibleOnline);
        }

        var prender = await ctx.Admin.PutAsJsonAsync($"/api/ofertas/{editada}", CuerpoDeOferta(articulo, "Editada", true, porcentaje: 20m));

        Assert.Equal(HttpStatusCode.OK, prender.StatusCode);
        await using var despues = apoyo.Db(ctx.IdTenant);
        Assert.True((await despues.Ofertas.SingleAsync(o => o.Id == editada)).DisponibleOnline);
    }

    /// <summary>El PUT sin <c>disponibleOnline</c> conserva lo guardado, sea <c>true</c> o <c>false</c>, y aplica el resto
    /// del cuerpo; un id que no existe o dado de baja da 404.</summary>
    [Fact]
    public async Task EditarUnaOfertaSinDisponibleOnlineConservaLoGuardadoYUnaQueNoExisteDa404()
    {
        var ctx = await apoyo.PrepararAsync(nameof(EditarUnaOfertaSinDisponibleOnlineConservaLoGuardadoYUnaQueNoExisteDa404), ModoPuntoVenta.Web);
        var articulo = await apoyo.ArticuloAsync(ctx, "articulo", 100m);
        var apagada = (await (await ctx.Admin.PostAsJsonAsync("/api/ofertas", CuerpoDeOferta(articulo, "Apagada", false))).Content
            .ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetInt32();
        var prendida = (await (await ctx.Admin.PostAsJsonAsync("/api/ofertas", CuerpoDeOferta(articulo, "Prendida", true))).Content
            .ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetInt32();
        var eliminada = (await (await ctx.Admin.PostAsJsonAsync("/api/ofertas", CuerpoDeOferta(articulo, "Se elimina", true))).Content
            .ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetInt32();
        Assert.Equal(HttpStatusCode.NoContent, (await ctx.Admin.DeleteAsync($"/api/ofertas/{eliminada}")).StatusCode);

        var sobreLaApagada = await ctx.Admin.PutAsJsonAsync($"/api/ofertas/{apagada}", CuerpoDeOferta(articulo, "Apagada editada", null, porcentaje: 50m));
        var sobreLaPrendida = await ctx.Admin.PutAsJsonAsync($"/api/ofertas/{prendida}", CuerpoDeOferta(articulo, "Prendida editada", null, porcentaje: 50m));

        Assert.Equal(HttpStatusCode.OK, sobreLaApagada.StatusCode);
        Assert.Equal(HttpStatusCode.OK, sobreLaPrendida.StatusCode);
        Assert.False((await sobreLaApagada.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("disponibleOnline").GetBoolean());
        Assert.True((await sobreLaPrendida.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("disponibleOnline").GetBoolean());
        await using (var db = apoyo.Db(ctx.IdTenant))
        {
            var guardadas = await db.Ofertas.ToDictionaryAsync(o => o.Id);
            Assert.Equal(("Apagada editada", false, 50m), (guardadas[apagada].Nombre, guardadas[apagada].DisponibleOnline, guardadas[apagada].Porcentaje));
            Assert.Equal(("Prendida editada", true, 50m), (guardadas[prendida].Nombre, guardadas[prendida].DisponibleOnline, guardadas[prendida].Porcentaje));
        }

        foreach (var inexistente in new[] { 987_654_321, eliminada })
        {
            var respuesta = await ctx.Admin.PutAsJsonAsync($"/api/ofertas/{inexistente}", CuerpoDeOferta(articulo, "X", null));

            Assert.Equal(HttpStatusCode.NotFound, respuesta.StatusCode);
        }
    }

    // ---- combos -----------------------------------------------------------------------------------

    private static object CuerpoDeCombo(int a, int b, string nombre, bool? disponibleOnline, bool activo = true) =>
        disponibleOnline is { } valor
            ? new { nombre, precio = 150m, componentes = new object[] { new { idArticulo = a, cantidad = 1 }, new { idArticulo = b, cantidad = 1 } }, activo, disponibleOnline = valor }
            : new { nombre, precio = 150m, componentes = new object[] { new { idArticulo = a, cantidad = 1 }, new { idArticulo = b, cantidad = 1 } }, activo };

    [Fact]
    public async Task ElComboNaceDisponibleOnlineSiElAltaNoDiceNadaYGuardaLoQueSeLePide()
    {
        var ctx = await apoyo.PrepararAsync(nameof(ElComboNaceDisponibleOnlineSiElAltaNoDiceNadaYGuardaLoQueSeLePide), ModoPuntoVenta.Web);
        var a = await apoyo.ArticuloAsync(ctx, "a", 100m);
        var b = await apoyo.ArticuloAsync(ctx, "b", 100m);

        var sinCampo = await ctx.Admin.PostAsJsonAsync("/api/combos", CuerpoDeCombo(a, b, "Sin campo", null));
        var apagado = await ctx.Admin.PostAsJsonAsync("/api/combos", CuerpoDeCombo(a, b, "Apagado", false));

        Assert.Equal(HttpStatusCode.Created, sinCampo.StatusCode);
        Assert.Equal(HttpStatusCode.Created, apagado.StatusCode);
        var jsonApagado = await apagado.Content.ReadFromJsonAsync<JsonElement>();
        Assert.False(jsonApagado.GetProperty("disponibleOnline").GetBoolean());
        Assert.True((await sinCampo.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("disponibleOnline").GetBoolean());

        await using var db = apoyo.Db(ctx.IdTenant);
        var guardados = await db.Combos.ToDictionaryAsync(c => c.Nombre, c => c.DisponibleOnline);
        Assert.True(guardados["Sin campo"]);
        Assert.False(guardados["Apagado"]);

        var listado = (await ctx.Admin.GetFromJsonAsync<JsonElement>("/api/combos")).EnumerateArray()
            .ToDictionary(c => c.GetProperty("nombre").GetString()!, c => c.GetProperty("disponibleOnline").GetBoolean());
        Assert.False(listado["Apagado"]);
        Assert.True(listado["Sin campo"]);
        var detalle = await ctx.Admin.GetFromJsonAsync<JsonElement>($"/api/combos/{jsonApagado.GetProperty("id").GetInt32()}");
        Assert.False(detalle.GetProperty("disponibleOnline").GetBoolean());
    }

    [Fact]
    public async Task EditarUnComboCambiaSuDisponibilidadOnlineSinTocarLaDelVecino()
    {
        var ctx = await apoyo.PrepararAsync(nameof(EditarUnComboCambiaSuDisponibilidadOnlineSinTocarLaDelVecino), ModoPuntoVenta.Web);
        var a = await apoyo.ArticuloAsync(ctx, "a", 100m);
        var b = await apoyo.ArticuloAsync(ctx, "b", 100m);
        var editado = (await (await ctx.Admin.PostAsJsonAsync("/api/combos", CuerpoDeCombo(a, b, "Editado", true))).Content
            .ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetInt32();
        var vecino = (await (await ctx.Admin.PostAsJsonAsync("/api/combos", CuerpoDeCombo(a, b, "Vecino", true))).Content
            .ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetInt32();

        var apagar = await ctx.Admin.PutAsJsonAsync($"/api/combos/{editado}", CuerpoDeCombo(a, b, "Editado", false));

        Assert.Equal(HttpStatusCode.OK, apagar.StatusCode);
        Assert.False((await apagar.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("disponibleOnline").GetBoolean());
        await using var db = apoyo.Db(ctx.IdTenant);
        Assert.False((await db.Combos.SingleAsync(c => c.Id == editado)).DisponibleOnline);
        Assert.True((await db.Combos.SingleAsync(c => c.Id == vecino)).DisponibleOnline);
    }

    /// <summary>El PUT sin <c>disponibleOnline</c> conserva lo guardado, sea <c>true</c> o <c>false</c>, y aplica el resto
    /// del cuerpo; un id que no existe o dado de baja da 404.</summary>
    [Fact]
    public async Task EditarUnComboSinDisponibleOnlineConservaLoGuardadoYUnoQueNoExisteDa404()
    {
        var ctx = await apoyo.PrepararAsync(nameof(EditarUnComboSinDisponibleOnlineConservaLoGuardadoYUnoQueNoExisteDa404), ModoPuntoVenta.Web);
        var a = await apoyo.ArticuloAsync(ctx, "a", 100m);
        var b = await apoyo.ArticuloAsync(ctx, "b", 100m);
        var apagado = (await (await ctx.Admin.PostAsJsonAsync("/api/combos", CuerpoDeCombo(a, b, "Apagado", false))).Content
            .ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetInt32();
        var prendido = (await (await ctx.Admin.PostAsJsonAsync("/api/combos", CuerpoDeCombo(a, b, "Prendido", true))).Content
            .ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetInt32();
        var eliminado = (await (await ctx.Admin.PostAsJsonAsync("/api/combos", CuerpoDeCombo(a, b, "Se elimina", true))).Content
            .ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetInt32();
        Assert.Equal(HttpStatusCode.NoContent, (await ctx.Admin.DeleteAsync($"/api/combos/{eliminado}")).StatusCode);

        var sobreElApagado = await ctx.Admin.PutAsJsonAsync($"/api/combos/{apagado}", CuerpoDeCombo(a, b, "Apagado editado", null));
        var sobreElPrendido = await ctx.Admin.PutAsJsonAsync($"/api/combos/{prendido}", CuerpoDeCombo(a, b, "Prendido editado", null));

        Assert.Equal(HttpStatusCode.OK, sobreElApagado.StatusCode);
        Assert.Equal(HttpStatusCode.OK, sobreElPrendido.StatusCode);
        Assert.False((await sobreElApagado.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("disponibleOnline").GetBoolean());
        Assert.True((await sobreElPrendido.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("disponibleOnline").GetBoolean());
        await using (var db = apoyo.Db(ctx.IdTenant))
        {
            var guardados = await db.Combos.ToDictionaryAsync(c => c.Id);
            Assert.Equal(("Apagado editado", false), (guardados[apagado].Nombre, guardados[apagado].DisponibleOnline));
            Assert.Equal(("Prendido editado", true), (guardados[prendido].Nombre, guardados[prendido].DisponibleOnline));
        }

        foreach (var inexistente in new[] { 987_654_321, eliminado })
        {
            var respuesta = await ctx.Admin.PutAsJsonAsync($"/api/combos/{inexistente}", CuerpoDeCombo(a, b, "X", null));

            Assert.Equal(HttpStatusCode.NotFound, respuesta.StatusCode);
        }
    }

    [Fact]
    public async Task SinElCampoDeDisponibleOnlineElJsonCrudoDeLaEdicionDeUnComboNoLoLeeComoFalso()
    {
        var ctx = await apoyo.PrepararAsync(nameof(SinElCampoDeDisponibleOnlineElJsonCrudoDeLaEdicionDeUnComboNoLoLeeComoFalso), ModoPuntoVenta.Web);
        var a = await apoyo.ArticuloAsync(ctx, "a", 100m);
        var b = await apoyo.ArticuloAsync(ctx, "b", 100m);
        var id = await apoyo.ComboAsync(ctx, 150m, [Art(a), Art(b)]);

        var cuerpo = $"{{\"nombre\":\"Crudo\",\"precio\":150,\"activo\":true,\"componentes\":[{{\"idArticulo\":{a},\"cantidad\":1}},{{\"idArticulo\":{b},\"cantidad\":1}}]}}";
        var respuesta = await ctx.Admin.PutAsync($"/api/combos/{id}", Json(cuerpo));

        Assert.Equal(HttpStatusCode.OK, respuesta.StatusCode);
        await using var db = apoyo.Db(ctx.IdTenant);
        var guardado = await db.Combos.SingleAsync(c => c.Id == id);
        Assert.Equal(("Crudo", true), (guardado.Nombre, guardado.DisponibleOnline));
    }
}
