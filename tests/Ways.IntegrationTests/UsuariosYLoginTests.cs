using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
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
/// <c>ux_usuarios_usuario</c> (design.md, ADR-7). Corren contra Postgres real, con la
/// migración <c>UsuariosMultiTenant</c> (gate #2, aprobada 2026-08-01) ya aplicada.
/// </summary>
[Collection("Ways.IntegrationTests secuencial")]
public class UsuariosYLoginTests(WaysApiFixture fixture) : IClassFixture<WaysApiFixture>
{
    private const string Password = "una-contraseña-larga";

    /// <summary>Siembra un tenant y un usuario propio en modo plataforma, con un hash real
    /// (no una API pública) — la API bajo prueba es la de login, no la de alta.
    ///
    /// Arranca el host primero (<see cref="WaysApiFixture.CreateClient"/>, idempotente:
    /// una vez levantado lo reutiliza) — necesario para que <c>InicializadorDeBaseDeDatos</c>
    /// ya haya sembrado los <c>roles</c> antes de insertar un <c>Usuario</c> propio: la FK
    /// <c>fk_usuarios_rol</c> no perdona una cuenta con un rol que todavía no existe.</summary>
    private async Task<(int IdTenant, string Mail)> SembrarTenantConUsuarioAsync(
        string nombre, EstadoTenant estadoTenant, RolConocido rol = RolConocido.Admin)
    {
        using var _ = fixture.CreateClient();

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

    [Fact]
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

    [Fact]
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

    [Fact]
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

        // ProblemDetails trae un `traceId` propio por request — comparar el cuerpo entero
        // como string siempre daría distinto. Lo que tiene que ser idéntico es el mensaje y
        // el código de error (ManejadorDeErrores), no el sobre completo.
        var problemaMailInexistente = await respuestaMailInexistente.Content.ReadFromJsonAsync<JsonElement>();
        var problemaPasswordIncorrecta = await respuestaPasswordIncorrecta.Content.ReadFromJsonAsync<JsonElement>();

        Assert.Equal(
            problemaMailInexistente.GetProperty("title").GetString(),
            problemaPasswordIncorrecta.GetProperty("title").GetString());
        Assert.Equal(
            problemaMailInexistente.GetProperty("codigo").GetString(),
            problemaPasswordIncorrecta.GetProperty("codigo").GetString());
    }

    [Fact]
    public async Task UnTenantSuspendidoBloqueaElLoginDeSuUsuario()
    {
        var (_, mail) = await SembrarTenantConUsuarioAsync(
            nameof(UnTenantSuspendidoBloqueaElLoginDeSuUsuario), EstadoTenant.Suspendido);

        using var cliente = fixture.CreateClient();
        var respuesta = await cliente.PostAsJsonAsync("/api/auth/login", new SolicitudDeLogin(mail, Password));

        Assert.Equal(HttpStatusCode.Forbidden, respuesta.StatusCode);
    }

    [Fact]
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

    [Fact]
    public async Task DosTenantsPuedenTenerCadaUnoUnUsuarioLlamadoAdminSinColision()
    {
        using var _ = fixture.CreateClient(); // arranca el host: siembra los roles primero

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

        // Scopeado a los dos tenants de esta prueba, no un conteo global: la clase comparte
        // un solo Postgres (IClassFixture) entre todos sus métodos, y varios otros también
        // siembran una cuenta "admin" en su propio tenant.
        var enTenantA = await db.Usuarios.AnyAsync(u => u.NombreUsuario == "admin" && u.IdTenant == tenantA.Id);
        var enTenantB = await db.Usuarios.AnyAsync(u => u.NombreUsuario == "admin" && u.IdTenant == tenantB.Id);

        Assert.True(enTenantA);
        Assert.True(enTenantB);
    }

    [Fact]
    public async Task UnSegundoUsuarioDePlataformaConElMismoNombreEsRechazado()
    {
        using var _ = fixture.CreateClient(); // arranca el host: siembra el root primero

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
