using System.Net;
using System.Net.Http.Json;
using Microsoft.EntityFrameworkCore;
using Npgsql;
using Ways.Application.Usuarios;
using Ways.Domain.Organizacion;
using Ways.Domain.Usuarios;
using Ways.Infrastructure.Multitenancy;
using Ways.Infrastructure.Seguridad;

namespace Ways.IntegrationTests;

/// <summary>
/// Slice 2 (tasks.md § "usuarios retrofit + suspension + mail login"): login por mail,
/// suspensión de tenant, y la prueba <c>NULLS NOT DISTINCT</c> del índice
/// <c>ux_usuarios_usuario</c> (design.md, ADR-7).
///
/// Todas las pruebas de esta clase dependen de la migración 2 (<c>UsuariosMultiTenant</c>:
/// <c>usuarios.id_tenant</c>, el índice rearmado, las policies de <c>usuarios</c>), que
/// todavía no existe — está detrás de la DB CHANGE GATE #2 (<c>CLAUDE.md</c>). Quedan
/// escritas y listas para correr, marcadas <see cref="FactAttribute.Skip"/> hasta que la
/// migración se genere y apruebe; no se implementa un doble/mock de la migración porque eso
/// probaría el doble, no RLS real (mismo criterio que ADR-17).
/// </summary>
public class UsuariosYLoginTests(WaysApiFixture fixture) : IClassFixture<WaysApiFixture>
{
    private const string GatePendiente =
        "Gate #2 pendiente: requiere la migración UsuariosMultiTenant (usuarios.id_tenant, " +
        "índice ux_usuarios_usuario, policies de login) aprobada y generada.";

    private const string Password = "una-contraseña-larga";

    /// <summary>Siembra un tenant y un usuario propio en modo plataforma, con un hash real
    /// (no una API pública) — la API bajo prueba es la de login, no la de alta.</summary>
    private async Task<(int IdTenant, string Mail)> SembrarTenantConUsuarioAsync(
        string nombre, EstadoTenant estadoTenant, RolConocido rol = RolConocido.Admin)
    {
        var hasheador = new HasheadorPbkdf2();
        await using var db = fixture.CrearContextoDeAplicacion(TenantActualFijo.Plataforma);

        var ahora = DateTimeOffset.UtcNow;
        var tenant = new Tenant { Nombre = nombre, Estado = estadoTenant, CreatedAt = ahora, UpdatedAt = ahora };
        db.Tenants.Add(tenant);
        await db.SaveChangesAsync();

        var mail = $"{nombre.ToLowerInvariant()}@ways.test";
        db.Usuarios.Add(new Usuario
        {
            IdTenant = tenant.Id,
            NombreUsuario = "admin",
            Mail = mail,
            RolId = (int)rol,
            PasswordHash = hasheador.Hashear(Password),
            PasswordAlgoritmo = hasheador.Algoritmo,
            PasswordActualizadoEl = ahora,
            CreatedAt = ahora,
            UpdatedAt = ahora
        });
        await db.SaveChangesAsync();

        return (tenant.Id, mail);
    }

    [Fact(Skip = GatePendiente)]
    public async Task UnUsuarioDeTenantIniciaSesionConSuMail()
    {
        var (_, mail) = await SembrarTenantConUsuarioAsync(
            nameof(UnUsuarioDeTenantIniciaSesionConSuMail), EstadoTenant.Activo);

        using var cliente = fixture.CreateClient();
        var respuesta = await cliente.PostAsJsonAsync(
            "/api/auth/login", new SolicitudDeLogin(mail, Password));

        Assert.Equal(HttpStatusCode.OK, respuesta.StatusCode);
        var autenticado = await respuesta.Content.ReadFromJsonAsync<UsuarioAutenticado>();
        Assert.NotNull(autenticado);
        Assert.NotNull(autenticado.IdTenant);
    }

    [Fact(Skip = GatePendiente)]
    public async Task ElRootDePlataformaIniciaSesionConSuMailSeed()
    {
        // El seed de InicializadorDeBaseDeDatos ya crea el root con la semilla por defecto
        // (test@test.com, doc 08) al levantar el host — no hace falta sembrar nada acá.
        using var cliente = fixture.CreateClient();
        var respuesta = await cliente.PostAsJsonAsync(
            "/api/auth/login", new SolicitudDeLogin("test@test.com", "root"));

        Assert.Equal(HttpStatusCode.OK, respuesta.StatusCode);
        var autenticado = await respuesta.Content.ReadFromJsonAsync<UsuarioAutenticado>();
        Assert.NotNull(autenticado);
        Assert.Null(autenticado.IdTenant);
    }

    [Fact(Skip = GatePendiente)]
    public async Task UnMailInexistenteYUnaPasswordIncorrectaDevuelvenElMismoError()
    {
        var (_, mail) = await SembrarTenantConUsuarioAsync(
            nameof(UnMailInexistenteYUnaPasswordIncorrectaDevuelvenElMismoError), EstadoTenant.Activo);

        using var cliente = fixture.CreateClient();

        var respuestaMailInexistente = await cliente.PostAsJsonAsync(
            "/api/auth/login", new SolicitudDeLogin("no-existe@ways.test", Password));
        var respuestaPasswordIncorrecta = await cliente.PostAsJsonAsync(
            "/api/auth/login", new SolicitudDeLogin(mail, "password-incorrecta"));

        Assert.Equal(HttpStatusCode.Unauthorized, respuestaMailInexistente.StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, respuestaPasswordIncorrecta.StatusCode);

        var cuerpoMailInexistente = await respuestaMailInexistente.Content.ReadAsStringAsync();
        var cuerpoPasswordIncorrecta = await respuestaPasswordIncorrecta.Content.ReadAsStringAsync();
        Assert.Equal(cuerpoMailInexistente, cuerpoPasswordIncorrecta);
    }

