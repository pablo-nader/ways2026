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

    /// <summary>stage-desktop-pos: <c>null</c> para toda sesión web normal (login por mail);
    /// presente solo en una sesión iniciada por <c>POST /api/auth/login-dispositivo</c> — el
    /// <c>id_dispositivo</c> que la abrió. Default a <c>null</c> (miembro con cuerpo, C# 8+) para
    /// que los <c>ContextoFijo</c> de test que no modelan un dispositivo no tengan que
    /// implementarlo.</summary>
    int? IdDispositivo => null;
}
