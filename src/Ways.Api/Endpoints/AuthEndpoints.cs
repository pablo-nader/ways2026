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
            CancellationToken ct) =>
        {
            // Único momento en que el contexto de tenant de la request se pone en modo
            // Login (doc 09, design.md "Login contract"): antes de resolver ninguna sesión,
            // así el interceptor setea `app.acceso = 'login'` en la primera conexión que
            // abre `ServicioDeAutenticacion`, y RLS deja leer/actualizar `usuarios` sin un
            // tenant resuelto todavía (gate #2 pendiente sobre las policies).
            tenantActual.Establecer(ModoDeAcceso.Login, idTenant: null);

            var usuario = await servicio.IniciarSesionAsync(solicitud, ct);

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
        .WithSummary("Inicia sesión y emite la cookie de sesión.");

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
            CancellationToken ct) =>
        {
            // El dispositivo se resuelve en modo Login (RLS dispositivos_login_lectura, mismo
            // patrón que usuarios_login_lectura): todavía no hay tenant resuelto.
            tenantActual.Establecer(ModoDeAcceso.Login, idTenant: null);

            var secreto = contexto.Request.Cookies[CookiesWays.Dispositivo];
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

            return Results.Ok(usuario);
        })
        .AllowAnonymous()
        .WithSummary("Login de cajero contra un dispositivo vinculado; sesión persistente de 365 días.");

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