    [Fact(Skip = GatePendiente)]
    public async Task UnTenantSuspendidoBloqueaElLoginDeSuUsuario()
    {
        var (_, mail) = await SembrarTenantConUsuarioAsync(
            nameof(UnTenantSuspendidoBloqueaElLoginDeSuUsuario), EstadoTenant.Suspendido);

        using var cliente = fixture.CreateClient();
        var respuesta = await cliente.PostAsJsonAsync("/api/auth/login", new SolicitudDeLogin(mail, Password));

        Assert.Equal(HttpStatusCode.Forbidden, respuesta.StatusCode);
    }

    [Fact(Skip = GatePendiente)]
    public async Task SuspenderElTenantCortaLaSesionActivaEnLaProximaRequest()
    {
        var (idTenant, mail) = await SembrarTenantConUsuarioAsync(
            nameof(SuspenderElTenantCortaLaSesionActivaEnLaProximaRequest), EstadoTenant.Activo);

        using var cliente = fixture.CreateClient();
        var login = await cliente.PostAsJsonAsync("/api/auth/login", new SolicitudDeLogin(mail, Password));
        Assert.Equal(HttpStatusCode.OK, login.StatusCode);

        var previa = await cliente.GetAsync("/api/auth/me");
        Assert.Equal(HttpStatusCode.OK, previa.StatusCode);

        await using (var db = fixture.CrearContextoDeAplicacion(TenantActualFijo.Plataforma))
        {
            var tenant = await db.Tenants.SingleAsync(t => t.Id == idTenant);
            tenant.Estado = EstadoTenant.Suspendido;
            await db.SaveChangesAsync();
        }

        // Misma cookie, próxima request: OnValidatePrincipal revalida el estado del tenant
        // (ADR-2) y corta la sesión sin esperar a que venza la cookie.
        var luegoDeSuspender = await cliente.GetAsync("/api/auth/me");
        Assert.Equal(HttpStatusCode.Unauthorized, luegoDeSuspender.StatusCode);
    }

    [Fact(Skip = GatePendiente)]
    public async Task DosTenantsPuedenTenerCadaUnoUnUsuarioLlamadoAdminSinColision()
    {
        var hasheador = new HasheadorPbkdf2();
        await using var db = fixture.CrearContextoDeAplicacion(TenantActualFijo.Plataforma);

        var ahora = DateTimeOffset.UtcNow;
        var tenantA = new Tenant { Nombre = "TenantA", Estado = EstadoTenant.Activo, CreatedAt = ahora, UpdatedAt = ahora };
        var tenantB = new Tenant { Nombre = "TenantB", Estado = EstadoTenant.Activo, CreatedAt = ahora, UpdatedAt = ahora };
        db.Tenants.AddRange(tenantA, tenantB);
        await db.SaveChangesAsync();

        db.Usuarios.AddRange(
            new Usuario
            {
                IdTenant = tenantA.Id, NombreUsuario = "admin", Mail = "admin-a@ways.test",
                RolId = (int)RolConocido.Admin, PasswordHash = hasheador.Hashear(Password),
                PasswordAlgoritmo = hasheador.Algoritmo, PasswordActualizadoEl = ahora,
                CreatedAt = ahora, UpdatedAt = ahora
            },
            new Usuario
            {
                IdTenant = tenantB.Id, NombreUsuario = "admin", Mail = "admin-b@ways.test",
                RolId = (int)RolConocido.Admin, PasswordHash = hasheador.Hashear(Password),
                PasswordAlgoritmo = hasheador.Algoritmo, PasswordActualizadoEl = ahora,
                CreatedAt = ahora, UpdatedAt = ahora
            });

        // No debe lanzar: los dos "admin" conviven porque ux_usuarios_usuario incluye
        // id_tenant (design.md, ADR-7).
        await db.SaveChangesAsync();

        var total = await db.Usuarios.CountAsync(u => u.NombreUsuario == "admin");
        Assert.Equal(2, total);
    }

    [Fact(Skip = GatePendiente)]
    public async Task UnSegundoUsuarioDePlataformaConElMismoNombreEsRechazado()
    {
        await using var cruda = await fixture.AbrirConexionCrudaAsync("plataforma", null);

        // root (id_tenant NULL, sembrado al levantar el host) ya ocupa "root" en el grupo
        // de plataforma. NULLS NOT DISTINCT (design.md ADR-7) hace que un segundo NULL
        // choque en vez de convivir — la prueba de que el mecanismo elegido funciona.
        await using var comando = cruda.CreateCommand();
        comando.CommandText =
            "INSERT INTO usuarios (usuario, mail, id_rol, password_hash, password_algoritmo, " +
            "password_actualizado_el, created_at, updated_at) " +
            "VALUES ('root', 'otro-root@ways.test', $1, 'x', 'x', now(), now(), now())";
        comando.Parameters.Add(new NpgsqlParameter { Value = (int)RolConocido.Root });

        var excepcion = await Assert.ThrowsAsync<PostgresException>(() => comando.ExecuteNonQueryAsync());
        Assert.Equal("23505", excepcion.SqlState); // unique_violation
    }
}
