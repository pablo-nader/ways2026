namespace Ways.Application.Organizacion;

public record SolicitudDeAprovisionamiento(
    string NombreTenant, string RazonSocialEmpresa, string NombrePuntoVenta, string MailAdmin);

/// <summary><paramref name="PasswordTemporal"/> se devuelve UNA sola vez, en esta respuesta:
/// no se persiste en texto plano en ningún lado (ADR-16) — solo el hash queda en
/// <c>usuarios</c>.</summary>
public record ResultadoAprovisionamiento(
    int IdTenant, int IdEmpresa, int IdPuntoVenta, int IdUsuarioAdmin, string PasswordTemporal);
