using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Ways.Application.Abstracciones;
using Ways.Application.Usuarios;
using Ways.Domain.Common;
using Ways.Domain.Organizacion;
using Ways.Domain.Usuarios;
using Ways.Infrastructure.Multitenancy;
using Ways.Infrastructure.Persistencia;
using Ways.Infrastructure.Seguridad;

namespace Ways.Application.Tests.Usuarios;

/// <summary>
/// Login por mail (flow B, design.md "Login contract") y la suspensión de tenant
/// (spec usuarios-y-login, "Login and Session Revalidation Respect Tenant State"), sobre
/// el proveedor InMemory: no depende de que exista la migración 2 (gate #2 pendiente),
/// solo del modelo de dominio en memoria.
/// </summary>
public class ServicioDeAutenticacionTests
{
    private static readonly DateTimeOffset Ahora = new(2026, 8, 1, 12, 0, 0, TimeSpan.Zero);
    private const string Password = "una-contraseña-larga";

    private sealed class RelojFijo(DateTimeOffset ahora) : IRelojDelSistema
    {
        public DateTimeOffset Ahora { get; } = ahora;
    }

    private static WaysDbContext CrearContexto(string nombreDeBase, ITenantActual tenantActual) =>
        new(new DbContextOptionsBuilder<WaysDbContext>().UseInMemoryDatabase(nombreDeBase).Options, tenantActual);

    private static ServicioDeAutenticacion CrearServicio(string nombreDeBase, HasheadorPbkdf2 hasheador)
    {
        var db = CrearContexto(nombreDeBase, new TenantActualFijo(ModoDeAcceso.Login, null));
        var dbPlataforma = CrearContexto(nombreDeBase, TenantActualFijo.Plataforma);

        return new ServicioDeAutenticacion(
            db, dbPlataforma, hasheador, new RelojFijo(Ahora), NullLogger<ServicioDeAutenticacion>.Instance);
    }

    /// <summary><c>ServicioDeAutenticacion</c> hace <c>Include(u => u.Rol)</c>: sin una
    /// fila de <see cref="Rol"/> sembrada, el proveedor InMemory descarta la fila entera
    /// al resolver esa navegación requerida (a diferencia de un LEFT JOIN real) — no es un
    /// comportamiento específico de este test, es InMemory tratando una FK no-nullable como
    /// obligatoria incluso sin constraint física.</summary>
    private static async Task SembrarRolAsync(WaysDbContext db, RolConocido rol)
    {
        if (await db.Roles.IgnoreQueryFilters().AnyAsync(r => r.Id == (int)rol))
        {
            return;
        }

        db.Roles.Add(new Rol { Id = (int)rol, Nombre = rol.ToString(), CreatedAt = Ahora, UpdatedAt = Ahora });
        await db.SaveChangesAsync();
    }

    private static async Task<(string NombreDeBase, int IdTenant)> SembrarTenantYUsuarioAsync(
        HasheadorPbkdf2 hasheador, EstadoTenant estadoTenant, string mail)
    {
        var nombreDeBase = Guid.NewGuid().ToString();

        await using var siembra = CrearContexto(nombreDeBase, TenantActualFijo.Plataforma);

        await SembrarRolAsync(siembra, RolConocido.Admin);

        var tenant = new Tenant
        {
            Nombre = "Tenant de prueba",
            Estado = estadoTenant,
            CreatedAt = Ahora,
            UpdatedAt = Ahora
        };
        siembra.Tenants.Add(tenant);
        await siembra.SaveChangesAsync();

        siembra.Usuarios.Add(new Usuario
        {
            IdTenant = tenant.Id,
            NombreUsuario = "admin",
            Mail = mail,
            RolId = (int)RolConocido.Admin,
            PasswordHash = hasheador.Hashear(Password),
            PasswordAlgoritmo = hasheador.Algoritmo,
            PasswordActualizadoEl = Ahora,
            CreatedAt = Ahora,
            UpdatedAt = Ahora
        });
        await siembra.SaveChangesAsync();

        return (nombreDeBase, tenant.Id);
    }

