using Ways.Domain.Usuarios;

namespace Ways.Application.Abstracciones;

/// <summary>Datos del usuario autenticado en la request en curso.</summary>
public interface IContextoDeUsuario
{
    bool EstaAutenticado { get; }
    int UsuarioId { get; }
    string NombreUsuario { get; }
    RolConocido Rol { get; }
}
