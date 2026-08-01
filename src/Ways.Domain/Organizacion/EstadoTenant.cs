namespace Ways.Domain.Organizacion;

/// <summary>
/// Estado del tenant. Se persiste como enum nativo de PostgreSQL (<c>estado_tenant</c>),
/// mismo criterio que <see cref="Ways.Domain.Usuarios.EstadoUsuario"/>.
/// </summary>
public enum EstadoTenant
{
    /// <summary>Opera con normalidad.</summary>
    Activo,

    /// <summary>Bloqueado por el operador de la plataforma. Sus usuarios no pueden iniciar sesión.</summary>
    Suspendido,

    /// <summary>Dado de baja. No se elimina físicamente: los datos quedan para exportación.</summary>
    Baja
}
