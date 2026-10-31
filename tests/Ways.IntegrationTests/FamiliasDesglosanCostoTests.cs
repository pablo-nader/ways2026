using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Ways.Application.Articulos;
using Ways.Application.Familias;
using Ways.Application.Precios;
using Ways.Domain.Articulos;
using Ways.Infrastructure.Multitenancy;
using static Ways.IntegrationTests.ApoyoDeFamilias;

namespace Ways.IntegrationTests;

/// <summary>
/// Familias que permiten desglosar el costo (doc 10 §3, "Familias de artículos") contra Postgres real: con la marca,
/// la composición del costo es propia de cada miembro y solo el costo nominal es común. Cubre la edición de un miembro
/// (<c>PUT /api/articulos/{id}</c>), el alta dentro de la familia, la edición de la familia (encender y apagar la
/// marca), agrupar y la previsualización. Sin la marca nada cambia: lo cubren las demás clases de familias.
///
/// <para>La base de las siembras es <see cref="ValoresBase"/>: costo de lista 50 con IVA, 10% de descuento, costo
/// nominal 45. <see cref="OtraComposicionDe45"/> y <see cref="OtraMasDe45"/> son composiciones distintas de la base
/// que dan el mismo nominal.</para>
/// </summary>
[Collection("Ways.IntegrationTests secuencial")]
public class FamiliasDesglosanCostoTests(WaysApiFixture fixture) : IClassFixture<WaysApiFixture>
{
    private readonly ApoyoDeFamilias apoyo = new(fixture);

    /// <summary>Costo de lista 45 con IVA y sin descuento: 45, como la base.</summary>
    private static ValoresCompartidosDeFamilia OtraComposicionDe45(Entorno e) =>
        ValoresBase(e) with { CostoLista = 45m, DescuentoProveedor = 0m, ImpuestoInterno = 3m };

    /// <summary>Costo de lista 60 con IVA y 25% de descuento: 45.</summary>
    private static ValoresCompartidosDeFamilia OtraMasDe45(Entorno e) =>
        ValoresBase(e) with { CostoLista = 60m, DescuentoProveedor = 25m };

    private sealed record Familiares(int Familia, int A1, int A2, int A3, int DeBaja);

    /// <summary>Una familia con la marca, tres miembros vivos con tres composiciones distintas del mismo costo
    /// nominal 45 y un miembro dado de baja con otra composición.</summary>
    private async Task<Familiares> SembrarAsync(Entorno e, bool permiteDesglosarCosto = true)
    {
        var familia = await apoyo.SembrarFamiliaAsync(e, "Gaseosas", permiteDesglosarCosto: permiteDesglosarCosto);
        var a1 = await apoyo.SembrarArticuloAsync(e, "a1", ValoresBase(e), familia);
        var a2 = await apoyo.SembrarArticuloAsync(e, "a2", OtraComposicionDe45(e), familia);
        var a3 = await apoyo.SembrarArticuloAsync(e, "a3", OtraMasDe45(e), familia);
        var deBaja = await apoyo.SembrarArticuloAsync(
            e, "de-baja", ValoresBase(e) with { CostoLista = 999m, CostoNominal = 999m }, familia, dadoDeBaja: true);

        return new Familiares(familia, a1, a2, a3, deBaja);
    }

    private static object Huella(Articulo a) => new
    {
        a.CodigoInterno, a.Nombre, a.Descripcion, a.IdMarca, a.Activo, a.IdFamilia,
        Compartidos = ValoresCompartidosDeFamilia.De(a), a.UpdatedAt, a.DeletedAt
    };

    private async Task<List<object>> HuellasAsync(params int[] ids)
    {
        var huellas = new List<object>();
        foreach (var id in ids)
        {
            huellas.Add(Huella(await apoyo.LeerAsync(id)));
        }

        return huellas;
    }

    private async Task AfirmarSinCambiosAsync(int[] ids, List<object> antes)
    {
        for (var i = 0; i < ids.Length; i++)
        {
            Assert.Equal(antes[i], Huella(await apoyo.LeerAsync(ids[i])));
        }
    }

    private static EdicionArticulo ConComposicion(
        EdicionArticulo edicion, decimal costoLista, decimal descuento, decimal impuestoInterno = 0m) =>
        edicion with { CostoLista = costoLista, DescuentoProveedor = descuento, ImpuestoInterno = impuestoInterno };

    private static async Task<JsonElement> ProblemaJsonAsync(HttpResponseMessage respuesta) =>
        await respuesta.Content.ReadFromJsonAsync<JsonElement>();

