namespace Ways.Application.Fiscal;

/// <summary>
/// Puerto hacia WSAA: intercambia una TRA firmada por un Ticket de Acceso (design.md: Ports and
/// the CAE machine). Implementado por <c>Ways.Infrastructure.Fiscal.ClienteWsaa</c> contra un
/// mock local en 19a — el manual publica strings de fault simbólicos (T3 del design), y esta
/// slice no tiene ningún caller de producción: lo cablea <c>ServicioDeFacturacionFiscal</c> en la
/// slice 5.
/// </summary>
public interface IClienteWsaa
{
    Task<TicketDeAcceso> ObtenerTicketAsync(SolicitudDeTicket solicitud, CancellationToken ct);
}
