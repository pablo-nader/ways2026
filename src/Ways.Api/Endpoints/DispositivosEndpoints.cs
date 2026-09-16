using Ways.Api.Seguridad;
using Ways.Application.Abstracciones;
using Ways.Application.Dispositivos;
using Ways.Infrastructure.Multitenancy;

namespace Ways.Api.Endpoints;

/// <summary>
/// Vinculación de dispositivos de escritorio (stage-desktop-pos): <c>GET /actual</c> es anónimo
/// (lee la cookie <c>ways.dispositivo</c>, pinta el encabezado del POS antes de cualquier login);
/// el resto del ABM es <see cref="Politicas.GestionDeCatalogo"/> (solo Admin) — mismo criterio que
/// el ABM de catálogo/parámetros de tenant.
/// </summary>
public static class DispositivosEndpoints
{
    public static IEndpointRouteBuilder MapearDispositivos(this IEndpointRouteBuilder app)
    {
        var publico = app.MapGroup("/api/dispositivos").WithTags("Dispositivos");

        publico.MapGet("/actual", async (
            ServicioDeDispositivos servicio,
            TenantActualDeSesion tenantActual,
            HttpContext contexto,
            CancellationToken ct) =>
        {
            // Igual que /auth/login: modo Login para que RLS (dispositivos_login_lectura)
            // deje leer la fila sin tenant resuelto todavía.
            tenantActual.Establecer(ModoDeAcceso.Login, idTenant: null);

            var secreto = contexto.Request.Cookies[CookiesWays.Dispositivo];
            var actual = await servicio.ResolverActualAsync(secreto, ct);
            return Results.Ok(actual);
        })
        .AllowAnonymous()
        .WithSummary(
            "Resuelve el dispositivo vinculado a este navegador vía cookie — 404 " +
            "dispositivo_no_vinculado si falta, es desconocido o fue revocado.");

        var admin = app.MapGroup("/api/dispositivos")
            .WithTags("Dispositivos")
            .RequireAuthorization(Politicas.GestionDeCatalogo);

        admin.MapPost("/", async (
            AltaDispositivo datos,
            ServicioDeDispositivos servicio,
            HttpContext contexto,
            CancellationToken ct) =>
        {
            var (actual, secreto) = await servicio.CrearAsync(datos, ct);
            EscribirCookieDeDispositivo(contexto, secreto);
            return Results.Created($"/api/dispositivos/{actual.Id}", actual);
        })
        .WithSummary("Vincula un dispositivo nuevo a un punto de venta del tenant.");

        admin.MapGet("/", (ServicioDeDispositivos servicio, CancellationToken ct) =>
            servicio.ListarAsync(ct))
        .WithSummary("Lista los dispositivos activos del tenant.");

        admin.MapDelete("/{id:int}", async (
            int id, ServicioDeDispositivos servicio, CancellationToken ct) =>
        {
            await servicio.RevocarAsync(id, ct);
            return Results.NoContent();
        })
        .WithSummary("Revoca (baja lógica) un dispositivo vinculado.");

        return app;
    }

    /// <summary>10 años, igual de "para siempre" que el resto de las decisiones de esta etapa
    /// (la sesión del cajero, no la cookie de dispositivo, es la que de verdad expira por
    /// revocación) — <c>SameAsRequest</c> se resuelve a mano porque <see cref="CookieOptions"/>
    /// no tiene el enum de <c>CookieAuthenticationOptions</c>: detrás del proxy,
    /// <c>UseForwardedHeaders</c> (Program.cs) ya deja <c>Request.IsHttps</c> en el valor
    /// correcto del lado del cliente.</summary>
    private static void EscribirCookieDeDispositivo(HttpContext contexto, string secreto)
    {
        contexto.Response.Cookies.Append(CookiesWays.Dispositivo, secreto, new CookieOptions
        {
            HttpOnly = true,
            SameSite = SameSiteMode.Lax,
            Secure = contexto.Request.IsHttps,
            IsEssential = true,
            Path = "/",
            Expires = DateTimeOffset.UtcNow.AddYears(10)
        });
    }
}
