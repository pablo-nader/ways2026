using System.Net;
using System.Net.Http.Json;
using Microsoft.EntityFrameworkCore;
using Ways.Application.Combos;
using Ways.Infrastructure.Multitenancy;
using static Ways.IntegrationTests.ApoyoDeFamilias;

namespace Ways.IntegrationTests;

/// <summary>
/// La disolución de una familia frente a los combos que la usan como componente: un combo VIVO la bloquea con
/// <c>409 familia_en_uso</c>, un combo dado de baja (o editado para no usarla) no. Y la carrera entre una escritura de
/// combo y la baja de lo que referencia: el combo toma <c>FOR KEY SHARE</c> sobre la fila viva, así que una baja que ya
/// tiene la fila no deja pasar un componente colgante.
/// </summary>
[Collection("Ways.IntegrationTests secuencial")]
public class CombosGuardasTests(WaysApiFixture fixture) : IClassFixture<WaysApiFixture>
{
    private readonly ApoyoDeFamilias apoyo = new(fixture);

    private static Task<HttpResponseMessage> DisolverAsync(HttpClient cliente, int idFamilia) =>
        cliente.DeleteAsync($"/api/familias/{idFamilia}");

    private static async Task<ComboListado> CrearAsync(HttpClient cliente, AltaCombo alta)
    {
        var respuesta = await cliente.PostAsJsonAsync("/api/combos", alta, OpcionesJson);
        Assert.Equal(HttpStatusCode.Created, respuesta.StatusCode);

        return (await respuesta.Content.ReadFromJsonAsync<ComboListado>(OpcionesJson))!;
    }

    private static AltaCombo AltaConFamilia(int idFamilia, int idArticulo) =>
        new(null, "Combo con familia", 5000m, null,
            [new ComponenteDeSolicitudDeCombo(idArticulo, null, 1m), new ComponenteDeSolicitudDeCombo(null, idFamilia, 1m)]);

    [Fact]
    public async Task UnaFamiliaComponenteDeUnComboVivoNoSeDisuelveYNoSeEscribeNada()
    {
        using var e = await apoyo.PrepararAsync(nameof(UnaFamiliaComponenteDeUnComboVivoNoSeDisuelveYNoSeEscribeNada));
        var familia = await apoyo.SembrarFamiliaAsync(e, "Gaseosas");
        var miembro = await apoyo.SembrarArticuloAsync(e, "miembro", ValoresBase(e), familia);
        var pancho = await apoyo.SembrarArticuloAsync(e, "pancho", ValoresBase(e));
        await CrearAsync(e.Admin, AltaConFamilia(familia, pancho));
        var familiaAntes = await apoyo.LeerFamiliaAsync(familia);
        var miembroAntes = await apoyo.LeerAsync(miembro);

        var respuesta = await DisolverAsync(e.Admin, familia);

        Assert.Equal(HttpStatusCode.Conflict, respuesta.StatusCode);
        var (codigo, mensaje) = await ProblemaAsync(respuesta);
        Assert.Equal("familia_en_uso", codigo);
        Assert.Equal("No se puede dar de baja la familia porque tiene combos.", mensaje);
        var familiaDespues = await apoyo.LeerFamiliaAsync(familia);
        Assert.Null(familiaDespues.DeletedAt);
        Assert.Equal(familiaAntes.UpdatedAt, familiaDespues.UpdatedAt);
        var miembroDespues = await apoyo.LeerAsync(miembro);
        Assert.Equal(familia, miembroDespues.IdFamilia);
        Assert.Equal(miembroAntes.UpdatedAt, miembroDespues.UpdatedAt);
    }

    /// <summary>Los componentes de un combo dado de baja se borran con él: no bloquean (a diferencia de las ofertas, que
    /// por OD4 siguen bloqueando aun dadas de baja).</summary>
    [Fact]
    public async Task UnaFamiliaQueSoloUsaUnComboDadoDeBajaSeDisuelve()
    {
        using var e = await apoyo.PrepararAsync(nameof(UnaFamiliaQueSoloUsaUnComboDadoDeBajaSeDisuelve));
        var familia = await apoyo.SembrarFamiliaAsync(e, "Gaseosas");
        var pancho = await apoyo.SembrarArticuloAsync(e, "pancho", ValoresBase(e));
        var combo = await CrearAsync(e.Admin, AltaConFamilia(familia, pancho));
        Assert.Equal(HttpStatusCode.Conflict, (await DisolverAsync(e.Admin, familia)).StatusCode);

        Assert.Equal(HttpStatusCode.NoContent, (await e.Admin.DeleteAsync($"/api/combos/{combo.Id}")).StatusCode);

        Assert.Equal(HttpStatusCode.NoContent, (await DisolverAsync(e.Admin, familia)).StatusCode);
        Assert.NotNull((await apoyo.LeerFamiliaAsync(familia)).DeletedAt);
    }