    private static AltaArticulo Alta(ValoresCompartidosDeFamilia v, int? idFamilia, string nombre = "nuevo") => new(
        CodigoInterno: null, Nombre: nombre, Descripcion: null, IdArea: v.IdArea, IdCategoria: v.IdCategoria,
        IdMarca: null, IdGrupo: v.IdGrupo, IdProveedorHabitual: v.IdProveedorHabitual,
        IdAlicuotaIva: v.IdAlicuotaIva, UnidadVenta: v.UnidadVenta, UnidadesPorBulto: v.UnidadesPorBulto,
        EsProducto: v.EsProducto, CostoLista: v.CostoLista, DescuentoProveedor: v.DescuentoProveedor,
        ControlaLote: v.ControlaLote, AcumulaEnVenta: v.AcumulaEnVenta, IdFamilia: idFamilia,
        CostoListaPor: v.CostoListaPor, CostoListaIncluyeIva: v.CostoListaIncluyeIva, ImpuestoInterno: v.ImpuestoInterno);

    private static Task<HttpResponseMessage> PostArticuloAsync(HttpClient admin, AltaArticulo alta) =>
        admin.PostAsJsonAsync("/api/articulos", alta, OpcionesJson);

    private static Task<HttpResponseMessage> PutFamiliaAsync(
        HttpClient cliente, int id, string? nombre, bool? activo, bool? permiteDesglosarCosto) =>
        cliente.PutAsJsonAsync($"/api/familias/{id}", new EdicionFamilia(nombre, activo, permiteDesglosarCosto), OpcionesJson);

    private async Task<int> ContarArticulosAsync(int idTenant)
    {
        await using var db = fixture.CrearContextoDeAplicacion(TenantActualFijo.Plataforma);

        return await db.Articulos.IgnoreQueryFilters().CountAsync(a => a.IdTenant == idTenant);
    }

    // =================================================================================================
    // Edición de un miembro
    // =================================================================================================

    /// <summary>Con la marca, una composición nueva que da el nominal de la familia se guarda SOLO en ese artículo y no
    /// pide alcance (tampoco con <c>Familia</c>, que no tiene nada que replicar). Los demás miembros, el dado de baja
    /// y el artículo siguen en la familia, con su <c>updated_at</c> intacto.</summary>
    [Theory]
    [InlineData(null)]
    [InlineData(AlcanceDeFamilia.Familia)]
    public async Task UnaComposicionConElMismoNominalSeGuardaSoloEnEseArticuloSinPedirAlcance(AlcanceDeFamilia? alcance)
    {
        using var e = await apoyo.PrepararAsync(nameof(UnaComposicionConElMismoNominalSeGuardaSoloEnEseArticuloSinPedirAlcance));
        var f = await SembrarAsync(e);
        var antes = await apoyo.LeerAsync(f.A2);
        var huellasDeLosDemas = await HuellasAsync(f.A1, f.A3, f.DeBaja);

        var pedido = ConComposicion(EdicionIgualA(antes, alcance), 60m, 25m, impuestoInterno: 8m) with
        {
            Nombre = "a2 editado"
        };

        var respuesta = await PutArticuloAsync(e.Admin, f.A2, pedido);

        Assert.Equal(HttpStatusCode.OK, respuesta.StatusCode);
        var despues = await apoyo.LeerAsync(f.A2);
        Assert.Equal("a2 editado", despues.Nombre);
        Assert.Equal(60m, despues.CostoLista);
        Assert.Equal(25m, despues.DescuentoProveedor);
        Assert.Equal(8m, despues.ImpuestoInterno);
        Assert.Equal(45m, despues.CostoNominal);
        Assert.Equal(f.Familia, despues.IdFamilia);
        await AfirmarSinCambiosAsync([f.A1, f.A3, f.DeBaja], huellasDeLosDemas);
    }

    /// <summary>Un nominal distinto es un cambio de un campo compartido: sin alcance, 409 <c>alcance_requerido</c> con
    /// los dos nominales estructurados en <c>datos</c> y en el mensaje, y no se escribe nada.</summary>
    [Fact]
    public async Task UnNominalDistintoSinAlcanceDa409ConLosDosNominalesYNoEscribeNada()
    {
        using var e = await apoyo.PrepararAsync(nameof(UnNominalDistintoSinAlcanceDa409ConLosDosNominalesYNoEscribeNada));
        var f = await SembrarAsync(e);
        var antes = await apoyo.LeerAsync(f.A1);
        var huellas = await HuellasAsync(f.A1, f.A2, f.A3, f.DeBaja);

        var respuesta = await PutArticuloAsync(
            e.Admin, f.A1, ConComposicion(EdicionIgualA(antes) with { Nombre = "no se escribe" }, 60m, 10m));

        Assert.Equal(HttpStatusCode.Conflict, respuesta.StatusCode);
        var problema = await ProblemaJsonAsync(respuesta);
        Assert.Equal("alcance_requerido", problema.GetProperty("codigo").GetString());
        var datos = problema.GetProperty("datos");
        Assert.Equal(45m, datos.GetProperty("costoNominalAnterior").GetDecimal());
        Assert.Equal(54m, datos.GetProperty("costoNominalNuevo").GetDecimal());
        Assert.Contains("45,00", problema.GetProperty("title").GetString());
        Assert.Contains("54,00", problema.GetProperty("title").GetString());
        await AfirmarSinCambiosAsync([f.A1, f.A2, f.A3, f.DeBaja], huellas);
    }

