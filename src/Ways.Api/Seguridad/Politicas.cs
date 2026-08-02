using Microsoft.AspNetCore.Authorization;
using Ways.Domain.Usuarios;

namespace Ways.Api.Seguridad;

public static class Politicas
{
    /// <summary>Root y admin. Es la puerta del ABM de usuarios.</summary>
    public const string GestionDeUsuarios = "gestion_usuarios";

    /// <summary>Root y admin (RolesBase: "admin administra usuarios, catálogo y
    /// configuración") — la puerta del ABM de catálogos y parámetros de tenant.</summary>
    public const string GestionDeCatalogo = "gestion_catalogo";

    /// <summary>Solo root: root administra tenants, no opera ninguno (doc 09) — la puerta del
    /// aprovisionamiento de tenants (ADR-16).</summary>
    public const string SoloPlataforma = "solo_plataforma";

    public static AuthorizationBuilder AgregarPoliticasWays(this AuthorizationBuilder builder)
    {
        return builder
            .AddPolicy(GestionDeUsuarios, politica =>
                politica.RequireAuthenticatedUser()
                        .RequireClaim(
                            ClaimsWays.RolId,
                            ((int)RolConocido.Root).ToString(),
                            ((int)RolConocido.Admin).ToString()))
            .AddPolicy(GestionDeCatalogo, politica =>
                politica.RequireAuthenticatedUser()
                        .RequireClaim(
                            ClaimsWays.RolId,
                            ((int)RolConocido.Root).ToString(),
                            ((int)RolConocido.Admin).ToString()))
            .AddPolicy(SoloPlataforma, politica =>
                politica.RequireAuthenticatedUser()
                        .RequireClaim(ClaimsWays.RolId, ((int)RolConocido.Root).ToString()));
    }
}
