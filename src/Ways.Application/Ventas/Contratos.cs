using Ways.Domain.Ventas;

namespace Ways.Application.Ventas;

/// <summary>
/// Cuerpo de <c>POST /api/ventas</c> (design: Checkout Contract; design decisión 3): sin ningún
/// campo de dinero — el total y el precio de cada línea se re-resuelven server-side vía
/// <see cref="Ofertas.ServicioDeOfertas.ResolverAsync"/>, nunca se confía en lo que mostró el
/// carrito. <see cref="IdCliente"/> es opcional: <c>null</c> ⇒ Consumidor Final del tenant (spec:
/// operacion-de-pos / Checkout Orchestration Contract, "Omitted idCliente defaults to Consumidor
/// Final"). Sin campo de empleado a propósito (design decisión 11, forward obligation de Slice
/// 3): <c>id_empleado</c> siempre sale de <c>IContextoDeUsuario.UsuarioId</c>, nunca de este
/// contrato.
///
/// stage-17-presupuestos-y-remitos, Slice 3 (design: Interfaces/Contracts, decisión 2/tensión
/// T7): <see cref="IdPresupuestoOrigen"/> convierte un presupuesto <c>enviado</c> en esta venta —
/// con él presente, <see cref="Lineas"/> tiene que llegar vacío/ausente (400
/// <c>lineas_no_admitidas</c>, <c>dto-contract-honesty</c> regla 1: un campo que el servidor
/// ignoraría no se acepta en silencio) y el precio de cada línea sale congelado de
/// <c>items_presupuesto</c>, nunca de <see cref="Ofertas.ServicioDeOfertas.ResolverAsync"/>.
/// Parámetro opcional al final (default <c>null</c>) — preserva el constructor posicional de
/// todo call site preexistente de una venta común.
/// </summary>
public sealed record SolicitudDeVenta(
    int IdPuntoVenta,
    int? IdCliente,
    string CodigoTipoComprobante,
    int? IdComprobanteAsociado,
    IReadOnlyList<LineaDeVenta>? Lineas,
    IReadOnlyList<PagoDeVenta>? Pagos,
    string? DireccionEntrega,
    string? Observaciones,
    int? IdPresupuestoOrigen = null);

/// <summary><see cref="Cantidad"/> siempre positiva, sin importar el tipo de comprobante — el
/// signo lo deriva <see cref="ServicioDeVentas"/> a partir de <c>tipos_comprobante.signo</c>
/// (design decisión 4): el operador del POS piensa en "cuántas unidades", nunca en el signo de
/// contabilidad. Sin <c>precioUnitario</c>/<c>descuento</c>/<c>total</c> (design decisión 3).
/// <see cref="CodigoBarra"/> es el que devolvió el escaneo — snapshot informativo en el item, no
/// se re-valida contra <c>codigos_barra</c> en el checkout (ya se validó en
/// <c>GET /api/articulos/escaneo</c>). <see cref="IdLote"/> (stage-12 slice 7) es opcional y solo
/// tiene efecto para una línea lote-efectiva (<c>ControlaLote AND lotesHabilitado</c>): omitido,
/// <see cref="ServicioDeVentas"/> aplica el default FEFO; un cliente legado que ni siquiera
/// conoce el campo transacciona igual (spec comprobantes-venta: "A client that knows nothing
/// about lots still transacts correctly"). Provisto sobre una línea SIN lote efectivo, el campo
/// no tiene destino real: se rechaza 400 lote_invalido en vez de ignorarse en silencio
/// (dto-contract-honesty, judgment-day del slice 7).</summary>
public sealed record LineaDeVenta(int IdArticulo, decimal Cantidad, string? CodigoBarra, int? IdLote = null);

/// <summary>Un medio de pago del checkout (design: Checkout Contract). A diferencia de
/// <see cref="LineaDeVenta"/>, SÍ lleva dinero: <see cref="Importe"/>/<see cref="Vuelto"/> son lo
/// que el cajero tipeó en caja, no un valor derivado del catálogo — <see cref="ValidadorDePagos"/>
/// los valida, no los calcula.</summary>
public sealed record PagoDeVenta(int IdMedioPago, decimal Importe, string? Referencia, decimal Vuelto);

