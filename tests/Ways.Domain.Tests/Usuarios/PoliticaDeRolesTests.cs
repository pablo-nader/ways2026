using Ways.Domain.Common;
using Ways.Domain.Usuarios;

namespace Ways.Domain.Tests.Usuarios;

public class PoliticaDeRolesTests
{
    [Theory]
    [InlineData(RolConocido.Root, true)]
    [InlineData(RolConocido.Admin, true)]
    [InlineData(RolConocido.Supervisor, false)]
    [InlineData(RolConocido.Vendedor, false)]
    public void SoloRootYAdminGestionanUsuarios(RolConocido rol, bool esperado)
    {
        Assert.Equal(esperado, PoliticaDeRoles.PuedeGestionarUsuarios(rol));
    }

    [Fact]
    public void NadiePuedeAsignarElRolRoot()
    {
        foreach (var actor in Enum.GetValues<RolConocido>())
        {
            Assert.Throws<ErrorDominio>(() =>
                PoliticaDeRoles.ValidarPuedeAsignarRol(actor, RolConocido.Root));
        }
    }

    [Fact]
    public void SoloRootPuedeAsignarAdmin()
    {
        PoliticaDeRoles.ValidarPuedeAsignarRol(RolConocido.Root, RolConocido.Admin);

        var error = Assert.Throws<ErrorDominio>(() =>
            PoliticaDeRoles.ValidarPuedeAsignarRol(RolConocido.Admin, RolConocido.Admin));

        Assert.Equal(403, error.EstadoHttp);
    }

    [Theory]
    [InlineData(RolConocido.Supervisor)]
    [InlineData(RolConocido.Vendedor)]
    public void RootYAdminAsignanSupervisorYVendedor(RolConocido destino)
    {
        PoliticaDeRoles.ValidarPuedeAsignarRol(RolConocido.Root, destino);
        PoliticaDeRoles.ValidarPuedeAsignarRol(RolConocido.Admin, destino);
    }

    [Fact]
    public void UnSupervisorNoPuedeAsignarNingunRol()
    {
        Assert.Empty(PoliticaDeRoles.RolesAsignablesPor(RolConocido.Supervisor));

        Assert.Throws<ErrorDominio>(() =>
            PoliticaDeRoles.ValidarPuedeAsignarRol(RolConocido.Supervisor, RolConocido.Vendedor));
    }

    [Fact]
    public void UnAdminNoPuedeTocarUnaCuentaRoot()
    {
        Assert.Throws<ErrorDominio>(() => PoliticaDeRoles.ValidarPuedeIntervenirSobre(
            actor: RolConocido.Admin, actorId: 2,
            rolObjetivo: RolConocido.Root, objetivoId: 1, esBaja: false));
    }

    [Fact]
    public void UnRootSiPuedeEditarOtraCuentaRoot()
    {
        PoliticaDeRoles.ValidarPuedeIntervenirSobre(
            actor: RolConocido.Root, actorId: 1,
            rolObjetivo: RolConocido.Root, objetivoId: 9, esBaja: false);
    }

    [Fact]
    public void LasCuentasRootNoSeEliminan()
    {
        Assert.Throws<ErrorDominio>(() => PoliticaDeRoles.ValidarPuedeIntervenirSobre(
            actor: RolConocido.Root, actorId: 1,
            rolObjetivo: RolConocido.Root, objetivoId: 9, esBaja: true));
    }

    [Fact]
    public void NadiePuedeEliminarseASiMismo()
    {
        var error = Assert.Throws<ErrorDominio>(() => PoliticaDeRoles.ValidarPuedeIntervenirSobre(
            actor: RolConocido.Admin, actorId: 7,
            rolObjetivo: RolConocido.Admin, objetivoId: 7, esBaja: true));

        Assert.Contains("tu propia cuenta", error.Message);
    }
}
