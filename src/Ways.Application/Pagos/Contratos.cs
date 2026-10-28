using System.Text.Json.Serialization;
using Ways.Domain.Pagos;

namespace Ways.Application.Pagos;

/// <summary>Cuerpo de <c>POST /api/gastos</c> (design: API Surface) — sin <c>idTurnoCaja</c> a
/// propósito, mismo criterio que <c>Ways.Application.Caja.SolicitudDeApertura</c>: el turno
/// siempre lo resuelve el servidor desde <see cref="IdPuntoVenta"/> (spec: Pago Requires An
/// Open Turno, Pago succeeds with an open turno), nunca este contrato.
///
/// <see cref="IdComprobanteCompra"/> aterriza en stage-8 Slice 4 (design decisión 7) — opcional,
/// al final para no romper ningún call site posicional existente. Cuando viene seteado,
/// <see cref="Categoria"/> tiene que ser <see cref="CategoriaPago.Proveedor"/> (spec: pagos / A
/// Comprobante Compra Link Requires Categoria Proveedor) e <see cref="IdProveedor"/> es opcional:
/// se deriva de la compra cuando falta, se exige que coincida cuando viene.
///
/// <see cref="OrigenFondos"/> aterriza en stage-gastos-origen-fondos-pos (PR2) — último campo,
/// opcional con default <see cref="OrigenFondosPago.CajaTurno"/> a propósito: retrocompatibilidad
/// con clientes existentes (incluida la cola offline del POS) que todavía no lo mandan nunca deben
/// perder el comportamiento de hoy.</summary>
public sealed record SolicitudDePago(
    int IdPuntoVenta,
    CategoriaPago Categoria,
    int? IdProveedor,
    int? IdArea,
    string Concepto,
    string? Detalle,
    int IdMedioPago,
    string? NumeroFactura,
    decimal Importe,
    int? IdComprobanteCompra = null,
    OrigenFondosPago OrigenFondos = OrigenFondosPago.CajaTurno);

/// <summary>Proyección de <see cref="Pago"/> ya persistido — respuesta de <c>POST
/// /api/gastos</c>.</summary>
public sealed record PagoRegistrado(
    int Id,
    int? IdTurnoCaja,
    int? IdPuntoVenta,
    DateTimeOffset Fecha,
    CategoriaPago Categoria,
    int? IdProveedor,
    int? IdArea,
    string Concepto,
    string? Detalle,
    int IdMedioPago,
    string? NumeroFactura,
    decimal Importe,
    int IdEmpleado,
    int? IdComprobanteCompra,
    OrigenFondosPago OrigenFondos);

/// <summary>Fila de <c>GET /api/gastos</c> (historial paginado) — mismo criterio de shape
/// reducido que <c>Ways.Application.Caja.TurnoListado</c>. <c>IdPuntoVenta</c> nullable: un pago
/// puede no llevar punto de venta puntual (histórico pre-empresa). <see cref="OrigenFondos"/>
/// también aparece acá (y en <c>LectorDeLineasDelTurno.LeerPagosAsync</c>, mismo DTO): esta fila
/// es informativa (Z-report/historial), nunca un total de arqueo — el origen viaja para que la UI
/// pueda etiquetar "Caja general" sin filtrar nada.
///
/// <see cref="IdTurnoCaja"/>/<see cref="TurnoAbierto"/> le dicen a la UI si el POS todavía puede
/// editar el pago (<c>PUT /api/gastos/{id}</c> exige el turno abierto); los campos editables
/// (<see cref="IdProveedor"/>…<see cref="NumeroFactura"/>) permiten precargar ese formulario desde
/// el detalle del turno sin una segunda lectura. <see cref="IdComprobanteCompra"/> le dice al POS
/// que el pago está ligado a una compra (categoría y proveedor quedan bloqueados en la edición).
/// <see cref="IdMovimientoPersonal"/> no nulo le dice que el pago nació de un pago de personal: no se edita,
/// no se da de baja ni se liga a una compra por separado (409 <c>gasto_de_pago_de_personal</c>).</summary>
public sealed record PagoListado(
    int Id,
    int? IdPuntoVenta,
    DateTimeOffset Fecha,
    CategoriaPago Categoria,
    int IdMedioPago,
    decimal Importe,
    OrigenFondosPago OrigenFondos,
    int? IdTurnoCaja,
    bool TurnoAbierto,
    int? IdProveedor,
    int? IdArea,
    string Concepto,
    string? Detalle,
    string? NumeroFactura,
    int? IdComprobanteCompra,
    int? IdMovimientoPersonal);

