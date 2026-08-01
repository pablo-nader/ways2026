using Ways.Domain.Common;
using Ways.Domain.Usuarios;

namespace Ways.Domain.Tests.Usuarios;

public class PoliticaDeRolesTenantTests
{
    [Fact]
    public void UnAdminGestionaUnUsuarioDeSuMismoTenant()
    {
        var actor = new ActorDeGestion(RolConocido.Admin, Id: 2, IdTenant: 1);

        PoliticaDeRoles.ValidarAlcanceDeTenant(actor, idTenantObjetivo: 1);
    }

    [Fact]
    public void UnAdminNoEncuentraUnUsuarioDeOtroTenant()
    {
        var actor = new ActorDeGestion(RolConocido.Admin, Id: 2, IdTenant: 1);

        var error = Assert.Throws<ErrorDominio>(() =>
            PoliticaDeRoles.ValidarAlcanceDeTenant(actor, idTenantObjetivo: 2));

        Assert.Equal(404, error.EstadoHttp);
    }

    [Fact]
    public void UnAdminNoPuedeGestionarUnaCuentaDePlataforma()
    {
        var actor = new ActorDeGestion(RolConocido.Admin, Id: 2, IdTenant: 1);

        var error = Assert.Throws<ErrorDominio>(() =>
            PoliticaDeRoles.ValidarAlcanceDeTenant(actor, idTenantObjetivo: null));

        Assert.Equal(403, error.EstadoHttp);
    }

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(null)]
    public void UnRootDePlataformaOperaSobreCualquierAlcance(int? idTenantObjetivo)
    {
        var actor = new ActorDeGestion(RolConocido.Root, Id: 1, IdTenant: null);

        PoliticaDeRoles.ValidarAlcanceDeTenant(actor, idTenantObjetivo);
    }

    [Fact]
    public void ElActorDeGestionSabeSiEsDePlataforma()
    {
        Assert.True(new ActorDeGestion(RolConocido.Root, 1, null).EsDePlataforma);
        Assert.False(new ActorDeGestion(RolConocido.Admin, 2, 1).EsDePlataforma);
    }

    [Fact]
    public void RolesAsignablesPorEsVacioCuandoElAlcanceEsInconsistente()
    {
        // root sin plataforma o admin marcado como plataforma son combinaciones que la
        // regla "Platform vs Tenant Role Meaning" no permite que existan: sin roles
        // asignables en vez de reventar, para no filtrar el error a una excepción.
        Assert.Empty(PoliticaDeRoles.RolesAsignablesPor(RolConocido.Root, esDePlataforma: false));
        Assert.Empty(PoliticaDeRoles.RolesAsignablesPor(RolConocido.Admin, esDePlataforma: true));
    }

    [Fact]
    public void RolesAsignablesPorCoincideConLaVariantePreviaCuandoElAlcanceEsConsistente()
    {
        Assert.Equal(
            PoliticaDeRoles.RolesAsignablesPor(RolConocido.Root),
            PoliticaDeRoles.RolesAsignablesPor(RolConocido.Root, esDePlataforma: true));

        Assert.Equal(
            PoliticaDeRoles.RolesAsignablesPor(RolConocido.Admin),
            PoliticaDeRoles.RolesAsignablesPor(RolConocido.Admin, esDePlataforma: false));
    }
}
