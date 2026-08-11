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
/// <summary>Una fila de <c>GET /api/reportes/articulos/top</c> (design decisión 10): agrupada por
/// <c>id_articulo</c> pero etiquetada con el snapshot de <c>descripcion</c> de la línea más
/// reciente del período — nunca re-unida contra <c>articulos</c> (doc-10 principio 6: la línea es
/// inmutable, un artículo renombrado o dado de baja no debe alterar retroactivamente un reporte ya
/// vendido). <see cref="Cantidad"/> y <see cref="Total"/> son netos: una NCX resta por
/// construcción, sin rama de signo (spec: An NCX Line Reduces Its Article's Ranking Figures).</summary>
public sealed record ArticuloTop(int IdArticulo, string Descripcion, decimal Cantidad, decimal Total);

/// <summary>Respuesta de <c>GET /api/reportes/articulos/top</c>. <see cref="Articulos"/> viene
/// ordenada por <see cref="ArticuloTop.Total"/> descendente (spec reportes-de-gestion: Top
/// Artículos Ranks By Net Quantity And Revenue) — sin costo ni margen: eso vive en
/// <c>/rentabilidad</c>, bajo <c>LecturaDeRentabilidad</c>.</summary>
public sealed record TopArticulos(
    DateOnly Desde, DateOnly Hasta, string ZonaHoraria, IReadOnlyList<ArticuloTop> Articulos);
/// <summary>Desglose de margen por artículo dentro de un período de rentabilidad (stage-10 slice
/// 4). Agrupa por <c>id_articulo</c> pero etiqueta con la <see cref="Descripcion"/> snapshot de la
/// línea (design decisión 10: nunca re-join contra <c>articulos</c>) — <c>IdArticulo</c> es
/// <c>null</c> en una línea de concepto libre. Solo incluye líneas efectivamente consideradas en el
/// margen (design: Interfaces / Contracts); <see cref="MargenPorcentaje"/> es nullable, nunca
/// <c>0</c>, con el mismo criterio que <see cref="ResumenDeVentas.TicketPromedio"/>.</summary>
public sealed record RentabilidadPorArticulo(
    int? IdArticulo, string Descripcion, decimal VentaConsiderada, decimal CostoConsiderado,
    decimal Margen, decimal? MargenPorcentaje);

/// <summary>Respuesta de <c>GET /api/reportes/rentabilidad</c> (design: Interfaces / Contracts).
/// <see cref="Cobertura"/> viaja SIEMPRE (spec rentabilidad-y-comisiones: NULL Cost Is Never Treated
/// As Zero, And Coverage Is Mandatory) — no existe una respuesta sin ella.</summary>
public sealed record Rentabilidad(
    DateOnly Desde, DateOnly Hasta, string ZonaHoraria,
    decimal VentaConsiderada, decimal CostoConsiderado, decimal Margen, decimal? MargenPorcentaje,
    CoberturaDeCosto Cobertura, IReadOnlyList<RentabilidadPorArticulo> PorArticulo);
/// <summary>Una fila de <c>ventas/por-punto-venta</c> — mismo criterio de <see cref="Neto"/> y
/// <see cref="TicketPromedio"/> que <see cref="BucketDeVentas"/> (design decisión 9: <c>Neto</c>
/// ya viene neto de NCX por construcción; <see cref="TicketPromedio"/> es <c>null</c>, nunca
/// <c>0</c>, sin ningún TX en el punto de venta).</summary>
public sealed record FilaVentasPorPuntoVenta(int IdPuntoVenta, decimal Neto, int CantidadTx, decimal? TicketPromedio);

/// <summary>Respuesta de <c>GET /api/reportes/ventas/por-punto-venta</c> (spec reportes-de-gestion:
/// Ventas Breakdown Endpoints By Punto De Venta, Vendedor, Medio De Pago) — cada fila reporta su
/// propio subtotal, nunca un porcentaje de un total implícito.</summary>
public sealed record VentasPorPuntoVenta(
    DateOnly Desde, DateOnly Hasta, string ZonaHoraria, IReadOnlyList<FilaVentasPorPuntoVenta> Filas);

