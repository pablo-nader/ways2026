namespace Ways.Domain.Pagos;

/// <summary>
/// Categoría de un <see cref="Pago"/> (doc 10 §5/§7). Enum nativo de Postgres
/// (<c>categoria_pago</c>). Ninguno de estos valores representa un retiro de efectivo (spec:
/// No Magic Tipo Encodes A Retiro As A Pago) — el legacy <c>tipo = 95</c> murió con esta etapa,
/// un retiro se registra en <c>movimientos_caja</c>.
/// </summary>
public enum CategoriaPago
{
    Proveedor,
    Sueldos,
    Viaticos,
    Impuestos,
    Servicios,
    Otros
}
