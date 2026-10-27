using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Nodes;
using Ways.Application.Articulos;
using Ways.Application.Precios;
using Ways.Domain.Articulos;
using static Ways.IntegrationTests.ApoyoDeFamilias;

namespace Ways.IntegrationTests;

/// <summary>
/// El porcentaje de impuestos internos del artículo (<c>porcentaje_impuesto_interno</c>) contra Postgres real: ida y
/// vuelta por el alta, el detalle, el listado y la edición, su efecto en el costo nominal que calcula el servidor, y el
/// rechazo de lo inválido. Que el vendedor no lo recibe está en <c>ArticulosCostoOcultoAlVendedorTests</c>. Las combinaciones del cálculo están en
/// <c>CalculadorDeCostoNominalTests</c>.
/// </summary>
[Collection("Ways.IntegrationTests secuencial")]
public class ArticulosImpuestoInternoTests(WaysApiFixture fixture) : IClassFixture<WaysApiFixture>
{
    private readonly ApoyoDeFamilias apoyo = new(fixture);

    private static JsonObject Cuerpo(Entorno e, string nombre) => new()
    {
        ["nombre"] = nombre,
        ["idArea"] = e.Areas[0],
        ["idAlicuotaIva"] = e.Alicuotas[0],
        ["unidadVenta"] = "Unidad",
        ["esProducto"] = true,
        ["disponibleParaTodas"] = true,
        ["activo"] = true,
        ["controlaLote"] = false
    };

    private static async Task<ArticuloListado> LeerListadoAsync(HttpResponseMessage respuesta)
    {
        var cuerpo = await respuesta.Content.ReadAsStringAsync();
        Assert.True(respuesta.IsSuccessStatusCode, cuerpo);
        return JsonSerializer.Deserialize<ArticuloListado>(cuerpo, OpcionesJson)!;
    }

    private static async Task AssertCodigoAsync(HttpResponseMessage respuesta, string codigo)
    {
        var cuerpo = await respuesta.Content.ReadAsStringAsync();
        Assert.True(respuesta.StatusCode == HttpStatusCode.BadRequest, cuerpo);
        Assert.Equal(codigo, JsonDocument.Parse(cuerpo).RootElement.GetProperty("codigo").GetString());
    }

    [Fact]
    public async Task ElAltaGuardaElPorcentajeLoDevuelveEnDetalleYListadoYLoSumaAlCostoNominalSinIva()
    {
        using var e = await apoyo.PrepararAsync(
            nameof(ElAltaGuardaElPorcentajeLoDevuelveEnDetalleYListadoYLoSumaAlCostoNominalSinIva));
        var cuerpo = Cuerpo(e, "con impuesto interno");
        cuerpo["costoLista"] = 100m;
        cuerpo["costoListaIncluyeIva"] = false;
        cuerpo["porcentajeImpuestoInterno"] = 5m;

        var creado = await LeerListadoAsync(await e.Admin.PostAsJsonAsync("/api/articulos", cuerpo));

        // 100 + 5 de impuesto interno + 21 de IVA (que no grava al impuesto interno).
        Assert.Equal(126m, creado.CostoNominal);
        Assert.Equal(5m, creado.PorcentajeImpuestoInterno);

        Assert.Equal(5m, (await apoyo.LeerAsync(creado.Id)).PorcentajeImpuestoInterno);
        var detalle = await LeerListadoAsync(await e.Admin.GetAsync($"/api/articulos/{creado.Id}"));
        Assert.Equal(5m, detalle.PorcentajeImpuestoInterno);
        var listado = await e.Admin.GetFromJsonAsync<JsonObject>($"/api/articulos?tamanio=200", OpcionesJson);
        var fila = listado!["items"]!.AsArray().Single(x => x!["id"]!.GetValue<int>() == creado.Id)!;
        Assert.Equal(5m, fila["porcentajeImpuestoInterno"]!.GetValue<decimal>());
    }

