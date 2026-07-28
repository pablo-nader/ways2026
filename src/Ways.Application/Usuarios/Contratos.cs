using Ways.Domain.Usuarios;

namespace Ways.Application.Usuarios;

public record SolicitudDeLogin(string Usuario, string Password);

public record UsuarioAutenticado(
    int Id,
    string Usuario,
    string Mail,
    int RolId,
    string Rol,
    DateTimeOffset? UltimaConexion);

public record UsuarioListado(
    int Id,
    string Usuario,
    string Mail,
    int RolId,
    string Rol,
    EstadoUsuario Estado,
    DateTimeOffset? UltimaConexion,
    DateTimeOffset CreatedAt);

public record CrearUsuario(
    string Usuario,
    string Mail,
    int RolId,
    string Password,
    EstadoUsuario Estado = EstadoUsuario.Activo);

public record ActualizarUsuario(
    string Usuario,
    string Mail,
    int RolId,
    EstadoUsuario Estado);

public record CambiarPassword(string PasswordNueva);

public record RolListado(int Id, string Nombre, string? Descripcion);

public record PaginaDe<T>(IReadOnlyList<T> Items, int Total, int Pagina, int Tamanio);