    /// <summary>Con <c>Familia</c> el nominal nuevo —y cualquier otro campo compartido que el pedido cambie— llega a
    /// todos los miembros vivos, y cada uno conserva su composición. El dado de baja no se toca.</summary>
    [Fact]
    public async Task UnNominalDistintoConAlcanceFamiliaLoAplicaATodosYCadaMiembroConservaSuComposicion()
    {
        using var e = await apoyo.PrepararAsync(nameof(UnNominalDistintoConAlcanceFamiliaLoAplicaATodosYCadaMiembroConservaSuComposicion));
        var f = await SembrarAsync(e);
        var a2Antes = await apoyo.LeerAsync(f.A2);
        var a3Antes = await apoyo.LeerAsync(f.A3);
        var huellaDeBaja = await HuellasAsync(f.DeBaja);

        var pedido = ConComposicion(
            EdicionIgualA(await apoyo.LeerAsync(f.A1), AlcanceDeFamilia.Familia) with { IdCategoria = e.Categorias[1] },
            60m, 10m);

        var respuesta = await PutArticuloAsync(e.Admin, f.A1, pedido);

        Assert.Equal(HttpStatusCode.OK, respuesta.StatusCode);
        var a1 = await apoyo.LeerAsync(f.A1);
        var a2 = await apoyo.LeerAsync(f.A2);
        var a3 = await apoyo.LeerAsync(f.A3);

        Assert.All(new[] { a1, a2, a3 }, a =>
        {
            Assert.Equal(54m, a.CostoNominal);
            Assert.Equal(e.Categorias[1], a.IdCategoria);
            Assert.Equal(f.Familia, a.IdFamilia);
        });
        Assert.Equal(60m, a1.CostoLista);
        Assert.Equal(10m, a1.DescuentoProveedor);
        Assert.Equal(ComposicionDelCosto.De(a2Antes), ComposicionDelCosto.De(a2));
        Assert.Equal(ComposicionDelCosto.De(a3Antes), ComposicionDelCosto.De(a3));
        await AfirmarSinCambiosAsync([f.DeBaja], huellaDeBaja);
    }

    /// <summary>Con "solo este" el artículo sale de la familia con su composición y su nominal nuevos; los demás no
    /// cambian.</summary>
    [Fact]
    public async Task UnNominalDistintoConSoloEsteSacaAlArticuloDeLaFamiliaYNoTocaALosDemas()
    {
        using var e = await apoyo.PrepararAsync(nameof(UnNominalDistintoConSoloEsteSacaAlArticuloDeLaFamiliaYNoTocaALosDemas));
        var f = await SembrarAsync(e);
        var huellas = await HuellasAsync(f.A2, f.A3, f.DeBaja);

        var pedido = ConComposicion(
            EdicionIgualA(await apoyo.LeerAsync(f.A1), AlcanceDeFamilia.SoloEste), 60m, 10m);

        Assert.Equal(HttpStatusCode.OK, (await PutArticuloAsync(e.Admin, f.A1, pedido)).StatusCode);

        var a1 = await apoyo.LeerAsync(f.A1);
        Assert.Null(a1.IdFamilia);
        Assert.Equal(54m, a1.CostoNominal);
        await AfirmarSinCambiosAsync([f.A2, f.A3, f.DeBaja], huellas);
    }

    /// <summary>Los otros once campos siguen compartidos: cambiar el área sin alcance sigue pidiéndolo, aunque la
    /// composición no cambie.</summary>
    [Fact]
    public async Task UnCampoCompartidoQueNoEsLaComposicionSigueExigiendoAlcance()
    {
        using var e = await apoyo.PrepararAsync(nameof(UnCampoCompartidoQueNoEsLaComposicionSigueExigiendoAlcance));
        var f = await SembrarAsync(e);
        var huellas = await HuellasAsync(f.A1, f.A2, f.A3, f.DeBaja);

        var respuesta = await PutArticuloAsync(
            e.Admin, f.A1, EdicionIgualA(await apoyo.LeerAsync(f.A1)) with { IdArea = e.Areas[1] });

        Assert.Equal(HttpStatusCode.Conflict, respuesta.StatusCode);
        Assert.Equal("alcance_requerido", (await ProblemaAsync(respuesta)).Codigo);
        await AfirmarSinCambiosAsync([f.A1, f.A2, f.A3, f.DeBaja], huellas);
    }

