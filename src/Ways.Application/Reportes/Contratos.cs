using Ways.Domain.Gastos;
using Ways.Domain.Reportes;
using Ways.Domain.Stock;

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

/// <summary>Una fila de <c>GET /api/reportes/stock/existencias</c> (stage-11-exportacion-reportes,
/// Slice 9; proposal decisión 10; design: "Two cap shapes, by report shape" — agregado acotado por
/// construcción). A diferencia de <see cref="ArticuloTop"/> (que etiqueta con el snapshot congelado
/// de una línea de venta), acá NO hay línea histórica que congelar — <c>stock</c> es estado
/// ACTUAL, así que <see cref="Nombre"/> sale del join en vivo contra <c>articulos</c> (spec:
/// Existencias Report Joins Stock To Artículos Under The Same Gate), nunca de un snapshot.
///
/// stage-13-stock-inteligente, Slice 2 (dto-contract-honesty — destino de cada campo nuevo):
/// <see cref="Minimo"/> y <see cref="Reposicion"/> se leen directo de la fila <c>stock</c> ya
/// unida (el mismo par que <c>PUT /api/stock/minimos</c> persiste, nunca re-derivado). Ambos
/// son de solo lectura acá — escribirlos es <c>PUT /api/stock/minimos</c>, bajo
/// <c>Politicas.GestionDeCatalogo</c>, una policy distinta de la de este reporte. <see
/// cref="Estado"/> se deriva vía <see cref="ReglaDeReposicion.Clasificar"/> sobre
/// <see cref="Cantidad"/>/<see cref="Minimo"/> — nunca una segunda definición del borde
/// <c>bajo</c>/<c>sin_minimo</c>/<c>ok</c> (design decisión 2, spec reportes-de-gestion: "Existencias
/// Report Joins Stock To Artículos Under The Same Gate").</summary>
public sealed record FilaExistencia(
    int IdArticulo, string Nombre, decimal Cantidad,
    decimal? Minimo, decimal? Reposicion, EstadoDeReposicion Estado);

/// <summary>Respuesta de <c>GET /api/reportes/stock/existencias</c>. Sin <c>desde</c>/<c>hasta</c>
/// ni <c>ZonaHoraria</c> (a diferencia del resto de los reportes de esta etapa): el stock no tiene
/// dimensión temporal, es una foto del estado actual.</summary>
public sealed record Existencias(int IdPuntoVenta, IReadOnlyList<FilaExistencia> Filas);

/// <summary>Fila de <c>GET /api/reportes/stock/vencimientos</c> (stage-12-lotes-vencimientos,
/// Slice 13; design decisión 15/16). Solo lotes con <c>stock_lotes.cantidad</c> positivo (spec
/// lotes-y-vencimientos: "A zero-balance lot never appears in the report") — <see cref="Articulo"/>
/// sale del join en vivo, mismo criterio que <see cref="FilaExistencia.Nombre"/> (estado actual,
/// nunca un snapshot). <see cref="FechaVencimiento"/> es <c>null</c> exactamente para el lote sin
/// identificar, que clasifica <see cref="EstadoDeVencimiento.SinFecha"/> y SE INCLUYE en el
/// reporte (nunca excluido — la omisión mentiría por defecto sobre el total).</summary>
public sealed record FilaDeVencimiento(
    int IdArticulo, string Articulo, int IdLote, string CodigoLote, DateOnly? FechaVencimiento,
    decimal Cantidad, EstadoDeVencimiento Estado);

/// <summary>Respuesta de <c>GET /api/reportes/stock/vencimientos</c>. <see cref="Hoy"/> y
/// <see cref="ZonaHoraria"/> son la fecha y la zona efectivamente resueltas para clasificar cada
/// fila — echo obligatorio (mismo criterio que <see cref="ResumenDeVentas.ZonaHoraria"/>): "hoy"
/// se calcula en la zona horaria del punto de venta, NUNCA en UTC (spec: "'Hoy' Is Resolved In The
/// Punto De Venta's Own Zona Horaria, Not UTC" — vinculante). <see cref="DiasDeAlerta"/> es el
/// horizonte efectivamente aplicado: el parámetro <c>dias</c> de la query si vino, si no el
/// <c>dias_alerta_vencimiento</c> resuelto (PV → empresa → default).</summary>
public sealed record Vencimientos(
    int IdPuntoVenta, DateOnly Hoy, int DiasDeAlerta, string ZonaHoraria, IReadOnlyList<FilaDeVencimiento> Filas);

