namespace Ways.Domain.Articulos;

/// <summary>
/// Unidad de venta de un artículo (doc 10 §3). Enum nativo de Postgres (<c>unidad_venta</c>).
/// <see cref="Peso"/> habilita cantidad con decimales (p.ej. <c>12,3</c> kg) en el futuro
/// motor de venta (stage 5) — sin uso propio en esta etapa más allá de declarar el valor.
/// </summary>
public enum UnidadVenta
{
    Unidad,
    Peso
}
