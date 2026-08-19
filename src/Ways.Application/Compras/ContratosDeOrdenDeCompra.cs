using Ways.Domain.Compras;

namespace Ways.Application.Compras;

/// <summary>Una línea del cuerpo de <c>POST/PUT /api/ordenes-compra</c> (design: Interfaces/
/// Contracts) — <c>orden</c> NO viaja en la solicitud: es server-asignado 1..N dentro del
/// replace-set (mutation target #14), nunca input de cliente. <see cref="CostoUnitarioEstimado"/>
/// es intención de precio, jamás un hecho — <c>NULL</c> = no cotizado.</summary>
public sealed record LineaDeOrdenSolicitada(
    int IdArticulo,
    string Descripcion,
    decimal CantidadPedida,
    decimal? CostoUnitarioEstimado);

/// <summary>Cuerpo de <c>POST /api/ordenes-compra</c> (crea un borrador) y de <c>PUT
/// /api/ordenes-compra/{id}</c> (design decisión 2 vía el precedente de compras: replace-set
/// completo del header + los items — un PUT reemplaza <see cref="Items"/> entero, nunca un CRUD
/// incremental por item).</summary>
public sealed record SolicitudDeOrdenDeCompra(
    int IdProveedor,
    int IdPuntoVenta,
    DateOnly? FechaEsperada,
    string? Observaciones,
    IReadOnlyList<LineaDeOrdenSolicitada> Items);

/// <summary>Un item ya persistido — <see cref="Orden"/> es el valor server-asignado.</summary>
public sealed record ItemDeOrden(
    int Orden,
    int IdArticulo,
    string Descripcion,
    decimal CantidadPedida,
    decimal? CostoUnitarioEstimado);

/// <summary>Respuesta de <c>POST /api/ordenes-compra</c>, <c>PUT /api/ordenes-compra/{id}</c> y
/// <c>POST /{id}/enviar</c> — el shape que esta slice puede llenar honestamente (header + items).
///
/// DEVIATION registrada (tasks.md decisión 15): el design (Interfaces/Contracts) define un único
/// <c>OrdenDeCompraDetalle</c> con <c>Cobertura</c>/<c>TotalEstimado</c>/<c>TotalReal</c>/
/// <c>DesvioTotal</c>/<c>ComprobantesLigados</c> — pero esos campos derivan del libro de
/// recepción (slice 3) y de la lectura paginada/cobertura (task 5.1, que crea explícitamente ese
/// tipo). Poblarlos acá con `null`/vacío constante sería deshonesto (dto-contract-honesty regla
/// 1: un campo que nunca varía no es un contrato, es relleno). Este tipo cubre solo lo que el
/// camino de escritura de esta slice puede proyectar honestamente; <c>OrdenDeCompraDetalle</c> se
/// crea en la slice 5 tal como el propio task list lo asigna, para <c>GET /{id}</c>.</summary>
/// <summary><see cref="FechaCierre"/>/<see cref="IdEmpleadoCierre"/> agregados en Slice 4
/// (design: Transactions — CERRAR OC): <c>CerrarAsync</c> es el camino de escritura que hace estos
/// dos campos honestos por primera vez — <c>NULL</c> hasta el cierre, poblados en el mismo
/// <c>UPDATE … RETURNING</c> que los escribe (dto-contract-honesty regla 1: no es relleno, varían
/// con el cierre real).</summary>
public sealed record OrdenDeCompraBorrador(
    int Id,
    int IdProveedor,
    int IdPuntoVenta,
    long? Numero,
    DateTimeOffset FechaEmision,
    DateTimeOffset? FechaEnvio,
    DateOnly? FechaEsperada,
    DateTimeOffset? FechaCierre,
    int? IdEmpleadoCierre,
    string? Observaciones,
    EstadoOrdenCompra Estado,
    IReadOnlyList<ItemDeOrden> Items);

/// <summary>Slice 5 (design decisión 13, task 5.1): cobertura POR ARTÍCULO, nunca por línea —
/// agrupar por <c>id_articulo</c> en ambos lados (design decisión 3) hace que un desglose por
/// línea sea un número que el sistema no tiene (<c>dto-contract-honesty</c> regla 1).
/// <see cref="Pedida"/> puede ser <c>0</c>: es la forma en que un artículo recibido-pero-no-pedido
/// se vuelve visible en vez de desaparecer en silencio. <see cref="Pendiente"/> nunca es negativo
/// (<c>Math.Max(Pedida - Recibida, 0)</c>): una sobre-entrega (decisión de diseño 3, T9) no genera
/// un "pendiente negativo" — <c>CompraEditor.tsx</c> (slice 6) usa <c>Pendiente &gt; 0</c> para
/// decidir qué líneas pre-llenar. <see cref="CostoEstimado"/>/<see cref="CostoReal"/>/
/// <see cref="Desvio"/> son promedios ponderados por cantidad, <c>null</c> cuando no hay dato
/// comparable de ese lado — JAMÁS <c>0</c> (design decisión 14, spec: "no comparable, never
/// zero").</summary>
public sealed record CoberturaDeArticulo(
    int IdArticulo,
    decimal Pedida,
    decimal Recibida,
    decimal Pendiente,
    decimal? CostoEstimado,
    decimal? CostoReal,
    decimal? Desvio);