/// <summary>Una fila de <c>ventas/por-vendedor</c>, agrupada por <c>id_empleado</c> (el vendedor
/// emisor, design decisión 11: hoy es <c>IContextoDeUsuario.UsuarioId</c> — no existe tabla
/// <c>empleados</c> separada todavía).</summary>
public sealed record FilaVentasPorVendedor(int IdEmpleado, decimal Neto, int CantidadTx, decimal? TicketPromedio);

/// <summary>Respuesta de <c>GET /api/reportes/ventas/por-vendedor</c>.</summary>
public sealed record VentasPorVendedor(
    DateOnly Desde, DateOnly Hasta, string ZonaHoraria, IReadOnlyList<FilaVentasPorVendedor> Filas);

/// <summary>Una fila de <c>ventas/por-medio-pago</c>, agrupada por <c>pagos_comprobante.id_medio_pago</c>.
/// <see cref="Neto"/> es <c>Σ (importe × signo del tipo de comprobante del encabezado)</c>: el
/// importe de un pago nunca es negativo (CHECK <c>ck_pagos_comprobante_importe_no_negativo</c>),
/// así que el signo lo aporta el encabezado — mismo discriminador que <c>ventas/resumen</c>
/// (design decisión 9), ninguna rama condicional. <see cref="CantidadPagos"/> cuenta filas de
/// <c>pagos_comprobante</c>, no comprobantes — un TX con pago dividido entre dos medios aporta una
/// fila a cada uno.</summary>
public sealed record FilaVentasPorMedioPago(int IdMedioPago, decimal Neto, int CantidadPagos);

/// <summary>Respuesta de <c>GET /api/reportes/ventas/por-medio-pago</c>.</summary>
public sealed record VentasPorMedioPago(
    DateOnly Desde, DateOnly Hasta, string ZonaHoraria, IReadOnlyList<FilaVentasPorMedioPago> Filas);

/// <summary>Fila de <c>GET /api/reportes/comisiones</c> (stage-10 slice 10, PROVISIONAL —
/// droppable en su totalidad), agrupada por <c>id_empleado</c> — mismo discriminador de vendedor
/// que <see cref="FilaVentasPorVendedor"/> (design decisión 11), reusa exactamente su agregado de
/// venta neta. <see cref="Comision"/> = <see cref="NetoVendido"/> × la tasa de la respuesta
/// (<see cref="Comisiones.ComisionPorcentaje"/>) / 100.</summary>
public sealed record ComisionPorEmpleado(int IdEmpleado, decimal NetoVendido, decimal Comision);

/// <summary>Respuesta de <c>GET /api/reportes/comisiones</c> (spec rentabilidad-y-comisiones:
/// Comisiones Is A Provisional, Non-Persisted Report). <see cref="ComisionPorcentaje"/> es la tasa
/// efectivamente resuelta (<c>ParametroConocido.ComisionPorcentaje</c>, PV → empresa → default
/// <c>0</c>) — echo obligatorio, mismo criterio que <see cref="ResumenDeVentas.ZonaHoraria"/>: con
/// el default en <c>0</c> ninguna fila tiene comisión distinta de cero.
/// <see cref="Provisional"/> viaja SIEMPRE en <c>true</c> — no existe una respuesta de este
/// endpoint que no lo sea, la fórmula espera la decisión real del dueño del producto (design: Open
/// Questions, "Commission rate scope").</summary>
public sealed record Comisiones(
    DateOnly Desde, DateOnly Hasta, string ZonaHoraria, decimal ComisionPorcentaje,
    IReadOnlyList<ComisionPorEmpleado> Filas, bool Provisional);