    [Fact]
    public async Task UnaFamiliaQueUnComboDejoDeUsarPorUnaEdicionSeDisuelve()
    {
        using var e = await apoyo.PrepararAsync(nameof(UnaFamiliaQueUnComboDejoDeUsarPorUnaEdicionSeDisuelve));
        var familia = await apoyo.SembrarFamiliaAsync(e, "Gaseosas");
        var pancho = await apoyo.SembrarArticuloAsync(e, "pancho", ValoresBase(e));
        var coca = await apoyo.SembrarArticuloAsync(e, "coca", ValoresBase(e));
        var combo = await CrearAsync(e.Admin, AltaConFamilia(familia, pancho));

        var edicion = new EdicionCombo(
            null, "Sin familia", 4000m, null,
            [new ComponenteDeSolicitudDeCombo(pancho, null, 1m), new ComponenteDeSolicitudDeCombo(coca, null, 1m)]);
        Assert.Equal(HttpStatusCode.OK, (await e.Admin.PutAsJsonAsync($"/api/combos/{combo.Id}", edicion, OpcionesJson)).StatusCode);

        Assert.Equal(HttpStatusCode.NoContent, (await DisolverAsync(e.Admin, familia)).StatusCode);
    }

    /// <summary>Lo que la guarda NO cuenta: un combo vivo que usa OTRA familia (o solo artículos) y el combo de otro
    /// tenant.</summary>
    [Fact]
    public async Task LosCombosQueNoUsanLaFamiliaNoLaBloquean()
    {
        using var e = await apoyo.PrepararAsync(nameof(LosCombosQueNoUsanLaFamiliaNoLaBloquean));
        using var otro = await apoyo.PrepararAsync(nameof(LosCombosQueNoUsanLaFamiliaNoLaBloquean) + "-ajeno");
        var familia = await apoyo.SembrarFamiliaAsync(e, "Libre");
        var otraFamilia = await apoyo.SembrarFamiliaAsync(e, "Usada");
        var familiaAjena = await apoyo.SembrarFamiliaAsync(otro, "Ajena");
        var pancho = await apoyo.SembrarArticuloAsync(e, "pancho", ValoresBase(e));
        var panchoAjeno = await apoyo.SembrarArticuloAsync(otro, "pancho", ValoresBase(otro));
        await CrearAsync(e.Admin, AltaConFamilia(otraFamilia, pancho));
        await CrearAsync(otro.Admin, AltaConFamilia(familiaAjena, panchoAjeno));

        Assert.Equal(HttpStatusCode.NoContent, (await DisolverAsync(e.Admin, familia)).StatusCode);
    }

    /// <summary>La baja de la fila gana la carrera: ya tiene la fila con <c>FOR UPDATE</c> (como la disolución y la baja
    /// de un artículo) cuando llega el alta del combo. El alta espera, ve la fila ya dada de baja al comitear la otra
    /// transacción y responde 400, en vez de comitear un componente que apunta a una fila de baja. Con un pre-chequeo
    /// <c>AnyAsync</c> sin lock el alta pasaba (veía la fila viva) y su FK esperaba y después aprobaba.</summary>
    [Theory]
    [InlineData("articulo")]
    [InlineData("familia")]
    public async Task UnAltaDeComboQueEsperaUnaBajaEnCursoDeSuComponenteRecibe400(string tipo)
    {
        using var e = await apoyo.PrepararAsync(nameof(UnAltaDeComboQueEsperaUnaBajaEnCursoDeSuComponenteRecibe400) + tipo);
        var sano = await apoyo.SembrarArticuloAsync(e, "sano", ValoresBase(e));
        var (tabla, columna, id) = tipo == "articulo"
            ? ("articulos", "id_articulo", await apoyo.SembrarArticuloAsync(e, "condenado", ValoresBase(e)))
            : ("familias", "id_familia", await apoyo.SembrarFamiliaAsync(e, "condenada"));
        var componente = tipo == "articulo"
            ? new ComponenteDeSolicitudDeCombo(id, null, 1m)
            : new ComponenteDeSolicitudDeCombo(null, id, 1m);

        var (poll, sostenedor, transaccion) = await apoyo.AbrirSostenedorAsync(e.IdTenant);
        await using var _poll = poll;
        await using var _sostenedor = sostenedor;
        await using var _transaccion = transaccion;

        await EjecutarAsync(sostenedor, transaccion, $"SELECT 1 FROM {tabla} WHERE {columna} = $1 FOR UPDATE", id);
        await EjecutarAsync(sostenedor, transaccion, $"UPDATE {tabla} SET deleted_at = now() WHERE {columna} = $1", id);

        var alta = e.Admin.PostAsJsonAsync(
            "/api/combos",
            new AltaCombo(null, "Condenado", 5000m, null, [new ComponenteDeSolicitudDeCombo(sano, null, 1m), componente]),
            OpcionesJson);

        await EsperarAsync(
            () => EsperandoUnaFilaAsync(poll),
            "El alta del combo nunca se observó esperando la fila de su componente: la prueba no prueba la carrera.");
        Assert.False(alta.IsCompleted);

        await transaccion.CommitAsync();

        var respuesta = await alta.WaitAsync(EsperaMaxima);
        Assert.Equal(HttpStatusCode.BadRequest, respuesta.StatusCode);
        Assert.Equal("referencia_invalida", (await ProblemaAsync(respuesta)).Codigo);

        await using var db = fixture.CrearContextoDeAplicacion(TenantActualFijo.Plataforma);
        Assert.False(await db.Combos.IgnoreQueryFilters().AnyAsync(c => c.IdTenant == e.IdTenant));
        Assert.False(await db.CombosComponentes.IgnoreQueryFilters().AnyAsync(c => c.IdTenant == e.IdTenant));
    }

