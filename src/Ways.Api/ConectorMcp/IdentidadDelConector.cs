using System.Globalization;
using System.Security.Claims;
using OpenIddict.Abstractions;
using Ways.Api.Seguridad;
using Ways.Application.Usuarios;
using static OpenIddict.Abstractions.OpenIddictConstants;

namespace Ways.Api.ConectorMcp;

/// <summary>Traducción entre la identidad de Ways (claims de <see cref="ClaimsWays"/>) y la que
/// viaja dentro de los tokens de OpenIddict (claims JWT cortos).</summary>
public static class IdentidadDelConector
{
    public const string TipoDeAutenticacion = "ways.mcp";

    /// <summary>Solo recibe usuarios de tenant: <c>EndpointsDeAutorizacion</c> rechaza a los de
    /// plataforma antes de firmar.</summary>
    public static ClaimsIdentity ParaOpenIddict(
        UsuarioAutenticado usuario, IEnumerable<string> alcances, IEnumerable<string> recursos)
    {
        var identidad = new ClaimsIdentity(TipoDeAutenticacion, Claims.Name, Claims.Role);

        identidad
            .SetClaim(Claims.Subject, usuario.Id.ToString(CultureInfo.InvariantCulture))
            .SetClaim(Claims.Name, usuario.Usuario)
            .SetClaim(Claims.Role, usuario.Rol)
            .SetClaim(ClaimsWays.RolId, usuario.RolId.ToString(CultureInfo.InvariantCulture))
            .SetClaim(ClaimsWays.IdTenant, usuario.IdTenant!.Value.ToString(CultureInfo.InvariantCulture));

        identidad.SetScopes(alcances);
        identidad.SetResources(recursos);
        identidad.SetDestinations(static _ => [Destinations.AccessToken]);

        return identidad;
    }

    /// <summary>Principal con la forma que esperan <see cref="ValidadorDeSesion"/> e
    /// <see cref="ContextoDeUsuarioHttp"/>. Los cinco claims vienen siempre, porque todo token del
    /// conector sale de <see cref="ParaOpenIddict"/>.</summary>
    public static ClaimsPrincipal ParaWays(ClaimsPrincipal deOpenIddict, string tipoDeAutenticacion) =>
        new(new ClaimsIdentity(
            [
                new Claim(ClaimTypes.NameIdentifier, deOpenIddict.GetClaim(Claims.Subject)!),
                new Claim(ClaimTypes.Name, deOpenIddict.GetClaim(Claims.Name)!),
                new Claim(ClaimTypes.Role, deOpenIddict.GetClaim(Claims.Role)!),
                new Claim(ClaimsWays.RolId, deOpenIddict.GetClaim(ClaimsWays.RolId)!),
                new Claim(ClaimsWays.IdTenant, deOpenIddict.GetClaim(ClaimsWays.IdTenant)!)
            ],
            tipoDeAutenticacion));
}
