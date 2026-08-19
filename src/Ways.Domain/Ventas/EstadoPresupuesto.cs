namespace Ways.Domain.Ventas;

/// <summary>
/// Ciclo de vida de un <see cref="Presupuesto"/> (proposal: Modelo de datos propuesto — §A,
/// design: Domain — pure, no database). Enum nativo de Postgres (<c>estado_presupuesto</c>),
/// registrado por <c>npgsql.MapEnum&lt;EstadoPresupuesto&gt;("estado_presupuesto")</c> en
/// ambos sitios (slice 1, task 1.18) — nunca también vía <c>HasPostgresEnum</c>. El ORDEN de
/// los miembros ES el orden de valores del tipo nativo — <c>dotnet ef migrations add</c> los
/// serializa alfabéticamente por defecto, corregido a mano en la migración (mismo residuo
/// documentado en <c>EstadoOrdenCompra</c>/stage-16 y en la de la etapa 15).
///
/// Un solo escritor por valor: <see cref="Borrador"/> ← <c>POST /api/presupuestos</c> (slice 2);
/// <see cref="Enviado"/> ← <c>POST /{id}/enviar</c> (slice 2); <see cref="Convertido"/> ← el
/// <c>UPDATE</c> guardado dentro de la transacción de venta (slice 3,
/// <c>EscriturasDePresupuesto.MarcarConvertidoAsync</c>); <see cref="Anulado"/> ←
/// <c>POST /{id}/anular</c> (slice 2). <see cref="Convertido"/> es terminal: ningún camino lo
/// abandona (proposal decisión 9, design decisión 4/tensión T1). Ningún valor especulativo.
/// </summary>
public enum EstadoPresupuesto
{
    Borrador,
    Enviado,
    Convertido,
    Anulado
}
