namespace Ways.Domain.Gastos;

/// <summary>
/// Categoría de un <see cref="Gasto"/> (doc 10 §5/§7). Enum nativo de Postgres
/// (<c>categoria_gasto</c>). Ninguno de estos valores representa un retiro de efectivo (spec:
/// No Magic Tipo Encodes A Retiro As A Gasto) — el legacy <c>tipo = 95</c> murió con esta etapa,
/// un retiro se registra en <c>movimientos_caja</c>.
/// </summary>
public enum CategoriaGasto
{
    Proveedor,
    Sueldos,
    Viaticos,
    Impuestos,
    Servicios,
    Otros
}
