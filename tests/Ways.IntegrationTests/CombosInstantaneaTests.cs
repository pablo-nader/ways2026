using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.DependencyInjection;
using Ways.Domain.Articulos;
using Ways.Domain.Organizacion;
using static Ways.IntegrationTests.CombosApoyo;

namespace Ways.IntegrationTests;

/// <summary>
/// Los combos de <c>GET /api/pos/instantanea?version=2</c>: qué combos viajan al dispositivo, que la etiqueta de
/// contenido cambie con ellos (y solo con ellos) y que el formato original siga sin combos.
/// </summary>
[Collection("Ways.IntegrationTests secuencial")]
public class CombosInstantaneaTests(WaysApiFixture fixture) : IClassFixture<WaysApiFixture>
{
    private readonly CombosApoyo apoyo = new(fixture);

    private const string RutaV2 = "/api/pos/instantanea?version=2";
    private const string RutaOriginal = "/api/pos/instantanea";

    private void VaciarCacheDeInstantaneas() =>
        ((MemoryCache)fixture.Services.GetRequiredService<IMemoryCache>()).Compact(1.0);

    private async Task<(JsonElement Cuerpo, EntityTagHeaderValue Etiqueta)> LeerAsync(Escenario ctx, string ruta = RutaV2)
    {
        VaciarCacheDeInstantaneas();
        var respuesta = await ctx.Cajero!.GetAsync(ruta);
        var texto = await respuesta.Content.ReadAsStringAsync();
        Assert.True(respuesta.StatusCode == HttpStatusCode.OK, texto);

        return (JsonDocument.Parse(texto).RootElement, respuesta.Headers.ETag!);
    }

    private static List<int> IdsDeCombos(JsonElement cuerpo) =>
        cuerpo.TryGetProperty("combos", out var combos)
            ? [.. combos.EnumerateArray().Select(c => c.GetProperty("idCombo").GetInt32())]
            : [];

    [Fact]
    public async Task LaInstantaneaTraeLosCombosConArticulosYFamiliasOrdenadosPorId()
    {
        var ctx = await apoyo.PrepararAsync(nameof(LaInstantaneaTraeLosCombosConArticulosYFamiliasOrdenadosPorId), ModoPuntoVenta.Escritorio);
        var gaseosas = await apoyo.FamiliaAsync(ctx, "Gaseosas");
        var pancho = await apoyo.ArticuloAsync(ctx, "pancho", 3000m);
        var papas = await apoyo.ArticuloAsync(ctx, "papas", 1800m);
        var coca = await apoyo.ArticuloAsync(ctx, "coca", 2500m, idFamilia: gaseosas);
        var primero = await apoyo.ComboAsync(ctx, 5000m, [Fam(gaseosas, 2m), Art(pancho)], nombre: "Pancho y dos gaseosas");
        var segundo = await apoyo.ComboAsync(ctx, 3600m, [Art(papas), Art(coca)], nombre: "Papas y coca");

        var (cuerpo, _) = await LeerAsync(ctx);

        var combos = cuerpo.GetProperty("combos");
        Assert.Equal([primero, segundo], IdsDeCombos(cuerpo));

        var combo = combos[0];
        Assert.Equal(1, combo.GetProperty("codigo").GetInt32());
        Assert.Equal("Pancho y dos gaseosas", combo.GetProperty("nombre").GetString());
        Assert.Equal(5000m, combo.GetProperty("precio").GetDecimal());

        // Artículos primero y familias después, aunque se hayan sembrado al revés.
        var componentes = combo.GetProperty("componentes");
        Assert.Equal(2, componentes.GetArrayLength());
        Assert.Equal(pancho, componentes[0].GetProperty("idArticulo").GetInt32());
        Assert.False(componentes[0].TryGetProperty("idFamilia", out _));
        Assert.Equal(1m, componentes[0].GetProperty("cantidad").GetDecimal());
        Assert.Equal(gaseosas, componentes[1].GetProperty("idFamilia").GetInt32());
        Assert.False(componentes[1].TryGetProperty("idArticulo", out _));
        Assert.Equal(2m, componentes[1].GetProperty("cantidad").GetDecimal());

        // El dispositivo empareja el componente familia con el idFamilia de cada artículo de la misma instantánea.
        var cocaEnLaInstantanea = cuerpo.GetProperty("articulos").EnumerateArray()
            .Single(a => a.GetProperty("idArticulo").GetInt32() == coca);
        Assert.Equal(gaseosas, cocaEnLaInstantanea.GetProperty("idFamilia").GetInt32());
    }

