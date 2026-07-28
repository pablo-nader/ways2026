using System.Security.Claims;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Ways.Api.Seguridad;
using Ways.Application.Abstracciones;
using Ways.Application.Usuarios;

namespace Ways.Api.Endpoints;

public static class AuthEndpoints
{
    public static IEndpointRouteBuilder MapearAuth(this IEndpointRouteBuilder app)
    {
        var grupo = app.MapGroup("/api/auth").WithTags("Auth");

        grupo.MapPost("/login", async (
            SolicitudDeLogin solicitud,
            ServicioDeAutenticacion servicio,
            HttpContext contexto,
            CancellationToken ct) =>
        {
            var usuario = await servicio.IniciarSesionAsync(solicitud, ct);

            var identidad = new ClaimsIdentity(
                [
                    new Claim(ClaimTypes.NameIdentifier, usuario.Id.ToString()),
                    new Claim(ClaimTypes.Name, usuario.Usuario),
                    new Claim(ClaimTypes.Role, usuario.Rol),
                    new Claim(ClaimsWays.RolId, usuario.RolId.ToString())
                ],
                CookieAuthenticationDefaults.AuthenticationScheme);

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
