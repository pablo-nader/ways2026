using System.Text.Json;

namespace Ways.Application.Auditoria;

/// <summary>
/// Los 7 filtros de <c>GET /api/auditoria</c> (design: Interfaces/Contracts, decisiones 12-16).
/// <c>dto-contract-honesty</c>: cada campo se lee EXCLUSIVAMENTE dentro de
/// <see cref="ServicioDeConsultaDeAuditoria.ConstruirQuery"/> — ningún filtro se acepta acá y se
/// descarta en silencio. <see cref="IdEntidad"/> sin <see cref="Entidad"/> es un `400
/// entidad_requerida` (design decisión 16): `id_entidad` es polimórfico, así que solo tiene
/// sentido junto con la entidad que lo desambigua.
/// </summary>
public sealed record FiltrosDeAuditoria(
    DateTimeOffset? Desde,
    DateTimeOffset? Hasta,
    string? Accion,
    int? IdActor,
    string? Entidad,
    int? IdEntidad,
    int? IdPuntoVenta);

/// <summary>
/// Una fila del log de auditoría tal como la ve la lectura (design decisiones 12/14).
/// <see cref="Actor"/> <c>null</c> significa "el nombre no es visible para esta sesión" (un
/// actor de plataforma, excluido por el filtro de tenant/RLS de <c>usuarios</c> bajo el LEFT
/// JOIN) — NUNCA "sin actor": <see cref="IdActor"/> siempre viaja, la pantalla lo muestra como
/// <c>#idActor</c>. <see cref="ValorAnterior"/>/<see cref="ValorNuevo"/> viajan como JSON crudo
/// — el cliente decide cómo mostrarlos, esta capa no los reinterpreta.
/// </summary>
public sealed record FilaDeAuditoria(
    long IdAuditoria,
    DateTimeOffset CreadoEl,
    string Accion,
    string Entidad,
    int IdEntidad,
    int IdActor,
    string? Actor,
    int? IdPuntoVenta,
    JsonElement? ValorAnterior,
    JsonElement ValorNuevo);

/// <summary>Página offset (design decisión 12: 7 precedentes de <c>PaginaDe*</c> en el repo, cero
/// keyset) — <c>Total</c> es lo que habilita "Página N de M" en el pager web (Slice 7).</summary>
public sealed record PaginaDeAuditoria(IReadOnlyList<FilaDeAuditoria> Items, int Total, int Pagina, int Tamanio);
