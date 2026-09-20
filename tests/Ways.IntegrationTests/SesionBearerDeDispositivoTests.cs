using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
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
/// stage-desktop-pos, slice bearer: <c>POST /api/auth/login-dispositivo</c> con
/// <c>SolicitarBearer=true</c> devuelve, además de la cookie <c>ways.sesion</c> de siempre, un
/// token bearer equivalente — pensado para el shell de escritorio (Tauri, slice 3), que va a
/// correr en un origen cross-site respecto de la API donde ninguna cookie <c>SameSite=Lax</c>
/// viaja en un <c>fetch</c>.
///
/// El foco de esta suite es la revocación: <c>ValidadorDeSesion.EsVigenteAsync</c> es el ÚNICO
/// lugar que decide vigencia, compartido por cookie y bearer — estos tests son el espejo exacto
/// de <c>DispositivosTests.RevocarElDispositivoCortaUnaSesionDeCajeroYaAbierta</c> y
/// <c>.DarDeBajaElPuntoDeVentaCortaUnaSesionDeCajeroYaAbierta</c>, pero autenticando por bearer
/// en vez de por cookie — si algún día el bearer dejara de llamar a ese método compartido (una
/// copia propia, por ejemplo), estos tests son los que lo detectan.
/// </summary>
[Collection("Ways.IntegrationTests secuencial")]
public class SesionBearerDeDispositivoTests(WaysApiFixture fixture) : IClassFixture<WaysApiFixture>
{
    private const string PasswordCajero = "una-contraseña-de-cajero";

    private async Task<(HttpClient Cliente, int IdTenant, int IdPuntoVenta)> AprovisionarYLoguearComoAdminAsync(
        string nombre)
    {
        using var root = fixture.CreateClient();
        var loginRoot = await root.PostAsJsonAsync(
            "/api/auth/login", new SolicitudDeLogin("test@test.com", "root"));
        Assert.Equal(HttpStatusCode.OK, loginRoot.StatusCode);

        var solicitud = new SolicitudDeAprovisionamiento(
            NombreTenant: nombre,
            RazonSocialEmpresa: $"Empresa {nombre}",
            NombrePuntoVenta: "Local 1",
            MailAdmin: $"{nombre.ToLowerInvariant()}-admin@ways.test",
            Modo: ModoPuntoVenta.Escritorio);

        var alta = await root.PostAsJsonAsync("/api/plataforma/tenants", solicitud);
        Assert.Equal(HttpStatusCode.Created, alta.StatusCode);
        var resultado = (await alta.Content.ReadFromJsonAsync<ResultadoAprovisionamiento>())!;

        var cliente = fixture.CreateClient();
        var login = await cliente.PostAsJsonAsync(
            "/api/auth/login", new SolicitudDeLogin(solicitud.MailAdmin, resultado.PasswordTemporal));
        Assert.Equal(HttpStatusCode.OK, login.StatusCode);

        return (cliente, resultado.IdTenant, resultado.IdPuntoVenta);
    }

    private async Task SembrarCajeroAsync(int idTenant, string nombreUsuario, string password)
    {
        var hasheador = new HasheadorPbkdf2();
        await using var db = fixture.CrearContextoDeAplicacion(TenantActualFijo.Plataforma);

        var ahora = DateTimeOffset.UtcNow;
        db.Usuarios.Add(new Usuario
        {
            IdTenant = idTenant,
            NombreUsuario = nombreUsuario,
            Mail = $"{nombreUsuario}-{idTenant}@ways.test",
            RolId = (int)RolConocido.Vendedor,
            PasswordHash = hasheador.Hashear(password),
            PasswordAlgoritmo = hasheador.Algoritmo,
            PasswordActualizadoEl = ahora,
            CreatedAt = ahora,
            UpdatedAt = ahora
        });
        await db.SaveChangesAsync();
    }

