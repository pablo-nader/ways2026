namespace Ways.Domain.Compras;

/// <summary>
/// Quién cargó un <see cref="ComprobanteCompra"/>. Enum nativo de Postgres
/// (<c>origen_compra</c>), registrado por <c>npgsql.MapEnum&lt;OrigenCompra&gt;("origen_compra")</c>
/// en cada sitio que ya mapea <see cref="EstadoCompra"/> — nunca también vía
/// <c>HasPostgresEnum</c>.
///
/// Lo fija solo la capa de aplicación al crear el borrador y no cambia después: la API REST
/// nunca lo acepta. <see cref="Asistente"/> marca un borrador cargado desde una factura por el
/// conector; la confirmación sigue siendo humana.
/// </summary>
public enum OrigenCompra
{
    Manual,
    Asistente
}
