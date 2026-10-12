using Ways.Application.Abstracciones;
using Ways.Infrastructure.Multitenancy;

namespace Ways.Application.Tests.Multitenancy;

/// <summary>
/// Lo que viaja a Postgres para cada modo de acceso. La propiedad que importa es que el modo público
/// lleva su tenant SOLO en <c>app.tenant_publico</c>: las policies <c>{tabla}_tenant</c> no miran el modo y
/// leen <c>app.tenant_id</c>, así que un tenant ahí abriría todas las tablas del comercio. La prueba contra
/// la base real está en <c>VitrinaAislamientoTests</c>; esta corre sin contenedor.
/// </summary>
public class ModoPublicoTests
{
    [Fact]
    public void ElModoPublicoLlevaSuTenantEnElGucPublicoYDejaVacioElEstandar()
    {
        var contexto = new TenantActualFijo(ModoDeAcceso.Publico, 7);

        var (modo, idTenant, idTenantPublico) = contexto.ComoGucs();

        Assert.Equal("publico", modo);
        Assert.Equal(string.Empty, idTenant);
        Assert.Equal("7", idTenantPublico);
    }

    [Fact]
    public void ElModoTenantLlevaSuTenantEnElGucEstandarYDejaVacioElPublico()
    {
        var (modo, idTenant, idTenantPublico) = new TenantActualFijo(ModoDeAcceso.Tenant, 7).ComoGucs();

        Assert.Equal("tenant", modo);
        Assert.Equal("7", idTenant);
        Assert.Equal(string.Empty, idTenantPublico);
    }

    [Theory]
    [InlineData(ModoDeAcceso.Plataforma, "plataforma")]
    [InlineData(ModoDeAcceso.Login, "login")]
    [InlineData(ModoDeAcceso.Ninguno, "")]
    public void LosModosSinTenantNoLlevanNingunoEnNingunGuc(ModoDeAcceso modoDeAcceso, string guc)
    {
        var (modo, idTenant, idTenantPublico) = new TenantActualFijo(modoDeAcceso, null).ComoGucs();

        Assert.Equal(guc, modo);
        Assert.Equal(string.Empty, idTenant);
        Assert.Equal(string.Empty, idTenantPublico);
    }

    [Fact]
    public void ElModoPublicoExigeUnTenantEnElContextoFijoYEnElDeSesion()
    {
        Assert.Throws<InvalidOperationException>(() => new TenantActualFijo(ModoDeAcceso.Publico, null));
        Assert.Throws<InvalidOperationException>(
            () => new TenantActualDeSesion().Establecer(ModoDeAcceso.Publico, idTenant: null));
    }

    [Fact]
    public void ElContextoDeSesionEnModoPublicoConservaSuTenantYEstablecerOtroModoLoSuelta()
    {
        var sesion = new TenantActualDeSesion();

        sesion.Establecer(ModoDeAcceso.Publico, 12);
        Assert.Equal(ModoDeAcceso.Publico, sesion.Modo);
        Assert.Equal(12, sesion.Id);
        Assert.False(((ITenantActual)sesion).EsPlataforma);

        sesion.Establecer(ModoDeAcceso.Login, idTenant: null);
        Assert.Null(sesion.Id);
    }
}
