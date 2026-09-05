using Microsoft.EntityFrameworkCore;
using Ways.Application.Abstracciones;
using Ways.Application.Auditoria;
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
///
/// Etapa 20 slice 4 — REUBICACIÓN PRESUPUESTADA (task 4.10, Reconciliación 5), con el mismo
/// "transaction-blocked-provider caveat" que documenta <c>ServicioDeOfertasTests</c>: las cuatro
/// escrituras (<c>ActualizarTenantAsync</c>, <c>CambiarEstadoTenantAsync</c>,
/// <c>ActualizarEmpresaAsync</c>, <c>ActualizarPuntoVentaAsync</c>) ahora envuelven la escritura y
/// su relectura en <c>Database.BeginTransactionAsync</c>, y el proveedor InMemory NO soporta
/// transacciones. Las TRES bajas (<c>EliminarTenantAsync</c>/<c>EliminarEmpresaAsync</c>/
/// <c>EliminarPuntoVentaAsync</c>) suman además el SQL crudo del guard de uso, que tampoco corre
/// sobre InMemory.
///
/// Los cinco casos de round-trip que vivían acá se retiran, cada uno contra su equivalente ya
/// existente en <c>OrganizacionTests</c> (Ways.IntegrationTests, Postgres real), y CADA UNO
/// AFIRMA LO MISMO QUE AFIRMABA ACÁ — donde al equivalente le faltaba una aserción de campo, se
/// le agregó, no se debilitó nada:
///
/// <list type="bullet">
/// <item><c>UnAdminEditaSuPropiaEmpresa</c> → <c>UnAdminEditaSuPropiaEmpresaOk</c> (razón social,
/// nombre de fantasía y CUIT).</item>
/// <item><c>UnaPlataformaVeYEditaCualquierEmpresa</c> →
/// <c>PlataformaListaYEditaCualquierEmpresaYPuntoDeVenta</c> (razón social editada).</item>
/// <item><c>UnAdminEditaSuPropioPuntoDeVenta</c> → <c>UnAdminEditaSuPropioPuntoDeVentaOk</c>
/// (nombre, domicilio y WhatsApp), agregado en esta slice porque no tenía equivalente.</item>
/// <item><c>ActualizarElNombreDeUnTenant</c> → <c>PlataformaEditaElNombreDeUnTenant</c>.</item>
/// <item><c>SuspenderYReactivarUnTenantAlternaElEstado</c> →
/// <c>PlataformaSuspendeUnTenantYSuUsuarioPierdeLaSesionEnLaProximaRequest</c> +
/// <c>ReactivarUnTenantSuspendidoPermiteVolverAIniciarSesion</c>.</item>
/// </list>
///
/// Lo que NO se movió y sigue alcanzable acá: todo lo que decide ANTES de abrir la transacción —
/// los 404 de alcance de tenant, el rechazo de razón social vacía, el <c>tenant_dado_de_baja</c> y
/// la idempotencia de suspender lo ya suspendido (que ni siquiera escribe).
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
        CrearServicio(CrearContexto(nombreDeBase, tenantActual), contexto);

    private static ServicioDeOrganizacion CrearServicio(WaysDbContext db, IContextoDeUsuario contexto)
    {
        var reloj = new RelojFijo(Ahora);
        return new ServicioDeOrganizacion(
            db, reloj, contexto, new InspectorDeUso(db), new ServicioDeAuditoria(db, reloj, contexto));
    }

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
    public async Task UnAdminNoPuedeEditarUnPuntoDeVentaDeOtroTenant()
    {
        var nombreDeBase = Guid.NewGuid().ToString();
        var (tenantA, _, _) = await SembrarAsync(nombreDeBase, "TenantA-PV-Editar");
        var (_, _, puntoVentaB) = await SembrarAsync(nombreDeBase, "TenantB-PV-Editar");

        var contexto = new ContextoFijo(RolConocido.Admin, usuarioId: 1, idTenant: tenantA.Id);
        var servicio = CrearServicio(nombreDeBase, new TenantActualFijo(ModoDeAcceso.Tenant, tenantA.Id), contexto);

        var datos = new PuntoVentaEdicion("Intento ajeno", null, null, null, null, null, null);
        var error = await Assert.ThrowsAsync<ErrorDominio>(
            () => servicio.ActualizarPuntoVentaAsync(puntoVentaB.Id, datos));

        Assert.Equal("no_encontrado", error.Codigo);
        Assert.Equal(404, error.EstadoHttp);
    }

    // --- Tenants: edición y transiciones de estado ---

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
        Assert.Equal("razon_social_requerido", error.Codigo);
    }
}