    /// <summary>Sin la marca nada cambia: la misma edición de composición con el mismo nominal exige alcance, y el
    /// error no lleva <c>datos</c>.</summary>
    [Fact]
    public async Task SinLaMarcaUnaComposicionDistintaSigueExigiendoAlcanceYElErrorNoLlevaDatos()
    {
        using var e = await apoyo.PrepararAsync(nameof(SinLaMarcaUnaComposicionDistintaSigueExigiendoAlcanceYElErrorNoLlevaDatos));
        var f = await SembrarAsync(e, permiteDesglosarCosto: false);
        var huellas = await HuellasAsync(f.A1, f.A2, f.A3);

        var respuesta = await PutArticuloAsync(
            e.Admin, f.A1, ConComposicion(EdicionIgualA(await apoyo.LeerAsync(f.A1)), 45m, 0m));

        Assert.Equal(HttpStatusCode.Conflict, respuesta.StatusCode);
        var problema = await ProblemaJsonAsync(respuesta);
        Assert.Equal("alcance_requerido", problema.GetProperty("codigo").GetString());
        Assert.False(problema.TryGetProperty("datos", out _));
        await AfirmarSinCambiosAsync([f.A1, f.A2, f.A3], huellas);
    }

    // =================================================================================================
    // Alta dentro de la familia
    // =================================================================================================

    /// <summary>La familia es de las composiciones distintas: el artículo que entra trae la suya y hereda el nominal.</summary>
    [Fact]
    public async Task UnAltaConOtraComposicionYElMismoNominalEntraConSuComposicionYElNominalDeLaFamilia()
    {
        using var e = await apoyo.PrepararAsync(nameof(UnAltaConOtraComposicionYElMismoNominalEntraConSuComposicionYElNominalDeLaFamilia));
        var f = await SembrarAsync(e);

        var respuesta = await PostArticuloAsync(
            e.Admin, Alta(ValoresBase(e) with { CostoLista = 90m, DescuentoProveedor = 50m, ImpuestoInterno = 2m }, f.Familia));

        Assert.Equal(HttpStatusCode.Created, respuesta.StatusCode);
        var creado = (await respuesta.Content.ReadFromJsonAsync<ArticuloListado>(OpcionesJson))!;
        var guardado = await apoyo.LeerAsync(creado.Id);
        Assert.Equal(f.Familia, guardado.IdFamilia);
        Assert.Equal(90m, guardado.CostoLista);
        Assert.Equal(50m, guardado.DescuentoProveedor);
        Assert.Equal(2m, guardado.ImpuestoInterno);
        Assert.Equal(45m, guardado.CostoNominal);
    }

    /// <summary>El nominal guardado de la familia (el efectivo que dejó una compra) ya no es el que da la composición de
    /// la referencia. Un alta que trae exactamente la composición de la referencia —lo que la web precarga— hereda ese
    /// nominal sin recalcularlo ni compararlo.</summary>
    [Fact]
    public async Task UnAltaConLaComposicionDeLaReferenciaHeredaSuNominalGuardadoAunqueNoCoincidaConElCalculado()
    {
        using var e = await apoyo.PrepararAsync(nameof(UnAltaConLaComposicionDeLaReferenciaHeredaSuNominalGuardadoAunqueNoCoincidaConElCalculado));
        var familia = await apoyo.SembrarFamiliaAsync(e, "Gaseosas", permiteDesglosarCosto: true);
        await apoyo.SembrarArticuloAsync(e, "a1", ValoresBase(e) with { CostoNominal = 99m }, familia);

        var respuesta = await PostArticuloAsync(e.Admin, Alta(ValoresBase(e), familia));

        Assert.Equal(HttpStatusCode.Created, respuesta.StatusCode);
        var guardado = await apoyo.LeerAsync((await respuesta.Content.ReadFromJsonAsync<ArticuloListado>(OpcionesJson))!.Id);
        Assert.Equal(familia, guardado.IdFamilia);
        Assert.Equal(50m, guardado.CostoLista);
        Assert.Equal(99m, guardado.CostoNominal);
    }

