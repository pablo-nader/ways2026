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

    /// <summary>Vendedor, supervisor o admin — la puerta de la superficie de lectura del POS
    /// (artículos, códigos de barra, clientes, listas de precio, parámetros, catálogos
    /// fiscales/medios de pago, resolución de ofertas) y del checkout/anulación/lectura de
    /// stock (etapa 5). Supervisor se suma por paridad con el legacy (decisión del
    /// orquestador, registrada en el spec). Root queda afuera, mismo criterio que
    /// <see cref="GestionDeCatalogo"/> ("root administra tenants, no opera ninguno" —
    /// design.md decisión 6). ASP.NET Core compone políticas con AND: los endpoints de
    /// escritura apilan <see cref="GestionDeCatalogo"/> sobre esta para no relajar el ABM.</summary>
    public const string OperacionDePos = "operacion_de_pos";

    /// <summary>Cualquier rol autenticado (Root, admin, supervisor o vendedor) — la puerta del
    /// <c>GET /api/puntos-venta</c> (listado). Es la única ruta de <c>OrganizacionEndpoints</c>
    /// que necesitan tanto el ABM administrativo (<see cref="GestionDeOrganizacion"/>, root/admin,
    /// <c>PuntosVenta.tsx</c>) como el selector de PV del POS (<see cref="OperacionDePos"/>,
    /// vendedor/supervisor/admin) — un AND de esas dos policies excluiría a vendedor y supervisor
    /// del admin, o a root del POS, así que en vez de apilarlas se usa esta policy combinada solo
    /// para el listado; el resto de <c>OrganizacionEndpoints</c> (obtener por id, editar) sigue
    /// exclusivamente bajo <see cref="GestionDeOrganizacion"/>.</summary>
    public const string LecturaDePuntosVenta = "lectura_puntos_venta";

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
                            ((int)RolConocido.Admin).ToString()))
            .AddPolicy(OperacionDePos, politica =>
                politica.RequireAuthenticatedUser()
                        .RequireClaim(
                            ClaimsWays.RolId,
                            ((int)RolConocido.Vendedor).ToString(),
                            ((int)RolConocido.Supervisor).ToString(),
                            ((int)RolConocido.Admin).ToString()))
            .AddPolicy(LecturaDePuntosVenta, politica =>
                politica.RequireAuthenticatedUser()
                        .RequireClaim(
                            ClaimsWays.RolId,
                            ((int)RolConocido.Root).ToString(),
                            ((int)RolConocido.Admin).ToString(),
                            ((int)RolConocido.Supervisor).ToString(),
                            ((int)RolConocido.Vendedor).ToString()));
    }
}
