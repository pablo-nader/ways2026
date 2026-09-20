using Ways.Domain.Usuarios;

namespace Ways.Application.Usuarios;

/// <summary>Login es por <c>mail</c>, no por <c>usuario</c> (flow B, doc 09 stage 1): el
/// mail resuelve la cuenta y, con ella, el tenant, sin que el request cargue contexto de
/// tenant alguno.</summary>
public record SolicitudDeLogin(string Mail, string Password);

/// <summary>Login de cajero contra un dispositivo ya vinculado (stage-desktop-pos,
/// <c>POST /api/auth/login-dispositivo</c>): por <c>usuario</c>, no por <c>mail</c> — el
/// dispositivo ya fija el tenant, así que no hace falta un identificador global.
/// <paramref name="SolicitarBearer"/> (slice bearer): <c>false</c> por default — el llamador
/// web/de hoy no lo manda y sigue recibiendo únicamente la cookie <c>ways.sesion</c>, sin
/// cambio de forma en la respuesta. El shell de escritorio (Tauri, slice 3) lo pone en
/// <c>true</c> para además recibir el token bearer en el cuerpo (<c>AuthEndpoints</c>).</summary>
public record SolicitudDeLoginDeDispositivo(string Usuario, string Password, bool SolicitarBearer = false);

public record UsuarioAutenticado(
    int Id,
    string Usuario,
    string Mail,
    int RolId,
    string Rol,
    DateTimeOffset? UltimaConexion,
    int? IdTenant);

/// <summary><paramref name="NombreTenant"/> viene en <c>null</c> en dos casos: cuando la cuenta
/// es de plataforma (<paramref name="IdTenant"/> nulo) y cuando el tenant dueño está dado de baja
/// lógicamente (<paramref name="IdTenant"/> no nulo, nombre igual ausente — el huérfano de design
/// D13). No es un "si y solo si": un nombre nulo NO implica personal de plataforma, esa distinción
/// la da <paramref name="IdTenant"/>. La API tampoco fabrica la etiqueta <c>"Plataforma"</c> —
/// esa copia la pone la web (design D14). El nombre de un tenant es texto libre, así que un tenant
/// llamado "Plataforma" sería indistinguible de una cuenta de plataforma si el servidor la
/// inventara, y el filtro, que se apoya en <paramref name="IdTenant"/>, discreparía de la columna
/// que tiene arriba.</summary>
public record UsuarioListado(
    int Id,
    string Usuario,
    string Mail,
    int RolId,
    string Rol,
    EstadoUsuario Estado,
    DateTimeOffset? UltimaConexion,
    DateTimeOffset CreatedAt,
    int? IdTenant,
    string? NombreTenant);

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