    [Fact]
    public async Task UnUsuarioDeUnTenantActivoInicioSesionConSuMail()
    {
        var hasheador = new HasheadorPbkdf2();
        var (nombreDeBase, idTenant) = await SembrarTenantYUsuarioAsync(
            hasheador, EstadoTenant.Activo, "vendedor@tenant1.com");

        var servicio = CrearServicio(nombreDeBase, hasheador);

        var autenticado = await servicio.IniciarSesionAsync(
            new SolicitudDeLogin("vendedor@tenant1.com", Password));

        Assert.Equal(idTenant, autenticado.IdTenant);
    }

    [Fact]
    public async Task ElRootDePlataformaInicioSesionConSuMailSinChequeoDeTenant()
    {
        var hasheador = new HasheadorPbkdf2();
        var nombreDeBase = Guid.NewGuid().ToString();

        await using (var siembra = CrearContexto(nombreDeBase, TenantActualFijo.Plataforma))
        {
            await SembrarRolAsync(siembra, RolConocido.Root);

            siembra.Usuarios.Add(new Usuario
            {
                IdTenant = null,
                NombreUsuario = "root",
                Mail = "test@test.com",
                RolId = (int)RolConocido.Root,
                PasswordHash = hasheador.Hashear(Password),
                PasswordAlgoritmo = hasheador.Algoritmo,
                PasswordActualizadoEl = Ahora,
                CreatedAt = Ahora,
                UpdatedAt = Ahora
            });
            await siembra.SaveChangesAsync();
        }

        var servicio = CrearServicio(nombreDeBase, hasheador);

        var autenticado = await servicio.IniciarSesionAsync(new SolicitudDeLogin("test@test.com", Password));

        Assert.Null(autenticado.IdTenant);
    }

    [Fact]
    public async Task UnMailDesconocidoYUnaPasswordIncorrectaDevuelvenElMismoError()
    {
        var hasheador = new HasheadorPbkdf2();
        var (nombreDeBase, _) = await SembrarTenantYUsuarioAsync(
            hasheador, EstadoTenant.Activo, "vendedor@tenant1.com");

        var servicioMailDesconocido = CrearServicio(nombreDeBase, hasheador);
        var errorMailDesconocido = await Assert.ThrowsAsync<ErrorDominio>(() =>
            servicioMailDesconocido.IniciarSesionAsync(
                new SolicitudDeLogin("no-existe@tenant1.com", Password)));

        var servicioPasswordIncorrecta = CrearServicio(nombreDeBase, hasheador);
        var errorPasswordIncorrecta = await Assert.ThrowsAsync<ErrorDominio>(() =>
            servicioPasswordIncorrecta.IniciarSesionAsync(
                new SolicitudDeLogin("vendedor@tenant1.com", "password-incorrecta")));

        Assert.Equal(errorMailDesconocido.Codigo, errorPasswordIncorrecta.Codigo);
        Assert.Equal(errorMailDesconocido.Message, errorPasswordIncorrecta.Message);
        Assert.Equal(401, errorMailDesconocido.EstadoHttp);
    }

    [Theory]
    [InlineData(EstadoTenant.Suspendido)]
    [InlineData(EstadoTenant.Baja)]
    public async Task UnTenantSuspendidoOEnBajaBloqueaElLoginConPasswordCorrecta(EstadoTenant estado)
    {
        var hasheador = new HasheadorPbkdf2();
        var (nombreDeBase, _) = await SembrarTenantYUsuarioAsync(hasheador, estado, "vendedor@tenant1.com");

        var servicio = CrearServicio(nombreDeBase, hasheador);

        var error = await Assert.ThrowsAsync<ErrorDominio>(() =>
            servicio.IniciarSesionAsync(new SolicitudDeLogin("vendedor@tenant1.com", Password)));

        Assert.Equal("tenant_suspendido", error.Codigo);
        Assert.Equal(403, error.EstadoHttp);
    }
}
