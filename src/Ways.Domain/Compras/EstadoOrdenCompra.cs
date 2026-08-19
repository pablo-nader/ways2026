namespace Ways.Domain.Compras;

/// <summary>
/// Ciclo de vida de una <see cref="OrdenCompra"/> (proposal: Modelo de datos propuesto — §A,
/// design: Domain — pure, no database). Enum nativo de Postgres (<c>estado_orden_compra</c>),
/// registrado por <c>npgsql.MapEnum&lt;EstadoOrdenCompra&gt;("estado_orden_compra")</c> en
/// ambos sitios (slice 1, task 1.17) — nunca también vía <c>HasPostgresEnum</c>. El ORDEN de
/// los miembros ES el orden de valores del tipo nativo — <c>dotnet ef migrations add</c> los
/// serializa alfabéticamente por defecto, corregido a mano en la migración (mismo residuo
/// documentado en <c>EstadoCompra</c>/stage-15).
///
/// Un solo escritor por valor: <see cref="Borrador"/> ← <c>POST /api/ordenes-compra</c>;
/// <see cref="Enviada"/> ← <c>POST /{id}/enviar</c>; <see cref="RecibidaParcial"/>/
/// <see cref="Cerrada"/> ← <c>EscriturasDeOrdenDeCompra.ProyectarEstadoAsync</c> (y
/// <see cref="Cerrada"/> también ← <c>POST /{id}/cerrar</c>); <see cref="Anulada"/> ←
/// <c>POST /{id}/anular</c>. <see cref="Anulada"/> es terminal: la proyección nunca la
/// abandona (design decisión 2/9). Ningún valor especulativo.
/// </summary>
public enum EstadoOrdenCompra
{
    Borrador,
    Enviada,
    RecibidaParcial,
    Cerrada,
    Anulada
}