    [Fact]
    public async Task UnAltaCuyaComposicionDaOtroNominalDa409CostoNominalDistintoDeFamiliaYNoEscribeNada()
    {
        using var e = await apoyo.PrepararAsync(nameof(UnAltaCuyaComposicionDaOtroNominalDa409CostoNominalDistintoDeFamiliaYNoEscribeNada));
        var f = await SembrarAsync(e);
        var articulosAntes = await ContarArticulosAsync(e.IdTenant);

        var respuesta = await PostArticuloAsync(e.Admin, Alta(ValoresBase(e) with { CostoLista = 60m, DescuentoProveedor = 10m }, f.Familia));

        Assert.Equal(HttpStatusCode.Conflict, respuesta.StatusCode);
        var problema = await ProblemaJsonAsync(respuesta);
        Assert.Equal("costo_nominal_distinto_de_familia", problema.GetProperty("codigo").GetString());
        var datos = problema.GetProperty("datos");
        Assert.Equal(45m, datos.GetProperty("costoNominalDeLaFamilia").GetDecimal());
        Assert.Equal(54m, datos.GetProperty("costoNominalDelPedido").GetDecimal());
        Assert.Equal(articulosAntes, await ContarArticulosAsync(e.IdTenant));
    }

    /// <summary>Sin costo de lista el pedido no trae composición: hereda el nominal como siempre.</summary>
    [Fact]
    public async Task UnAltaSinCostoDeListaHeredaElNominalDeLaFamilia()
    {
        using var e = await apoyo.PrepararAsync(nameof(UnAltaSinCostoDeListaHeredaElNominalDeLaFamilia));
        var f = await SembrarAsync(e);

        var respuesta = await PostArticuloAsync(
            e.Admin, Alta(ValoresBase(e) with { CostoLista = null, DescuentoProveedor = null }, f.Familia));

        Assert.Equal(HttpStatusCode.Created, respuesta.StatusCode);
        var guardado = await apoyo.LeerAsync((await respuesta.Content.ReadFromJsonAsync<ArticuloListado>(OpcionesJson))!.Id);
        Assert.Null(guardado.CostoLista);
        Assert.Equal(45m, guardado.CostoNominal);
    }

    [Fact]
    public async Task UnAltaConOtroCampoCompartidoDistintoSigueDandoFamiliaValoresDistintos()
    {
        using var e = await apoyo.PrepararAsync(nameof(UnAltaConOtroCampoCompartidoDistintoSigueDandoFamiliaValoresDistintos));
        var f = await SembrarAsync(e);
        var articulosAntes = await ContarArticulosAsync(e.IdTenant);

        var respuesta = await PostArticuloAsync(e.Admin, Alta(ValoresBase(e) with { IdArea = e.Areas[1] }, f.Familia));

        Assert.Equal(HttpStatusCode.Conflict, respuesta.StatusCode);
        Assert.Equal("familia_valores_distintos", (await ProblemaAsync(respuesta)).Codigo);
        Assert.Equal(articulosAntes, await ContarArticulosAsync(e.IdTenant));
    }

    [Fact]
    public async Task SinLaMarcaUnAltaConOtraComposicionSigueDandoFamiliaValoresDistintos()
    {
        using var e = await apoyo.PrepararAsync(nameof(SinLaMarcaUnAltaConOtraComposicionSigueDandoFamiliaValoresDistintos));
        var f = await SembrarAsync(e, permiteDesglosarCosto: false);

        var respuesta = await PostArticuloAsync(e.Admin, Alta(OtraComposicionDe45(e), f.Familia));

        Assert.Equal(HttpStatusCode.Conflict, respuesta.StatusCode);
        Assert.Equal("familia_valores_distintos", (await ProblemaAsync(respuesta)).Codigo);
    }

    // =================================================================================================
    // Edición de la familia: encender y apagar la marca
    // =================================================================================================

    [Fact]
    public async Task UnaFamiliaEncendidaYApagadaConLaMismaComposicionPersisteLaMarcaYLaDevuelve()
    {
        using var e = await apoyo.PrepararAsync(nameof(UnaFamiliaEncendidaYApagadaConLaMismaComposicionPersisteLaMarcaYLaDevuelve));
        var familia = await apoyo.SembrarFamiliaAsync(e, "Original");
        await apoyo.SembrarArticuloAsync(e, "m1", ValoresBase(e), familia);
        await apoyo.SembrarArticuloAsync(e, "m2", ValoresBase(e), familia);

        var encendida = (await (await PutFamiliaAsync(e.Admin, familia, "Original", true, true))
            .Content.ReadFromJsonAsync<FamiliaListado>(OpcionesJson))!;
        Assert.True(encendida.PermiteDesglosarCosto);
        Assert.True((await apoyo.LeerFamiliaAsync(familia)).PermiteDesglosarCosto);

        var listado = (await e.Admin.GetFromJsonAsync<List<FamiliaListado>>("/api/familias", OpcionesJson))!;
        Assert.True(listado.Single(l => l.Id == familia).PermiteDesglosarCosto);
        var detalle = (await e.Admin.GetFromJsonAsync<FamiliaDetalle>($"/api/familias/{familia}", OpcionesJson))!;
        Assert.True(detalle.PermiteDesglosarCosto);

        var apagada = (await (await PutFamiliaAsync(e.Admin, familia, "Original", true, false))
            .Content.ReadFromJsonAsync<FamiliaListado>(OpcionesJson))!;
        Assert.False(apagada.PermiteDesglosarCosto);
        Assert.False((await apoyo.LeerFamiliaAsync(familia)).PermiteDesglosarCosto);
    }

