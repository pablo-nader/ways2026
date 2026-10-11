using System.Net;
using Microsoft.EntityFrameworkCore;
using Ways.Domain.Articulos;
using Ways.Domain.Ofertas;
using Ways.Infrastructure.Multitenancy;
using static Ways.IntegrationTests.ApoyoDeFamilias;

namespace Ways.IntegrationTests;

/// <summary>
/// <c>DELETE /api/familias/{id}</c> frente a las ofertas que apuntan a la familia: disolverla con CUALQUIER oferta
/// que la referencie —viva o dada de baja, la misma política que la baja de un grupo (OD4)— es <c>409
/// familia_en_uso</c> y no escribe nada. Los artículos no cuentan como referencia: los miembros se sueltan en la
/// disolución y los dados de baja conservan su <c>id_familia</c>.
/// </summary>
[Collection("Ways.IntegrationTests secuencial")]
public class FamiliasDisolverConOfertasTests(WaysApiFixture fixture) : IClassFixture<WaysApiFixture>
{
    private readonly ApoyoDeFamilias apoyo = new(fixture);

    private static Task<HttpResponseMessage> DisolverAsync(HttpClient cliente, int idFamilia) =>
        cliente.DeleteAsync($"/api/familias/{idFamilia}");

    private async Task<int> SembrarOfertaDeFamiliaAsync(Entorno e, int idFamilia, bool dadaDeBaja = false)
    {
        await using var db = fixture.CrearContextoDeAplicacion(TenantActualFijo.Plataforma);
        var ahora = DateTimeOffset.UtcNow;

        var oferta = new Oferta
        {
            IdTenant = e.IdTenant, Nombre = "oferta de familia", IdFamilia = idFamilia, Porcentaje = 10m,
            CreatedAt = ahora, UpdatedAt = ahora, DeletedAt = dadaDeBaja ? ahora : null
        };
        db.Ofertas.Add(oferta);
        await db.SaveChangesAsync();

        return oferta.Id;
    }

    private async Task<Familia> FamiliaAsync(int idFamilia) => await apoyo.LeerFamiliaAsync(idFamilia);

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task UnaFamiliaReferenciadaPorUnaOfertaVivaODadaDeBajaDa409YNoEscribeNada(bool ofertaDadaDeBaja)
    {
        using var e = await apoyo.PrepararAsync(
            nameof(UnaFamiliaReferenciadaPorUnaOfertaVivaODadaDeBajaDa409YNoEscribeNada) + ofertaDadaDeBaja);
        var familia = await apoyo.SembrarFamiliaAsync(e, "Remeras");
        var miembro = await apoyo.SembrarArticuloAsync(e, "miembro", ValoresBase(e), familia);
        await SembrarOfertaDeFamiliaAsync(e, familia, ofertaDadaDeBaja);
        var familiaAntes = await FamiliaAsync(familia);
        var miembroAntes = await apoyo.LeerAsync(miembro);

        var respuesta = await DisolverAsync(e.Admin, familia);

        Assert.Equal(HttpStatusCode.Conflict, respuesta.StatusCode);
        var (codigo, mensaje) = await ProblemaAsync(respuesta);
        Assert.Equal("familia_en_uso", codigo);
        Assert.Equal("No se puede dar de baja la familia porque tiene ofertas.", mensaje);

        var familiaDespues = await FamiliaAsync(familia);
        Assert.Null(familiaDespues.DeletedAt);
        Assert.Equal(familiaAntes.UpdatedAt, familiaDespues.UpdatedAt);
        var miembroDespues = await apoyo.LeerAsync(miembro);
        Assert.Equal(familia, miembroDespues.IdFamilia);
        Assert.Equal(miembroAntes.UpdatedAt, miembroDespues.UpdatedAt);
    }

