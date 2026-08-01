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

    /// <summary>Cierra la brecha de ADR-3 (documentada en <c>TenantActualDeSesion</c>): loguearse
    /// con una cookie de sesión ya activa (de otro tenant) tiene que reemplazar el contexto por
    /// completo, no mezclarlo con el anterior. <c>AuthEndpoints</c> pone el contexto en modo
    /// <c>ModoDeAcceso.Login</c> ANTES de llamar a <c>ServicioDeAutenticacion</c>, así
    /// que el segundo login nunca debería filtrar `usuarios` por el tenant de la cookie vieja.</summary>
    [Fact]
    public async Task LoguearseConUnaCookieDeOtroTenantYaActivaReemplazaLaSesionPorCompleto()
    {
        var (idTenantA, mailA) = await SembrarTenantConUsuarioAsync(
            nameof(LoguearseConUnaCookieDeOtroTenantYaActivaReemplazaLaSesionPorCompleto) + "A", EstadoTenant.Activo);
        var (idTenantB, mailB) = await SembrarTenantConUsuarioAsync(
            nameof(LoguearseConUnaCookieDeOtroTenantYaActivaReemplazaLaSesionPorCompleto) + "B", EstadoTenant.Activo);

        using var cliente = fixture.CreateClient();

        var loginA = await cliente.PostAsJsonAsync("/api/auth/login", new SolicitudDeLogin(mailA, Password));
        Assert.Equal(HttpStatusCode.OK, loginA.StatusCode);
        var autenticadoA = await loginA.Content.ReadFromJsonAsync<UsuarioAutenticado>();
        Assert.NotNull(autenticadoA);
        Assert.Equal(idTenantA, autenticadoA.IdTenant);

        // Mismo HttpClient (misma cookie de sesión de tenant A) — logueamos otra cuenta encima,
        // sin desloguear antes.
        var loginB = await cliente.PostAsJsonAsync("/api/auth/login", new SolicitudDeLogin(mailB, Password));
        Assert.Equal(HttpStatusCode.OK, loginB.StatusCode);
        var autenticadoB = await loginB.Content.ReadFromJsonAsync<UsuarioAutenticado>();
        Assert.NotNull(autenticadoB);
        Assert.Equal(idTenantB, autenticadoB.IdTenant);

        // La sesión activa ahora es la de B: /me tiene que devolver la cuenta de B, no la de A.
        var me = await cliente.GetAsync("/api/auth/me");
        Assert.Equal(HttpStatusCode.OK, me.StatusCode);
        var actual = await me.Content.ReadFromJsonAsync<UsuarioAutenticado>();
        Assert.NotNull(actual);
        Assert.Equal(autenticadoB.Id, actual.Id);
        Assert.Equal(idTenantB, actual.IdTenant);
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

    /// <summary>Contra Postgres real, a través de la API completa (no del servicio en
    /// memoria): confirma el fix CRITICAL de judgment-day — antes, el chequeo previo de
    /// <c>ExigirDisponibilidadAsync</c> corría sobre <c>db.Usuarios</c> filtrado por tenant, así
    /// que un admin de tenant B nunca veía la colisión con el mail de un usuario de tenant A y
    /// el conflicto recién explotaba en el <c>SaveChangesAsync</c> como un 23505 sin traducir
    /// (500 genérico, y un oráculo de enumeración cross-tenant: 409 en el mismo tenant contra
    /// 500 en otro tenant delataba en qué tenant vivía el mail).</summary>
    [Fact]
    public async Task CrearUnUsuarioConElMailDeOtroTenantDevuelve409NoUnError500()
    {
        var (_, mailAdminA) = await SembrarTenantConUsuarioAsync(
            nameof(CrearUnUsuarioConElMailDeOtroTenantDevuelve409NoUnError500) + "A", EstadoTenant.Activo);
        var (_, mailAdminB) = await SembrarTenantConUsuarioAsync(
            nameof(CrearUnUsuarioConElMailDeOtroTenantDevuelve409NoUnError500) + "B", EstadoTenant.Activo);

        var mailCompartido = $"{nameof(CrearUnUsuarioConElMailDeOtroTenantDevuelve409NoUnError500)}@ways.test";

        using var clienteA = fixture.CreateClient();
        var loginA = await clienteA.PostAsJsonAsync("/api/auth/login", new SolicitudDeLogin(mailAdminA, Password));
        Assert.Equal(HttpStatusCode.OK, loginA.StatusCode);

        var creacionEnA = await clienteA.PostAsJsonAsync("/api/usuarios", new CrearUsuario(
            "vendedor-a", mailCompartido, (int)RolConocido.Vendedor, Password));
        Assert.Equal(HttpStatusCode.Created, creacionEnA.StatusCode);

        using var clienteB = fixture.CreateClient();
        var loginB = await clienteB.PostAsJsonAsync("/api/auth/login", new SolicitudDeLogin(mailAdminB, Password));
        Assert.Equal(HttpStatusCode.OK, loginB.StatusCode);

        var creacionEnB = await clienteB.PostAsJsonAsync("/api/usuarios", new CrearUsuario(
            "vendedor-b", mailCompartido, (int)RolConocido.Vendedor, Password));

        Assert.Equal(HttpStatusCode.Conflict, creacionEnB.StatusCode);
        var problema = await creacionEnB.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("mail_duplicado", problema.GetProperty("codigo").GetString());
    }

    /// <summary>Approved hardening de judgment-day (batch 9, ronda 2): la prueba CRITICAL de
    /// arriba siembra la cuenta colisionante ANTES de crear la segunda, así que después del
    /// fix del gate #4 (el chequeo de mail ahora corre contra un <c>dbPlataforma</c> dedicado,
    /// no <c>IgnoreQueryFilters(["Tenant"])</c>, que RLS igual le hubiese vaciado a un actor de
    /// tenant) esa prueba pasa siempre por el chequeo previo, nunca por el backstop de
    /// <c>ManejadorDeErrores</c>. Esta prueba fuerza la carrera real: dos altas con el mismo
    /// mail disparadas a la vez, cada una desde su propio admin de tenant — las dos preguntas
    /// "¿el mail ya existe?" corren antes de que cualquiera de las dos haya hecho commit, así
    /// que las dos pasan el chequeo previo y el 23505 de <c>ux_usuarios_mail</c> recién aparece
    /// en el <c>SaveChangesAsync</c> de la que pierde la carrera — es el único de los dos
    /// caminos posibles que puede dar 500 si <c>ManejadorDeErrores</c> no lo tradujera.</summary>
    [Fact]
    public async Task DosAltasConcurrentesConElMismoMailDisparanElBackstopDelSaveChanges()
    {
        var (_, mailAdminA) = await SembrarTenantConUsuarioAsync(
            nameof(DosAltasConcurrentesConElMismoMailDisparanElBackstopDelSaveChanges) + "A", EstadoTenant.Activo);
        var (_, mailAdminB) = await SembrarTenantConUsuarioAsync(
            nameof(DosAltasConcurrentesConElMismoMailDisparanElBackstopDelSaveChanges) + "B", EstadoTenant.Activo);

        var mailCompartido = $"{nameof(DosAltasConcurrentesConElMismoMailDisparanElBackstopDelSaveChanges)}@ways.test";

        using var clienteA = fixture.CreateClient();
        var loginA = await clienteA.PostAsJsonAsync("/api/auth/login", new SolicitudDeLogin(mailAdminA, Password));
        Assert.Equal(HttpStatusCode.OK, loginA.StatusCode);

        using var clienteB = fixture.CreateClient();
        var loginB = await clienteB.PostAsJsonAsync("/api/auth/login", new SolicitudDeLogin(mailAdminB, Password));
        Assert.Equal(HttpStatusCode.OK, loginB.StatusCode);

        var tareaA = clienteA.PostAsJsonAsync("/api/usuarios", new CrearUsuario(
            "vendedor-concurrente-a", mailCompartido, (int)RolConocido.Vendedor, Password));
        var tareaB = clienteB.PostAsJsonAsync("/api/usuarios", new CrearUsuario(
            "vendedor-concurrente-b", mailCompartido, (int)RolConocido.Vendedor, Password));

        var respuestas = await Task.WhenAll(tareaA, tareaB);

        var estados = respuestas.Select(r => r.StatusCode).ToArray();

        // Exactamente una gana (201) y la otra choca (409): la garantía real es del unique
        // index de Postgres, no de ninguno de los dos chequeos en memoria — por eso este
        // invariante se cumple sin importar cuál de las dos ganó la carrera.
        Assert.Contains(HttpStatusCode.Created, estados);
        Assert.Contains(HttpStatusCode.Conflict, estados);

        var respuestaConflicto = respuestas.Single(r => r.StatusCode == HttpStatusCode.Conflict);
        var problema = await respuestaConflicto.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("mail_duplicado", problema.GetProperty("codigo").GetString());
    }

    /// <summary>Mismo mecanismo que <see cref="DosAltasConcurrentesConElMismoMailDisparanElBackstopDelSaveChanges"/>,
    /// pero contra el otro índice único: <c>usuario</c> (nombre de cuenta) es único POR TENANT,
    /// no global (<c>ux_usuarios_usuario</c>, doc 09 ADR-7), así que la carrera se arma con dos
    /// altas del MISMO tenant, mismo <c>NombreUsuario</c>, mails distintos (el mail no puede
    /// chocar acá, o el backstop atraparía el 23505 de <c>ux_usuarios_mail</c> en su lugar y la
    /// prueba dejaría de ejercitar la rama que le interesa). El chequeo previo en memoria
    /// (<c>ExigirDisponibilidadAsync</c>) no alcanza a ver la fila de la otra request porque
    /// ninguna de las dos hizo commit todavía, así que el 23505 de <c>ux_usuarios_usuario</c>
    /// recién aparece en el <c>SaveChangesAsync</c> de la que pierde la carrera — es la rama
    /// <c>usuario_duplicado</c> de <c>ManejadorDeErrores</c>, nunca ejercitada por un choque en
    /// memoria porque ese siempre pasa por el chequeo previo, no por el backstop.</summary>
    [Fact]
    public async Task DosAltasConcurrentesConElMismoUsuarioEnElMismoTenantDisparanElBackstopDelSaveChanges()
    {
        var (_, mailAdmin) = await SembrarTenantConUsuarioAsync(
            nameof(DosAltasConcurrentesConElMismoUsuarioEnElMismoTenantDisparanElBackstopDelSaveChanges),
            EstadoTenant.Activo);

        // Nombre corto a propósito: `usuario` tiene un tope de 40 caracteres
        // (ServicioDeUsuarios.Normalizar) y el nombre del método de test no entra.
        const string usuarioCompartido = "vendedor-concurrente-usuario";

        using var cliente = fixture.CreateClient();
        var login = await cliente.PostAsJsonAsync("/api/auth/login", new SolicitudDeLogin(mailAdmin, Password));
        Assert.Equal(HttpStatusCode.OK, login.StatusCode);

        var tareaA = cliente.PostAsJsonAsync("/api/usuarios", new CrearUsuario(
            usuarioCompartido, $"{usuarioCompartido}-a@ways.test", (int)RolConocido.Vendedor, Password));
        var tareaB = cliente.PostAsJsonAsync("/api/usuarios", new CrearUsuario(
            usuarioCompartido, $"{usuarioCompartido}-b@ways.test", (int)RolConocido.Vendedor, Password));

        var respuestas = await Task.WhenAll(tareaA, tareaB);

        var estados = respuestas.Select(r => r.StatusCode).ToArray();

        // Misma garantía que en el caso del mail: exactamente una gana (201) y la otra choca
        // (409), sin importar cuál de las dos ganó la carrera.
        Assert.Contains(HttpStatusCode.Created, estados);
        Assert.Contains(HttpStatusCode.Conflict, estados);

        var respuestaConflicto = respuestas.Single(r => r.StatusCode == HttpStatusCode.Conflict);
        var problema = await respuestaConflicto.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("usuario_duplicado", problema.GetProperty("codigo").GetString());
    }

    /// <summary>Approved hardening de judgment-day (batch 9, ronda 2): prueba HTTP de punta a
    /// punta de lo que <c>ServicioDeUsuarios.BuscarAsync</c> ya cubre a nivel de servicio
    /// (<c>UnAdminNoVeUnUsuarioDeOtroTenant</c> en <c>ServicioDeUsuariosTests</c>) — acá contra
    /// Postgres real y a través del endpoint completo, con las tres capas (EF + RLS +
    /// <c>PoliticaDeRoles.ValidarAlcanceDeTenant</c>) puestas en juego a la vez.</summary>
    [Fact]
    public async Task UnAdminDeUnTenantRecibe404AlConsultarUnUsuarioDeOtroTenant()
    {
        var (_, mailAdminA) = await SembrarTenantConUsuarioAsync(
            nameof(UnAdminDeUnTenantRecibe404AlConsultarUnUsuarioDeOtroTenant) + "A", EstadoTenant.Activo);
        var (_, mailAdminB) = await SembrarTenantConUsuarioAsync(
            nameof(UnAdminDeUnTenantRecibe404AlConsultarUnUsuarioDeOtroTenant) + "B", EstadoTenant.Activo);

        using var clienteB = fixture.CreateClient();
        var loginB = await clienteB.PostAsJsonAsync("/api/auth/login", new SolicitudDeLogin(mailAdminB, Password));
        Assert.Equal(HttpStatusCode.OK, loginB.StatusCode);
        var autenticadoB = await loginB.Content.ReadFromJsonAsync<UsuarioAutenticado>();
        Assert.NotNull(autenticadoB);

        using var clienteA = fixture.CreateClient();
        var loginA = await clienteA.PostAsJsonAsync("/api/auth/login", new SolicitudDeLogin(mailAdminA, Password));
        Assert.Equal(HttpStatusCode.OK, loginA.StatusCode);

        var respuesta = await clienteA.GetAsync($"/api/usuarios/{autenticadoB.Id}");

        Assert.Equal(HttpStatusCode.NotFound, respuesta.StatusCode);
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