    /// <summary>Aprovisiona tenant + PV + dispositivo vinculado + cajero, y devuelve el
    /// <see cref="HttpClient"/> del admin (todavía logueado, para poder revocar/dar de baja
    /// después) junto con el secreto de dispositivo y los ids relevantes.</summary>
    private async Task<(HttpClient Admin, int IdTenant, int IdPuntoVenta, int IdDispositivo, string SecretoDispositivo)>
        PrepararDispositivoYCajeroAsync(string nombre)
    {
        var (admin, idTenant, idPuntoVenta) = await AprovisionarYLoguearComoAdminAsync(nombre);

        var alta = await admin.PostAsJsonAsync("/api/dispositivos", new AltaDispositivo(idPuntoVenta, "Caja 1"));
        Assert.Equal(HttpStatusCode.Created, alta.StatusCode);
        var vinculado = (await alta.Content.ReadFromJsonAsync<DispositivoVinculado>())!;

        await SembrarCajeroAsync(idTenant, "cajero1", PasswordCajero);

        return (admin, idTenant, idPuntoVenta, vinculado.Datos.Id, vinculado.Secreto);
    }

    private static HttpRequestMessage RequestConBearer(HttpMethod metodo, string ruta, string token)
    {
        var request = new HttpRequestMessage(metodo, ruta);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        return request;
    }

    private static async Task<string> LoguearComoCajeroYObtenerTokenAsync(HttpClient cajero, string secretoDispositivo)
    {
        using var login = new HttpRequestMessage(HttpMethod.Post, "/api/auth/login-dispositivo")
        {
            Content = JsonContent.Create(new SolicitudDeLoginDeDispositivo("cajero1", PasswordCajero, SolicitarBearer: true))
        };
        login.Headers.Add("Authorization", $"Dispositivo {secretoDispositivo}");

        var respuesta = await cajero.SendAsync(login);
        Assert.Equal(HttpStatusCode.OK, respuesta.StatusCode);

        var cuerpo = await respuesta.Content.ReadFromJsonAsync<JsonElement>();
        var token = cuerpo.GetProperty("token").GetString();
        Assert.False(string.IsNullOrEmpty(token));
        return token!;
    }

    [Fact]
    public async Task LoginDeDispositivoConSolicitarBearerDevuelveUnTokenQueAutenticaSinCookies()
    {
        var (admin, _, _, _, secreto) = await PrepararDispositivoYCajeroAsync(
            nameof(LoginDeDispositivoConSolicitarBearerDevuelveUnTokenQueAutenticaSinCookies));
        admin.Dispose();

        using var cajero = fixture.CreateClient();
        var token = await LoguearComoCajeroYObtenerTokenAsync(cajero, secreto);

        // Ningún cookie jar: el cliente autentica ÚNICAMENTE con el header Authorization.
        using var sinCookies = fixture.CreateClient();
        var me = await sinCookies.SendAsync(RequestConBearer(HttpMethod.Get, "/api/auth/me", token));

        Assert.Equal(HttpStatusCode.OK, me.StatusCode);
        var usuario = await me.Content.ReadFromJsonAsync<UsuarioAutenticado>();
        Assert.Equal("cajero1", usuario!.Usuario);
    }

    /// <summary>Contrato sin cambio de forma para quien NO pide bearer (el navegador de hoy):
    /// el cuerpo sigue siendo exactamente <see cref="UsuarioAutenticado"/>, sin una propiedad
    /// <c>token</c> — dto-contract-honesty: <c>SolicitarBearer=false</c> (el default) no cambia
    /// nada de lo que ya existía antes de este slice.</summary>
    [Fact]
    public async Task LoginDeDispositivoSinSolicitarBearerNoDevuelveNingunToken()
    {
        var (admin, _, _, _, secreto) = await PrepararDispositivoYCajeroAsync(
            nameof(LoginDeDispositivoSinSolicitarBearerNoDevuelveNingunToken));
        admin.Dispose();

        using var cajero = fixture.CreateClient();
        using var login = new HttpRequestMessage(HttpMethod.Post, "/api/auth/login-dispositivo")
        {
            Content = JsonContent.Create(new SolicitudDeLoginDeDispositivo("cajero1", PasswordCajero))
        };
        login.Headers.Add("Authorization", $"Dispositivo {secreto}");

        var respuesta = await cajero.SendAsync(login);
        Assert.Equal(HttpStatusCode.OK, respuesta.StatusCode);

        var cuerpo = await respuesta.Content.ReadFromJsonAsync<JsonElement>();
        Assert.False(cuerpo.TryGetProperty("token", out _));
        Assert.True(cuerpo.TryGetProperty("usuario", out _) || cuerpo.TryGetProperty("id", out _));
    }

    [Fact]
    public async Task UnTokenBearerCorruptoDaNoAutenticado()
    {
        using var cliente = fixture.CreateClient();

        var respuesta = await cliente.SendAsync(
            RequestConBearer(HttpMethod.Get, "/api/auth/me", "esto-no-es-un-token-valido"));

        Assert.Equal(HttpStatusCode.Unauthorized, respuesta.StatusCode);
    }