    /// <summary>Lo que la guarda NO cuenta: los artículos (miembros vivos y un dado de baja que conserva su
    /// <c>id_familia</c>) y las ofertas de OTRA familia o de otro tenant. La familia se disuelve igual.</summary>
    [Fact]
    public async Task UnaFamiliaConMiembrosYUnArticuloDadoDeBajaSeDisuelveSiNingunaOfertaLaReferencia()
    {
        using var e = await apoyo.PrepararAsync(
            nameof(UnaFamiliaConMiembrosYUnArticuloDadoDeBajaSeDisuelveSiNingunaOfertaLaReferencia));
        using var otro = await apoyo.PrepararAsync(
            nameof(UnaFamiliaConMiembrosYUnArticuloDadoDeBajaSeDisuelveSiNingunaOfertaLaReferencia) + "-ajeno");
        var familia = await apoyo.SembrarFamiliaAsync(e, "Remeras");
        var otraFamilia = await apoyo.SembrarFamiliaAsync(e, "Pantalones");
        var familiaAjena = await apoyo.SembrarFamiliaAsync(otro, "Ajena");
        var miembro = await apoyo.SembrarArticuloAsync(e, "miembro", ValoresBase(e), familia);
        var deBaja = await apoyo.SembrarArticuloAsync(e, "de-baja", ValoresBase(e), familia, dadoDeBaja: true);
        await SembrarOfertaDeFamiliaAsync(e, otraFamilia);
        await SembrarOfertaDeFamiliaAsync(otro, familiaAjena);

        var respuesta = await DisolverAsync(e.Admin, familia);

        Assert.Equal(HttpStatusCode.NoContent, respuesta.StatusCode);
        Assert.NotNull((await FamiliaAsync(familia)).DeletedAt);
        Assert.Null((await apoyo.LeerAsync(miembro)).IdFamilia);
        Assert.Equal(familia, (await apoyo.LeerAsync(deBaja)).IdFamilia);
        Assert.Null((await FamiliaAsync(otraFamilia)).DeletedAt);
    }

    /// <summary>Una familia que ya no existe responde 404 antes de mirar las ofertas: una oferta que quedó apuntando
    /// a una familia dada de baja no convierte el 404 en un 409.</summary>
    [Fact]
    public async Task UnaFamiliaDadaDeBajaQueUnaOfertaTodaviaReferenciaDa404YNoEl409()
    {
        using var e = await apoyo.PrepararAsync(nameof(UnaFamiliaDadaDeBajaQueUnaOfertaTodaviaReferenciaDa404YNoEl409));
        var disuelta = await apoyo.SembrarFamiliaAsync(e, "Disuelta", dadaDeBaja: true);
        await SembrarOfertaDeFamiliaAsync(e, disuelta);

        var respuesta = await DisolverAsync(e.Admin, disuelta);

        Assert.Equal(HttpStatusCode.NotFound, respuesta.StatusCode);
        Assert.Equal("no_encontrado", (await ProblemaAsync(respuesta)).Codigo);
    }

    /// <summary>Cuando la oferta deja de existir como fila viva y se la da de baja, la guarda sigue contándola (OD4):
    /// la familia solo se puede disolver si NINGUNA fila la referencia, sin importar su estado.</summary>
    [Fact]
    public async Task LaOfertaDadaDeBajaPorLaApiSigueBloqueandoLaDisolucion()
    {
        using var e = await apoyo.PrepararAsync(nameof(LaOfertaDadaDeBajaPorLaApiSigueBloqueandoLaDisolucion));
        var familia = await apoyo.SembrarFamiliaAsync(e, "Remeras");
        var idOferta = await SembrarOfertaDeFamiliaAsync(e, familia);

        Assert.Equal(HttpStatusCode.Conflict, (await DisolverAsync(e.Admin, familia)).StatusCode);

        Assert.Equal(HttpStatusCode.NoContent, (await e.Admin.DeleteAsync($"/api/ofertas/{idOferta}")).StatusCode);

        await using var db = fixture.CrearContextoDeAplicacion(TenantActualFijo.Plataforma);
        Assert.NotNull((await db.Ofertas.IgnoreQueryFilters().SingleAsync(o => o.Id == idOferta)).DeletedAt);

        var respuesta = await DisolverAsync(e.Admin, familia);

        Assert.Equal(HttpStatusCode.Conflict, respuesta.StatusCode);
        Assert.Equal("familia_en_uso", (await ProblemaAsync(respuesta)).Codigo);
        Assert.Null((await FamiliaAsync(familia)).DeletedAt);
    }
}
