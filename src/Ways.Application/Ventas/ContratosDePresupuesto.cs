using Ways.Domain.Ventas;

namespace Ways.Application.Ventas;

/// <summary>Una línea del cuerpo de <c>POST/PUT /api/presupuestos</c> (design: Interfaces/
/// Contracts). <c>orden</c> NO viaja: es server-asignado 1..N dentro del replace-set (mismo
/// criterio que <c>LineaDeOrdenSolicitada</c>/<c>LineaDeVenta</c>). Sin dinero en la solicitud —
/// el precio lo resuelve <c>ServicioDeOfertas</c> al guardar el borrador, igual que el checkout
/// (design decisión 2, <c>dto-contract-honesty</c> regla 1).</summary>
public sealed record LineaDePresupuesto(int IdArticulo, decimal Cantidad);

/// <summary>Cuerpo de <c>POST /api/presupuestos</c> (crea un borrador) y de <c>PUT
/// /api/presupuestos/{id}</c> (replace-set completo del header + los items — un PUT reemplaza
/// <see cref="Lineas"/> entero, nunca un CRUD incremental por item). <see cref="IdCliente"/>
/// omitido resuelve a Consumidor Final, mismo criterio que <c>SolicitudDeVenta</c>.</summary>
public sealed record SolicitudDePresupuesto(
    int IdPuntoVenta, int? IdCliente, string? Observaciones, IReadOnlyList<LineaDePresupuesto> Lineas);

/// <summary>Cuerpo de <c>POST /{id}/enviar</c> — el único dato que el cliente aporta al enviar:
/// el vencimiento. <c>numero</c>/<c>fecha_envio</c> son enteramente server-derivados (design:
/// Interfaces/Contracts).</summary>
public sealed record SolicitudDeEnvio(DateOnly Vencimiento);

/// <summary>Un item ya persistido — <see cref="Orden"/> es el valor server-asignado; el resto es
/// la procedencia de precio congelada al guardar el borrador (design: Interfaces/Contracts).</summary>
public sealed record ItemDePresupuesto(
    int Orden,
    int IdArticulo,
    string Descripcion,
    decimal Cantidad,
    decimal PrecioUnitario,
    decimal Descuento,
    decimal Total,
    int IdListaPrecio,
    int? IdOferta,
    int IdAlicuotaIva,
    decimal PorcentajeIva);

/// <summary>Respuesta de <c>POST/PUT /api/presupuestos</c>, <c>POST /{id}/enviar</c>,
/// <c>POST /{id}/anular</c> y <c>GET /{id}</c> — el shape completo (design: Interfaces/Contracts):
/// <see cref="Vencido"/>/<see cref="Convertible"/> son DERIVADOS en cada lectura
/// (<c>ReglaDePresupuestos</c>, zona horaria del punto de venta — decisión 16), nunca columnas.
/// <see cref="IdComprobanteVenta"/> es honesto por construcción: la columna que lo respalda
/// (<c>comprobantes_venta.id_presupuesto_origen</c>) existe desde la Slice 1 y esta lectura
/// SIEMPRE la consulta — en esta slice no hay escritor todavía (Slice 3), así que siempre
/// devuelve <c>null</c>, pero no es relleno: el día que la Slice 3 convierta un presupuesto, este
/// mismo camino de lectura, sin ningún cambio, empieza a devolver el id real
/// (<c>dto-contract-honesty</c> regla 1 — un campo real con un escritor todavía no mergeado no es
/// lo mismo que un campo sin escritor posible).</summary>
public sealed record PresupuestoDetalle(
    int Id,
    int IdPuntoVenta,
    int IdCliente,
    int IdEmpleado,
    long? Numero,
    string? NumeroFormateado,
    DateTimeOffset FechaEmision,
    DateTimeOffset? FechaEnvio,
    DateOnly? Vencimiento,
    bool Vencido,
    bool Convertible,
    string ZonaId,
    string? Observaciones,
    decimal Subtotal,
    decimal DescuentoTotal,
    decimal Total,
    EstadoPresupuesto Estado,
    int? IdComprobanteVenta,
    IReadOnlyList<ItemDePresupuesto> Items);

/// <summary><c>GET /{id}/para-venta</c> (Slice 3 — el endpoint se cablea recién ahí, design:
/// Slicing). El contrato se declara ACÁ, junto al resto de <c>ContratosDePresupuesto</c>, porque
/// es lectura pura del mismo agregado. Deliberadamente NO es un <c>SolicitudDeVenta</c>
/// pre-armado (<c>dto-contract-honesty</c> regla 1 — design: Interfaces/Contracts): un shape que
/// el POS pudiera postear tal cual haría creíble al carrito para el dinero, exactamente lo que la
/// congelación de precio (decisión 4 del proposal) existe para impedir.</summary>
public sealed record PresupuestoParaVenta(
    int IdPresupuesto,
    long? Numero,
    int IdPuntoVenta,
    int IdCliente,
    DateOnly? Vencimiento,
    bool Vencido,
    bool Convertible,
    decimal Subtotal,
    decimal DescuentoTotal,
    decimal Total,
    IReadOnlyList<ItemDePresupuesto> Items);

/// <summary><c>GET /api/presupuestos</c>, una fila del listado (design: API Surface — decisión
/// 15/16, mismo criterio que <c>OrdenDeCompraListada</c>). Lleva <see cref="Vencido"/>/
/// <see cref="Convertible"/> — a diferencia de <c>OrdenDeCompraListada</c>, acá son baratos: ya
/// se resuelve una zona por punto de venta DISTINTO de la página completa (decisión 16), no una
/// consulta agregada por fila.</summary>
public sealed record PresupuestoListado(
    int Id,
    int IdPuntoVenta,
    int IdCliente,
    long? Numero,
    string? NumeroFormateado,
    DateTimeOffset FechaEmision,
    DateOnly? Vencimiento,
    bool Vencido,
    bool Convertible,
    decimal Total,
    EstadoPresupuesto Estado);

/// <summary>Mismo shape que <c>PaginaDeOrdenesDeCompra</c>: <c>CountAsync</c> +
/// <c>Skip/Take</c> sobre el mismo <c>ConstruirQuery</c> que el <c>COUNT</c>.</summary>
public sealed record PaginaDePresupuestos(
    IReadOnlyList<PresupuestoListado> Items,
    int Total,
    int Pagina,
    int Tamanio);