/// <summary>Slice 5 (design: Interfaces/Contracts, task 5.1) — <c>GET /api/ordenes-compra/{id}</c>.
/// <see cref="Estado"/> es SIEMPRE la columna proyectada por <see
/// cref="EscriturasDeOrdenDeCompra"/> (slice 3/4) — este tipo NUNCA re-deriva el estado (design
/// decisión 12). <see cref="Cobertura"/> es la derivación per-artículo (LINQ, propia de esta
/// lectura — deliberadamente SEPARADA de la derivación raw-ADO de escritura de <see
/// cref="EscriturasDeOrdenDeCompra"/>, cuya prueba de consistencia es la "projection fidelity" del
/// Testing Strategy de design.md, task 5.9 — no la reutilización de SQL: la escritura solo
/// necesita dos booleanos agregados por su propio <c>WITH</c>, mientras que esta lectura necesita
/// las filas per-artículo incluyendo recibido-no-pedido, un shape distinto). <see
/// cref="TotalEstimado"/> es el total estimado de la PORCIÓN COTIZADA, sumado a NIVEL LÍNEA
/// (<c>CostoUnitarioEstimado * CantidadPedida</c> sobre los items con costo seteado — judgment-day
/// ronda 2: jamás el promedio por-artículo de <see cref="CoberturaDeArticulo.CostoEstimado"/>
/// multiplicado por la <c>Pedida</c> total del artículo, que extrapolaría en silencio el costo de
/// una línea nunca cotizada). <see cref="TotalReal"/>/<see cref="DesvioTotal"/> SÍ agregan <see
/// cref="CoberturaDeArticulo"/> ponderando por <c>Recibida</c> (población coherente: <c>CostoReal</c>
/// y <c>Recibida</c> derivan siempre del mismo grupo de items recibidos, sin línea "recibida sin
/// costo" que las desacople), <c>null</c> cuando ningún artículo tiene el lado comparable. <see
/// cref="ComprobantesLigados"/> son TODOS los comprobantes con <c>id_orden_compra</c> = esta orden
/// (cualquier estado — informativo, design: API Surface "linked comprobante ids").</summary>
public sealed record OrdenDeCompraDetalle(
    int Id,
    int IdProveedor,
    int IdPuntoVenta,
    long? Numero,
    DateTimeOffset FechaEmision,
    DateTimeOffset? FechaEnvio,
    DateOnly? FechaEsperada,
    DateTimeOffset? FechaCierre,
    bool CierreManual,
    string? Observaciones,
    EstadoOrdenCompra Estado,
    IReadOnlyList<ItemDeOrden> Items,
    IReadOnlyList<CoberturaDeArticulo> Cobertura,
    decimal? TotalEstimado,
    decimal? TotalReal,
    decimal? DesvioTotal,
    IReadOnlyList<int> ComprobantesLigados);

/// <summary>Slice 5 (design decisión 15, task 5.1/5.2) — <c>GET /api/ordenes-compra</c>, una fila
/// del listado. Nunca lleva <c>Cobertura</c>/desvío: esos campos son costosos de derivar (dos
/// consultas agregadas por orden) y el listado no los muestra — mismo criterio que
/// <c>CompraListada</c> frente a <c>CompraDetalle</c>.</summary>
public sealed record OrdenDeCompraListada(
    int Id,
    int IdProveedor,
    int IdPuntoVenta,
    long? Numero,
    DateTimeOffset FechaEmision,
    DateOnly? FechaEsperada,
    EstadoOrdenCompra Estado);

/// <summary>Slice 5 (design decisión 15, task 5.2) — mismo shape que
/// <c>PaginaDeEstadoDeCuentaDeProveedor</c>/<c>PaginaDeCompras</c>: <c>CountAsync</c> +
/// <c>Skip/Take</c> sobre el mismo <c>ConstruirQuery</c> que el <c>COUNT</c>.</summary>
public sealed record PaginaDeOrdenesDeCompra(
    IReadOnlyList<OrdenDeCompraListada> Items,
    int Total,
    int Pagina,
    int Tamanio);
