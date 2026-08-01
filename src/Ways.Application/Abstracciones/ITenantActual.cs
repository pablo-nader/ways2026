namespace Ways.Application.Abstracciones;

/// <summary>
/// Modo de acceso vigente en la conexión actual (doc 09). Viaja como GUC de Postgres
/// (<c>app.acceso</c>) y es lo que las policies de RLS evalúan.
/// </summary>
public enum ModoDeAcceso
{
    /// <summary>Sin contexto resuelto. Las policies de RLS no ven nada: falla cerrado.</summary>
    Ninguno,

    /// <summary>Sesión de un usuario de tenant: ve solo las filas de su tenant.</summary>
    Tenant,

    /// <summary>Sesión de staff de plataforma (root): ve todos los tenants.</summary>
    Plataforma,

    /// <summary>Único modo permitido antes de resolver la sesión: <c>POST /api/auth/login</c>,
    /// que solo puede leer y actualizar <c>usuarios</c>.</summary>
    Login
}

/// <summary>
/// Tenant resuelto para la operación en curso. Se puebla una sola vez, en
/// <c>OnValidatePrincipal</c> (ADR-2): nunca viaja como parámetro editable por el cliente.
/// </summary>
public interface ITenantActual
{
    /// <summary><c>null</c> cuando <see cref="Modo"/> no es <see cref="ModoDeAcceso.Tenant"/>.</summary>
    int? Id { get; }

    ModoDeAcceso Modo { get; }

    bool EsPlataforma => Modo == ModoDeAcceso.Plataforma;
}
