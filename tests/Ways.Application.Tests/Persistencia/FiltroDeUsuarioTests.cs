using Microsoft.EntityFrameworkCore;
using Ways.Application.Abstracciones;
using Ways.Domain.Usuarios;
using Ways.Infrastructure.Multitenancy;
using Ways.Infrastructure.Persistencia;

namespace Ways.Application.Tests.Persistencia;

/// <summary>
/// Verificación de apply-time del filtro "Tenant" escrito a mano para <see cref="Usuario"/>
/// (design.md, stage-1-organization-and-catalogs, ADR-6 / ADR-7 / "Login contract"):
/// a diferencia de <see cref="Ways.Domain.Common.EntidadTenant"/>, <c>Usuario.IdTenant</c>
/// es nullable (NULL = plataforma), y el filtro tiene una tercera rama que no existe en
/// ningún otro caso: modo <see cref="ModoDeAcceso.Login"/> ve todas las cuentas, de
/// cualquier tenant, porque el login busca por <c>mail</c> antes de que haya un tenant
/// resuelto. Provider InMemory: alcanza para probar composición de filtros, no hace falta
/// Postgres (y la migración 2 todavía no existe — gate #2 pendiente).
/// </summary>
public class FiltroDeUsuarioTests
{
    private static WaysDbContext CrearContexto(string nombreDeBase, ITenantActual tenantActual)
    {
        var opciones = new DbContextOptionsBuilder<WaysDbContext>()
            .UseInMemoryDatabase(nombreDeBase)
            .Options;

        return new WaysDbContext(opciones, tenantActual);
    }

    private static Usuario Nuevo(int? idTenant, string usuario, string mail) => new()
    {
        IdTenant = idTenant,
        NombreUsuario = usuario,
        Mail = mail,
        RolId = idTenant is null ? (int)RolConocido.Root : (int)RolConocido.Admin,
        PasswordHash = "hash",
        PasswordAlgoritmo = "test",
        CreatedAt = DateTimeOffset.UtcNow,
        UpdatedAt = DateTimeOffset.UtcNow
    };

    private static async Task<string> SembrarAsync()
    {
        var nombreDeBase = Guid.NewGuid().ToString();

        await using var siembra = CrearContexto(nombreDeBase, TenantActualFijo.Plataforma);
        siembra.Usuarios.AddRange(
            Nuevo(null, "root", "root@ways.test"),
            Nuevo(1, "admin", "admin1@ways.test"),
            Nuevo(2, "admin", "admin2@ways.test"));

        await siembra.SaveChangesAsync();
        return nombreDeBase;
    }

    [Fact]
    public async Task UnaSesionDeTenantSoloVeSusPropiasCuentas()
    {
        var nombreDeBase = await SembrarAsync();

        await using var tenant1 = CrearContexto(nombreDeBase, new TenantActualFijo(ModoDeAcceso.Tenant, 1));

        var visibles = await tenant1.Usuarios.Select(u => u.Mail).OrderBy(x => x).ToListAsync();

        Assert.Equal(["admin1@ways.test"], visibles);
    }

    [Fact]
    public async Task PlataformaVeTodasLasCuentasIncluidasLasDePlataforma()
    {
        var nombreDeBase = await SembrarAsync();

        await using var plataforma = CrearContexto(nombreDeBase, TenantActualFijo.Plataforma);

        var visibles = await plataforma.Usuarios.Select(u => u.Mail).OrderBy(x => x).ToListAsync();

        Assert.Equal(["admin1@ways.test", "admin2@ways.test", "root@ways.test"], visibles);
    }

    [Fact]
    public async Task ModoLoginVeCualquierCuentaDeCualquierTenant()
    {
        var nombreDeBase = await SembrarAsync();

        // Sin esta rama, ServicioDeAutenticacion nunca encontraría una cuenta de tenant al
        // buscar por mail: el filtro compararía IdTenant contra un TenantActual.Id nulo.
        await using var login = CrearContexto(nombreDeBase, new TenantActualFijo(ModoDeAcceso.Login, null));

        var encontrado = await login.Usuarios.SingleOrDefaultAsync(u => u.Mail == "admin2@ways.test");

        Assert.NotNull(encontrado);
        Assert.Equal(2, encontrado.IdTenant);
    }

    [Fact]
    public async Task SinContextoResueltoNoVeNingunaCuenta()
    {
        var nombreDeBase = await SembrarAsync();

        await using var sinContexto = CrearContexto(nombreDeBase, new TenantActualFijo(ModoDeAcceso.Ninguno, null));

        var visibles = await sinContexto.Usuarios.ToListAsync();

        Assert.Empty(visibles);
    }

    /// <summary>Espejo de <see cref="FiltroDeTenantTests.SaveChangesRechazaModificarElIdTenantDeUnaFilaExistente"/>
    /// para <see cref="Usuario"/>: no hereda de <see cref="Ways.Domain.Common.EntidadTenant"/>,
    /// así que el guard de <c>WaysDbContext.EstamparTenant</c> necesita su propio loop escrito
    /// a mano para atrapar el mismo tamper.</summary>
    [Fact]
    public async Task SaveChangesRechazaModificarElIdTenantDeUnUsuarioExistente()
    {
        var nombreDeBase = Guid.NewGuid().ToString();
        int idUsuario;

        await using (var siembra = CrearContexto(nombreDeBase, TenantActualFijo.Plataforma))
        {
            var usuario = Nuevo(1, "admin", "admin@ways.test");
            siembra.Usuarios.Add(usuario);
            await siembra.SaveChangesAsync();
            idUsuario = usuario.Id;
        }

        await using var tenant1 = CrearContexto(nombreDeBase, new TenantActualFijo(ModoDeAcceso.Tenant, 1));
        var usuarioExistente = await tenant1.Usuarios.SingleAsync(u => u.Id == idUsuario);
        usuarioExistente.IdTenant = 2;

        await Assert.ThrowsAsync<InvalidOperationException>(() => tenant1.SaveChangesAsync());
    }

    [Fact]
    public async Task IgnoreQueryFiltersDeBajaLogicaNoArrastraElDeTenant()
    {
        var nombreDeBase = Guid.NewGuid().ToString();

        await using (var siembra = CrearContexto(nombreDeBase, TenantActualFijo.Plataforma))
        {
            var baja = Nuevo(1, "vendedor-baja", "baja@ways.test");
            baja.DeletedAt = DateTimeOffset.UtcNow;
            siembra.Usuarios.AddRange(Nuevo(1, "admin", "admin1@ways.test"), baja, Nuevo(2, "admin", "admin2@ways.test"));
            await siembra.SaveChangesAsync();
        }

        await using var tenant1 = CrearContexto(nombreDeBase, new TenantActualFijo(ModoDeAcceso.Tenant, 1));

        var conBaja = await tenant1.Usuarios
            .IgnoreQueryFilters(["BajaLogica"])
            .Select(u => u.Mail)
            .OrderBy(x => x)
            .ToListAsync();

        Assert.Equal(["admin1@ways.test", "baja@ways.test"], conBaja);
    }
}