    /// <summary>Precedencia explícita del selector de esquema (<c>Program.cs</c>,
    /// <c>AddPolicyScheme</c>): el selector solo despacha a bearer cuando el header
    /// <c>Authorization</c> empieza con el prefijo <c>Bearer </c> — este test manda justamente
    /// ese prefijo, así que autentica por bearer y NUNCA cae de vuelta a una cookie de sesión
    /// válida que el mismo cliente también esté mandando. Un token corrupto con prefijo
    /// <c>Bearer </c> tiene que rechazar la request aunque haya una sesión de cookie
    /// perfectamente vigente al lado. La otra mitad de la regla — un header con OTRO esquema
    /// (<c>Basic</c>, <c>Dispositivo</c>, etc.) sigue yendo por cookie — la cubre
    /// <see cref="UnHeaderAuthorizationConOtroEsquemaAutenticaPorLaCookie"/>.</summary>
    [Fact]
    public async Task UnHeaderAuthorizationInvalidoNoCaeALaCookieAunConSesionCookieValida()
    {
        var (admin, idTenant, idPuntoVenta, _, secreto) = await PrepararDispositivoYCajeroAsync(
            nameof(UnHeaderAuthorizationInvalidoNoCaeALaCookieAunConSesionCookieValida));
        admin.Dispose();

        using var cajero = fixture.CreateClient();
        using var loginCookie = new HttpRequestMessage(HttpMethod.Post, "/api/auth/login-dispositivo")
        {
            Content = JsonContent.Create(new SolicitudDeLoginDeDispositivo("cajero1", PasswordCajero))
        };
        loginCookie.Headers.Add("Authorization", $"Dispositivo {secreto}");
        var loginRespuesta = await cajero.SendAsync(loginCookie);
        Assert.Equal(HttpStatusCode.OK, loginRespuesta.StatusCode);

        // La cookie ways.sesion ya quedó en el cookie jar de `cajero` (HandleCookies = true por
        // default). Se agrega ADEMÁS un header Authorization corrupto en el mismo mensaje.
        var meConHeaderCorrupto = RequestConBearer(HttpMethod.Get, "/api/auth/me", "token-corrupto");
        var respuesta = await cajero.SendAsync(meConHeaderCorrupto);

        Assert.Equal(HttpStatusCode.Unauthorized, respuesta.StatusCode);

        _ = idTenant;
        _ = idPuntoVenta;
    }

    /// <summary>judgment-day ronda 1 (hallazgo WARNING, ambos jueces): el selector de esquema
    /// (<c>Program.cs</c>, <c>ForwardDefaultSelector</c>) despachaba a bearer por la mera
    /// PRESENCIA del header <c>Authorization</c>, sin mirar su prefijo — un header con OTRO
    /// esquema (<c>Dispositivo ...</c>, el que usa <c>login-dispositivo</c>; <c>Basic ...</c>;
    /// cualquier valor espurio) hacía que una request con la cookie <c>ways.sesion</c>
    /// perfectamente vigente fallara con 401 en vez de autenticar por cookie como toda la app
    /// hacía antes de este slice. Ahora solo el prefijo <c>Bearer </c> decide bearer; todo lo
    /// demás sigue yendo por cookie.</summary>
    [Theory]
    [InlineData("Dispositivo un-secreto-cualquiera")]
    [InlineData("Basic dXNlcjpwYXNz")]
    public async Task UnHeaderAuthorizationConOtroEsquemaAutenticaPorLaCookie(string valorHeader)
    {
        var (admin, idTenant, idPuntoVenta, _, secreto) = await PrepararDispositivoYCajeroAsync(
            nameof(UnHeaderAuthorizationConOtroEsquemaAutenticaPorLaCookie) + valorHeader.Split(' ')[0]);
        admin.Dispose();

        using var cajero = fixture.CreateClient();
        using var loginCookie = new HttpRequestMessage(HttpMethod.Post, "/api/auth/login-dispositivo")
        {
            Content = JsonContent.Create(new SolicitudDeLoginDeDispositivo("cajero1", PasswordCajero))
        };
        loginCookie.Headers.Add("Authorization", $"Dispositivo {secreto}");
        var loginRespuesta = await cajero.SendAsync(loginCookie);
        Assert.Equal(HttpStatusCode.OK, loginRespuesta.StatusCode);

        // La cookie ways.sesion ya quedó en el cookie jar de `cajero`. Se agrega ADEMÁS un header
        // Authorization con un esquema distinto de Bearer en el mismo mensaje — tiene que
        // autenticar igual por la cookie, sin caer al 401 que daba antes de este fix.
        using var meConOtroEsquema = new HttpRequestMessage(HttpMethod.Get, "/api/auth/me");
        meConOtroEsquema.Headers.Add("Authorization", valorHeader);
        var respuesta = await cajero.SendAsync(meConOtroEsquema);

        Assert.Equal(HttpStatusCode.OK, respuesta.StatusCode);
        var usuario = await respuesta.Content.ReadFromJsonAsync<UsuarioAutenticado>();
        Assert.Equal("cajero1", usuario!.Usuario);

        _ = idTenant;
        _ = idPuntoVenta;
    }

