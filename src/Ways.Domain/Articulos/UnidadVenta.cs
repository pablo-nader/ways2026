namespace Ways.Domain.Articulos;

/// <summary>
/// Unidad de venta de un artículo (doc 10 §3). Enum nativo de Postgres (<c>unidad_venta</c>).
/// Decide la granularidad de las cantidades (<see cref="ReglaDeCantidadDeVenta"/>):
/// <see cref="Unidad"/> se vende de a unidades enteras y <see cref="Peso"/> admite decimales
/// (p.ej. <c>12,3</c> kg). <c>ServicioDeVentas</c> la exige en las líneas de una venta online (no en
/// la ya cobrada offline, en la conversión de un presupuesto ni en una devolución) y las pantallas
/// la usan para el paso y el mínimo de los campos de cantidad.
/// </summary>
public enum UnidadVenta
{
    Unidad,
    Peso
}