    /// <summary>Apagar con composiciones distintas: 409 <c>composicion_distinta</c> y no cambia nada, tampoco el
    /// nombre ni el estado que el mismo pedido traía.</summary>
    [Fact]
    public async Task ApagarLaMarcaConComposicionesDistintasDa409ComposicionDistintaYNoCambiaNada()
    {
        using var e = await apoyo.PrepararAsync(nameof(ApagarLaMarcaConComposicionesDistintasDa409ComposicionDistintaYNoCambiaNada));
        var f = await SembrarAsync(e);
        var antes = await apoyo.LeerFamiliaAsync(f.Familia);
        var huellas = await HuellasAsync(f.A1, f.A2, f.A3, f.DeBaja);

        var respuesta = await PutFamiliaAsync(e.Admin, f.Familia, "Otro nombre", false, false);

        Assert.Equal(HttpStatusCode.Conflict, respuesta.StatusCode);
        Assert.Equal("composicion_distinta", (await ProblemaAsync(respuesta)).Codigo);
        var despues = await apoyo.LeerFamiliaAsync(f.Familia);
        Assert.Equal((antes.Nombre, antes.Activo, antes.PermiteDesglosarCosto, antes.UpdatedAt),
            (despues.Nombre, despues.Activo, despues.PermiteDesglosarCosto, despues.UpdatedAt));
        Assert.True(despues.PermiteDesglosarCosto);
        await AfirmarSinCambiosAsync([f.A1, f.A2, f.A3, f.DeBaja], huellas);
    }

    /// <summary>El miembro dado de baja no cuenta: con los vivos iguales entre sí, apagar se puede aunque el de baja
    /// tenga otra composición.</summary>
    [Fact]
    public async Task ApagarLaMarcaIgnoraALosMiembrosDadosDeBaja()
    {
        using var e = await apoyo.PrepararAsync(nameof(ApagarLaMarcaIgnoraALosMiembrosDadosDeBaja));
        var familia = await apoyo.SembrarFamiliaAsync(e, "Gaseosas", permiteDesglosarCosto: true);
        await apoyo.SembrarArticuloAsync(e, "m1", ValoresBase(e), familia);
        await apoyo.SembrarArticuloAsync(e, "m2", ValoresBase(e), familia);
        await apoyo.SembrarArticuloAsync(e, "de-baja", OtraMasDe45(e), familia, dadoDeBaja: true);

        Assert.Equal(HttpStatusCode.OK, (await PutFamiliaAsync(e.Admin, familia, "Gaseosas", true, false)).StatusCode);
        Assert.False((await apoyo.LeerFamiliaAsync(familia)).PermiteDesglosarCosto);
    }

    /// <summary>Un pedido sin <c>permiteDesglosarCosto</c> conserva la marca guardada: no la apaga en silencio.</summary>
    [Fact]
    public async Task UnaEdicionSinElCampoConservaLaMarca()
    {
        using var e = await apoyo.PrepararAsync(nameof(UnaEdicionSinElCampoConservaLaMarca));
        var f = await SembrarAsync(e);

        var respuesta = await PutFamiliaAsync(e.Admin, f.Familia, "Renombrada", true, permiteDesglosarCosto: null);

        Assert.Equal(HttpStatusCode.OK, respuesta.StatusCode);
        Assert.True((await respuesta.Content.ReadFromJsonAsync<FamiliaListado>(OpcionesJson))!.PermiteDesglosarCosto);
        var guardada = await apoyo.LeerFamiliaAsync(f.Familia);
        Assert.True(guardada.PermiteDesglosarCosto);
        Assert.Equal("Renombrada", guardada.Nombre);
    }

