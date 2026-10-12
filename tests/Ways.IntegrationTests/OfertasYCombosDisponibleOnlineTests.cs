using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Ways.Domain.Organizacion;
using static Ways.IntegrationTests.CombosApoyo;

namespace Ways.IntegrationTests;

/// <summary>
/// "Disponible online" de ofertas y combos se escribe por su POST y su PUT de siempre (un solo destino por campo): el
/// alta lo toma como <c>true</c> si falta, la edición lo exige (<c>400 disponible_online_requerido</c>, después del
/// 404), y el listado y el detalle lo devuelven. Cada test siembra dos filas con valores distintos para que un cruce
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
        Assert.Equal(true, guardadas["Sin campo"]);
        Assert.Equal(false, guardadas["Apagada"]);
        Assert.Equal(true, guardadas["Prendida"]);

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

    /// <summary>El PUT sin <c>disponibleOnline</c> es 400 y no escribe (ni el resto de los campos); un id que no existe,
    /// dado de baja o de otro tenant da 404 ANTES de mirar la falta del campo.</summary>
    [Fact]
    public async Task EditarUnaOfertaSinDisponibleOnlineDa400PeroUnaQueNoExisteDa404Primero()
    {
        var ctx = await apoyo.PrepararAsync(nameof(EditarUnaOfertaSinDisponibleOnlineDa400PeroUnaQueNoExisteDa404Primero), ModoPuntoVenta.Web);
        var articulo = await apoyo.ArticuloAsync(ctx, "articulo", 100m);
        var id = (await (await ctx.Admin.PostAsJsonAsync("/api/ofertas", CuerpoDeOferta(articulo, "Original", false))).Content
            .ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetInt32();
        var eliminada = (await (await ctx.Admin.PostAsJsonAsync("/api/ofertas", CuerpoDeOferta(articulo, "Se elimina", true))).Content
            .ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetInt32();
        Assert.Equal(HttpStatusCode.NoContent, (await ctx.Admin.DeleteAsync($"/api/ofertas/{eliminada}")).StatusCode);

        var sinCampo = await ctx.Admin.PutAsJsonAsync($"/api/ofertas/{id}", CuerpoDeOferta(articulo, "Cambiada", null, porcentaje: 50m));

        Assert.Equal(HttpStatusCode.BadRequest, sinCampo.StatusCode);
        Assert.Equal("disponible_online_requerido", await CodigoAsync(sinCampo));
        await using (var db = apoyo.Db(ctx.IdTenant))
        {
            var intacta = await db.Ofertas.SingleAsync(o => o.Id == id);
            Assert.Equal(("Original", false, 10m), (intacta.Nombre, intacta.DisponibleOnline, intacta.Porcentaje));
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
        Assert.Equal(true, guardados["Sin campo"]);
        Assert.Equal(false, guardados["Apagado"]);

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

    [Fact]
    public async Task EditarUnComboSinDisponibleOnlineDa400PeroUnoQueNoExisteDa404Primero()
    {
        var ctx = await apoyo.PrepararAsync(nameof(EditarUnComboSinDisponibleOnlineDa400PeroUnoQueNoExisteDa404Primero), ModoPuntoVenta.Web);
        var a = await apoyo.ArticuloAsync(ctx, "a", 100m);
        var b = await apoyo.ArticuloAsync(ctx, "b", 100m);
        var id = (await (await ctx.Admin.PostAsJsonAsync("/api/combos", CuerpoDeCombo(a, b, "Original", false))).Content
            .ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetInt32();
        var eliminado = (await (await ctx.Admin.PostAsJsonAsync("/api/combos", CuerpoDeCombo(a, b, "Se elimina", true))).Content
            .ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetInt32();
        Assert.Equal(HttpStatusCode.NoContent, (await ctx.Admin.DeleteAsync($"/api/combos/{eliminado}")).StatusCode);

        var sinCampo = await ctx.Admin.PutAsJsonAsync($"/api/combos/{id}", CuerpoDeCombo(a, b, "Cambiado", null));

        Assert.Equal(HttpStatusCode.BadRequest, sinCampo.StatusCode);
        Assert.Equal("disponible_online_requerido", await CodigoAsync(sinCampo));
        await using (var db = apoyo.Db(ctx.IdTenant))
        {
            var intacto = await db.Combos.SingleAsync(c => c.Id == id);
            Assert.Equal(("Original", false), (intacto.Nombre, intacto.DisponibleOnline));
        }

        foreach (var inexistente in new[] { 987_654_321, eliminado })
        {
            var respuesta = await ctx.Admin.PutAsJsonAsync($"/api/combos/{inexistente}", CuerpoDeCombo(a, b, "X", null));

            Assert.Equal(HttpStatusCode.NotFound, respuesta.StatusCode);
        }
    }

    [Fact]
    public async Task SinElCampoDeDisponibleOnlineElJsonDeLaEdicionDeUnComboNoLoLeeComoFalso()
    {
        var ctx = await apoyo.PrepararAsync(nameof(SinElCampoDeDisponibleOnlineElJsonDeLaEdicionDeUnComboNoLoLeeComoFalso), ModoPuntoVenta.Web);
        var a = await apoyo.ArticuloAsync(ctx, "a", 100m);
        var b = await apoyo.ArticuloAsync(ctx, "b", 100m);
        var id = await apoyo.ComboAsync(ctx, 150m, [Art(a), Art(b)]);

        var cuerpo = $"{{\"nombre\":\"Crudo\",\"precio\":150,\"activo\":true,\"componentes\":[{{\"idArticulo\":{a},\"cantidad\":1}},{{\"idArticulo\":{b},\"cantidad\":1}}]}}";
        var respuesta = await ctx.Admin.PutAsync($"/api/combos/{id}", Json(cuerpo));

        Assert.Equal(HttpStatusCode.BadRequest, respuesta.StatusCode);
        await using var db = apoyo.Db(ctx.IdTenant);
        Assert.True((await db.Combos.SingleAsync(c => c.Id == id)).DisponibleOnline);
    }
}
