using Microsoft.EntityFrameworkCore;
using Ways.Application.Abstracciones;
using Ways.Application.Organizacion;
using Ways.Domain.Common;
using Ways.Domain.Organizacion;
using Ways.Domain.Usuarios;
using Ways.Infrastructure.Multitenancy;
using Ways.Infrastructure.Persistencia;

namespace Ways.Application.Tests.Organizacion;

/// <summary>
/// <see cref="ServicioDeOrganizacion"/> sobre el proveedor InMemory: alcance de tenant en
/// empresas/puntos de venta (doc 09, ADR-8, mismo patrón que <c>ServicioDeUsuariosTests</c>)
/// y las transiciones de estado de un tenant. Los tres métodos de tenant confían en la
/// policy de la API (<c>Politicas.SoloPlataforma</c>) para la autorización — igual que
/// <c>ServicioDeAprovisionamiento</c> — así que un "admin de tenant no puede suspender" se
/// prueba a nivel HTTP (<c>OrganizacionTests</c>, `Ways.IntegrationTests`), no acá.
/// </summary>
public class ServicioDeOrganizacionTests
{
    private static readonly DateTimeOffset Ahora = new(2026, 8, 2, 12, 0, 0, TimeSpan.Zero);

    private sealed class RelojFijo(DateTimeOffset ahora) : IRelojDelSistema
    {
        public DateTimeOffset Ahora { get; } = ahora;
    }

    private sealed class ContextoFijo(RolConocido rol, int usuarioId, int? idTenant) : IContextoDeUsuario
    {
        public bool EstaAutenticado => true;
        public int UsuarioId { get; } = usuarioId;
        public string NombreUsuario => "actor-de-prueba";
        public RolConocido Rol { get; } = rol;
        public int? IdTenant { get; } = idTenant;
    }

    private static WaysDbContext CrearContexto(string nombreDeBase, ITenantActual tenantActual) =>
        new(new DbContextOptionsBuilder<WaysDbContext>().UseInMemoryDatabase(nombreDeBase).Options, tenantActual);

    private static ServicioDeOrganizacion CrearServicio(
        string nombreDeBase, ITenantActual tenantActual, IContextoDeUsuario contexto) =>
        new(CrearContexto(nombreDeBase, tenantActual), new RelojFijo(Ahora), contexto);

    private static async Task<(Tenant Tenant, Empresa Empresa, PuntoVenta PuntoVenta)> SembrarAsync(
        string nombreDeBase, string nombreTenant, EstadoTenant estado = EstadoTenant.Activo)
    {
        await using var siembra = CrearContexto(nombreDeBase, TenantActualFijo.Plataforma);

        var tenant = new Tenant { Nombre = nombreTenant, Estado = estado, CreatedAt = Ahora, UpdatedAt = Ahora };
        siembra.Tenants.Add(tenant);
        await siembra.SaveChangesAsync();

        var empresa = new Empresa
        {
            IdTenant = tenant.Id,
            RazonSocial = $"{nombreTenant} SRL",
            CreatedAt = Ahora,
            UpdatedAt = Ahora
        };
        siembra.Empresas.Add(empresa);
        await siembra.SaveChangesAsync();

        var puntoVenta = new PuntoVenta
        {
            IdTenant = tenant.Id,
            IdEmpresa = empresa.Id,
            Nombre = $"{nombreTenant} - Local 1",
            CreatedAt = Ahora,
            UpdatedAt = Ahora
        };
        siembra.PuntosVenta.Add(puntoVenta);
        await siembra.SaveChangesAsync();

        return (tenant, empresa, puntoVenta);
    }

    // --- Empresas: alcance de tenant ---

    [Fact]
    public async Task UnAdminEditaSuPropiaEmpresa()
    {
        var nombreDeBase = Guid.NewGuid().ToString();
        var (tenant, empresa, _) = await SembrarAsync(nombreDeBase, nameof(UnAdminEditaSuPropiaEmpresa));

        var contexto = new ContextoFijo(RolConocido.Admin, usuarioId: 1, idTenant: tenant.Id);
        var servicio = CrearServicio(nombreDeBase, new TenantActualFijo(ModoDeAcceso.Tenant, tenant.Id), contexto);

        var datos = new EmpresaEdicion("Nueva razón social SRL", "Nombre fantasía", "20-12345678-9");
        var actualizada = await servicio.ActualizarEmpresaAsync(empresa.Id, datos);

        Assert.Equal("Nueva razón social SRL", actualizada.RazonSocial);
        Assert.Equal("Nombre fantasía", actualizada.NombreFantasia);
        Assert.Equal("20-12345678-9", actualizada.Cuit);
    }

