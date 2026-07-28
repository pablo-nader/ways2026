namespace Ways.Domain.Usuarios;

/// <summary>
/// Estado de la cuenta. Se persiste como enum nativo de PostgreSQL (<c>estado_usuario</c>),
/// no como número: en el legacy los <c>tipo</c> numéricos hacían ilegible cualquier consulta
/// a mano. Agregar un valor nuevo es un <c>ALTER TYPE ... ADD VALUE</c>.
/// </summary>
public enum EstadoUsuario
{
    /// <summary>Puede iniciar sesión.</summary>
    Activo,

    /// <summary>Dado de baja operativamente. No puede iniciar sesión.</summary>
    Inactivo,

    /// <summary>Bloqueado por intentos fallidos o por decisión de un administrador.</summary>
    Bloqueado
}