    /// <summary>judgment-day ronda 2 (residual #3, ambos jueces): el selector de esquema
    /// (<c>Program.cs</c>, <c>ForwardDefaultSelector</c>) y <c>ManejadorBearerDeSesion</c> leen el
    /// mismo <c>Request.Headers["Authorization"].ToString()</c> — con DOS headers
    /// <c>Authorization</c> en la misma request (el transporte HTTP lo permite a nivel de wire,
    /// aunque ningún cliente legítimo de esta app lo haga), <c>StringValues.ToString()</c> los
    /// junta con una coma SIN espacio: <c>"Bearer &lt;token&gt;,Basic xxx"</c>. Con el token bearer
    /// GENUINO primero, la coma mete el segundo header dentro del "token" que
    /// <c>ManejadorBearerDeSesion</c> extrae — deja de ser el string cifrado que <c>Unprotect</c>
    /// puede decodificar, así que falla. Fail-closed: la forma multi-valor nunca autentica, nunca
    /// es una vía de escalada.</summary>
    [Fact]
    public async Task DosHeadersAuthorizationBearerValidoLuegoBasicNoAutentica()
    {
        var (admin, _, _, _, secreto) = await PrepararDispositivoYCajeroAsync(
            nameof(DosHeadersAuthorizationBearerValidoLuegoBasicNoAutentica));
        admin.Dispose();

        using var cajero = fixture.CreateClient();
        var token = await LoguearComoCajeroYObtenerTokenAsync(cajero, secreto);

        using var sinCookies = fixture.CreateClient();
        using var request = new HttpRequestMessage(HttpMethod.Get, "/api/auth/me");
        request.Headers.TryAddWithoutValidation(
            "Authorization", new[] { $"Bearer {token}", "Basic dXNlcjpwYXNz" });

        var respuesta = await sinCookies.SendAsync(request);

        Assert.Equal(HttpStatusCode.Unauthorized, respuesta.StatusCode);
    }

    /// <summary>Misma coma, orden invertido: el header unido queda
    /// <c>"Basic xxx,Bearer &lt;token&gt;"</c>, que YA NO empieza con <c>"Bearer "</c> — el
    /// selector despacha a la cookie en vez de al bearer, y sin cookie de sesión no hay nada que
    /// autentique: el token genuino queda enterrado en el segundo valor y jamás se lee.
    /// Fail-closed en los dos órdenes posibles.</summary>
    [Fact]
    public async Task DosHeadersAuthorizationBasicLuegoBearerValidoNoAutentica()
    {
        var (admin, _, _, _, secreto) = await PrepararDispositivoYCajeroAsync(
            nameof(DosHeadersAuthorizationBasicLuegoBearerValidoNoAutentica));
        admin.Dispose();

        using var cajero = fixture.CreateClient();
        var token = await LoguearComoCajeroYObtenerTokenAsync(cajero, secreto);

        using var sinCookies = fixture.CreateClient();
        using var request = new HttpRequestMessage(HttpMethod.Get, "/api/auth/me");
        request.Headers.TryAddWithoutValidation(
            "Authorization", new[] { "Basic dXNlcjpwYXNz", $"Bearer {token}" });

        var respuesta = await sinCookies.SendAsync(request);

        Assert.Equal(HttpStatusCode.Unauthorized, respuesta.StatusCode);
    }

