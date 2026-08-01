using Microsoft.EntityFrameworkCore;
using Ways.Application.Abstracciones;
using Ways.Application.Usuarios;
using Ways.Domain.Common;
using Ways.Domain.Usuarios;
using Ways.Infrastructure.Multitenancy;
using Ways.Infrastructure.Persistencia;
using Ways.Infrastructure.Seguridad;

namespace Ways.Application.Tests.Usuarios;

/// <summary>
/// ABM de usuarios (<see cref="ServicioDeUsuarios"/>) sobre el proveedor InMemory: alcance de
/// tenant (doc 09, ADR-8), consistencia rol/alcance, y las dos unicidades del ABM (`usuario`
/// por tenant, `mail` global). Incluye el caso que motivó el batch de judgment-day: una
/// colisión de mail entre tenants tiene que devolver 409 desde el chequeo previo, no colarse
/// por el filtro de tenant y reventar recién en el <c>SaveChangesAsync</c>. El chequeo de mail
/// corre sobre un <c>dbPlataforma</c> separado del <c>db</c> de sesión — acá comparten
/// InMemory, la separación real (RLS bajo Postgres) se prueba en <c>UsuariosYLoginTests</c>.
/// </summary>
public class ServicioDeUsuariosTests
{
    private static readonly DateTimeOffset Ahora = new(2026, 8, 1, 12, 0, 0, TimeSpan.Zero);
    private const string Password = "una-contraseña-larga";

    private sealed class RelojFijo(DateTimeOffset ahora) : IRelojDelSistema
    {
        public DateTimeOffset Ahora { get; } = ahora;
    }

    /// <summary>Identidad del actor para <see cref="PoliticaDeRoles"/>, independiente del
    /// <see cref="ITenantActual"/> que ve la conexión — igual que en producción, donde las
    /// dos derivan de la misma sesión pero son objetos distintos.</summary>
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

    private static ServicioDeUsuarios CrearServicio(
        string nombreDeBase, ITenantActual tenantActual, IContextoDeUsuario contexto) =>
        new(
            CrearContexto(nombreDeBase, tenantActual),
            CrearContexto(nombreDeBase, TenantActualFijo.Plataforma),
            new HasheadorPbkdf2(),
            new RelojFijo(Ahora),
            contexto);

    private static async Task SembrarRolesAsync(string nombreDeBase)
    {
        await using var siembra = CrearContexto(nombreDeBase, TenantActualFijo.Plataforma);

        foreach (var rol in Enum.GetValues<RolConocido>())
        {
            siembra.Roles.Add(new Rol { Id = (int)rol, Nombre = rol.ToString(), CreatedAt = Ahora, UpdatedAt = Ahora });
        }

        await siembra.SaveChangesAsync();
    }

    private static async Task<Usuario> SembrarUsuarioAsync(
        string nombreDeBase, int? idTenant, string usuario, string mail, RolConocido rol = RolConocido.Vendedor)
    {
        await using var siembra = CrearContexto(nombreDeBase, TenantActualFijo.Plataforma);
        var hasheador = new HasheadorPbkdf2();

        var entidad = new Usuario
        {
            IdTenant = idTenant,
            NombreUsuario = usuario,
            Mail = mail,
            RolId = (int)rol,
            PasswordHash = hasheador.Hashear(Password),
            PasswordAlgoritmo = hasheador.Algoritmo,
            PasswordActualizadoEl = Ahora,
            CreatedAt = Ahora,
            UpdatedAt = Ahora
        };

        siembra.Usuarios.Add(entidad);
        await siembra.SaveChangesAsync();
        return entidad;
    }

    [Fact]
    public async Task UnAdminGestionaUnUsuarioDeSuPropioTenant()
    {
        var nombreDeBase = Guid.NewGuid().ToString();
        await SembrarRolesAsync(nombreDeBase);
        var objetivo = await SembrarUsuarioAsync(nombreDeBase, idTenant: 1, "vendedor", "vendedor@t1.test");

        var contexto = new ContextoFijo(RolConocido.Admin, usuarioId: 999, idTenant: 1);
        var servicio = CrearServicio(nombreDeBase, new TenantActualFijo(ModoDeAcceso.Tenant, 1), contexto);

        var listado = await servicio.ObtenerAsync(objetivo.Id);

        Assert.Equal(objetivo.Id, listado.Id);
        Assert.Equal("vendedor@t1.test", listado.Mail);
    }

    [Fact]
    public async Task UnAdminNoVeUnUsuarioDeOtroTenant()
    {
        var nombreDeBase = Guid.NewGuid().ToString();
        await SembrarRolesAsync(nombreDeBase);
        var objetivo = await SembrarUsuarioAsync(nombreDeBase, idTenant: 2, "vendedor", "vendedor@t2.test");

        var contexto = new ContextoFijo(RolConocido.Admin, usuarioId: 999, idTenant: 1);
        var servicio = CrearServicio(nombreDeBase, new TenantActualFijo(ModoDeAcceso.Tenant, 1), contexto);

        var error = await Assert.ThrowsAsync<ErrorDominio>(() => servicio.ObtenerAsync(objetivo.Id));

        Assert.Equal("no_encontrado", error.Codigo);
        Assert.Equal(404, error.EstadoHttp);
    }