    /// <summary>Apagar la marca es un cambio que depende de la composición de TODOS los miembros: toma el lock de
    /// membresía exclusivo antes que nada, así que no se cruza con la edición de un miembro. Con un exclusivo ajeno
    /// sostenido, apagar queda observado esperando <c>ExclusiveLock</c> sin haber escrito; en cambio la edición que no
    /// toca la marca (campo ausente) no lo toma y termina.</summary>
    [Fact]
    public async Task ApagarLaMarcaEsperaAlLockDeMembresiaExclusivoYUnaEdicionSinLaMarcaNo()
    {
        using var e = await apoyo.PrepararAsync(nameof(ApagarLaMarcaEsperaAlLockDeMembresiaExclusivoYUnaEdicionSinLaMarcaNo));
        var familia = await apoyo.SembrarFamiliaAsync(e, "Gaseosas", permiteDesglosarCosto: true);
        await apoyo.SembrarArticuloAsync(e, "m1", ValoresBase(e), familia);
        await apoyo.SembrarArticuloAsync(e, "m2", ValoresBase(e), familia);

        var (poll, sostenedor, transaccion) = await apoyo.AbrirSostenedorAsync(e.IdTenant);
        await using var _poll = poll;
        await using var _sostenedor = sostenedor;
        await using var _transaccion = transaccion;

        await EjecutarAsync(sostenedor, transaccion, "SELECT pg_advisory_xact_lock($1)", LockDeMembresiaDeFamilias.ClaveDe(e.IdTenant));

        var sinMarca = await PutFamiliaAsync(e.Admin, familia, "Sin tocar la marca", true, permiteDesglosarCosto: null)
            .WaitAsync(EsperaMaxima);
        Assert.Equal(HttpStatusCode.OK, sinMarca.StatusCode);

        var apagar = PutFamiliaAsync(e.Admin, familia, "Sin tocar la marca", true, permiteDesglosarCosto: false);

        var esperando = await EsperarAsync(
            () => EsperandoLaMembresiaAsync(poll, e.IdTenant),
            "Apagar la marca nunca se observó esperando el lock de membresía.");

        Assert.Equal("ExclusiveLock", esperando.Modo);
        Assert.False(apagar.IsCompleted);
        Assert.Equal(0, await TransactionIdsConcedidosAsync(poll, esperando.Pid));
        Assert.True((await apoyo.LeerFamiliaAsync(familia)).PermiteDesglosarCosto);

        await transaccion.CommitAsync();

        Assert.Equal(HttpStatusCode.OK, (await apagar.WaitAsync(EsperaMaxima)).StatusCode);
        Assert.False((await apoyo.LeerFamiliaAsync(familia)).PermiteDesglosarCosto);
    }

    // =================================================================================================
    // Agrupar y previsualizar
    // =================================================================================================

    private sealed record ParaAgrupar(int Referencia, int Destino);

    /// <summary>La referencia es la base (nominal 45); el destino tiene otra composición, otro nominal y otra
    /// categoría.</summary>
    private async Task<ParaAgrupar> SembrarParaAgruparAsync(Entorno e)
    {
        var referencia = await apoyo.SembrarArticuloAsync(e, "ref", ValoresBase(e));
        var destino = await apoyo.SembrarArticuloAsync(
            e, "destino",
            ValoresBase(e) with { CostoLista = 90m, DescuentoProveedor = 0m, CostoNominal = 90m, IdCategoria = e.Categorias[1] });

        return new ParaAgrupar(referencia, destino);
    }

    [Fact]
    public async Task CrearUnaFamiliaConLaMarcaAlineaOnceCamposYElNominalSinTocarLaComposicion()
    {
        using var e = await apoyo.PrepararAsync(nameof(CrearUnaFamiliaConLaMarcaAlineaOnceCamposYElNominalSinTocarLaComposicion));
        var s = await SembrarParaAgruparAsync(e);

        var respuesta = await e.Admin.PostAsJsonAsync(
            "/api/familias",
            new AltaDeFamilia("Nueva", s.Referencia, [s.Destino], PermiteDesglosarCosto: true),
            OpcionesJson);

        Assert.Equal(HttpStatusCode.Created, respuesta.StatusCode);
        var resultado = (await respuesta.Content.ReadFromJsonAsync<ResultadoDeAgrupacion>(OpcionesJson))!;
        Assert.True((await apoyo.LeerFamiliaAsync(resultado.IdFamilia)).PermiteDesglosarCosto);

        var destino = await apoyo.LeerAsync(s.Destino);
        Assert.Equal(resultado.IdFamilia, destino.IdFamilia);
        Assert.Equal(e.Categorias[0], destino.IdCategoria);
        Assert.Equal(45m, destino.CostoNominal);
        Assert.Equal(90m, destino.CostoLista);
        Assert.Equal(0m, destino.DescuentoProveedor);

        var cambios = resultado.Articulos.Single();
        Assert.Contains("id_categoria", cambios.Campos);
        Assert.Contains("costo_nominal", cambios.Campos);
        Assert.DoesNotContain("costo_lista", cambios.Campos);
        Assert.DoesNotContain("descuento_proveedor", cambios.Campos);
        Assert.Equal(90m, cambios.Nuevo.CostoLista);
        Assert.Equal(45m, cambios.Nuevo.CostoNominal);
    }