    /// <summary>Un solo header, con un espacio inicial antes de <c>"Bearer"</c> puesto a mano vía
    /// <c>TryAddWithoutValidation</c> (fuera del formato que valida el parser normal de
    /// <c>Authorization</c>). Comprobado en runtime (no asumido): el propio pipeline de
    /// <c>HttpHeaders</c>/Kestrel recorta el espacio ANTES de que <c>Request.Headers["Authorization"]</c>
    /// lo entregue — <c>StartsWith("Bearer ", ...)</c> del selector nunca lo ve, así que la
    /// request autentica NORMALMENTE por bearer con el token genuino. No es una vía de escalada
    /// (el token sigue siendo el mismo, válido, del mismo cajero) ni un caso especial de este
    /// código: es tolerancia de espacios en blanco del transporte HTTP subyacente, ajena a
    /// <c>ManejadorBearerDeSesion</c> y al selector.</summary>
    [Fact]
    public async Task UnHeaderAuthorizationConEspacioInicialAntesDeBearerAutenticaIgual()
    {
        var (admin, _, _, _, secreto) = await PrepararDispositivoYCajeroAsync(
            nameof(UnHeaderAuthorizationConEspacioInicialAntesDeBearerAutenticaIgual));
        admin.Dispose();

        using var cajero = fixture.CreateClient();
        var token = await LoguearComoCajeroYObtenerTokenAsync(cajero, secreto);

        using var sinCookies = fixture.CreateClient();
        using var request = new HttpRequestMessage(HttpMethod.Get, "/api/auth/me");
        request.Headers.TryAddWithoutValidation("Authorization", $" Bearer {token}");

        var respuesta = await sinCookies.SendAsync(request);

        Assert.Equal(HttpStatusCode.OK, respuesta.StatusCode);
        var usuario = await respuesta.Content.ReadFromJsonAsync<UsuarioAutenticado>();
        Assert.Equal("cajero1", usuario!.Usuario);
    }

    [Fact]
    public async Task RevocarElDispositivoCortaUnaSesionBearerYaEmitida()
    {
        var (admin, _, _, idDispositivo, secreto) = await PrepararDispositivoYCajeroAsync(
            nameof(RevocarElDispositivoCortaUnaSesionBearerYaEmitida));

        using var cajero = fixture.CreateClient();
        var token = await LoguearComoCajeroYObtenerTokenAsync(cajero, secreto);

        var meAntes = await cajero.SendAsync(RequestConBearer(HttpMethod.Get, "/api/auth/me", token));
        Assert.Equal(HttpStatusCode.OK, meAntes.StatusCode);

        var revocar = await admin.DeleteAsync($"/api/dispositivos/{idDispositivo}");
        Assert.Equal(HttpStatusCode.NoContent, revocar.StatusCode);
        admin.Dispose();

        var meDespues = await cajero.SendAsync(RequestConBearer(HttpMethod.Get, "/api/auth/me", token));
        Assert.Equal(HttpStatusCode.Unauthorized, meDespues.StatusCode);
    }

    [Fact]
    public async Task DarDeBajaElPuntoDeVentaCortaUnaSesionBearerYaEmitida()
    {
        var (admin, _, idPuntoVenta, _, secreto) = await PrepararDispositivoYCajeroAsync(
            nameof(DarDeBajaElPuntoDeVentaCortaUnaSesionBearerYaEmitida));
        admin.Dispose();

        using var cajero = fixture.CreateClient();
        var token = await LoguearComoCajeroYObtenerTokenAsync(cajero, secreto);

        var meAntes = await cajero.SendAsync(RequestConBearer(HttpMethod.Get, "/api/auth/me", token));
        Assert.Equal(HttpStatusCode.OK, meAntes.StatusCode);

        await using (var db = fixture.CrearContextoDeAplicacion(TenantActualFijo.Plataforma))
        {
            var pv = await db.PuntosVenta.FirstAsync(p => p.Id == idPuntoVenta);
            pv.DeletedAt = DateTimeOffset.UtcNow;
            await db.SaveChangesAsync();
        }

        var meDespues = await cajero.SendAsync(RequestConBearer(HttpMethod.Get, "/api/auth/me", token));
        Assert.Equal(HttpStatusCode.Unauthorized, meDespues.StatusCode);
    }

