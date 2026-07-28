using Microsoft.AspNetCore.Authorization;
using Ways.Domain.Usuarios;

namespace Ways.Api.Seguridad;

public static class Politicas
{
    /// <summary>Root y admin. Es la puerta del ABM de usuarios.</summary>
    public const string GestionDeUsuarios = "gestion_usuarios";

    public static AuthorizationBuilder AgregarPoliticasWays(this AuthorizationBuilder builder)
    {
        return builder.AddPolicy(GestionDeUsuarios, politica =>
            politica.RequireAuthenticatedUser()
                    .RequireClaim(
                        ClaimsWays.RolId,
                        ((int)RolConocido.Root).ToString(),
                        ((int)RolConocido.Admin).ToString()));
    }
}
