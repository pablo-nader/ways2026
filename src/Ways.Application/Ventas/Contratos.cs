using Ways.Application.Fiscal;
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
///
/// stage-pos-reserva-de-numeracion (DB CHANGE GATE aprobado): <see cref="NumeroPreasignado"/> es
/// el número que un dispositivo YA reservó offline (<c>ServicioDeReservasDeNumeracion</c>) y
/// quiere consumir ahora. Solo un actor con claim de dispositivo puede traerlo (400
/// <c>numero_preasignado_no_admitido</c> para un actor web) y tiene que pertenecerle (409
/// <c>numero_preasignado_no_reservado</c> si no hay una reserva viva que lo contenga) — ver
/// <c>ServicioDeVentas.EmitirAsync</c>. Ausente, el servidor sigue asignando como hoy.
///
/// stage-pos-venta-offline-backend (decisión del dueño — inversión deliberada de la decisión 3
/// original de este mismo comentario, cerrando/acotando docs/10-modelo-de-datos.md §9.2): con
/// <see cref="NumeroPreasignado"/> presente, <see cref="Lineas"/> puede además traer
/// <see cref="LineaDeVenta.PrecioUnitario"/>/<see cref="LineaDeVenta.DescuentoUnitario"/> — el
/// precio que el DISPOSITIVO ya cobró offline, no una vuelta atrás de la decisión 3 para el
/// camino web/online, que sigue exactamente igual. <c>ServicioDeVentas.EmitirAsync</c> lo exige
/// completo (todas las líneas con precio, o ninguna) y solo junto a un número pre-asignado (400
/// <c>precio_offline_no_admitido</c>/<c>precio_offline_incompleto</c> en cualquier otro caso). El
/// servidor sigue recomputando lo que HUBIERA cobrado (<c>ServicioDeOfertas.ResolverAsync</c>) y
/// deja un rastro auditable (<c>AccionAuditada.VentaDiscrepanciaDePrecio</c>) si difiere — nunca
/// bloquea, nunca pisa el precio recibido.
///
/// <see cref="LimiteDeCreditoNoValidado"/> (decisión del dueño, venta local a clientes
/// identificados): el dispositivo cobró con cuenta corriente sin poder consultar el límite de
/// crédito al servidor (sin conexión o sin respuesta a tiempo) y registró la venta igual. Solo se
/// admite junto a <see cref="NumeroPreasignado"/> (400 <c>limite_no_validado_no_admitido</c> en
/// cualquier otro caso). Cuando se honra, el servidor no rechaza la venta por el límite pero sí
/// actualiza el saldo, y si el saldo resultante lo supera deja el rastro
/// <c>AccionAuditada.VentaExcedioLimiteSinValidar</c>.
///
/// <see cref="ForzarFactura"/> (facturación por medio de pago): el cajero pide facturar este ticket sí o sí. Se guarda
/// en <c>comprobantes_venta.factura_forzada</c> y es lo ÚNICO que viaja de la facturación: la decisión, el porcentaje y
/// el CAE los pone el servidor. Solo tiene sentido en una venta; en una nota de crédito se rechaza (400
/// <c>forzar_factura_no_admitido</c>) en vez de ignorarse. Entra en la identidad de un reenvío con número
/// pre-asignado, igual que el resto de lo que tipea el operador.
///
/// <see cref="ConEnvio"/> (venta con envío): el cajero pide agregar el envío. El servidor exige un cliente identificado
/// (no Consumidor Final) con domicilio cargado (400 <c>envio_requiere_cliente</c> / <c>envio_requiere_domicilio</c>),
/// resuelve el parámetro <c>importe_envio</c> del punto de venta y, si es mayor a cero, agrega UNA línea de concepto libre
/// "Envío" (sin artículo, a 21 % de IVA) que suma al subtotal y al total. Sin <see cref="DireccionEntrega"/> propia, la
/// dirección de entrega del comprobante sale del domicilio del cliente. Solo tiene sentido en una venta común: en una
/// nota de crédito o en la conversión de un presupuesto se rechaza (400 <c>envio_no_admitido</c>) en vez de ignorarse. El
/// importe nunca viaja en el pedido: lo pone el servidor, también en una venta con número pre-asignado.</summary>
public sealed record SolicitudDeVenta(
    int IdPuntoVenta,
    int? IdCliente,
    string CodigoTipoComprobante,
    int? IdComprobanteAsociado,
    IReadOnlyList<LineaDeVenta>? Lineas,
    IReadOnlyList<PagoDeVenta>? Pagos,
    string? DireccionEntrega,
    string? Observaciones,
    int? IdPresupuestoOrigen = null,
    long? NumeroPreasignado = null,
    bool LimiteDeCreditoNoValidado = false,
    bool ForzarFactura = false,
    bool ConEnvio = false);

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
/// (dto-contract-honesty, judgment-day del slice 7).
///
/// stage-pos-venta-offline-backend (DB CHANGE GATE: sin cambio de esquema — decisión del dueño,
/// "la venta offline carga sus precios"): <see cref="PrecioUnitario"/>/
/// <see cref="DescuentoUnitario"/> son el precio/descuento que un DISPOSITIVO offline ya cobró,
/// tomados de su última <c>GET /api/pos/instantanea</c> — nunca lo que <c>ServicioDeOfertas.
/// ResolverAsync</c> resolvería HOY al sincronizar. Ambos <c>null</c> (el default) es el único
/// camino admitido para un actor web o un dispositivo sin <see cref="SolicitudDeVenta.
/// NumeroPreasignado"/>: <c>ServicioDeVentas.EmitirAsync</c> los rechaza con 400 en ese caso
/// (<c>ExigirPreciosOfflineValidos</c>), y exige que TODAS las líneas de la solicitud los traigan,
/// o ninguna — no hay un tercer estado "algunas sí, algunas no". <see cref="DescuentoUnitario"/>
/// sin <see cref="PrecioUnitario"/> tampoco tiene destino (dto-contract-honesty regla 1): se
/// rechaza igual que un <c>idLote</c> sin lote efectivo, arriba.
///
/// <see cref="AjusteManualPorcentaje"/> (ajuste manual de precio en caja): porcentaje con signo
/// —negativo = descuento, positivo = recargo— que el operador aplica sobre el neto de la línea
/// (bruto menos el descuento de oferta). Es lo ÚNICO que el cliente manda del ajuste: el monto lo
/// calcula el servidor (<c>CalculadorDeTotales</c>), también en la venta offline, donde conviven el
/// precio que cobró el dispositivo y este porcentaje. <c>null</c> = sin ajuste; cero, fuera de
/// ±100 o con más de dos decimales ⇒ 400 <c>ajuste_manual_invalido</c>
/// (<see cref="ReglaDeAjusteManual"/>). Con <see cref="SolicitudDeVenta.IdPresupuestoOrigen"/> las
/// líneas no se admiten, así que el porcentaje tampoco llega nunca por esa conversión.
///
/// <see cref="UnidadesConsumidasPorCombo"/>, <see cref="AhorroDeCombo"/> e <see cref="IdCombo"/> (feature combos)
/// son lo que el dispositivo offline calculó con su motor de combos para la línea, junto a su
/// <see cref="PrecioUnitario"/>: la cantidad de la línea que un combo consumió (mayor a cero, hasta la cantidad de la
/// línea y de hasta tres decimales, sin mirar la unidad de venta actual del artículo: el dispositivo ya cobró), la
/// parte del combo como IMPORTE DE LÍNEA (no unitario; entre 0 y el bruto de esas unidades) y el combo. Van los tres
/// o ninguno, y solo con el precio offline (400 <c>combo_offline_no_admitido</c> sin él, <c>combo_offline_incompleto</c>
/// si falta alguno, <c>combo_offline_invalido</c> fuera de rango). El servidor los graba tal cual —el descuento de la
/// línea es <c>round(DescuentoUnitario × (Cantidad − UnidadesConsumidasPorCombo)) + AhorroDeCombo</c> y el combo
/// queda en <c>items_comprobante_venta.id_combo</c>— y solo audita la diferencia contra su propia detección. El
/// camino online no los admite nunca (el servidor detecta los combos).
///
/// <see cref="Importe"/> es el monto que el cajero tipea para un artículo que se vende por importe
/// (<c>UnidadVenta.Importe</c>): es la base del precio unitario de la línea (cantidad siempre 1): con una lista derivada del cliente
/// el servidor le aplica el porcentaje (tipeado 1500 a -10 % queda en 1350), con una fija queda igual; nunca hay ofertas ni
/// combos. El ajuste manual se aplica encima. Obligatorio en ese artículo (400 <c>importe_requerido</c>) y rechazado en cualquier otro
/// (400 <c>importe_no_admitido</c>); mayor a cero, de a lo sumo dos decimales y dentro de <c>numeric(14,2)</c> (400
/// <c>importe_invalido</c>). Solo viaja junto a <see cref="PrecioUnitario"/> en una venta offline (con número pre-asignado;
/// en cualquier otra, 400 <c>importe_con_precio_no_admitido</c>): ahí es el precio que cobró el dispositivo y el servidor
/// lo graba tal cual, auditando la diferencia contra el precio que derivaría hoy. Una línea por importe offline sin precio
/// (paquete anterior de la caja) se registra por el importe tipeado, sin lista; en ambos casos queda fuera del
/// todas-o-ninguna del precio de las demás.
/// En una nota de crédito asociada es el precio unitario con que se vendió la línea que se devuelve (el que ya lleva el
/// porcentaje de la lista): el servidor lo toma tal cual y no lo aplica de nuevo.</summary>
public sealed record LineaDeVenta(
    int IdArticulo, decimal Cantidad, string? CodigoBarra, int? IdLote = null,
    decimal? PrecioUnitario = null, decimal? DescuentoUnitario = null, decimal? AjusteManualPorcentaje = null,
    decimal? UnidadesConsumidasPorCombo = null, decimal? AhorroDeCombo = null, int? IdCombo = null,
    decimal? Importe = null);

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
/// checkout fresco.
///
/// <see cref="PrecioDiscrepante"/> (stage-pos-venta-offline-backend): warning, nunca bloqueo —
/// mismo criterio EXACTO que <see cref="LoteVencido"/>, incluida su misma limitación: solo se
/// completa en el checkout fresco que detectó la discrepancia (<c>ServicioDeVentas.
/// MaterializarItems</c>), NO se persiste en <c>items_comprobante_venta</c> (DB CHANGE GATE
/// evitado a propósito) — una relectura/reprint siempre devuelve <c>false</c> acá, aunque el
/// rastro completo (precio cobrado vs. esperado) siga disponible en <c>GET /api/auditoria</c>
/// (<c>AccionAuditada.VentaDiscrepanciaDePrecio</c>).
///
/// <see cref="AjusteManualPorcentaje"/>/<see cref="AjusteManual"/> sí se persisten
/// (<c>items_comprobante_venta.ajuste_manual_porcentaje</c>/<c>ajuste_manual</c>), así que una
/// relectura devuelve lo mismo que el checkout fresco: porcentaje con signo (<c>null</c> = sin
/// ajuste) y monto con signo ya incluido en <see cref="Total"/> (<c>0</c> sin ajuste). Un item de
/// un <c>TXR</c> (que viene de <c>items_remito</c>) siempre los lleva en <c>null</c>/<c>0</c>.
///
/// <see cref="IdCombo"/> sí se persiste (<c>items_comprobante_venta.id_combo</c>) y una relectura devuelve
/// lo mismo que el checkout fresco: el combo (el de menor id si varios consumieron la línea) cuyo descuento
/// está incluido en <see cref="Descuento"/>; <c>null</c> si ninguno. En un item de nota de crédito asociada a
/// una venta con combo es el <c>id_combo</c> del item original cuyas unidades devuelve. Solo el id, igual que
/// <see cref="IdOferta"/>: el nombre no viaja en la venta emitida y un combo dado de baja después no la
/// afecta. Un item de un <c>TXR</c> siempre lo lleva en <c>null</c>.
///
/// <see cref="PorImporte"/> sí se persiste (<c>items_comprobante_venta.por_importe</c>): la línea se vendió por el importe que tipeó el
/// cajero, y <see cref="PrecioUnitario"/> es el precio con que se cobró: el importe con el porcentaje de la lista derivada
/// del cliente si la venta fue online, o lo que cobró el dispositivo (su precio, o el importe tipeado si no mandó
/// uno) si fue offline. Un item de un <c>TXR</c> siempre lo lleva en <c>false</c>.</summary>
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
    decimal? AjusteManualPorcentaje,
    decimal AjusteManual,
    decimal Total,
    int? IdLote = null,
    string? CodigoLote = null,
    bool LoteVencido = false,
    bool PrecioDiscrepante = false,
    int? IdCombo = null,
    bool PorImporte = false);

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
/// round-trip).
///
/// <see cref="DescuentoManualTotal"/>/<see cref="RecargoManualTotal"/>: suma de los ajustes manuales
/// de las líneas de porcentaje negativo/positivo, por separado a propósito (un recargo en una línea
/// no esconde un descuento en otra). <see cref="Total"/> = <see cref="Subtotal"/> −
/// <see cref="DescuentoTotal"/> − <see cref="DescuentoManualTotal"/> + <see cref="RecargoManualTotal"/>.
///
/// <see cref="FacturaForzada"/> es el round-trip de <see cref="SolicitudDeVenta.ForzarFactura"/>. <see cref="Fiscal"/>
/// es lo que el servidor decidió sobre la facturación del ticket: lo completan la respuesta de la emisión (y de su
/// reenvío) , la lectura por id (reimpresión), que solo lo lee, y la anulación; <c>null</c> significa "no consultado". Si el servidor alcanzó a emitir la factura antes de responder (o ya estaba emitida en un reenvío), <c>Fiscal.Estado</c> es
/// <c>aprobado</c> y <c>Fiscal.Comprobante</c> lleva sus datos (<see cref="EmisionEnLineaAlCobrar"/>). <c>Fiscal.Estado</c> es
/// uno de: <c>no_corresponde</c> (no se factura), <c>sin_decidir</c> (la decisión falló y se completa después), <c>pendiente</c>
/// (se decidió facturar y la factura no está emitida), <c>aprobado</c>, <c>rechazado</c> (ARCA rechazó) o <c>cancelado</c> (la
/// venta se anuló antes de procesarse su facturación; se ve en un reenvío, en una lectura o si la anulación llega durante el cobro). Con la venta anulada y su factura aprobada, <c>Fiscal.NotaDeCredito</c> informa la nota de crédito (<c>estado</c>
/// <c>pendiente</c>, <c>aprobado</c> o <c>rechazado</c>, <c>idFacturacion</c>, <c>error</c> y, aprobada, <c>comprobante</c>); la factura
/// conserva su estado <c>aprobado</c>. <c>Fiscal.Error</c> es solo un código, nunca texto libre; las observaciones de ARCA de una factura aprobada (<c>Fiscal.Comprobante.Observaciones</c>) sí viajan con su texto, porque son las que se imprimen en el comprobante.</summary>
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
    decimal DescuentoManualTotal,
    decimal RecargoManualTotal,
    decimal Total,
    string? DireccionEntrega,
    string? Observaciones,
    IReadOnlyList<ItemEmitido> Items,
    IReadOnlyList<PagoEmitido> Pagos,
    int? IdPresupuestoOrigen = null,
    bool FacturaForzada = false,
    EstadoFiscalDeVenta? Fiscal = null);

