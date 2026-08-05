namespace Ways.Domain.Compras;

/// <summary>
/// Ciclo de vida de un <see cref="ComprobanteCompra"/> (doc 10 §5, design: Table Shapes — C).
/// Enum nativo de Postgres (<c>estado_compra</c>), registrado por
/// <c>npgsql.MapEnum&lt;EstadoCompra&gt;("estado_compra")</c> en ambos sitios (Slice 1, task
/// 1.6) — nunca también vía <c>HasPostgresEnum</c>.
///
/// <see cref="Borrador"/> es el único estado editable (mutación segura porque no produjo ningún
/// movimiento todavía); <see cref="Confirmada"/> y <see cref="Anulada"/> son terminales de
/// escritura de items — la única transición posterior a <see cref="Confirmada"/> es hacia
/// <see cref="Anulada"/> (<c>ServicioDeCompras.AnularAsync</c>, Slice 2, <c>UPDATE ... WHERE
/// estado = 'confirmada'</c> condicional, el mismo patrón que <c>ComprobanteVenta.Estado</c>).
/// </summary>
public enum EstadoCompra
{
    Borrador,
    Confirmada,
    Anulada
}
