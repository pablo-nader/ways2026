using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using Microsoft.EntityFrameworkCore;
using Ways.Application.Dispositivos;
using Ways.Application.Organizacion;
using Ways.Application.Usuarios;
using Ways.Domain.Organizacion;
using Ways.Domain.Usuarios;
using Ways.Infrastructure.Multitenancy;
using Ways.Infrastructure.Seguridad;

namespace Ways.IntegrationTests;

/// <summary>
/// fix/sesion-relee-el-rol: <c>ValidadorDeSesion.EsVigenteAsync</c> releía en cada request si el
/// usuario seguía activo, si el tenant seguía activo y (para sesión de dispositivo) si el
/// dispositivo/PV seguían vigentes — pero NUNCA el rol. <c>ClaimsWays.RolId</c> se fija en el
/// login (<c>AuthEndpoints.ConstruirClaims</c>) y viajaba sin cambios toda la vida de la sesión:
/// con la cookie deslizante de 1h (el refresh reemite las MISMAS claims) o con la sesión de
/// dispositivo de 365 días, degradar el rol de un usuario en la base no tenía ningún efecto
/// hasta el próximo login.
///
/// Este archivo prueba la cláusula puntual "el rol de la claim tiene que coincidir con el rol
/// actual en la base" para los DOS transportes que comparten <c>ValidadorDeSesion</c> — cookie
/// y bearer —, mismo criterio de espejo exacto que <c>SesionBearerDeDispositivoTests</c> frente a
/// <c>DispositivosTests</c> para dispositivo/PV/usuario activo.
///
/// El endpoint bajo prueba es <c>GET /api/usuarios/</c> (<see cref="Politicas.GestionDeUsuarios"/>:
/// Root o Admin): no depende de ningún dato previo (una lista vacía es una respuesta 200 válida)
/// y tanto la policy de ASP.NET Core (<c>RequireClaim</c>) como
/// <c>ServicioDeUsuarios.ExigirPermisoDeGestion</c> leen el MISMO claim <c>ways:id_rol</c> sin
/// tocar la base — ninguna de las dos capas reintroduce el chequeo que falta, así que un 200
/// después de degradar a Vendedor en la base prueba exactamente que la claim vieja sigue viajando
/// sin releerse (y no una confusión con algún otro guard que ya lo cubriera).
/// </summary>
[Collection("Ways.IntegrationTests secuencial")]
public class ValidadorDeSesionRolTests(WaysApiFixture fixture) : IClassFixture<WaysApiFixture>
{
    private const string MailRoot = "test@test.com";
    private const string PasswordRoot = "root";

    /// <summary>Password compartida por las dos cuentas sembradas directo en la base de
    /// este archivo: el admin de tenant de <c>SembrarTenantConAdminAsync</c> y el cajero
    /// de dispositivo de <c>CambiarElRolDelUsuarioCortaUnaSesionBearerYaEmitida</c>. El
    /// nombre describe lo que las dos comparten — loguear con una password conocida para
    /// después comparar la claim ya emitida contra el rol vigente en la base — no a cuál
    /// de las dos pertenece.</summary>
    private const string PasswordClaimsMatchCode = "una-contraseña-de-cajero";

    /// <summary>Siembra un tenant y un admin propio directo en la base, con hash real — mismo
    /// patrón que <c>UsuariosYLoginTests.SembrarTenantConUsuarioAsync</c>, duplicado a propósito
    /// (cada archivo de esta suite trae su propia siembra mínima).</summary>
    private async Task<(int IdTenant, string Mail)> SembrarTenantConAdminAsync(string nombre)
    {
        using var _ = fixture.CreateClient();

        var hasheador = new HasheadorPbkdf2();
        await using var db = fixture.CrearContextoDeAplicacion(TenantActualFijo.Plataforma);

        var ahora = DateTimeOffset.UtcNow;
        var tenant = new Tenant
        {
            Nombre = nombre, Estado = EstadoTenant.Activo, CreatedAt = ahora, UpdatedAt = ahora
        };
        db.Tenants.Add(tenant);
        await db.SaveChangesAsync();

        var mail = $"{nombre.ToLowerInvariant()}@ways.test";
        db.Usuarios.Add(new Usuario
        {
            IdTenant = tenant.Id,
            NombreUsuario = "admin",
            Mail = mail,
            RolId = (int)RolConocido.Admin,
            PasswordHash = hasheador.Hashear(PasswordClaimsMatchCode),
            PasswordAlgoritmo = hasheador.Algoritmo,
            PasswordActualizadoEl = ahora,
            CreatedAt = ahora,
            UpdatedAt = ahora
        });
        await db.SaveChangesAsync();

        return (tenant.Id, mail);
    }

