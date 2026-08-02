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

public record TenantListado(int Id, string Nombre, EstadoTenant Estado, DateTimeOffset CreatedAt);

/// <summary>Solo el nombre: <see cref="EstadoTenant"/> se cambia por las acciones dedicadas
/// (suspender/reactivar), no por esta edición general.</summary>
public record TenantEdicion(string Nombre);

public record EmpresaListado(int Id, int IdTenant, string RazonSocial, string? NombreFantasia, string? Cuit);

public record EmpresaEdicion(string RazonSocial, string? NombreFantasia, string? Cuit);

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
    string? Web);

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