    [Fact]
    public async Task UnAdminNoVeUnaCuentaDePlataforma()
    {
        var nombreDeBase = Guid.NewGuid().ToString();
        await SembrarRolesAsync(nombreDeBase);
        var root = await SembrarUsuarioAsync(nombreDeBase, idTenant: null, "root", "root@ways.test", RolConocido.Root);

        var contexto = new ContextoFijo(RolConocido.Admin, usuarioId: 999, idTenant: 1);
        var servicio = CrearServicio(nombreDeBase, new TenantActualFijo(ModoDeAcceso.Tenant, 1), contexto);

        var error = await Assert.ThrowsAsync<ErrorDominio>(() => servicio.ObtenerAsync(root.Id));

        Assert.Equal("no_encontrado", error.Codigo);
        Assert.Equal(404, error.EstadoHttp);
    }

    /// <summary>Un actor de plataforma (root) tiene que elegir tenant explícito para
    /// cualquier rol que no sea root (<see cref="PoliticaDeRoles.ValidarConsistenciaDeRolYAlcance"/>):
    /// sin <c>IdTenant</c> en <see cref="CrearUsuario"/>, la creación de un vendedor es
    /// inconsistente y se rechaza antes de tocar la base.</summary>
    [Fact]
    public async Task CrearUnVendedorSinTenantDestinoEsRechazadoPorInconsistencia()
    {
        var nombreDeBase = Guid.NewGuid().ToString();
        await SembrarRolesAsync(nombreDeBase);

        var contexto = new ContextoFijo(RolConocido.Root, usuarioId: 1, idTenant: null);
        var servicio = CrearServicio(nombreDeBase, TenantActualFijo.Plataforma, contexto);

        var datos = new CrearUsuario("vendedor", "vendedor@sin-tenant.test", (int)RolConocido.Vendedor, Password);

        var error = await Assert.ThrowsAsync<ErrorDominio>(() => servicio.CrearAsync(datos));

        Assert.Equal("tenant_requerido", error.Codigo);
        Assert.Equal(400, error.EstadoHttp);
    }

    [Fact]
    public async Task DosUsuariosConElMismoNombreEnElMismoTenantEsRechazado()
    {
        var nombreDeBase = Guid.NewGuid().ToString();
        await SembrarRolesAsync(nombreDeBase);
        await SembrarUsuarioAsync(nombreDeBase, idTenant: 1, "vendedor", "existente@t1.test");

        var contexto = new ContextoFijo(RolConocido.Admin, usuarioId: 999, idTenant: 1);
        var servicio = CrearServicio(nombreDeBase, new TenantActualFijo(ModoDeAcceso.Tenant, 1), contexto);

        var datos = new CrearUsuario("vendedor", "nuevo@t1.test", (int)RolConocido.Vendedor, Password);

        var error = await Assert.ThrowsAsync<ErrorDominio>(() => servicio.CrearAsync(datos));

        Assert.Equal("usuario_duplicado", error.Codigo);
        Assert.Equal(409, error.EstadoHttp);
    }

    [Fact]
    public async Task ElMismoNombreDeUsuarioEnDosTenantsDistintosConvive()
    {
        var nombreDeBase = Guid.NewGuid().ToString();
        await SembrarRolesAsync(nombreDeBase);
        await SembrarUsuarioAsync(nombreDeBase, idTenant: 1, "admin", "admin1@ways.test", RolConocido.Admin);

        var contexto = new ContextoFijo(RolConocido.Root, usuarioId: 1, idTenant: null);
        var servicio = CrearServicio(nombreDeBase, TenantActualFijo.Plataforma, contexto);

        var datos = new CrearUsuario(
            "admin", "admin2@ways.test", (int)RolConocido.Admin, Password, IdTenant: 2);

        var creado = await servicio.CrearAsync(datos);

        Assert.Equal("admin", creado.Usuario);
        Assert.Equal("admin2@ways.test", creado.Mail);
    }

    /// <summary>El caso CRITICAL de judgment-day: antes del fix, <c>ExigirDisponibilidadAsync</c>
    /// chequeaba el mail sobre <c>db.Usuarios</c> filtrado por tenant, así que un admin de
    /// tenant B nunca veía la cuenta de tenant A y la colisión pasaba el chequeo — para
    /// reventar recién en el <c>SaveChangesAsync</c> con un 23505 sin traducir. El chequeo de
    /// mail ahora corre contra un contexto de plataforma dedicado (<c>dbPlataforma</c>), así
    /// que la colisión se atrapa acá, como el mismo 409 de negocio que el duplicado dentro de
    /// un mismo tenant — con el proveedor InMemory esto ya alcanzaba antes también (no aplica
    /// RLS), la brecha real solo se veía contra Postgres (ver <c>UsuariosYLoginTests</c>).</summary>
    [Fact]
    public async Task UnMailUsadoEnOtroTenantEsRechazadoConConflicto409()
    {
        var nombreDeBase = Guid.NewGuid().ToString();
        await SembrarRolesAsync(nombreDeBase);
        await SembrarUsuarioAsync(nombreDeBase, idTenant: 1, "vendedor-a", "compartido@ways.test");

        // Admin de tenant 2: su sesión de EF solo ve las filas de tenant 2 en las queries
        // normales — la cuenta de tenant 1 le es invisible salvo que el chequeo de mail
        // ignore el filtro de tenant a propósito.
        var contexto = new ContextoFijo(RolConocido.Admin, usuarioId: 999, idTenant: 2);
        var servicio = CrearServicio(nombreDeBase, new TenantActualFijo(ModoDeAcceso.Tenant, 2), contexto);

        var datos = new CrearUsuario("vendedor-b", "compartido@ways.test", (int)RolConocido.Vendedor, Password);

        var error = await Assert.ThrowsAsync<ErrorDominio>(() => servicio.CrearAsync(datos));

        Assert.Equal("mail_duplicado", error.Codigo);
        Assert.Equal(409, error.EstadoHttp);
    }
}