    [Fact]
    public async Task UnAdminNoVeUnaEmpresaDeOtroTenant()
    {
        var nombreDeBase = Guid.NewGuid().ToString();
        var (tenantA, _, _) = await SembrarAsync(nombreDeBase, "TenantA");
        var (_, empresaB, _) = await SembrarAsync(nombreDeBase, "TenantB");

        var contexto = new ContextoFijo(RolConocido.Admin, usuarioId: 1, idTenant: tenantA.Id);
        var servicio = CrearServicio(nombreDeBase, new TenantActualFijo(ModoDeAcceso.Tenant, tenantA.Id), contexto);

        var error = await Assert.ThrowsAsync<ErrorDominio>(() => servicio.ObtenerEmpresaAsync(empresaB.Id));

        Assert.Equal("no_encontrado", error.Codigo);
        Assert.Equal(404, error.EstadoHttp);
    }

    [Fact]
    public async Task UnAdminNoPuedeEditarUnaEmpresaDeOtroTenant()
    {
        var nombreDeBase = Guid.NewGuid().ToString();
        var (tenantA, _, _) = await SembrarAsync(nombreDeBase, "TenantA");
        var (_, empresaB, _) = await SembrarAsync(nombreDeBase, "TenantB");

        var contexto = new ContextoFijo(RolConocido.Admin, usuarioId: 1, idTenant: tenantA.Id);
        var servicio = CrearServicio(nombreDeBase, new TenantActualFijo(ModoDeAcceso.Tenant, tenantA.Id), contexto);

        var datos = new EmpresaEdicion("Intento ajeno SRL", null, null);
        var error = await Assert.ThrowsAsync<ErrorDominio>(
            () => servicio.ActualizarEmpresaAsync(empresaB.Id, datos));

        Assert.Equal("no_encontrado", error.Codigo);
        Assert.Equal(404, error.EstadoHttp);
    }

    [Fact]
    public async Task UnaPlataformaVeYEditaCualquierEmpresa()
    {
        var nombreDeBase = Guid.NewGuid().ToString();
        var (_, empresa, _) = await SembrarAsync(nombreDeBase, nameof(UnaPlataformaVeYEditaCualquierEmpresa));

        var contexto = new ContextoFijo(RolConocido.Root, usuarioId: 1, idTenant: null);
        var servicio = CrearServicio(nombreDeBase, TenantActualFijo.Plataforma, contexto);

        var datos = new EmpresaEdicion("Editada por plataforma SRL", null, null);
        var actualizada = await servicio.ActualizarEmpresaAsync(empresa.Id, datos);

        Assert.Equal("Editada por plataforma SRL", actualizada.RazonSocial);
    }

    [Fact]
    public async Task ListarEmpresasDeUnAdminSoloTraeLasDeSuTenant()
    {
        var nombreDeBase = Guid.NewGuid().ToString();
        var (tenantA, _, _) = await SembrarAsync(nombreDeBase, "TenantA-Lista");
        await SembrarAsync(nombreDeBase, "TenantB-Lista");

        var contexto = new ContextoFijo(RolConocido.Admin, usuarioId: 1, idTenant: tenantA.Id);
        var servicio = CrearServicio(nombreDeBase, new TenantActualFijo(ModoDeAcceso.Tenant, tenantA.Id), contexto);

        var empresas = await servicio.ListarEmpresasAsync();

        Assert.Single(empresas);
        Assert.Equal(tenantA.Id, empresas[0].IdTenant);
    }

    // --- Puntos de venta: mismo patrón que empresas ---

    [Fact]
    public async Task UnAdminNoVeUnPuntoDeVentaDeOtroTenant()
    {
        var nombreDeBase = Guid.NewGuid().ToString();
        var (tenantA, _, _) = await SembrarAsync(nombreDeBase, "TenantA-PV");
        var (_, _, puntoVentaB) = await SembrarAsync(nombreDeBase, "TenantB-PV");

        var contexto = new ContextoFijo(RolConocido.Admin, usuarioId: 1, idTenant: tenantA.Id);
        var servicio = CrearServicio(nombreDeBase, new TenantActualFijo(ModoDeAcceso.Tenant, tenantA.Id), contexto);

        var error = await Assert.ThrowsAsync<ErrorDominio>(
            () => servicio.ObtenerPuntoVentaAsync(puntoVentaB.Id));

        Assert.Equal("no_encontrado", error.Codigo);
        Assert.Equal(404, error.EstadoHttp);
    }