    [Fact]
    public async Task ElAltaSinElCampoGuardaCeroYNoCambiaElCostoNominal()
    {
        using var e = await apoyo.PrepararAsync(nameof(ElAltaSinElCampoGuardaCeroYNoCambiaElCostoNominal));
        var cuerpo = Cuerpo(e, "sin impuesto interno");
        cuerpo["costoLista"] = 100m;
        cuerpo["costoListaIncluyeIva"] = false;

        var creado = await LeerListadoAsync(await e.Admin.PostAsJsonAsync("/api/articulos", cuerpo));

        Assert.Equal(121m, creado.CostoNominal);
        Assert.Equal(0m, creado.PorcentajeImpuestoInterno);
        Assert.Equal(0m, (await apoyo.LeerAsync(creado.Id)).PorcentajeImpuestoInterno);
    }

    [Fact]
    public async Task ConElCostoDeListaConIvaElPorcentajeSeGuardaPeroNoCambiaElCostoNominal()
    {
        using var e = await apoyo.PrepararAsync(nameof(ConElCostoDeListaConIvaElPorcentajeSeGuardaPeroNoCambiaElCostoNominal));
        var cuerpo = Cuerpo(e, "lista con iva");
        cuerpo["costoLista"] = 121m;
        cuerpo["porcentajeImpuestoInterno"] = 5m;

        var creado = await LeerListadoAsync(await e.Admin.PostAsJsonAsync("/api/articulos", cuerpo));

        Assert.Equal(121m, creado.CostoNominal);
        Assert.Equal(5m, creado.PorcentajeImpuestoInterno);
    }

    [Fact]
    public async Task LaEdicionCambiaElPorcentajeYRecalculaElCostoNominalYSinElCampoLoConserva()
    {
        using var e = await apoyo.PrepararAsync(nameof(LaEdicionCambiaElPorcentajeYRecalculaElCostoNominalYSinElCampoLoConserva));
        var alta = Cuerpo(e, "para editar");
        alta["costoLista"] = 100m;
        alta["costoListaIncluyeIva"] = false;
        alta["porcentajeImpuestoInterno"] = 5m;
        var creado = await LeerListadoAsync(await e.Admin.PostAsJsonAsync("/api/articulos", alta));
        Assert.Equal(126m, creado.CostoNominal);

        var edicion = Cuerpo(e, "para editar");
        edicion["costoLista"] = 100m;
        edicion["porcentajeImpuestoInterno"] = 10m;
        var editado = await LeerListadoAsync(await e.Admin.PutAsJsonAsync($"/api/articulos/{creado.Id}", edicion));
        Assert.Equal(10m, editado.PorcentajeImpuestoInterno);
        Assert.Equal(131m, editado.CostoNominal);
        Assert.Equal(10m, (await apoyo.LeerAsync(creado.Id)).PorcentajeImpuestoInterno);

        var sinCampo = Cuerpo(e, "para editar");
        sinCampo["costoLista"] = 100m;
        var conservado = await LeerListadoAsync(await e.Admin.PutAsJsonAsync($"/api/articulos/{creado.Id}", sinCampo));
        Assert.Equal(10m, conservado.PorcentajeImpuestoInterno);
        Assert.Equal(131m, conservado.CostoNominal);

        var aCero = Cuerpo(e, "para editar");
        aCero["costoLista"] = 100m;
        aCero["porcentajeImpuestoInterno"] = 0m;
        var enCero = await LeerListadoAsync(await e.Admin.PutAsJsonAsync($"/api/articulos/{creado.Id}", aCero));
        Assert.Equal(0m, enCero.PorcentajeImpuestoInterno);
        Assert.Equal(121m, enCero.CostoNominal);
    }