    [Fact]
    public async Task CrearUnaFamiliaSinLaMarcaAlineaLosDieciseisComoSiempre()
    {
        using var e = await apoyo.PrepararAsync(nameof(CrearUnaFamiliaSinLaMarcaAlineaLosDieciseisComoSiempre));
        var s = await SembrarParaAgruparAsync(e);

        var respuesta = await e.Admin.PostAsJsonAsync(
            "/api/familias", new AltaDeFamilia("Nueva", s.Referencia, [s.Destino]), OpcionesJson);

        Assert.Equal(HttpStatusCode.Created, respuesta.StatusCode);
        var resultado = (await respuesta.Content.ReadFromJsonAsync<ResultadoDeAgrupacion>(OpcionesJson))!;
        Assert.False((await apoyo.LeerFamiliaAsync(resultado.IdFamilia)).PermiteDesglosarCosto);
        Assert.Equal(50m, (await apoyo.LeerAsync(s.Destino)).CostoLista);
        Assert.Contains("costo_lista", resultado.Articulos.Single().Campos);
    }

    [Fact]
    public async Task AgregarArticulosAUnaFamiliaConLaMarcaNoTocaLaComposicionDeLosQueEntran()
    {
        using var e = await apoyo.PrepararAsync(nameof(AgregarArticulosAUnaFamiliaConLaMarcaNoTocaLaComposicionDeLosQueEntran));
        var f = await SembrarAsync(e);
        var destino = await apoyo.SembrarArticuloAsync(
            e, "destino",
            ValoresBase(e) with { CostoLista = 90m, DescuentoProveedor = 0m, CostoNominal = 90m, IdCategoria = e.Categorias[1] });

        var respuesta = await e.Admin.PostAsJsonAsync(
            $"/api/familias/{f.Familia}/articulos", new AgregadoDeArticulos([destino]), OpcionesJson);

        Assert.Equal(HttpStatusCode.OK, respuesta.StatusCode);
        var despues = await apoyo.LeerAsync(destino);
        Assert.Equal(f.Familia, despues.IdFamilia);
        Assert.Equal(45m, despues.CostoNominal);
        Assert.Equal(e.Categorias[0], despues.IdCategoria);
        Assert.Equal(90m, despues.CostoLista);
        Assert.Equal(0m, despues.DescuentoProveedor);
    }

    [Fact]
    public async Task LaPrevisualizacionNoComparaLaComposicionSiLaFamiliaDeLaReferenciaTieneLaMarca()
    {
        using var e = await apoyo.PrepararAsync(nameof(LaPrevisualizacionNoComparaLaComposicionSiLaFamiliaDeLaReferenciaTieneLaMarca));
        var f = await SembrarAsync(e);
        var destino = await apoyo.SembrarArticuloAsync(
            e, "destino", ValoresBase(e) with { CostoLista = 90m, DescuentoProveedor = 0m, CostoNominal = 90m });

        var respuesta = await e.Admin.PostAsJsonAsync(
            "/api/familias/previsualizacion", new SolicitudDePrevisualizacion(f.A1, [destino]), OpcionesJson);

        var previsualizacion = (await respuesta.Content.ReadFromJsonAsync<PrevisualizacionDeAgrupacion>(OpcionesJson))!;
        var cambios = previsualizacion.Articulos.Single();
        Assert.Equal(["costo_nominal"], cambios.Campos);
        Assert.Equal(90m, cambios.Nuevo.CostoLista);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task LaPrevisualizacionDeUnaFamiliaNuevaUsaLaMarcaDelPedido(bool permite)
    {
        using var e = await apoyo.PrepararAsync(nameof(LaPrevisualizacionDeUnaFamiliaNuevaUsaLaMarcaDelPedido) + permite);
        var s = await SembrarParaAgruparAsync(e);

        var respuesta = await e.Admin.PostAsJsonAsync(
            "/api/familias/previsualizacion",
            new SolicitudDePrevisualizacion(s.Referencia, [s.Destino], permite),
            OpcionesJson);

        var campos = (await respuesta.Content.ReadFromJsonAsync<PrevisualizacionDeAgrupacion>(OpcionesJson))!
            .Articulos.Single().Campos;
        Assert.Equal(permite, !campos.Contains("costo_lista"));
    }
}