/// <summary>Un item ya emitido — snapshot inmutable (spec: Snapshot Immutability of Items).
/// <see cref="IdLote"/>/<see cref="CodigoLote"/>/<see cref="LoteVencido"/> (stage-12 slice 7) solo
/// se completan para una línea lote-efectiva: el lote resuelto en la fase de decisión (FEFO
/// default u honrado si vino explícito), su código proyectado, y si su vencimiento ya pasó
/// (warning, nunca bloqueo — spec: "Expired Lot Sale Warns, Never Blocks"). NULL/false para una
/// línea sin lote. Desde slice 8, <c>id_lote</c> también se persiste como snapshot congelado en
/// <c>items_comprobante_venta.id_lote</c> — una relectura (reprint) devuelve el mismo valor que el
/// checkout fresco.</summary>
public sealed record ItemEmitido(
    int Orden,
    int? IdArticulo,
    string Descripcion,
    string? CodigoBarra,
    int IdArea,
    int IdListaPrecio,
    int? IdOferta,
    int IdAlicuotaIva,
    decimal PorcentajeIva,
    decimal Cantidad,
    decimal PrecioUnitario,
    decimal Descuento,
    decimal Total,
    int? IdLote = null,
    string? CodigoLote = null,
    bool LoteVencido = false);

/// <summary>Un pago ya emitido.</summary>
public sealed record PagoEmitido(int IdMedioPago, decimal Importe, string? Referencia, decimal Vuelto);

/// <summary>Respuesta de checkout/reprint (spec: operacion-de-pos / Checkout Orchestration
/// Contract) — <see cref="NumeroVisible"/> es <c>NumeroDeComprobante.Formatear(IdPuntoVenta,
/// Numero)</c>, ya formateado para no obligar al front a reimplementar el padding.
///
/// stage-17-presupuestos-y-remitos, Slice 3 (design: Interfaces/Contracts, OD9/T7): <see
/// cref="IdPresupuestoOrigen"/> — <c>null</c> en el 100% del tráfico previo a esta etapa,
/// round-trip del presupuesto convertido cuando la venta nació de uno
/// (<c>dto-contract-honesty</c> regla 2: un campo request-only no alcanza para probar el
/// round-trip).</summary>
public sealed record ComprobanteEmitido(
    int Id,
    long Numero,
    string NumeroVisible,
    EstadoComprobante Estado,
    DateTimeOffset Fecha,
    int IdPuntoVenta,
    int IdCliente,
    int? IdComprobanteAsociado,
    decimal Subtotal,
    decimal DescuentoTotal,
    decimal Total,
    string? DireccionEntrega,
    string? Observaciones,
    IReadOnlyList<ItemEmitido> Items,
    IReadOnlyList<PagoEmitido> Pagos,
    int? IdPresupuestoOrigen = null);

/// <summary>Fila de <c>GET /api/ventas</c> (listado paginado) — sin items/pagos, mismo criterio
/// que los demás <c>*Listado</c> del proyecto (evita el N+1 de traer el detalle completo de cada
/// fila listada).</summary>
public sealed record ComprobanteListado(
    int Id,
    long Numero,
    string NumeroVisible,
    EstadoComprobante Estado,
    DateTimeOffset Fecha,
    int IdPuntoVenta,
    int IdCliente,
    decimal Total);

/// <summary>Página de resultados de <c>GET /api/ventas</c> — mismo shape que
/// <c>Ways.Application.Usuarios.PaginaDe&lt;T&gt;</c>, redeclarado acá porque ese genérico vive en
/// un namespace de un ABM no relacionado (evita un acoplamiento cruzado innecesario).</summary>
public sealed record PaginaDeVentas(IReadOnlyList<ComprobanteListado> Items, int Total, int Pagina, int Tamanio);