/// <summary>Página de resultados de <c>GET /api/gastos</c> — mismo shape que
/// <c>Ways.Application.Caja.PaginaDeTurnos</c>.</summary>
public sealed record PaginaDePagos(IReadOnlyList<PagoListado> Items, int Total, int Pagina, int Tamanio);

// ---- Pagos de administración (stage-gastos-admin-retroactivos, PR3) -----------------------
//
// Owner's use case 2: el admin registra, en cualquier momento (incluida una fecha RETROACTIVA),
// un pago que nunca pasó por un turno — pagado directo de la tesorería (caja general) de la
// empresa. Contrato propio, no una extensión de SolicitudDePago: la forma es distinta a
// propósito (IdEmpresa + Fecha de negocio en vez de IdPuntoVenta obligatorio resolviendo turno)
// — mezclar los dos en un solo record con campos condicionales sería más confuso que dos
// contratos chicos, mismo criterio que separar SolicitudDeCompra de SolicitudDeAjuste.

/// <summary>Cuerpo de <c>POST /api/gastos/administracion</c>. <see cref="Fecha"/> es la fecha de
/// NEGOCIO elegida por el admin — puede ser retroactiva (spec: owner's use case 2), nunca futura
/// respecto del "hoy" local de <see cref="IdEmpresa"/>/<see cref="IdPuntoVenta"/>
/// (<c>ServicioDePagos.ExigirFechaNoFutura</c>) — mismo shape <c>DateOnly</c> que
/// <c>SolicitudDeCompra.FechaComprobante</c>: la hora la sigue poniendo el servidor
/// (<c>reloj.Ahora</c>) para los movimientos de ledger, la fecha de negocio es lo único que el
/// admin controla. <see cref="IdEmpresa"/> reemplaza a <c>IdPuntoVenta</c> como ancla del pago
/// (la tesorería es un fondo POR EMPRESA, doc 10 §7) — <see cref="IdPuntoVenta"/> es opcional,
/// solo trazabilidad, nunca resuelve un turno (no hay turno en este camino:
/// <see cref="Ways.Application.Pagos.ServicioDePagos.RegistrarDeAdministracionAsync"/> nunca
/// llama a <c>ServicioDeTurnos</c>). <see cref="IdComprobanteCompra"/>/<see cref="IdProveedor"/>
/// siguen las mismas reglas de ligadura que <c>SolicitudDePago</c> (categoría proveedor
/// obligatoria, proveedor derivado/validado contra la compra).</summary>
public sealed record SolicitudDePagoDeAdministracion(
    DateOnly Fecha,
    int IdEmpresa,
    int? IdPuntoVenta,
    CategoriaPago Categoria,
    int? IdProveedor,
    int? IdArea,
    string Concepto,
    string? Detalle,
    int IdMedioPago,
    string? NumeroFactura,
    decimal Importe,
    int? IdComprobanteCompra = null);

