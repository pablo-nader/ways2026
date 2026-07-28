using Ways.Domain.Common;

namespace Ways.Domain.Usuarios;

/// <summary>
/// Rol del sistema. Los IDs son fijos y conocidos por el código (ver <see cref="RolConocido"/>),
/// por eso la tabla no usa identity: se siembra con IDs explícitos.
/// Los permisos finos se agregan más adelante; por ahora el rol es la unidad de autorización.
/// </summary>
public class Rol : EntidadBase
{
    public int Id { get; set; }
    public required string Nombre { get; set; }
    public string? Descripcion { get; set; }

    public ICollection<Usuario> Usuarios { get; set; } = [];
}

/// <summary>
/// IDs de rol fijos. El orden importa: cuanto menor el valor, mayor el privilegio.
/// </summary>
public enum RolConocido
{
    Root = 1,
    Admin = 2,
    Supervisor = 3,
    Vendedor = 4
}
