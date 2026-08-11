using Ways.Domain.Gastos;
using Ways.Domain.Reportes;

namespace Ways.Application.Reportes;

/// <summary>Un bucket de la serie de ventas ya rellenada (sin huecos, design decisión 4).
/// <see cref="TicketPromedio"/> es <c>null</c>, nunca <c>0</c>, cuando el bucket no tuvo ningún
/// TX — un denominador vacío no tiene respuesta.</summary>
public sealed record BucketDeVentas(string Etiqueta, DateOnly Inicio, decimal Neto, int CantidadTx, decimal? TicketPromedio);

/// <summary>Respuesta de <c>GET /api/reportes/ventas/resumen</c> (design: Interfaces /
/// Contracts). <see cref="ZonaHoraria"/> es la zona efectivamente resuelta y aplicada al
/// bucketing — echo obligatorio (design decisión 5): un número cuyo corte de día es invisible no
/// es auditable.</summary>
public sealed record ResumenDeVentas(
    DateOnly Desde, DateOnly Hasta, Granularidad Granularidad, string ZonaHoraria,
    IReadOnlyList<BucketDeVentas> Serie,
    decimal NetoVendido, int CantidadTx, decimal? TicketPromedio, int CantidadNcx, decimal NetoNcx);

/// <summary>Fila de <c>GET /api/reportes/compras/por-proveedor</c> (stage-10 slice 5) — ya
/// filtrada a compras <c>Confirmada</c> dentro del rango (spec: Compras Bucketed By Fecha De
/// Recepción, Confirmada Only). Subtotal propio, sin porcentaje de un total implícito — mismo
/// criterio que las rupturas por dimensión de ventas (spec: Ventas Breakdown Endpoints).</summary>
public sealed record CompraPorProveedor(int IdProveedor, string NombreProveedor, decimal Total, int CantidadCompras);

/// <summary>Respuesta de <c>GET /api/reportes/compras/por-proveedor</c>.</summary>
public sealed record ComprasPorProveedor(
    DateOnly Desde, DateOnly Hasta, IReadOnlyList<CompraPorProveedor> PorProveedor, decimal TotalGeneral);

/// <summary>Un bucket de la serie de gastos ya rellenada — mismo criterio de gap-fill que
/// <see cref="BucketDeVentas"/> (design decisión 4).</summary>
public sealed record BucketDeGastos(string Etiqueta, DateOnly Inicio, decimal Importe);

/// <summary>Desglose por categoría de <c>GET /api/reportes/gastos/resumen</c> (spec: Gastos
/// Resumen, "optionally grouped by categoria") — agregado LINQ aparte de la serie cruda: solo la
/// serie por fecha necesita SQL crudo (design decisión 1), la categoría no.</summary>
public sealed record GastoPorCategoria(CategoriaGasto Categoria, decimal Importe, int CantidadGastos);

/// <summary>Respuesta de <c>GET /api/reportes/gastos/resumen</c> — misma forma que
/// <see cref="ResumenDeVentas"/> más <see cref="PorCategoria"/>; sin NCX ni ticket promedio,
/// <c>gastos</c> no tiene esa semántica.</summary>
public sealed record ResumenDeGastos(
    DateOnly Desde, DateOnly Hasta, Granularidad Granularidad, string ZonaHoraria,
    IReadOnlyList<BucketDeGastos> Serie, decimal ImporteTotal, IReadOnlyList<GastoPorCategoria> PorCategoria);
