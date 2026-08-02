namespace Ways.Domain.Catalogos;

/// <summary>
/// A qué lado del negocio pertenece un <see cref="TipoComprobante"/> (doc 10 §1). Enum
/// nativo de Postgres (<c>clase_comprobante</c>).
/// </summary>
public enum ClaseComprobante
{
    Venta,
    Compra
}
