using System.Security.Claims;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Ways.Api.Seguridad;
using Ways.Application.Abstracciones;
using Ways.Application.Dispositivos;
using Ways.Application.Usuarios;
using Ways.Infrastructure.Multitenancy;

namespace Ways.Api.Endpoints;

public static class AuthEndpoints
{
    public static IEndpointRouteBuilder MapearAuth(this IEndpointRouteBuilder app)
    {
        var grupo = app.MapGroup("/api/auth").WithTags("Auth");

        grupo.MapPost("/login", async (
            SolicitudDeLogin solicitud,
            ServicioDeAutenticacion servicio,
            TenantActualDeSesion tenantActual,
            HttpContext contexto,
            IRelojDelSistema reloj,
            FormateadorDeTicketBearer formateadorBearer,
            CancellationToken ct) =>
        {
            // Único momento en que el contexto de tenant de la request se pone en modo
            // Login (doc 09, design.md "Login contract"): antes de resolver ninguna sesión,
            // así el interceptor setea `app.acceso = 'login'` en la primera conexión que
            // abre `ServicioDeAutenticacion`, y RLS deja leer/actualizar `usuarios` sin un
            // tenant resuelto todavía (gate #2 pendiente sobre las policies).
            tenantActual.Establecer(ModoDeAcceso.Login, idTenant: null);

            var usuario = await servicio.IniciarSesionAsync(solicitud, ct);

            // Con SolicitarBearer la sesión viaja SOLO como token: no se firma la cookie, porque
            // el llamador (la vinculación del POS de escritorio, en otro origen) no la puede
            // recibir y una sesión de cookie paralela quedaría viva sin que nadie la cierre.
            if (solicitud.SolicitarBearer)
            {
                var expira = reloj.Ahora.Add(VigenciaDelBearerDeLogin);
                var token = EmitirBearer(ConstruirClaims(usuario), expira, formateadorBearer);
                return Results.Ok(new SesionConBearer(usuario, token, expira));
            }

            var identidad = new ClaimsIdentity(
                ConstruirClaims(usuario), CookieAuthenticationDefaults.AuthenticationScheme);

            await contexto.SignInAsync(
                CookieAuthenticationDefaults.AuthenticationScheme,
                new ClaimsPrincipal(identidad),
                // Persistente: la sesión sobrevive al cierre del navegador.
                // El vencimiento lo maneja la cookie con expiración deslizante de 1 hora.
                new AuthenticationProperties { IsPersistent = true });

            return Results.Ok(usuario);
        })
        .AllowAnonymous()
        .WithSummary(
            "Inicia sesión y emite la cookie de sesión. Con SolicitarBearer=true, en cambio, " +
            "devuelve un token bearer de 15 minutos y no emite la cookie.");

        // stage-desktop-pos: login del cajero contra un dispositivo YA vinculado. Exige la
        // cookie ways.dispositivo (nunca funciona en la app web normal, que no la tiene) y emite
        // la MISMA cookie de sesión (ways.sesion) que /login, con una claim extra
        // (ways:id_dispositivo) y un vencimiento propio de 365 días en vez del default de 1h —
        // ver el comentario de Program.cs sobre por qué la expiración deslizante conserva ese
        // span en vez de resetearlo al ExpireTimeSpan global.
        grupo.MapPost("/login-dispositivo", async (
            SolicitudDeLoginDeDispositivo solicitud,
            ServicioDeAutenticacion servicioAuth,
            ServicioDeDispositivos servicioDispositivos,
            TenantActualDeSesion tenantActual,
            HttpContext contexto,
            IRelojDelSistema reloj,
            FormateadorDeTicketBearer formateadorBearer,
            CancellationToken ct) =>
        {
            // El dispositivo se resuelve en modo Login (RLS dispositivos_login_lectura, mismo
            // patrón que usuarios_login_lectura): todavía no hay tenant resuelto.
            tenantActual.Establecer(ModoDeAcceso.Login, idTenant: null);

            var secreto = ResolucionDeCredencialDeDispositivo.Resolver(contexto);
            var (idDispositivo, idTenantDispositivo) =
                await servicioDispositivos.ResolverIdentidadAsync(secreto, ct);

            // A diferencia del login por mail, acá el tenant YA se conoce (el del dispositivo):
            // se pasa a modo Tenant antes de tocar `usuarios`, así que la policy estándar
            // usuarios_tenant alcanza — no hace falta ninguna policy de login nueva para este
            // camino.
            tenantActual.Establecer(ModoDeAcceso.Tenant, idTenantDispositivo);

            var usuario = await servicioAuth.IniciarSesionDeDispositivoAsync(
                idTenantDispositivo, solicitud, ct);

            var claims = ConstruirClaims(usuario);
            claims.Add(new Claim(ClaimsWays.IdDispositivo, idDispositivo.ToString()));

            var identidad = new ClaimsIdentity(claims, CookieAuthenticationDefaults.AuthenticationScheme);

            await contexto.SignInAsync(
                CookieAuthenticationDefaults.AuthenticationScheme,
                new ClaimsPrincipal(identidad),
                new AuthenticationProperties
                {
                    IsPersistent = true,
                    // Mutation-proof: se quitó este ExpiresUtc (queda solo IsPersistent) y se
                    // corrió SesionDeDispositivoExpiracionTests con un TimeProvider controlable
                    // (WaysApiFixture.ConRelojDeAutenticacionEnElHost): pasó de VERDE a ROJO ya en
                    // la PRIMERA aserción, a los 200 días (`Expected: OK, Actual: Unauthorized`) —
                    // sin este valor explícito, CookieAuthenticationHandler cae al ExpireTimeSpan
                    // global (1h) al firmar, y el refresh deslizante conserva ESE span, no 365
                    // días. Revertido, el test vuelve a VERDE.
                    ExpiresUtc = reloj.Ahora.AddDays(365)
                });

            await servicioDispositivos.RegistrarUsoAsync(idDispositivo, ct);

            // Slice bearer: el token SOLO se emite si el llamador lo pide explícito
            // (SolicitudDeLoginDeDispositivo.SolicitarBearer) — un caller que no lo pide (el
            // navegador de hoy) recibe exactamente el mismo Results.Ok(usuario) que antes de
            // este slice, cero cambio de forma.
            //
            // Vencimiento: FIJO a 365 días desde ahora, sin refresh deslizante — a propósito,
            // distinto de la cookie (que SÍ desliza en cada request, ver Program.cs). Un bearer
            // no tiene una request de "refresh" propia en este slice y la revocación real no
            // depende del vencimiento del token sino de ValidadorDeSesion (corre contra la base
            // en cada request, esté el token vencido o no) — un vencimiento fijo alcanza para
            // que un token filtrado/perdido no sea válido para siempre, sin necesitar todavía un
            // endpoint de refresh. 365 días replica la misma decisión de producto que ya rige la
            // cookie de dispositivo (AuthEndpoints, más arriba: "la sesión no vence hasta que se
            // cierra a mano"); si algún día hace falta invalidar el token antes de esa fecha sin
            // pasar por ValidadorDeSesion, ahí sí va a hacer falta un mecanismo de refresh/
            // revocación de tokens — no es parte de este slice.
            if (!solicitud.SolicitarBearer)
            {
                return Results.Ok(usuario);
            }

            var expiraToken = reloj.Ahora.AddDays(365);
            var tokenBearer = EmitirBearer(claims, expiraToken, formateadorBearer);

            return Results.Ok(new SesionConBearer(usuario, tokenBearer, expiraToken));
        })
        .AllowAnonymous()
        .WithSummary(
            "Login de cajero contra un dispositivo vinculado; sesión persistente de 365 días. " +
            "Con SolicitarBearer=true, además devuelve un token bearer equivalente.");

        grupo.MapPost("/logout", async (HttpContext contexto) =>
        {
            await contexto.SignOutAsync(CookieAuthenticationDefaults.AuthenticationScheme);
            return Results.NoContent();
        })
        .WithSummary("Cierra la sesión.");

        grupo.MapGet("/me", async (
            IContextoDeUsuario actual,
            ServicioDeAutenticacion servicio,
            CancellationToken ct) =>
        {
            var usuario = await servicio.ObtenerAsync(actual.UsuarioId, ct);
            return usuario is null ? Results.Unauthorized() : Results.Ok(usuario);
        })
        .WithSummary("Devuelve el usuario de la sesión en curso.");

        return app;
    }