/// <summary>Respuesta de <c>GET /api/reportes/stock/vencimientos/resumen</c> — el tile de Tablero
/// (spec: "a Tablero tile MUST surface the counts of vencido, por_vencer, and sin_fecha"). Mismos
/// tres conteos que <see cref="Vencimientos.Filas"/> agrupados por <see cref="FilaDeVencimiento.Estado"/>
/// — nunca una query de agregación separada, para que el tile y el reporte no puedan divergir.</summary>
public sealed record ResumenDeVencimientos(int IdPuntoVenta, int Vencidos, int PorVencer, int SinFecha);

/// <summary>Fila de <c>GET /api/reportes/stock/reposicion</c> (stage-13-stock-inteligente, Slice 4;
/// design: Interfaces / Contracts, decisión 3). Solo existe porque <c>minimo IS NOT NULL AND
/// cantidad &lt;= minimo</c> (spec reposicion-de-stock: The Low-Stock Boundary Is Inclusive), así
/// que <see cref="Minimo"/> viaja NO nullable — no hay fila sin mínimo. <c>dto-contract-honesty</c>:
/// <list type="bullet">
/// <item><see cref="Sugerido"/> — <see cref="Ways.Domain.Stock.ReglaDeReposicion.Sugerido"/>
/// aplicada a <see cref="Cantidad"/>/<see cref="Reposicion"/>: <c>null</c> (JAMÁS <c>0</c>) cuando
/// <see cref="Reposicion"/> es <c>null</c> (spec: sugerido Is Null, Never Zero, When Reposicion Is
/// Unset).</item>
/// <item><see cref="IdProveedor"/>/<see cref="Proveedor"/> — ambos <c>null</c> ⇒ la fila cae en el
/// grupo <c>"Sin proveedor"</c> (design decisión 3: el LEFT JOIN nunca la excluye), nunca
/// filtrada. Un <c>id_proveedor_habitual</c> que apunta a un proveedor soft-deleted proyecta
/// <see cref="IdProveedor"/> <c>null</c> también — el FK crudo NUNCA viaja al cliente cuando el
/// proveedor referenciado no resuelve (orchestrator decision 12, tasks.md stage-13): un solo
/// bucket "Sin proveedor", nunca un FK colgante ni un segundo bucket a mitad de lista.</item>
/// <item><see cref="ConsumoDiarioPromedio"/>/<see cref="DiasDeCobertura"/> — stage-13, Slice 5:
/// <see cref="Ways.Domain.Stock.ReglaDeReposicion.ConsumoDiario"/>/<see
/// cref="Ways.Domain.Stock.ReglaDeReposicion.DiasDeCobertura"/> aplicadas sobre el consumo que
/// <c>LeerConsumoAsync</c> lee de <c>movimientos_stock</c> en la ventana de rotación — AMBOS
/// <c>null</c> (JAMÁS <c>0</c>) cuando el artículo no tiene ningún movimiento calificado en la
/// ventana (spec: "A zero-history articulo shows no suggestion rather than a suggestion of
/// zero"), nunca una segunda definición de "consumo" distinta de la que usa <c>GET
/// /reportes/stock/rotacion</c> (design decisión 5).</item>
/// </list></summary>
public sealed record FilaDeReposicion(
    int IdArticulo, string Articulo, decimal Cantidad, decimal Minimo, decimal? Reposicion,
    decimal? Sugerido, int? IdProveedor, string? Proveedor,
    decimal? ConsumoDiarioPromedio, decimal? DiasDeCobertura);

/// <summary>Respuesta de <c>GET /api/reportes/stock/reposicion</c>. <see cref="Hoy"/> y
/// <see cref="ZonaHoraria"/> son la fecha y la zona resueltas para el punto de venta — mismo
/// criterio de echo obligatorio que <see cref="Vencimientos.Hoy"/>/<see cref="Vencimientos.ZonaHoraria"/>;
/// desde la slice 5 también gobiernan la ventana de rotación de cada fila (<see
/// cref="Ways.Domain.Stock.ReglaDeReposicion.VentanaDeRotacion"/>), nunca una segunda resolución de
/// "hoy". <see cref="DiasDeRotacion"/> es el horizonte efectivamente resuelto: el parámetro
/// <c>dias</c> de la query si vino, si no <c>dias_rotacion</c> (default 30).</summary>
public sealed record Reposicion(
    int IdPuntoVenta, DateOnly Hoy, int DiasDeRotacion, string ZonaHoraria,
    IReadOnlyList<FilaDeReposicion> Filas);

