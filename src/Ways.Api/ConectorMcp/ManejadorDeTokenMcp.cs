using System.Text.Encodings.Web;
using Microsoft.AspNetCore.Authentication;
using Microsoft.Extensions.Options;
using OpenIddict.Abstractions;
using OpenIddict.Validation.AspNetCore;
using Ways.Api.Seguridad;
using Ways.Infrastructure.Persistencia;

namespace Ways.Api.ConectorMcp;

/// <summary>
/// Autenticación de <c>/mcp</c>: acepta solamente un access token de OpenIddict emitido para el
/// recurso MCP de esta request y con el alcance <see cref="ConstantesDeMcp.AlcanceMcp"/>. El
/// esquema MCP del SDK le reenvía la autenticación (ForwardAuthenticate) y se queda con el
/// challenge, que es el que agrega <c>WWW-Authenticate: Bearer resource_metadata=...</c>. Cada
/// rechazo deja su motivo en la línea de diagnóstico de la request.
///
/// Igual que <see cref="ManejadorBearerDeSesion"/>, corre <see cref="ValidadorDeSesion.EsVigenteAsync"/>
/// en cada request: revalida la cuenta contra la base y fija el modo de tenant que usa RLS.
/// </summary>
public sealed class ManejadorDeTokenMcp(
    IOptionsMonitor<AuthenticationSchemeOptions> opciones,
    ILoggerFactory logueadorFactory,
    UrlEncoder encoder,
    UrlsDelConector urls)
    : AuthenticationHandler<AuthenticationSchemeOptions>(opciones, logueadorFactory, encoder)
{
    protected override async Task<AuthenticateResult> HandleAuthenticateAsync()
    {
        var resultado = await Context.AuthenticateAsync(OpenIddictValidationAspNetCoreDefaults.AuthenticationScheme);
        if (resultado.None)
        {
            DiagnosticoDelConector.RegistrarMotivo(Context, "sin token");
            return AuthenticateResult.NoResult();
        }

        if (!resultado.Succeeded)
        {
            var descripcion = resultado.Properties?.GetString(
                OpenIddictValidationAspNetCoreConstants.Properties.ErrorDescription);
            DiagnosticoDelConector.RegistrarMotivo(Context, $"token inválido o vencido ({descripcion})");

            // Sin éxito y sin None, AuthenticateResult siempre trae Failure.
            return AuthenticateResult.Fail(resultado.Failure!);
        }

        var principal = resultado.Principal;

        if (!principal.GetAudiences().Contains(urls.Recurso(Context), StringComparer.Ordinal))
        {
            DiagnosticoDelConector.RegistrarMotivo(Context, "token emitido para otro recurso");
            return AuthenticateResult.Fail("El access token no fue emitido para este recurso.");
        }

        if (!principal.HasScope(ConstantesDeMcp.AlcanceMcp))
        {
            DiagnosticoDelConector.RegistrarMotivo(Context, $"token sin el alcance {ConstantesDeMcp.AlcanceMcp}");
            return AuthenticateResult.Fail("El access token no incluye el alcance requerido.");
        }

        var deWays = IdentidadDelConector.ParaWays(principal, Scheme.Name);
        var db = Context.RequestServices.GetRequiredService<WaysDbContext>();

        if (!await ValidadorDeSesion.EsVigenteAsync(deWays, Context, db))
        {
            DiagnosticoDelConector.RegistrarMotivo(Context, "sesión revocada (usuario, rol o tenant ya no vigentes)");
            return AuthenticateResult.Fail("Sesión revocada.");
        }

        return AuthenticateResult.Success(new AuthenticationTicket(deWays, Scheme.Name));
    }
}
