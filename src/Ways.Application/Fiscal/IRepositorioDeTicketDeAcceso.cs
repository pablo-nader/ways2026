namespace Ways.Application.Fiscal;

/// <summary>
/// Puerto de cache del TA (design decisión D8): la implementación de 19a vive en memoria —
/// persistir un TA es persistir una credencial portadora, y eso queda como ítem de gate para 19b
/// (proposal decisión 10, tabla <c>tickets_acceso_fiscal</c>). <see cref="ObtenerVigenteAsync"/>
/// devuelve <c>null</c> tanto si no hay ticket cacheado como si el que hay ya cruzó el margen de
/// seguridad — el llamador no distingue esos dos casos, en ninguno hay nada reusable.
/// </summary>
public interface IRepositorioDeTicketDeAcceso
{
    Task<TicketDeAcceso?> ObtenerVigenteAsync(ClaveDeTicket clave, CancellationToken ct);
    Task GuardarAsync(ClaveDeTicket clave, TicketDeAcceso ticket, CancellationToken ct);
}