/// <summary>Fila de <c>GET /api/gastos/administracion</c> — proyección más rica que
/// <see cref="PagoListado"/> (pensada para el historial operativo del turno, esta es la
/// pantalla de gestión completa): nombres resueltos de proveedor/área/medio de pago vía LEFT
/// JOIN (dangling-fk-read-models — un catálogo dado de baja lógica nunca puede tirar la fila ni
/// romper el listado, el nombre simplemente sale <c>null</c>) y el número de compra ligada para
/// trazabilidad. <see cref="TurnoAbierto"/>: <c>true</c> solo si el pago tiene turno y ese turno
/// sigue abierto (la edición de un pago de caja de un turno cerrado recalcula su arqueo).
/// <see cref="IdMovimientoPersonal"/> no nulo: el pago nació de un pago de personal y su edición, su baja y su
/// vinculación a una compra responden 409 <c>gasto_de_pago_de_personal</c>.</summary>
public sealed record PagoDeAdministracionListado(
    int Id,
    DateTimeOffset Fecha,
    int IdEmpresa,
    int? IdPuntoVenta,
    int? IdTurnoCaja,
    CategoriaPago Categoria,
    int? IdProveedor,
    string? NombreProveedor,
    int? IdArea,
    string? NombreArea,
    string Concepto,
    string? Detalle,
    int IdMedioPago,
    string? NombreMedioPago,
    string? NumeroFactura,
    decimal Importe,
    OrigenFondosPago OrigenFondos,
    int? IdComprobanteCompra,
    bool TurnoAbierto,
    int? IdMovimientoPersonal);

/// <summary>Página de resultados de <c>GET /api/gastos/administracion</c> — mismo shape que
/// <see cref="PaginaDePagos"/>.</summary>
public sealed record PaginaDePagosDeAdministracion(
    IReadOnlyList<PagoDeAdministracionListado> Items, int Total, int Pagina, int Tamanio);

// ---- Vinculación posterior a una compra (stage-gasto-a-compra, PR4) ------------------------
//
// Owner requirement: cualquier pago (POS o admin) puede ligarse a una compra DESPUÉS de creado,
// no solo al momento del alta (a diferencia de SolicitudDePago.IdComprobanteCompra/
// SolicitudDePagoDeAdministracion.IdComprobanteCompra, que solo aplican en el alta).

/// <summary>Cuerpo de <c>POST /api/gastos/{id}/vincular-compra</c> — un solo campo a propósito,
/// mismo criterio minimalista que <c>SolicitudDeAplicarPrecios</c> para un comando puntual.</summary>
public sealed record SolicitudDeVincularCompra(int IdComprobanteCompra);

// ---- Edición y baja de pagos ---------------------------------------------------------------

/// <summary>Cuerpo de <c>PUT /api/gastos/{id}</c> (POS, solo con el turno del pago abierto) y de
/// <c>PUT /api/gastos/administracion/{id}</c> (admin, cualquier pago). Solo los campos editables:
/// origen de fondos, punto de venta, empresa, turno, fecha y compra ligada no se cambian por esta
/// vía. Mismas validaciones que el alta.</summary>
public sealed record SolicitudDeEdicionDePago(
    CategoriaPago Categoria,
    int? IdProveedor,
    int? IdArea,
    string Concepto,
    string? Detalle,
    int IdMedioPago,
    string? NumeroFactura,
    decimal Importe);

// ---- Pago de una compra desde su detalle o su cuenta corriente --------------------------------

/// <summary>Cuerpo de <c>POST /api/compras/{id}/pagos</c>. La empresa, el punto de venta, el
/// proveedor y la categoría salen de la compra, nunca del cliente: un pago es siempre un pago de
/// categoría proveedor, de origen tesorería, imputado a esa compra. <see cref="Fecha"/> es la fecha
/// de negocio (puede ser retroactiva, nunca futura, mismas reglas que
/// <see cref="SolicitudDePagoDeAdministracion.Fecha"/>). <see cref="Concepto"/> es opcional: sin él
/// queda <c>Pago &lt;tipo&gt; &lt;número&gt;</c>.</summary>
public sealed record SolicitudDePagoDeCompra(DateOnly Fecha, decimal Importe, int IdMedioPago, string? Concepto);

/// <summary>Respuesta de <c>POST /api/compras/{id}/pagos</c>: el pago creado y el estado de pago de
/// la compra después de este pago, leído bajo el mismo lock que lo validó.</summary>
public sealed record ResultadoDePagoDeCompra([property: JsonPropertyName("gasto")] PagoRegistrado Pago, decimal Pagado, decimal SaldoPendiente);