    [Fact]
    public async Task SinCombosLaClaveNoViaja()
    {
        var ctx = await apoyo.PrepararAsync(nameof(SinCombosLaClaveNoViaja), ModoPuntoVenta.Escritorio);
        await apoyo.ArticuloAsync(ctx, "pancho", 3000m);

        var (cuerpo, _) = await LeerAsync(ctx);

        Assert.False(cuerpo.TryGetProperty("combos", out _));
    }

    [Fact]
    public async Task ElFormatoOriginalNoLlevaCombos()
    {
        var ctx = await apoyo.PrepararAsync(nameof(ElFormatoOriginalNoLlevaCombos), ModoPuntoVenta.Escritorio);
        var pancho = await apoyo.ArticuloAsync(ctx, "pancho", 3000m);
        var coca = await apoyo.ArticuloAsync(ctx, "coca", 2500m);
        await apoyo.ComboAsync(ctx, 5000m, [Art(pancho), Art(coca)]);

        var (original, _) = await LeerAsync(ctx, RutaOriginal);
        var (v2, _) = await LeerAsync(ctx);

        Assert.False(original.TryGetProperty("combos", out _));
        Assert.Equal(1, IdsDeCombos(v2).Count);
    }

    // ---- Qué combos viajan ----

    [Fact]
    public async Task SoloViajanLosCombosActivosVivosYDeLaEmpresaDelPuntoDeVentaODelTenant()
    {
        var ctx = await apoyo.PrepararAsync(nameof(SoloViajanLosCombosActivosVivosYDeLaEmpresaDelPuntoDeVentaODelTenant), ModoPuntoVenta.Escritorio);
        var otraEmpresa = await apoyo.OtraEmpresaAsync(ctx);
        var a = await apoyo.ArticuloAsync(ctx, "a", 1000m);
        var b = await apoyo.ArticuloAsync(ctx, "b", 1000m);
        var deTodos = await apoyo.ComboAsync(ctx, 1500m, [Art(a), Art(b)]);
        var propio = await apoyo.ComboAsync(ctx, 1600m, [Art(a), Art(b)], idEmpresa: ctx.IdEmpresa);
        await apoyo.ComboAsync(ctx, 1700m, [Art(a), Art(b)], idEmpresa: otraEmpresa);
        await apoyo.ComboAsync(ctx, 1800m, [Art(a), Art(b)], activo: false);
        await apoyo.ComboAsync(ctx, 1900m, [Art(a), Art(b)], dadoDeBaja: true);

        var (cuerpo, _) = await LeerAsync(ctx);

        Assert.Equal([deTodos, propio], IdsDeCombos(cuerpo));
    }