    private async Task CambiarRolAsync(int idTenant, string nombreUsuario, RolConocido rol)
    {
        await using var db = fixture.CrearContextoDeAplicacion(TenantActualFijo.Plataforma);
        var usuario = await db.Usuarios.FirstAsync(u => u.IdTenant == idTenant && u.NombreUsuario == nombreUsuario);
        usuario.RolId = (int)rol;
        await db.SaveChangesAsync();
    }

    /// <summary>Variante de <see cref="CambiarRolAsync"/> para la cuenta root: no tiene
    /// <c>IdTenant</c> (es de plataforma) así que se busca por <c>Mail</c> en vez de
    /// <c>(IdTenant, NombreUsuario)</c>.</summary>
    private async Task CambiarRolDeRootAsync(RolConocido rol)
    {
        await using var db = fixture.CrearContextoDeAplicacion(TenantActualFijo.Plataforma);
        var root = await db.Usuarios.FirstAsync(u => u.Mail == MailRoot);
        root.RolId = (int)rol;
        await db.SaveChangesAsync();
    }

    [Fact]
    public async Task CambiarElRolDelUsuarioCortaUnaSesionDeCookieYaAbierta()
    {
        var (idTenant, mail) = await SembrarTenantConAdminAsync(
            nameof(CambiarElRolDelUsuarioCortaUnaSesionDeCookieYaAbierta));

        using var cliente = fixture.CreateClient();
        var login = await cliente.PostAsJsonAsync("/api/auth/login", new SolicitudDeLogin(mail, PasswordClaimsMatchCode));
        Assert.Equal(HttpStatusCode.OK, login.StatusCode);

        var antes = await cliente.GetAsync("/api/usuarios/");
        Assert.Equal(HttpStatusCode.OK, antes.StatusCode);

        // Degradado a Vendedor SIN pasar por ningún re-login: la claim ways:id_rol de la cookie
        // ya emitida sigue diciendo Admin.
        await CambiarRolAsync(idTenant, "admin", RolConocido.Vendedor);

        // Misma cookie, próxima request: tiene que cortar la sesión (igual que
        // UsuariosYLoginTests.SuspenderElTenantCortaLaSesionActivaEnLaProximaRequest), no
        // simplemente devolver 403 con la policy vieja.
        var despues = await cliente.GetAsync("/api/usuarios/");
        Assert.Equal(HttpStatusCode.Unauthorized, despues.StatusCode);
    }

    private static HttpRequestMessage RequestConBearer(HttpMethod metodo, string ruta, string token)
    {
        var request = new HttpRequestMessage(metodo, ruta);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        return request;
    }