    [Fact]
    public async Task UnArticuloComponenteDeUnComboVivoNoSeDaDeBajaYNoSeEscribeNada()
    {
        using var e = await apoyo.PrepararAsync(nameof(UnArticuloComponenteDeUnComboVivoNoSeDaDeBajaYNoSeEscribeNada));
        var pancho = await apoyo.SembrarArticuloAsync(e, "pancho", ValoresBase(e));
        var coca = await apoyo.SembrarArticuloAsync(e, "coca", ValoresBase(e));
        await CrearAsync(
            e.Admin,
            new AltaCombo(null, "Combo", 5000m, null,
                [new ComponenteDeSolicitudDeCombo(pancho, null, 1m), new ComponenteDeSolicitudDeCombo(coca, null, 1m)]));
        var antes = await apoyo.LeerAsync(pancho);

        var respuesta = await e.Admin.DeleteAsync($"/api/articulos/{pancho}");

        Assert.Equal(HttpStatusCode.Conflict, respuesta.StatusCode);
        var (codigo, mensaje) = await ProblemaAsync(respuesta);
        Assert.Equal("articulo_en_uso", codigo);
        Assert.Equal("No se puede dar de baja el artículo porque tiene combos.", mensaje);
        var despues = await apoyo.LeerAsync(pancho);
        Assert.Null(despues.DeletedAt);
        Assert.Equal(antes.UpdatedAt, despues.UpdatedAt);
    }

    [Fact]
    public async Task UnArticuloQueSoloUsaUnComboDadoDeBajaOUnComboAjenoSeDaDeBaja()
    {
        using var e = await apoyo.PrepararAsync(nameof(UnArticuloQueSoloUsaUnComboDadoDeBajaOUnComboAjenoSeDaDeBaja));
        var pancho = await apoyo.SembrarArticuloAsync(e, "pancho", ValoresBase(e));
        var coca = await apoyo.SembrarArticuloAsync(e, "coca", ValoresBase(e));
        var otro = await apoyo.SembrarArticuloAsync(e, "otro", ValoresBase(e));
        var combo = await CrearAsync(
            e.Admin,
            new AltaCombo(null, "Combo", 5000m, null,
                [new ComponenteDeSolicitudDeCombo(pancho, null, 1m), new ComponenteDeSolicitudDeCombo(coca, null, 1m)]));

        Assert.Equal(HttpStatusCode.NoContent, (await e.Admin.DeleteAsync($"/api/articulos/{otro}")).StatusCode);
        Assert.Equal(HttpStatusCode.Conflict, (await e.Admin.DeleteAsync($"/api/articulos/{pancho}")).StatusCode);

        await e.Admin.DeleteAsync($"/api/combos/{combo.Id}");

        Assert.Equal(HttpStatusCode.NoContent, (await e.Admin.DeleteAsync($"/api/articulos/{pancho}")).StatusCode);
        Assert.NotNull((await apoyo.LeerAsync(pancho)).DeletedAt);
    }
}