    /// <summary>Cláusula: un combo viaja entero o no viaja. Cada caso rompe UN componente del combo bajo prueba y deja
    /// intactos otros combos, que sí tienen que viajar: el de dos artículos por unidad y los dos con un componente
    /// pesable de cantidad fraccionaria (un artículo y una familia). La misma fracción sobre un artículo por unidad, o
    /// sobre una familia que solo tiene artículos por unidad, no viaja.</summary>
    [Fact]
    public async Task UnComboQueNoSePuedeCompletarOfflineNoViajaNiParcial()
    {
        var ctx = await apoyo.PrepararAsync(nameof(UnComboQueNoSePuedeCompletarOfflineNoViajaNiParcial), ModoPuntoVenta.Escritorio);
        var sano1 = await apoyo.ArticuloAsync(ctx, "sano1", 1000m);
        var sano2 = await apoyo.ArticuloAsync(ctx, "sano2", 1000m);
        var inactivo = await apoyo.ArticuloAsync(ctx, "inactivo", 1000m, activo: false);
        var sinPrecio = await apoyo.ArticuloAsync(ctx, "sinprecio", 1000m, conPrecio: false);
        var pesable = await apoyo.ArticuloAsync(ctx, "pesable", 1000m, UnidadVenta.Peso);
        var dadoDeBaja = await apoyo.ArticuloAsync(ctx, "dadodebaja", 1000m);
        var familiaVacia = await apoyo.FamiliaAsync(ctx, "Vacia");
        var familiaSoloPesables = await apoyo.FamiliaAsync(ctx, "SoloPesables");
        await apoyo.ArticuloAsync(ctx, "pesable-de-familia", 1000m, UnidadVenta.Peso, familiaSoloPesables);
        var familiaSoloPorUnidad = await apoyo.FamiliaAsync(ctx, "SoloPorUnidad");
        await apoyo.ArticuloAsync(ctx, "unidad-de-familia", 1000m, idFamilia: familiaSoloPorUnidad);
        var familiaConInactivo = await apoyo.FamiliaAsync(ctx, "ConInactivo");
        await apoyo.ArticuloAsync(ctx, "inactivo-de-familia", 1000m, idFamilia: familiaConInactivo, activo: false);

        var completo = await apoyo.ComboAsync(ctx, 1500m, [Art(sano1), Art(sano2)]);
        await apoyo.ComboAsync(ctx, 1500m, [Art(sano1), Art(inactivo)]);
        await apoyo.ComboAsync(ctx, 1500m, [Art(sano1), Art(sinPrecio)]);
        var conPesable = await apoyo.ComboAsync(ctx, 1500m, [Art(sano1), Art(pesable, 0.200m)]);
        await apoyo.ComboAsync(ctx, 1500m, [Art(sano1), Art(sano2, 0.500m)]);
        await apoyo.ComboAsync(ctx, 1500m, [Art(sano1), Art(dadoDeBaja)]);
        await apoyo.ComboAsync(ctx, 1500m, [Art(sano1), Fam(familiaVacia)]);
        var conFamiliaDePesables = await apoyo.ComboAsync(ctx, 1500m, [Art(sano1), Fam(familiaSoloPesables, 0.250m)]);
        await apoyo.ComboAsync(ctx, 1500m, [Art(sano1), Fam(familiaSoloPorUnidad, 0.250m)]);
        await apoyo.ComboAsync(ctx, 1500m, [Art(sano1), Fam(familiaConInactivo)]);

        await using (var db = apoyo.Db(ctx.IdTenant))
        {
            await db.Database.ExecuteSqlInterpolatedAsync(
                $"UPDATE articulos SET deleted_at = now() WHERE id_articulo = {dadoDeBaja}");
        }

        var (cuerpo, _) = await LeerAsync(ctx);

        Assert.Equal([completo, conPesable, conFamiliaDePesables], IdsDeCombos(cuerpo));
    }

    // ---- Etiqueta de contenido ----

    private static async Task<HttpResponseMessage> CondicionalAsync(Escenario ctx, EntityTagHeaderValue etiqueta)
    {
        using var solicitud = new HttpRequestMessage(HttpMethod.Get, RutaV2);
        solicitud.Headers.IfNoneMatch.Add(etiqueta);

        return await ctx.Cajero!.SendAsync(solicitud);
    }