/// <summary>Fila de <c>GET /api/ventas</c> (listado paginado) — sin items/pagos, mismo criterio
/// que los demás <c>*Listado</c> del proyecto (evita el N+1 de traer el detalle completo de cada
/// fila listada). <see cref="DescuentoManualTotal"/>/<see cref="RecargoManualTotal"/> son los mismos
/// dos totales de <see cref="ComprobanteEmitido"/>: alcanzan para marcar en el listado una venta
/// con precio cambiado a mano sin abrir el detalle.</summary>
public sealed record ComprobanteListado(
    int Id,
    long Numero,
    string NumeroVisible,
    EstadoComprobante Estado,
    DateTimeOffset Fecha,
    int IdPuntoVenta,
    int IdCliente,
    decimal Total,
    decimal DescuentoManualTotal,
    decimal RecargoManualTotal);

/// <summary>Página de resultados de <c>GET /api/ventas</c> — mismo shape que
/// <c>Ways.Application.Usuarios.PaginaDe&lt;T&gt;</c>, redeclarado acá porque ese genérico vive en
/// un namespace de un ABM no relacionado (evita un acoplamiento cruzado innecesario).</summary>
public sealed record PaginaDeVentas(IReadOnlyList<ComprobanteListado> Items, int Total, int Pagina, int Tamanio);