    [Theory]
    [InlineData(-1)]
    [InlineData(1000)]
    public async Task UnPorcentajeFueraDeRangoEsUn400EnElAltaYEnLaEdicion(int porcentaje)
    {
        using var e = await apoyo.PrepararAsync($"{nameof(UnPorcentajeFueraDeRangoEsUn400EnElAltaYEnLaEdicion)}{porcentaje + 1}");
        var alta = Cuerpo(e, "invalido");
        alta["porcentajeImpuestoInterno"] = porcentaje;
        await AssertCodigoAsync(await e.Admin.PostAsJsonAsync("/api/articulos", alta), "porcentaje_impuesto_interno_invalido");

        var creado = await LeerListadoAsync(await e.Admin.PostAsJsonAsync("/api/articulos", Cuerpo(e, "valido")));
        var edicion = Cuerpo(e, "valido");
        edicion["porcentajeImpuestoInterno"] = porcentaje;
        await AssertCodigoAsync(
            await e.Admin.PutAsJsonAsync($"/api/articulos/{creado.Id}", edicion), "porcentaje_impuesto_interno_invalido");
        Assert.Equal(0m, (await apoyo.LeerAsync(creado.Id)).PorcentajeImpuestoInterno);
    }

    [Fact]
    public async Task EditarLaTasaConAlcanceDeFamiliaLaLlevaALosMiembrosVivosJuntoConElCostoNominalRecalculado()
    {
        using var e = await apoyo.PrepararAsync(
            nameof(EditarLaTasaConAlcanceDeFamiliaLaLlevaALosMiembrosVivosJuntoConElCostoNominalRecalculado));
        var iva = e.PorcentajeDeLaAlicuota;
        var inicial = ValoresBase(e) with
        {
            CostoLista = 100m, CostoListaIncluyeIva = false, DescuentoProveedor = 0m, PorcentajeImpuestoInterno = 0m,
            CostoNominal = Math.Round(100m * (1 + iva / 100m), 2, MidpointRounding.AwayFromZero)
        };
        var familia = await apoyo.SembrarFamiliaAsync(e, "Con impuesto interno");
        var a1 = await apoyo.SembrarArticuloAsync(e, "a1", inicial, familia);
        var a2 = await apoyo.SembrarArticuloAsync(e, "a2", inicial, familia);
        var suelto = await apoyo.SembrarArticuloAsync(e, "suelto", inicial);
        var esperado = Math.Round(100m * (1 + (5m + iva) / 100m), 2, MidpointRounding.AwayFromZero);

        var pedido = EdicionIgualA(await apoyo.LeerAsync(a2), AlcanceDeFamilia.Familia) with { PorcentajeImpuestoInterno = 5m };
        var respuesta = await PutArticuloAsync(e.Admin, a2, pedido);

        Assert.Equal(HttpStatusCode.OK, respuesta.StatusCode);
        foreach (var id in new[] { a1, a2 })
        {
            var miembro = await apoyo.LeerAsync(id);
            Assert.Equal((5m, esperado), (miembro.PorcentajeImpuestoInterno, miembro.CostoNominal));
        }

        var ajeno = await apoyo.LeerAsync(suelto);
        Assert.Equal((0m, inicial.CostoNominal), (ajeno.PorcentajeImpuestoInterno, ajeno.CostoNominal));
    }

    [Fact]
    public async Task EditarSoloLaTasaDeUnMiembroSinAlcanceEsUn409YNoEscribeNada()
    {
        using var e = await apoyo.PrepararAsync(nameof(EditarSoloLaTasaDeUnMiembroSinAlcanceEsUn409YNoEscribeNada));
        var inicial = ValoresBase(e);
        var familia = await apoyo.SembrarFamiliaAsync(e, "Sin alcance");
        var a1 = await apoyo.SembrarArticuloAsync(e, "a1", inicial, familia);
        var a2 = await apoyo.SembrarArticuloAsync(e, "a2", inicial, familia);

        var pedido = EdicionIgualA(await apoyo.LeerAsync(a2)) with { PorcentajeImpuestoInterno = 5m };
        var respuesta = await PutArticuloAsync(e.Admin, a2, pedido);

        Assert.Equal(HttpStatusCode.Conflict, respuesta.StatusCode);
        Assert.Equal("alcance_requerido", (await ProblemaAsync(respuesta)).Codigo);
        Assert.Equal(0m, (await apoyo.LeerAsync(a1)).PorcentajeImpuestoInterno);
        Assert.Equal(0m, (await apoyo.LeerAsync(a2)).PorcentajeImpuestoInterno);
    }
}
