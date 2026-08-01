using System.Security.Claims;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Ways.Api.Seguridad;
using Ways.Application.Abstracciones;
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

            var identidad = new ClaimsIdentity(claims, CookieAuthenticationDefaults.AuthenticationScheme);

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
}
