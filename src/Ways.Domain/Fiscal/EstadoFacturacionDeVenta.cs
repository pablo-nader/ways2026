namespace Ways.Domain.Fiscal;

/// <summary>
/// Ciclo de vida de una <see cref="FacturacionDeVenta"/>. Enum nativo de Postgres
/// (<c>estado_facturacion_venta</c>), registrado por <c>MapEnum</c> en los mismos sitios que el resto de
/// los enums. El ORDEN de los miembros es el orden de valores del tipo nativo: <c>dotnet ef migrations
/// add</c> los serializa alfabéticamente y la migración lo corrige a mano.
/// </summary>
public enum EstadoFacturacionDeVenta
{
    Pendiente,
    Emitida,
    Rechazada,
    Cancelada
}