/// <summary>Fila de <c>GET /api/ventas/por-turno/{idTurno}</c> (pantalla "Ventas del turno" del POS
/// de escritorio) — a diferencia de <see cref="ComprobanteListado"/>, que deliberadamente omite
/// cliente/medios de pago para evitar el N+1 en un listado paginado sin cota, esta fila SÍ los
/// lleva: el conjunto de ventas de UN turno es acotado por diseño (mismo criterio que
/// <c>LectorDeLineasDelTurno</c>), así que el batch extra (dos consultas indexadas, nunca N+1) es
/// barato, y la pantalla necesita reconciliar sin abrir cada comprobante uno por uno. Incluye
/// <see cref="EstadoComprobante.Anulado"/> a propósito — a diferencia de
/// <c>LectorDeLineasDelTurno.LeerTicketsAsync</c>, acá el objetivo es justamente poder ver que una
/// anulación surtió efecto.
///
/// <see cref="MediosDePago"/> lleva el monto NETO por medio (ampliación "Ventas del turno" con
/// totales por medio arriba de la tabla) — el front deriva el nombre y arma los totales a partir
/// de esta única lista, nunca de una segunda fuente. <see cref="DescuentoManualTotal"/>/
/// <see cref="RecargoManualTotal"/>: los mismos totales de ajuste manual de
/// <see cref="ComprobanteEmitido"/>. <see cref="EstadoFiscal"/> es solo el estado (uno de
/// <see cref="Ways.Application.Fiscal.EstadosFiscalesDeVenta"/>, el mismo de <c>ComprobanteEmitido.Fiscal.Estado</c>): sin la
/// factura ni el error, que se piden por id.
/// <see cref="EstadoNotaDeCredito"/> es el estado de la nota de crédito de la venta anulada con factura aprobada
/// (<c>pendiente</c>, <c>aprobado</c> o <c>rechazado</c>); <c>null</c> si no tiene.</summary>
public sealed record VentaDeTurnoListado(
    int Id,
    long Numero,
    string NumeroVisible,
    EstadoComprobante Estado,
    DateTimeOffset Fecha,
    int IdCliente,
    string NombreCliente,
    decimal Total,
    decimal DescuentoManualTotal,
    decimal RecargoManualTotal,
    IReadOnlyList<MedioDeVentaNeto> MediosDePago,
    string EstadoFiscal,
    string? EstadoNotaDeCredito = null);