/// <summary>Respuesta de <c>GET /api/reportes/stock/reposicion/resumen</c> — el tile de Tablero
/// (stage-13-stock-inteligente, Slice 7; design decisión 8/9, con la corrección de nombre del
/// tercer campo registrada en orchestrator decision 5, tasks.md: <c>SinProveedor</c>, no la
/// <c>SinSugerencia</c> vieja de design.md). Los tres conteos salen de <see cref="Reposicion.Filas"/>
/// vía <c>ObtenerResumenDeReposicionAsync</c>, que reusa <c>ObtenerReposicionAsync</c> — nunca una
/// segunda query de agregación, mismo criterio que <see cref="ResumenDeVencimientos"/>.
/// <c>dto-contract-honesty</c>: <see cref="SinProveedor"/> cuenta el grupo <c>"Sin proveedor"</c>
/// (<see cref="FilaDeReposicion.IdProveedor"/> <c>null</c>) — NUNCA conflado con "sin sugerido"
/// (<see cref="FilaDeReposicion.Sugerido"/> <c>null</c>, <c>reposicion</c> sin configurar): son dos
/// causas distintas (falta cargar un proveedor vs. falta configurar un <c>reposicion</c>) detrás de
/// un número, y confundirlas es exactamente lo que esta doc-comment y la spec ratificada
/// (reposicion-de-stock: "sinProveedor counts the Sin proveedor group, not a missing suggestion")
/// prohíben.</summary>
public sealed record ResumenDeReposicion(int IdPuntoVenta, int BajoMinimo, int SinStock, int SinProveedor);

/// <summary>Fila de <c>GET /api/reportes/stock/rotacion</c> (stage-13-stock-inteligente, Slice 5;
/// design: Interfaces / Contracts, decisión 14). Solo existe porque el artículo tiene AL MENOS UN
/// movimiento calificado (<c>venta</c> o <c>anulacion</c> de venta) en la ventana — un artículo sin
/// historia NO ES UNA FILA de esta lista, la ausencia es la respuesta honesta (nunca una fila con
/// <see cref="MinimoSugerido"/> <c>0</c>; design decisión 14). <c>dto-contract-honesty</c>:
/// <see cref="ConsumoEnVentana"/> es <c>-SUM(movimientos_stock.cantidad)</c> sobre las filas
/// calificadas de la ventana (design decisión 6, la trampa del neteo: venta negativa, anulación de
/// venta positiva, anulación de COMPRA excluida vía <c>id_comprobante_compra IS NOT NULL</c>),
/// recortado a <c>0</c> — nunca negativo — cuando las devoluciones superan a las ventas (mismo
/// criterio de <see cref="Ways.Domain.Stock.ReglaDeReposicion.ConsumoDiario"/>: un consumo negativo
/// no es una magnitud de negocio, pero la fila SÍ existe porque hubo historia calificada);
/// <see cref="ConsumoDiarioPromedio"/> es <see cref="ConsumoEnVentana"/> dividido por
/// <c>dias_rotacion</c> efectivo (nunca negativo — recortado a 0 si las devoluciones superan a las
/// ventas); <see cref="MinimoSugerido"/> es <see cref="ConsumoDiarioPromedio"/> ×
/// <c>dias_cobertura_objetivo</c> (<see cref="Ways.Domain.Stock.ReglaDeReposicion.MinimoSugerido"/>),
/// una SUGERENCIA que ningún camino automatizado escribe en <c>stock.minimo</c> (spec
/// reposicion-de-stock: "minimoSugerido is never written to minimo automatically").</summary>
public sealed record FilaDeRotacion(
    int IdArticulo, string Articulo, decimal ConsumoEnVentana, decimal ConsumoDiarioPromedio,
    decimal MinimoSugerido);

/// <summary>Respuesta de <c>GET /api/reportes/stock/rotacion</c>. <see cref="Hoy"/>/<see
/// cref="ZonaHoraria"/>/<see cref="DiasDeRotacion"/> son el mismo eco obligatorio que <see
/// cref="Reposicion"/> — misma ventana, misma definición de consumo (design decisión 5, nunca una
/// segunda). <see cref="DiasCoberturaObjetivo"/> es el horizonte de cobertura efectivamente
/// resuelto (<c>dias_cobertura_objetivo</c>, PV → empresa → default 7) que <see
/// cref="FilaDeRotacion.MinimoSugerido"/> multiplica — echo obligatorio, mismo criterio que
/// <see cref="DiasDeRotacion"/>: una sugerencia cuyo horizonte es invisible no es auditable.</summary>
public sealed record Rotacion(
    int IdPuntoVenta, DateOnly Hoy, int DiasDeRotacion, int DiasCoberturaObjetivo, string ZonaHoraria,
    IReadOnlyList<FilaDeRotacion> Filas);