    /// <summary>Mismo motivo de revocación que las dos anteriores, pero por el lado del USUARIO
    /// (no del dispositivo): bloquear/desactivar la cuenta del cajero corta la sesión bearer en
    /// la request siguiente — prueba que <c>ValidadorDeSesion</c> corre el chequeo de
    /// <c>EstadoUsuario.Activo</c> para el bearer, no solo el de dispositivo/PV.
    ///
    /// Mutation-proof (regla 3, "route the test BELOW the confound"): usa
    /// <c>GET /api/puntos-venta</c>, NUNCA <c>/api/auth/me</c> — ese último re-chequea
    /// <c>Usuario.PuedeIniciarSesion</c> (<c>Estado == Activo</c>) por su cuenta
    /// (<c>ServicioDeAutenticacion.ObtenerAsync</c>), así que mataría el mutante de
    /// <c>ValidadorDeSesion</c> por una razón DISTINTA a la que este test quiere probar —
    /// confirmado corriendo la mutación (<c>if (!vigente)</c> → <c>if (!vigente &amp;&amp; false)</c>
    /// en <c>ValidadorDeSesion.EsVigenteAsync</c>) contra <c>/me</c>: siguió en VERDE (falso
    /// negativo). Contra <c>/api/puntos-venta</c> (que no repite ese chequeo) la misma mutación
    /// dio ROJO como corresponde; revertida, vuelve a VERDE.</summary>
    [Fact]
    public async Task InactivarElUsuarioCortaUnaSesionBearerYaEmitida()
    {
        var (admin, idTenant, _, _, secreto) = await PrepararDispositivoYCajeroAsync(
            nameof(InactivarElUsuarioCortaUnaSesionBearerYaEmitida));
        admin.Dispose();

        using var cajero = fixture.CreateClient();
        var token = await LoguearComoCajeroYObtenerTokenAsync(cajero, secreto);

        var antes = await cajero.SendAsync(RequestConBearer(HttpMethod.Get, "/api/puntos-venta", token));
        Assert.Equal(HttpStatusCode.OK, antes.StatusCode);

        await using (var db = fixture.CrearContextoDeAplicacion(TenantActualFijo.Plataforma))
        {
            var usuario = await db.Usuarios.FirstAsync(u => u.IdTenant == idTenant && u.NombreUsuario == "cajero1");
            usuario.Estado = EstadoUsuario.Inactivo;
            await db.SaveChangesAsync();
        }

        var despues = await cajero.SendAsync(RequestConBearer(HttpMethod.Get, "/api/puntos-venta", token));
        Assert.Equal(HttpStatusCode.Unauthorized, despues.StatusCode);
    }

    /// <summary>judgment-day ronda 1 (hallazgo WARNING, juez A): la cláusula de tenant activo de
    /// <c>ValidadorDeSesion.ResolverModoDeLaSesionAsync</c> ya tenía cobertura de "una sesión YA
    /// abierta se corta al suspender el tenant" para cookie
    /// (<c>UsuariosYLoginTests.SuspenderElTenantCortaLaSesionActivaEnLaProximaRequest</c>) — pero
    /// nunca para bearer, y ahora esa misma cláusula respalda los dos esquemas. Mismo criterio de
    /// "route the test BELOW the confound" que <c>InactivarElUsuarioCortaUnaSesionBearerYaEmitida</c>:
    /// <c>/api/puntos-venta</c>, no <c>/api/auth/me</c>.</summary>
    [Fact]
    public async Task SuspenderElTenantCortaUnaSesionBearerYaEmitida()
    {
        var (admin, idTenant, _, _, secreto) = await PrepararDispositivoYCajeroAsync(
            nameof(SuspenderElTenantCortaUnaSesionBearerYaEmitida));
        admin.Dispose();

        using var cajero = fixture.CreateClient();
        var token = await LoguearComoCajeroYObtenerTokenAsync(cajero, secreto);

        var antes = await cajero.SendAsync(RequestConBearer(HttpMethod.Get, "/api/puntos-venta", token));
        Assert.Equal(HttpStatusCode.OK, antes.StatusCode);

        await using (var db = fixture.CrearContextoDeAplicacion(TenantActualFijo.Plataforma))
        {
            var tenant = await db.Tenants.FirstAsync(t => t.Id == idTenant);
            tenant.Estado = EstadoTenant.Suspendido;
            await db.SaveChangesAsync();
        }

        var despues = await cajero.SendAsync(RequestConBearer(HttpMethod.Get, "/api/puntos-venta", token));
        Assert.Equal(HttpStatusCode.Unauthorized, despues.StatusCode);
    }
}