/// <summary>Monto neto cobrado por UN medio de pago dentro de una <see cref="VentaDeTurnoListado"/>
/// — <c>Σ(Importe − Vuelto)</c> agrupado por <see cref="IdMedioPago"/> (nunca el importe bruto
/// tecleado en caja: un pago en efectivo con vuelto solo suma lo que realmente quedó en la caja).
/// Reemplaza el <c>IReadOnlyList&lt;string&gt;</c> anterior de solo nombres — la pantalla necesita
/// el monto para totalizar por medio, no solo listar los nombres usados.</summary>
public sealed record MedioDeVentaNeto(int IdMedioPago, string Nombre, decimal Importe);

/// <summary>Cuerpo de <c>POST /api/ventas/reservas-numeracion</c> (stage-pos-reserva-de-numeracion,
/// DB CHANGE GATE aprobado) — solo un dispositivo (claim <c>ways:id_dispositivo</c>) puede pedir
/// un bloque, y solo para SU propio punto de venta (<c>PoliticaDeModoDePuntoVenta</c>, mismo
/// criterio que <c>SolicitudDeVenta.IdPuntoVenta</c>). <see cref="Cantidad"/> tiene un tope
/// (<c>ServicioDeReservasDeNumeracion.CantidadMaxima</c>) — 400 <c>cantidad_invalida</c> fuera de
/// rango.</summary>
public sealed record SolicitudDeReservaDeNumeracion(int IdPuntoVenta, string CodigoTipoComprobante, int Cantidad);

/// <summary>Respuesta de <c>POST /api/ventas/reservas-numeracion</c> — <see cref="Desde"/>/
/// <see cref="Hasta"/> inclusive, el rango que el dispositivo puede repartir localmente hasta
/// agotarlo o hasta pedir uno nuevo (que abandona este, ver <c>AsignadorDeNumeroComprobante.
/// ReservarBloqueAsync</c>).</summary>
public sealed record BloqueDeNumeracionReservado(
    long Desde, long Hasta, int IdPuntoVenta, string CodigoTipoComprobante);
