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
public sealed record OrdenDeCompraBorrador(
    int Id,
    int IdProveedor,
    int IdPuntoVenta,
    long? Numero,
    DateTimeOffset FechaEmision,
    DateTimeOffset? FechaEnvio,
    DateOnly? FechaEsperada,
    string? Observaciones,
    EstadoOrdenCompra Estado,
    IReadOnlyList<ItemDeOrden> Items);
