using Ways.Domain.Organizacion;

namespace Ways.Application.Organizacion;

public record SolicitudDeAprovisionamiento(
    string NombreTenant, string RazonSocialEmpresa, string NombrePuntoVenta, string MailAdmin);

/// <summary><paramref name="PasswordTemporal"/> se devuelve UNA sola vez, en esta respuesta:
/// no se persiste en texto plano en ningún lado (ADR-16) — solo el hash queda en
/// <c>usuarios</c>.</summary>
public record ResultadoAprovisionamiento(
    int IdTenant, int IdEmpresa, int IdPuntoVenta, int IdUsuarioAdmin, string PasswordTemporal);

// --- Lectura/edición de organización (ServicioDeOrganizacion) ---
// Alta y baja siguen siendo plataforma-only vía ServicioDeAprovisionamiento (ADR-16); estos
// contratos son solo listado/detalle/edición de datos descriptivos + suspensión de tenants.

/// <summary>Los tres contadores son hijos VIVOS del tenant (el filtro <c>"BajaLogica"</c> corre
/// dentro de las subconsultas correlacionadas de <see cref="ServicioDeOrganizacion"/>) y
/// <paramref name="CantidadUsuarios"/> cuenta solo cuentas del tenant: el personal de plataforma
/// (<c>id_tenant IS NULL</c>) no se cuenta bajo ningún tenant.</summary>
public record TenantListado(
    int Id,
    string Nombre,
    EstadoTenant Estado,
    DateTimeOffset CreatedAt,
    int CantidadEmpresas,
    int CantidadPuntosVenta,
    int CantidadUsuarios);

/// <summary>Solo el nombre: <see cref="EstadoTenant"/> se cambia por las acciones dedicadas
/// (suspender/reactivar), no por esta edición general.</summary>
public record TenantEdicion(string Nombre);

/// <summary><paramref name="NombreTenant"/> es nullable a propósito (design D13): si el tenant
/// dueño quedó dado de baja, la empresa se sigue listando con el nombre en <c>null</c> — se
/// muestra como anomalía en vez de desaparecer del listado. <paramref name="IdTenant"/> deja de
/// renderizarse y pasa a ser la clave del filtro por tenant.</summary>
public record EmpresaListado(
    int Id,
    int IdTenant,
    string RazonSocial,
    string? NombreFantasia,
    string? Cuit,
    string? NombreTenant);

public record EmpresaEdicion(string RazonSocial, string? NombreFantasia, string? Cuit);

/// <summary>Los dos nombres de dueño son nullable por el mismo criterio que
/// <see cref="EmpresaListado.NombreTenant"/> (design D13); <paramref name="IdTenant"/> e
/// <paramref name="IdEmpresa"/> dejan de renderizarse y quedan como claves de los dos filtros.</summary>
public record PuntoVentaListado(
    int Id,
    int IdTenant,
    int IdEmpresa,
    string Nombre,
    string? Domicilio,
    string? Horario,
    string? Whatsapp,
    string? Instagram,
    string? Facebook,
    string? Web,
    string? NombreTenant,
    string? RazonSocialEmpresa);

/// <summary><see cref="PuntoVentaListado.IdEmpresa"/> no es editable acá: es estructural
/// (a qué empresa pertenece), no descriptivo — moverlo de empresa queda fuera de esta
/// edición.</summary>
public record PuntoVentaEdicion(
    string Nombre,
    string? Domicilio,
    string? Horario,
    string? Whatsapp,
    string? Instagram,
    string? Facebook,
    string? Web);
