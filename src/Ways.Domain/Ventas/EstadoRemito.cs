namespace Ways.Domain.Ventas;

/// <summary>
/// Ciclo de vida de un <see cref="Remito"/> (proposal: Modelo de datos propuesto — §A, design:
/// Domain — pure, no database). Enum nativo de Postgres (<c>estado_remito</c>), registrado por
/// <c>npgsql.MapEnum&lt;EstadoRemito&gt;("estado_remito")</c> en ambos sitios (slice 4, task
/// 4.19) — nunca también vía <c>HasPostgresEnum</c>. El ORDEN de los miembros ES el orden de
/// valores del tipo nativo — <c>dotnet ef migrations add</c> los serializa alfabéticamente por
/// defecto, corregido a mano en la migración (mismo residuo documentado en
/// <c>EstadoPresupuesto</c>/stage 17 slice 1).
///
/// Un solo escritor por valor: <see cref="Borrador"/> ← <c>POST /api/remitos</c> (slice 5);
/// <see cref="Emitido"/> ← <c>POST /{id}/emitir</c> (slice 5, el cuarto write site);
/// <see cref="Facturado"/> ← la consolidación (slice 6, <c>ServicioDeFacturacionDeRemitos</c>);
/// <see cref="Anulado"/> ← <c>POST /{id}/anular</c> (slice 5). <see cref="Facturado"/> puede
/// volver a <see cref="Emitido"/> únicamente por el desligue guardado de la anulación de su
/// <c>TXR</c> (<c>ck_remitos_facturacion</c> — estado y link vuelven JUNTOS, slice 6). Ningún
/// valor especulativo.
/// </summary>
public enum EstadoRemito
{
    Borrador,
    Emitido,
    Facturado,
    Anulado
}
