using System.Text.Encodings.Web;
using Microsoft.AspNetCore.Authentication;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Ways.Infrastructure.Persistencia;

namespace Ways.Api.Seguridad;

/// <summary>
/// Esquema bearer de la sesión de cajero (stage-desktop-pos, slice bearer): decodifica el token
/// del header <c>Authorization: Bearer &lt;token&gt;</c> con <see cref="FormateadorDeTicketBearer"/>
/// y corre EXACTAMENTE el mismo chequeo de vigencia que la cookie
/// (<see cref="ValidadorDeSesion.EsVigenteAsync"/>) antes de aceptar el principal — sin este
/// paso, un dispositivo/usuario revocado seguiría autenticando por bearer aunque la cookie ya lo
/// hubiera cortado (ver el comentario de <see cref="ValidadorDeSesion"/> sobre por qué esto no
/// puede ser una copia).
///
/// <see cref="AuthenticationSchemeOptions.TimeProvider"/> (no <see cref="DateTimeOffset.UtcNow"/>)
/// para el chequeo de expiración: mismo mecanismo que
/// <c>WaysApiFixture.ConRelojDeAutenticacionEnElHost</c> ya usa para el
/// <c>CookieAuthenticationOptions.TimeProvider</c> del esquema de cookie — un test puede
/// adelantar este reloj sin esperar 365 días de verdad.
/// </summary>
public sealed class ManejadorBearerDeSesion(
    IOptionsMonitor<AuthenticationSchemeOptions> opciones,
    ILoggerFactory logueadorFactory,
    UrlEncoder encoder,
    FormateadorDeTicketBearer formateador)
    : AuthenticationHandler<AuthenticationSchemeOptions>(opciones, logueadorFactory, encoder)
{
    private const string PrefijoBearer = "Bearer ";

    protected override async Task<AuthenticateResult> HandleAuthenticateAsync()
    {
        var encabezado = Request.Headers["Authorization"].ToString();
        if (string.IsNullOrEmpty(encabezado) ||
            !encabezado.StartsWith(PrefijoBearer, StringComparison.OrdinalIgnoreCase))
        {
            return AuthenticateResult.NoResult();
        }

        var token = encabezado[PrefijoBearer.Length..].Trim();
        if (token.Length == 0)
        {
            return AuthenticateResult.NoResult();
        }

        AuthenticationTicket? ticket;
        try
        {
            ticket = formateador.Formato.Unprotect(token);
        }
        catch
        {
            // Token corrupto o cifrado bajo otro propósito de protección (por ejemplo, un valor
            // de la cookie ways.sesion reusado a mano) — mismo criterio que un JWT con firma
            // inválida: se falla sin distinguir el motivo exacto.
            return AuthenticateResult.Fail("Token bearer inválido.");
        }

        if (ticket is null)
        {
            return AuthenticateResult.Fail("Token bearer inválido.");
        }

        var ahora = (Options.TimeProvider ?? TimeProvider.System).GetUtcNow();
        if (ticket.Properties.ExpiresUtc is { } expira && expira < ahora)
        {
            return AuthenticateResult.Fail("Token bearer expirado.");
        }

        var db = Context.RequestServices.GetRequiredService<WaysDbContext>();
        if (!await ValidadorDeSesion.EsVigenteAsync(ticket.Principal, Context, db))
        {
            return AuthenticateResult.Fail("Sesión revocada.");
        }

        return AuthenticateResult.Success(new AuthenticationTicket(ticket.Principal, Scheme.Name));
    }
}
