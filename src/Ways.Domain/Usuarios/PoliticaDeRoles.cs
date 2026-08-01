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

    /// <summary>Valida que <paramref name="actor"/> pueda operar sobre un recurso del
    /// alcance <paramref name="idTenantObjetivo"/> (doc 09). Plataforma opera sobre
    /// cualquier tenant o cuenta de plataforma; un actor de tenant solo sobre su propio
    /// tenant, y nunca sobre una cuenta de plataforma.
    ///
    /// Cruzar de tenant devuelve <c>NoEncontrado</c>, no <c>Prohibido</c> (ADR-8): no hay
    /// que confirmarle a nadie que el recurso existe en otro tenant.</summary>
    public static void ValidarAlcanceDeTenant(ActorDeGestion actor, int? idTenantObjetivo)
    {
        if (actor.EsDePlataforma)
        {
            return;
        }

        if (idTenantObjetivo is null)
        {
            throw ErrorDominio.Prohibido("No podés gestionar cuentas de plataforma.");
        }

        if (idTenantObjetivo != actor.IdTenant)
        {
            throw ErrorDominio.NoEncontrado("No existe el recurso solicitado.");
        }
    }

    /// <summary>Variante de <see cref="RolesAsignablesPor(RolConocido)"/> que además
    /// valida la consistencia rol/alcance (doc 09: <c>root</c> es siempre de plataforma,
    /// <c>admin</c> siempre de un tenant). Un rol usado fuera de su alcance esperado no
    /// tiene roles asignables: esa combinación no debería existir.</summary>
    public static IReadOnlyList<RolConocido> RolesAsignablesPor(RolConocido actor, bool esDePlataforma)
    {
        var esConsistente = (actor == RolConocido.Root) == esDePlataforma;
        return esConsistente ? RolesAsignablesPor(actor) : [];
    }
}

/// <summary>Identidad tenant-aware del actor que ejecuta una acción de gestión.
/// <see cref="IdTenant"/> <c>null</c> ⇒ staff de plataforma (root); un valor ⇒ pertenece
/// a ese tenant.</summary>
public readonly record struct ActorDeGestion(RolConocido Rol, int Id, int? IdTenant)
{
    public bool EsDePlataforma => IdTenant is null;
}