    [Fact]
    public async Task UnAdminEditaSuPropioPuntoDeVenta()
    {
        var nombreDeBase = Guid.NewGuid().ToString();
        var (tenant, _, puntoVenta) = await SembrarAsync(nombreDeBase, nameof(UnAdminEditaSuPropioPuntoDeVenta));

        var contexto = new ContextoFijo(RolConocido.Admin, usuarioId: 1, idTenant: tenant.Id);
        var servicio = CrearServicio(nombreDeBase, new TenantActualFijo(ModoDeAcceso.Tenant, tenant.Id), contexto);

        var datos = new PuntoVentaEdicion(
            "Local renovado", "Av. Siempre Viva 742", "9 a 20", "+54 11 5555-5555", null, null, null);
        var actualizado = await servicio.ActualizarPuntoVentaAsync(puntoVenta.Id, datos);

        Assert.Equal("Local renovado", actualizado.Nombre);
        Assert.Equal("Av. Siempre Viva 742", actualizado.Domicilio);
        Assert.Equal("+54 11 5555-5555", actualizado.Whatsapp);
    }

    // --- Tenants: edición y transiciones de estado ---

    [Fact]
    public async Task ActualizarElNombreDeUnTenant()
    {
        var nombreDeBase = Guid.NewGuid().ToString();
        var (tenant, _, _) = await SembrarAsync(nombreDeBase, nameof(ActualizarElNombreDeUnTenant));

        var contexto = new ContextoFijo(RolConocido.Root, usuarioId: 1, idTenant: null);
        var servicio = CrearServicio(nombreDeBase, TenantActualFijo.Plataforma, contexto);

        var actualizado = await servicio.ActualizarTenantAsync(tenant.Id, new TenantEdicion("Nuevo nombre"));

        Assert.Equal("Nuevo nombre", actualizado.Nombre);
    }

    [Fact]
    public async Task SuspenderYReactivarUnTenantAlternaElEstado()
    {
        var nombreDeBase = Guid.NewGuid().ToString();
        var (tenant, _, _) = await SembrarAsync(nombreDeBase, nameof(SuspenderYReactivarUnTenantAlternaElEstado));

        var contexto = new ContextoFijo(RolConocido.Root, usuarioId: 1, idTenant: null);
        var servicio = CrearServicio(nombreDeBase, TenantActualFijo.Plataforma, contexto);

        var suspendido = await servicio.SuspenderTenantAsync(tenant.Id);
        Assert.Equal(EstadoTenant.Suspendido, suspendido.Estado);

        var reactivado = await servicio.ReactivarTenantAsync(tenant.Id);
        Assert.Equal(EstadoTenant.Activo, reactivado.Estado);
    }

    [Fact]
    public async Task SuspenderUnTenantYaSuspendidoEsIdempotente()
    {
        var nombreDeBase = Guid.NewGuid().ToString();
        var (tenant, _, _) = await SembrarAsync(
            nombreDeBase, nameof(SuspenderUnTenantYaSuspendidoEsIdempotente), EstadoTenant.Suspendido);

        var contexto = new ContextoFijo(RolConocido.Root, usuarioId: 1, idTenant: null);
        var servicio = CrearServicio(nombreDeBase, TenantActualFijo.Plataforma, contexto);

        var resultado = await servicio.SuspenderTenantAsync(tenant.Id);

        Assert.Equal(EstadoTenant.Suspendido, resultado.Estado);
    }

    [Fact]
    public async Task UnTenantDadoDeBajaNoSePuedeSuspenderNiReactivar()
    {
        var nombreDeBase = Guid.NewGuid().ToString();
        var (tenant, _, _) = await SembrarAsync(
            nombreDeBase, nameof(UnTenantDadoDeBajaNoSePuedeSuspenderNiReactivar), EstadoTenant.Baja);

        var contexto = new ContextoFijo(RolConocido.Root, usuarioId: 1, idTenant: null);
        var servicio = CrearServicio(nombreDeBase, TenantActualFijo.Plataforma, contexto);

        var error = await Assert.ThrowsAsync<ErrorDominio>(() => servicio.SuspenderTenantAsync(tenant.Id));

        Assert.Equal("tenant_dado_de_baja", error.Codigo);
        Assert.Equal(409, error.EstadoHttp);
    }

    [Fact]
    public async Task ActualizarUnaEmpresaConRazonSocialVaciaEsRechazada()
    {
        var nombreDeBase = Guid.NewGuid().ToString();
        var (tenant, empresa, _) = await SembrarAsync(
            nombreDeBase, nameof(ActualizarUnaEmpresaConRazonSocialVaciaEsRechazada));

        var contexto = new ContextoFijo(RolConocido.Admin, usuarioId: 1, idTenant: tenant.Id);
        var servicio = CrearServicio(nombreDeBase, new TenantActualFijo(ModoDeAcceso.Tenant, tenant.Id), contexto);

        var error = await Assert.ThrowsAsync<ErrorDominio>(
            () => servicio.ActualizarEmpresaAsync(empresa.Id, new EmpresaEdicion("   ", null, null)));

        Assert.Equal(400, error.EstadoHttp);
    }
}
