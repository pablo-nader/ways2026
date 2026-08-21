using Ways.Domain.Ventas;

namespace Ways.Application.Ventas;

/// <summary>
/// stage-17-presupuestos-y-remitos, Slice 5 (design.md:204-207, task 5.1). Una línea del cuerpo de
/// <c>POST/PUT /api/remitos</c>. <see cref="IdLote"/> es la elección EXPLÍCITA del cliente
/// (dto-contract-honesty regla 1: un campo del contrato tiene que tener un destino real) — a
/// diferencia de <see cref="ItemPresupuesto"/>, <see cref="Ways.Domain.Ventas.ItemRemito"/> SÍ
/// tiene una columna <c>id_lote</c> escribible antes de <c>emitir</c>, así que este valor persiste
/// directo ahí en el replace-set (pre-check contra <c>lotes</c> — design: Backstop Map FK 22, "Yes
/// (item lines)"). <c>EmitirAsync</c> (Slice 5) trata un <see cref="ItemRemito.IdLote"/> ya no-nulo
/// como el pick EXPLÍCITO a honrar (re-validado contra el saldo vigente), y solo corre FEFO para
/// las líneas que llegaron sin uno — mismo árbol de decisión que <c>ServicioDeVentas.EmitirAsync</c>
/// (design decisión 10/mutation target 47: FEFO parity, "an explicit idLote is honoured in both").
/// <c>orden</c> NO viaja: server-asignado 1..N dentro del replace-set, mismo criterio que
/// <c>LineaDePresupuesto</c>.</summary>
public sealed record LineaDeRemito(int IdArticulo, decimal Cantidad, int? IdLote);

/// <summary>Cuerpo de <c>POST /api/remitos</c> (crea un borrador) y de <c>PUT /api/remitos/{id}</c>
/// (replace-set completo del header + los items). <see cref="IdCliente"/> omitido resuelve a
/// Consumidor Final, mismo criterio que <c>SolicitudDePresupuesto</c>/<c>SolicitudDeVenta</c>. Sin
/// dinero en la solicitud — el precio lo resuelve <see cref="Ofertas.ServicioDeOfertas"/> al
/// guardar el borrador (design: Technical Approach, fact 1), igual que el checkout/presupuesto.
/// </summary>
public sealed record SolicitudDeRemito(
    int IdPuntoVenta, int? IdCliente, string? DireccionEntrega, string? Observaciones,
    IReadOnlyList<LineaDeRemito> Lineas);

/// <summary>Un item ya persistido — <see cref="Orden"/> es el valor server-asignado; el resto de la
/// procedencia de precio se congela al guardar el borrador, igual que
/// <c>ItemDePresupuesto</c>. <see cref="CostoUnitario"/>/<see cref="CostoEsEstimado"/>/
/// <see cref="IdLote"/> son <c>null</c>/<c>false</c> mientras el remito está en <c>borrador</c>
/// (salvo <see cref="IdLote"/>, que el cliente puede fijar de antemano — ver el doc-comment de
/// <see cref="LineaDeRemito"/>) y quedan congelados recién al <c>emitir</c> (design.md:292).</summary>
public sealed record ItemDeRemito(
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
    decimal PorcentajeIva,
    decimal? CostoUnitario,
    bool CostoEsEstimado,
    int? IdLote);

/// <summary>Respuesta de <c>POST/PUT /api/remitos</c>, <c>POST /{id}/emitir</c>,
/// <c>POST /{id}/anular</c> y <c>GET /{id}</c> — el shape completo (design: Interfaces/Contracts,
/// mismo criterio que <c>PresupuestoDetalle</c>). Sin <c>Vencido</c>/<c>Convertible</c>: un remito
/// no expira.</summary>
public sealed record RemitoDetalle(
    int Id,
    int IdPuntoVenta,
    int IdCliente,
    int IdEmpleado,
    long? Numero,
    string? NumeroFormateado,
    DateTimeOffset FechaEmision,
    DateTimeOffset? FechaSalida,
    string? DireccionEntrega,
    string? Observaciones,
    decimal Subtotal,
    decimal DescuentoTotal,
    decimal Total,
    EstadoRemito Estado,
    int? IdComprobanteVenta,
    IReadOnlyList<ItemDeRemito> Items);

/// <summary><c>GET /api/remitos</c>, una fila del listado (design: API Surface, mismo criterio que
/// <c>PresupuestoListado</c>).</summary>
public sealed record RemitoListado(
    int Id,
    int IdPuntoVenta,
    int IdCliente,
    long? Numero,
    string? NumeroFormateado,
    DateTimeOffset FechaEmision,
    decimal Total,
    EstadoRemito Estado,
    int? IdComprobanteVenta);

/// <summary>Mismo shape que <c>PaginaDePresupuestos</c>: <c>CountAsync</c> + <c>Skip/Take</c> sobre
/// el mismo <c>ConstruirQuery</c> que el <c>COUNT</c>.</summary>
public sealed record PaginaDeRemitos(
    IReadOnlyList<RemitoListado> Items,
    int Total,
    int Pagina,
    int Tamanio);

/// <summary>
/// stage-17-presupuestos-y-remitos, Slice 6 (design.md:211-212, task 6.2). Cuerpo de
/// <c>POST /api/remitos/facturacion</c> — consolida <see cref="IdsRemito"/> (N remitos, mismo
/// cliente/PV, todos <c>emitido</c> y sin ligar) en UN comprobante <c>TXR</c> itemless. Sin
/// <c>IdCliente</c> (dto-contract-honesty regla 1): el cliente se DERIVA de los remitos mismos
/// (todos comparten uno, guard de <c>ServicioDeFacturacionDeRemitos</c>) — un valor en conflicto
/// no tendría destino real, así que el campo ni siquiera existe en el contrato. <see cref="Pagos"/>
/// mismo shape que <see cref="PagoDeVenta"/> del checkout — <see cref="ValidadorDePagos"/> los
/// valida igual, incluido el backstop de límite de crédito re-implementado dentro de la
/// transacción (OD9/T9).</summary>
public sealed record SolicitudDeFacturacionDeRemitos(
    int IdPuntoVenta, IReadOnlyList<int> IdsRemito, IReadOnlyList<PagoDeVenta> Pagos, string? Observaciones);