    [Fact]
    public async Task LaEtiquetaCambiaAlCambiarUnComboOUnComponenteYNoCambiaSiNadaRelevanteCambio()
    {
        var ctx = await apoyo.PrepararAsync(nameof(LaEtiquetaCambiaAlCambiarUnComboOUnComponenteYNoCambiaSiNadaRelevanteCambio), ModoPuntoVenta.Escritorio);
        var pancho = await apoyo.ArticuloAsync(ctx, "pancho", 3000m);
        var coca = await apoyo.ArticuloAsync(ctx, "coca", 2500m);
        var papas = await apoyo.ArticuloAsync(ctx, "papas", 1800m);
        var combo = await apoyo.ComboAsync(ctx, 5000m, [Art(pancho), Art(coca)]);

        var (_, base_) = await LeerAsync(ctx);

        // Sin cambios (aunque la caché venza y se rearme): misma etiqueta y 304.
        var (_, repetida) = await LeerAsync(ctx);
        Assert.Equal(base_, repetida);
        VaciarCacheDeInstantaneas();
        Assert.Equal(HttpStatusCode.NotModified, (await CondicionalAsync(ctx, base_)).StatusCode);

        // Un cambio que no es de la instantánea (tocar updated_at de la fila):
        // no cambia el contenido ni la etiqueta.
        await using (var db = apoyo.Db(ctx.IdTenant))
        {
            await db.Database.ExecuteSqlInterpolatedAsync(
                $"UPDATE combos SET updated_at = now() WHERE id_combo = {combo}");
        }

        var (_, trasTocarLaFila) = await LeerAsync(ctx);
        Assert.Equal(base_, trasTocarLaFila);

        // Precio del combo.
        await using (var db = apoyo.Db(ctx.IdTenant))
        {
            await db.Database.ExecuteSqlInterpolatedAsync($"UPDATE combos SET precio = 4900 WHERE id_combo = {combo}");
        }

        var (_, trasElPrecio) = await LeerAsync(ctx);
        Assert.NotEqual(base_, trasElPrecio);

        // Cantidad de un componente.
        await using (var db = apoyo.Db(ctx.IdTenant))
        {
            await db.Database.ExecuteSqlInterpolatedAsync(
                $"UPDATE combos_componentes SET cantidad = 2 WHERE id_combo = {combo} AND id_articulo = {coca}");
        }

        var (_, trasLaCantidad) = await LeerAsync(ctx);
        Assert.NotEqual(trasElPrecio, trasLaCantidad);

        // Otro componente.
        await using (var db = apoyo.Db(ctx.IdTenant))
        {
            await db.Database.ExecuteSqlInterpolatedAsync(
                $"UPDATE combos_componentes SET id_articulo = {papas} WHERE id_combo = {combo} AND id_articulo = {coca}");
        }

        var (_, trasElComponente) = await LeerAsync(ctx);
        Assert.NotEqual(trasLaCantidad, trasElComponente);

        // Un combo nuevo.
        await apoyo.ComboAsync(ctx, 3000m, [Art(pancho), Art(papas)]);
        var (_, trasUnComboNuevo) = await LeerAsync(ctx);
        Assert.NotEqual(trasElComponente, trasUnComboNuevo);
    }

    [Fact]
    public async Task LaEtiquetaCambiaCuandoUnComponenteDejaDeSerVendible()
    {
        var ctx = await apoyo.PrepararAsync(nameof(LaEtiquetaCambiaCuandoUnComponenteDejaDeSerVendible), ModoPuntoVenta.Escritorio);
        var pancho = await apoyo.ArticuloAsync(ctx, "pancho", 3000m);
        var coca = await apoyo.ArticuloAsync(ctx, "coca", 2500m);
        await apoyo.ComboAsync(ctx, 5000m, [Art(pancho), Art(coca)]);

        var (con, etiquetaCon) = await LeerAsync(ctx);
        Assert.Equal(1, IdsDeCombos(con).Count);

        await using (var db = apoyo.Db(ctx.IdTenant))
        {
            await db.Database.ExecuteSqlInterpolatedAsync($"UPDATE articulos SET activo = false WHERE id_articulo = {coca}");
        }

        var (sin, etiquetaSin) = await LeerAsync(ctx);

        Assert.Empty(IdsDeCombos(sin));
        Assert.NotEqual(etiquetaCon, etiquetaSin);
    }

    [Fact]
    public async Task UnDispositivoDeOtroTenantNoVeLosCombosAjenos()
    {
        var ctxA = await apoyo.PrepararAsync(nameof(UnDispositivoDeOtroTenantNoVeLosCombosAjenos) + "A", ModoPuntoVenta.Escritorio);
        var ctxB = await apoyo.PrepararAsync(nameof(UnDispositivoDeOtroTenantNoVeLosCombosAjenos) + "B", ModoPuntoVenta.Escritorio);
        var a1 = await apoyo.ArticuloAsync(ctxA, "a1", 1000m);
        var a2 = await apoyo.ArticuloAsync(ctxA, "a2", 1000m);
        await apoyo.ComboAsync(ctxA, 1500m, [Art(a1), Art(a2)]);
        await apoyo.ArticuloAsync(ctxB, "b1", 1000m);

        var (cuerpoB, _) = await LeerAsync(ctxB);

        Assert.False(cuerpoB.TryGetProperty("combos", out _));
    }
}