    [Fact]
    public async Task CambiarElRolDelUsuarioCortaUnaSesionBearerYaEmitida()
    {
        using var root = fixture.CreateClient();
        var loginRoot = await root.PostAsJsonAsync(
            "/api/auth/login", new SolicitudDeLogin(MailRoot, PasswordRoot));
        Assert.Equal(HttpStatusCode.OK, loginRoot.StatusCode);

        var nombre = nameof(CambiarElRolDelUsuarioCortaUnaSesionBearerYaEmitida);
        var solicitud = new SolicitudDeAprovisionamiento(
            NombreTenant: nombre,
            RazonSocialEmpresa: $"Empresa {nombre}",
            NombrePuntoVenta: "Local 1",
            MailAdmin: $"{nombre.ToLowerInvariant()}-admin@ways.test",
            Modo: ModoPuntoVenta.Escritorio);

        var alta = await root.PostAsJsonAsync("/api/plataforma/tenants", solicitud);
        Assert.Equal(HttpStatusCode.Created, alta.StatusCode);
        var resultado = (await alta.Content.ReadFromJsonAsync<ResultadoAprovisionamiento>())!;

        using var admin = fixture.CreateClient();
        var loginAdmin = await admin.PostAsJsonAsync(
            "/api/auth/login", new SolicitudDeLogin(solicitud.MailAdmin, resultado.PasswordTemporal));
        Assert.Equal(HttpStatusCode.OK, loginAdmin.StatusCode);

        var altaDispositivo = await admin.PostAsJsonAsync(
            "/api/dispositivos", new AltaDispositivo(resultado.IdPuntoVenta, "Caja 1"));
        Assert.Equal(HttpStatusCode.Created, altaDispositivo.StatusCode);
        var vinculado = (await altaDispositivo.Content.ReadFromJsonAsync<DispositivoVinculado>())!;

        // El cajero de dispositivo se siembra directo en la base como Admin — el único motivo es
        // poder pasar la MISMA policy GestionDeUsuarios que el test de cookie de arriba; el login
        // de dispositivo admite Admin igual que Vendedor/Supervisor
        // (ServicioDeAutenticacion.RolesPermitidosEnPos).
        var hasheador = new HasheadorPbkdf2();
        await using (var db = fixture.CrearContextoDeAplicacion(TenantActualFijo.Plataforma))
        {
            var ahora = DateTimeOffset.UtcNow;
            db.Usuarios.Add(new Usuario
            {
                IdTenant = resultado.IdTenant,
                NombreUsuario = "cajero1",
                Mail = $"cajero1-{resultado.IdTenant}@ways.test",
                RolId = (int)RolConocido.Admin,
                PasswordHash = hasheador.Hashear(PasswordClaimsMatchCode),
                PasswordAlgoritmo = hasheador.Algoritmo,
                PasswordActualizadoEl = ahora,
                CreatedAt = ahora,
                UpdatedAt = ahora
            });
            await db.SaveChangesAsync();
        }

        using var loginDispositivo = new HttpRequestMessage(HttpMethod.Post, "/api/auth/login-dispositivo")
        {
            Content = JsonContent.Create(new SolicitudDeLoginDeDispositivo("cajero1", PasswordClaimsMatchCode, SolicitarBearer: true))
        };
        loginDispositivo.Headers.Add("Authorization", $"Dispositivo {vinculado.Secreto}");

        using var cajero = fixture.CreateClient();
        var respuestaLogin = await cajero.SendAsync(loginDispositivo);
        Assert.Equal(HttpStatusCode.OK, respuestaLogin.StatusCode);
        var cuerpo = await respuestaLogin.Content.ReadFromJsonAsync<System.Text.Json.JsonElement>();
        var token = cuerpo.GetProperty("token").GetString();
        Assert.False(string.IsNullOrEmpty(token));

        var antes = await cajero.SendAsync(RequestConBearer(HttpMethod.Get, "/api/usuarios/", token!));
        Assert.Equal(HttpStatusCode.OK, antes.StatusCode);

        // Degradado a Vendedor SIN pasar por ningún re-login: la claim ways:id_rol del token
        // bearer ya emitido sigue diciendo Admin, y el token vive 365 días.
        await CambiarRolAsync(resultado.IdTenant, "cajero1", RolConocido.Vendedor);

        var despues = await cajero.SendAsync(RequestConBearer(HttpMethod.Get, "/api/usuarios/", token!));
        Assert.Equal(HttpStatusCode.Unauthorized, despues.StatusCode);
    }

    /// <summary>Judgment-day (juez B): los dos tests de arriba prueban la cláusula de mismatch
    /// para una cuenta de TENANT — ninguno la ejerce para root/plataforma, el único caso donde
    /// equivocarse deja al dueño afuera de su propia plataforma. <c>PoliticaDeRoles.ValidarPuedeAsignarRol</c>
    /// rechaza asignar o reasignar el rol root desde la aplicación (no hay ningún endpoint que
    /// pueda producir este estado); la única forma de ejercitarlo es escribir <c>RolId</c> directo
    /// en la base, igual que <see cref="CambiarRolAsync"/> hace para las otras dos cuentas.
    ///
    /// Con la claim ways:id_rol todavía diciendo Root, <c>ResolverModoDeLaSesionAsync</c> deja el
    /// contexto en modo Plataforma — el filtro de tenant de EF no esconde la fila (plataforma ve
    /// todo) — así que la comparación de <c>RolId</c> sí llega a evaluarse y tiene que rechazar.</summary>
    [Fact]
    public async Task DegradarElRolDeRootDirectoEnLaBaseCortaUnaSesionYaAbierta()
    {
        using var root = fixture.CreateClient();
        var login = await root.PostAsJsonAsync("/api/auth/login", new SolicitudDeLogin(MailRoot, PasswordRoot));
        Assert.Equal(HttpStatusCode.OK, login.StatusCode);

        var antes = await root.GetAsync("/api/usuarios/");
        Assert.Equal(HttpStatusCode.OK, antes.StatusCode);

        try
        {
            // Degradado a Admin SIN pasar por ningún re-login: la claim ways:id_rol de la
            // cookie ya emitida sigue diciendo Root.
            await CambiarRolDeRootAsync(RolConocido.Admin);

            var despues = await root.GetAsync("/api/usuarios/");
            Assert.Equal(HttpStatusCode.Unauthorized, despues.StatusCode);
        }
        finally
        {
            // La cuenta root es única y la comparte toda esta clase (mismo WaysApiFixture,
            // mismo contenedor) — a diferencia de las cuentas de los otros dos tests, que
            // siembran su propio tenant aislado, este test tiene que devolverla como la
            // encontró para no filtrar estado a los tests que corren después (la colección
            // "secuencial" garantiza que nunca corren en paralelo, pero sí en cualquier orden).
            await CambiarRolDeRootAsync(RolConocido.Root);
        }
    }
}
