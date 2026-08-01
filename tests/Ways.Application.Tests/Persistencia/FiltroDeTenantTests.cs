using Microsoft.EntityFrameworkCore;
using Ways.Application.Abstracciones;
using Ways.Domain.Organizacion;
using Ways.Infrastructure.Multitenancy;
using Ways.Infrastructure.Persistencia;

namespace Ways.Application.Tests.Persistencia;

/// <summary>
/// Verificación de apply-time exigida por ADR-6 (design.md, stage-1-organization-and-catalogs):
/// confirma que los filtros nombrados "BajaLogica" y "Tenant" se componen en las queries
/// normales y son independientemente ignorables con <c>IgnoreQueryFilters([clave])</c>, sin
/// que ignorar uno arrastre al otro. Usa el proveedor InMemory: alcanza para probar
/// composición de filtros, no hace falta Postgres para esto.
/// </summary>
public class FiltroDeTenantTests
{
    private static WaysDbContext CrearContexto(string nombreDeBase, ITenantActual tenantActual)
    {
        var opciones = new DbContextOptionsBuilder<WaysDbContext>()
            .UseInMemoryDatabase(nombreDeBase)
            .Options;

        return new WaysDbContext(opciones, tenantActual);
    }

    [Fact]
    public async Task ElFiltroDeTenantAislaYEsIndependienteDeLaBajaLogica()
    {
        var nombreDeBase = Guid.NewGuid().ToString();

        await using (var siembra = CrearContexto(nombreDeBase, TenantActualFijo.Plataforma))
        {
            var ahora = DateTimeOffset.UtcNow;
            siembra.Empresas.AddRange(
                new Empresa { IdTenant = 1, RazonSocial = "T1-activa", CreatedAt = ahora, UpdatedAt = ahora },
                new Empresa
                {
                    IdTenant = 1, RazonSocial = "T1-baja",
                    CreatedAt = ahora, UpdatedAt = ahora, DeletedAt = ahora
                },
                new Empresa { IdTenant = 2, RazonSocial = "T2-activa", CreatedAt = ahora, UpdatedAt = ahora });

            await siembra.SaveChangesAsync();
        }

        await using var tenant1 = CrearContexto(nombreDeBase, new TenantActualFijo(ModoDeAcceso.Tenant, 1));

        var visibles = await tenant1.Empresas
            .Select(e => e.RazonSocial).OrderBy(x => x).ToListAsync();
        Assert.Equal(["T1-activa"], visibles);

        // IgnoreQueryFilters(["BajaLogica"]): ve la baja lógica propia, no cruza tenant.
        var conBaja = await tenant1.Empresas
            .IgnoreQueryFilters(["BajaLogica"])
            .Select(e => e.RazonSocial).OrderBy(x => x).ToListAsync();
        Assert.Equal(["T1-activa", "T1-baja"], conBaja);

        // IgnoreQueryFilters(["Tenant"]): cruza de tenant, no ve la baja lógica.
        var cruzandoTenant = await tenant1.Empresas
            .IgnoreQueryFilters(["Tenant"])
            .Select(e => e.RazonSocial).OrderBy(x => x).ToListAsync();
        Assert.Equal(["T1-activa", "T2-activa"], cruzandoTenant);

        await using var plataforma = CrearContexto(nombreDeBase, TenantActualFijo.Plataforma);
        var todasActivas = await plataforma.Empresas
            .Select(e => e.RazonSocial).OrderBy(x => x).ToListAsync();
        Assert.Equal(["T1-activa", "T2-activa"], todasActivas);
    }

    [Fact]
    public async Task SaveChangesEstampaElIdTenantDeLaSesionEnUnaFilaNueva()
    {
        var nombreDeBase = Guid.NewGuid().ToString();
        await using var tenant1 = CrearContexto(nombreDeBase, new TenantActualFijo(ModoDeAcceso.Tenant, 1));

        var ahora = DateTimeOffset.UtcNow;
        var empresa = new Empresa { IdTenant = 999, RazonSocial = "Ignorado", CreatedAt = ahora, UpdatedAt = ahora };
        tenant1.Empresas.Add(empresa);

        await tenant1.SaveChangesAsync();

        // El caso de uso nunca decide el id_tenant: SaveChanges lo pisa con el de la sesión.
        Assert.Equal(1, empresa.IdTenant);
    }

    [Fact]
    public async Task SaveChangesRechazaModificarElIdTenantDeUnaFilaExistente()
    {
        var nombreDeBase = Guid.NewGuid().ToString();
        int idEmpresa;

        await using (var siembra = CrearContexto(nombreDeBase, TenantActualFijo.Plataforma))
        {
            var ahora = DateTimeOffset.UtcNow;
            var empresa = new Empresa { IdTenant = 1, RazonSocial = "T1", CreatedAt = ahora, UpdatedAt = ahora };
            siembra.Empresas.Add(empresa);
            await siembra.SaveChangesAsync();
            idEmpresa = empresa.Id;
        }

        await using var tenant1 = CrearContexto(nombreDeBase, new TenantActualFijo(ModoDeAcceso.Tenant, 1));
        var empresaExistente = await tenant1.Empresas.SingleAsync(e => e.Id == idEmpresa);
        empresaExistente.IdTenant = 2;

        await Assert.ThrowsAsync<InvalidOperationException>(() => tenant1.SaveChangesAsync());
    }

    [Fact]
    public async Task SaveChangesFallaCerradoSinContextoDeTenant()
    {
        var nombreDeBase = Guid.NewGuid().ToString();
        await using var sinContexto = CrearContexto(
            nombreDeBase, new TenantActualFijo(ModoDeAcceso.Ninguno, null));

        var ahora = DateTimeOffset.UtcNow;
        sinContexto.Empresas.Add(new Empresa { RazonSocial = "T?", CreatedAt = ahora, UpdatedAt = ahora });

        await Assert.ThrowsAsync<InvalidOperationException>(() => sinContexto.SaveChangesAsync());
    }

    /// <summary>
    /// El <c>SaveChanges()</c> sync tiene que pasar por el mismo estampado/rechazo que la
    /// variante async — los cuatro puntos de entrada públicos de <c>SaveChanges</c> lo
    /// invocan por igual (ver <see cref="WaysDbContext"/>).
    /// </summary>
    [Fact]
    public void SaveChangesSyncEstampaElIdTenantYRechazaElTamper()
    {
        var nombreDeBase = Guid.NewGuid().ToString();
        int idEmpresa;

        using (var tenant1 = CrearContexto(nombreDeBase, new TenantActualFijo(ModoDeAcceso.Tenant, 1)))
        {
            var ahora = DateTimeOffset.UtcNow;
            var empresa = new Empresa { IdTenant = 999, RazonSocial = "Ignorado", CreatedAt = ahora, UpdatedAt = ahora };
            tenant1.Empresas.Add(empresa);

            tenant1.SaveChanges();

            // El caso de uso nunca decide el id_tenant: SaveChanges lo pisa con el de la sesión.
            Assert.Equal(1, empresa.IdTenant);
            idEmpresa = empresa.Id;
        }

        using var tenant1Otra = CrearContexto(nombreDeBase, new TenantActualFijo(ModoDeAcceso.Tenant, 1));
        var empresaExistente = tenant1Otra.Empresas.Single(e => e.Id == idEmpresa);
        empresaExistente.IdTenant = 2;

        Assert.Throws<InvalidOperationException>(() => tenant1Otra.SaveChanges());
    }
}
