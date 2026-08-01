using Ways.Domain.Usuarios;

namespace Ways.Application.Abstracciones;

/// <summary>Datos del usuario autenticado en la request en curso.</summary>
public interface IContextoDeUsuario
{
    bool EstaAutenticado { get; }
    int UsuarioId { get; }
    string NombreUsuario { get; }
    RolConocido Rol { get; }

    /// <summary><c>null</c> para staff de plataforma (root); el tenant de la cuenta en
    /// cualquier otro caso (doc 09).</summary>
    int? IdTenant { get; }
}
