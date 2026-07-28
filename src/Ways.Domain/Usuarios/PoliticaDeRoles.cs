using Ways.Domain.Common;

namespace Ways.Domain.Usuarios;

/// <summary>
/// Quién puede administrar a quién. Regla de negocio pura, sin dependencias:
/// vive acá para poder testearla sin base de datos.
///
/// Reglas vigentes:
///   - Solo root y admin pueden gestionar usuarios.
///   - Nadie puede crear ni asignar el rol root.
///   - Solo root puede crear o asignar el rol admin.
///   - Un admin no puede tocar la cuenta de un root.
///   - Nadie puede eliminarse a sí mismo.
/// </summary>
public static class PoliticaDeRoles
{
    public const int UmbralBloqueoPorIntentosFallidos = 5;

    public static bool PuedeGestionarUsuarios(RolConocido actor) =>
        actor is RolConocido.Root or RolConocido.Admin;

    /// <summary>Roles que <paramref name="actor"/> tiene permitido asignar a otra cuenta.</summary>
    public static IReadOnlyList<RolConocido> RolesAsignablesPor(RolConocido actor) => actor switch
    {
        RolConocido.Root => [RolConocido.Admin, RolConocido.Supervisor, RolConocido.Vendedor],
        RolConocido.Admin => [RolConocido.Supervisor, RolConocido.Vendedor],
        _ => []
    };

    public static void ValidarPuedeAsignarRol(RolConocido actor, RolConocido rolDestino)
    {
        if (!PuedeGestionarUsuarios(actor))
        {
            throw ErrorDominio.Prohibido("No tenés permisos para gestionar usuarios.");
        }

        if (rolDestino == RolConocido.Root)
        {
            throw ErrorDominio.Prohibido(
                "El rol root no se puede asignar desde la aplicación.");
        }

        if (!RolesAsignablesPor(actor).Contains(rolDestino))
        {
            throw ErrorDominio.Prohibido(
                $"Un usuario con rol {actor} no puede asignar el rol {rolDestino}.");
        }
    }

    /// <summary>Valida que el actor pueda modificar o eliminar la cuenta indicada.</summary>
    public static void ValidarPuedeIntervenirSobre(
        RolConocido actor,
        int actorId,
        RolConocido rolObjetivo,
        int objetivoId,
        bool esBaja)
    {
        if (!PuedeGestionarUsuarios(actor))
        {
            throw ErrorDominio.Prohibido("No tenés permisos para gestionar usuarios.");
        }

        if (rolObjetivo == RolConocido.Root && actor != RolConocido.Root)
        {
            throw ErrorDominio.Prohibido("Solo un root puede intervenir sobre una cuenta root.");
        }

        if (esBaja && actorId == objetivoId)
        {
            throw ErrorDominio.Prohibido("No podés eliminar tu propia cuenta.");
        }

        if (esBaja && rolObjetivo == RolConocido.Root)
        {
            throw ErrorDominio.Prohibido("Las cuentas root no se pueden eliminar.");
        }
    }
}
