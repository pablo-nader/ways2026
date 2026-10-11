using Ways.Domain.Organizacion;

namespace Ways.Domain.Tests.Organizacion;

public class PoliticaDeDominioTests
{
    [Theory]
    [InlineData(false, 7)]
    [InlineData(false, null)]
    [InlineData(true, null)]
    [InlineData(true, 7)]
    public void EnElHostDeLaPlataformaCualquierSesionCorresponde(bool esDePlataforma, int? idTenantDeLaSesion)
    {
        Assert.True(PoliticaDeDominio.SesionCorrespondeAlHost(null, esDePlataforma, idTenantDeLaSesion));
    }

    [Fact]
    public void EnUnHostDeTenantCorrespondeLaSesionDeEseMismoTenant()
    {
        Assert.True(PoliticaDeDominio.SesionCorrespondeAlHost(7, false, 7));
    }

    [Fact]
    public void EnUnHostDeTenantNoCorrespondeLaSesionDeOtroTenant()
    {
        Assert.False(PoliticaDeDominio.SesionCorrespondeAlHost(7, false, 8));
    }

    [Fact]
    public void EnUnHostDeTenantNoCorrespondeUnaSesionSinTenantQueNoEsDePlataforma()
    {
        Assert.False(PoliticaDeDominio.SesionCorrespondeAlHost(7, false, null));
    }

    [Theory]
    [InlineData(null)]
    [InlineData(8)]
    public void EnUnHostDeTenantLaSesionDePlataformaCorrespondeSinImportarSuTenant(int? idTenantDeLaSesion)
    {
        Assert.True(PoliticaDeDominio.SesionCorrespondeAlHost(7, true, idTenantDeLaSesion));
    }
}