    /// <summary>Vigencia fija del bearer que emite <c>POST /api/auth/login</c> con
    /// <c>SolicitarBearer=true</c>. Corta a propósito: su único uso es que un Admin vincule un
    /// equipo desde la pantalla de vinculación del POS de escritorio, y el token no tiene
    /// revocación propia más allá de <see cref="ValidadorDeSesion"/>.</summary>
    private static readonly TimeSpan VigenciaDelBearerDeLogin = TimeSpan.FromMinutes(15);

    /// <summary>Cuerpo de <c>POST /api/auth/login</c> y <c>POST /api/auth/login-dispositivo</c>
    /// cuando el llamador pidió <c>SolicitarBearer=true</c> — el mismo
    /// <see cref="UsuarioAutenticado"/> de siempre, más el token bearer y su vencimiento (para que
    /// el cliente sepa cuándo va a tener que volver a loguear sin necesidad de decodificar el
    /// token, que es opaco).</summary>
    private record SesionConBearer(UsuarioAutenticado Usuario, string Token, DateTimeOffset ExpiraEl);

    /// <summary>Cifra un ticket del esquema <see cref="EsquemasWays.Bearer"/> con vencimiento fijo
    /// en <paramref name="expira"/>, que <see cref="ManejadorBearerDeSesion"/> hace cumplir.</summary>
    private static string EmitirBearer(
        List<Claim> claims, DateTimeOffset expira, FormateadorDeTicketBearer formateador)
    {
        var ticket = new AuthenticationTicket(
            new ClaimsPrincipal(new ClaimsIdentity(claims, EsquemasWays.Bearer)),
            new AuthenticationProperties { ExpiresUtc = expira },
            EsquemasWays.Bearer);
        return formateador.Formato.Protect(ticket);
    }

    /// <summary>Claims base compartidas por <c>/login</c> y <c>/login-dispositivo</c>.</summary>
    private static List<Claim> ConstruirClaims(UsuarioAutenticado usuario)
    {
        List<Claim> claims =
        [
            new Claim(ClaimTypes.NameIdentifier, usuario.Id.ToString()),
            new Claim(ClaimTypes.Name, usuario.Usuario),
            new Claim(ClaimTypes.Role, usuario.Rol),
            new Claim(ClaimsWays.RolId, usuario.RolId.ToString())
        ];

        // Ausente para staff de plataforma (root): OnValidatePrincipal ya trata "sin
        // claim" como plataforma cuando el rol es root, y como "Ninguno" en cualquier
        // otro caso.
        if (usuario.IdTenant is not null)
        {
            claims.Add(new Claim(ClaimsWays.IdTenant, usuario.IdTenant.Value.ToString()));
        }

        return claims;
    }
}
