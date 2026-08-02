using Microsoft.AspNetCore.Authorization;
using Ways.Domain.Usuarios;

namespace Ways.Api.Seguridad;

public static class Politicas
{
    /// <summary>Root y admin. Es la puerta del ABM de usuarios.</summary>
    public const string GestionDeUsuarios = "gestion_usuarios";

    /// <summary>Solo admin (RolesBase: "admin administra usuarios, catálogo y
    /// configuración") — la puerta del ABM de catálogos y parámetros de tenant. Root queda
    /// afuera a propósito (doc 09/design.md: "root administra tenants, no opera ninguno"),
    /// mismo criterio que <see cref="SoloPlataforma"/> en espejo.</summary>
    public const string GestionDeCatalogo = "gestion_catalogo";

    /// <summary>Solo root: root administra tenants, no opera ninguno (doc 09) — la puerta del
    /// aprovisionamiento de tenants (ADR-16) y de las acciones plataforma-only sobre
    /// tenants (listar/editar/suspender/reactivar, <c>OrganizacionEndpoints</c>).</summary>
    public const string SoloPlataforma = "solo_plataforma";

    /// <summary>Root o admin — la puerta de lectura/edición de empresas y puntos de venta
    /// (<c>OrganizacionEndpoints</c>): root ve/edita cualquiera, un admin de tenant ve/edita
    /// solo los de su propio tenant (lo garantiza el filtro de EF/RLS +
    /// <c>PoliticaDeRoles.ValidarAlcanceDeTenant</c>, no esta policy). Mismo criterio de
    /// claims que <see cref="GestionDeUsuarios"/>, nombrada aparte porque es un concern
    /// distinto (organización, no cuentas).</summary>
    public const string GestionDeOrganizacion = "gestion_organizacion";

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
                        .RequireClaim(ClaimsWays.RolId, ((int)RolConocido.Admin).ToString()))
            .AddPolicy(SoloPlataforma, politica =>
                politica.RequireAuthenticatedUser()
                        .RequireClaim(ClaimsWays.RolId, ((int)RolConocido.Root).ToString()))
            .AddPolicy(GestionDeOrganizacion, politica =>
                politica.RequireAuthenticatedUser()
                        .RequireClaim(
                            ClaimsWays.RolId,
                            ((int)RolConocido.Root).ToString(),
                            ((int)RolConocido.Admin).ToString()));
    }
}
