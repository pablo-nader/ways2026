using Ways.Domain.Usuarios;

namespace Ways.Application.Usuarios;

/// <summary>Login es por <c>mail</c>, no por <c>usuario</c> (flow B, doc 09 stage 1): el
/// mail resuelve la cuenta y, con ella, el tenant, sin que el request cargue contexto de
/// tenant alguno.</summary>
public record SolicitudDeLogin(string Mail, string Password);

public record UsuarioAutenticado(
    int Id,
    string Usuario,
    string Mail,
    int RolId,
    string Rol,
    DateTimeOffset? UltimaConexion,
    int? IdTenant);

public record UsuarioListado(
    int Id,
    string Usuario,
    string Mail,
    int RolId,
    string Rol,
    EstadoUsuario Estado,
    DateTimeOffset? UltimaConexion,
    DateTimeOffset CreatedAt);

/// <summary><paramref name="IdTenant"/> solo lo usa un actor de plataforma para elegir a
/// qué tenant pertenece la cuenta creada; un actor de tenant siempre crea dentro del suyo
/// propio y este valor se ignora (<see cref="ServicioDeUsuarios"/>).</summary>
public record CrearUsuario(
    string Usuario,
    string Mail,
    int RolId,
    string Password,
    EstadoUsuario Estado = EstadoUsuario.Activo,
    int? IdTenant = null);

public record ActualizarUsuario(
    string Usuario,
    string Mail,
    int RolId,
    EstadoUsuario Estado);

public record CambiarPassword(string PasswordNueva);

public record RolListado(int Id, string Nombre, string? Descripcion);

public record PaginaDe<T>(IReadOnlyList<T> Items, int Total, int Pagina, int Tamanio);
