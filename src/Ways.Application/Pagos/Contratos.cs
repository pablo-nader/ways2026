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
/// no se da de baja ni se liga a una compra por separado (409 <c>gasto_de_pago_de_personal</c>).
/// <see cref="Lineas"/> trae el reparto por medio; <see cref="IdMedioPago"/> es el de la primera línea.</summary>
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
    int? IdMovimientoPersonal,
    IReadOnlyList<LineaDePagoListada> Lineas);

/// <summary>Línea por medio de un pago en un listado. <see cref="PagoListado.IdMedioPago"/> sigue exponiendo solo el medio
/// de la primera línea (el del ítem vivo de menor id) para los clientes que aún no leen las líneas.
/// <see cref="NombreMedioPago"/> sale en <c>null</c> si el medio se dio de baja.</summary>
public sealed record LineaDePagoListada(int IdItem, int IdMedioPago, string? NombreMedioPago, decimal Importe, string? Referencia);

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
/// vinculación a una compra responden 409 <c>gasto_de_pago_de_personal</c>.
/// <see cref="Lineas"/> trae el reparto por medio; <see cref="IdMedioPago"/> es el de la primera línea.</summary>
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
    int? IdMovimientoPersonal,
    IReadOnlyList<LineaDePagoListada> Lineas);

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

/// <summary>Un pago de sueldo recién persistido junto con su único ítem: el desglose del libro de personal lista una
/// línea por ítem.</summary>
public sealed record PagoDeSueldoPersistido(Pago Pago, ItemPago Item);

/// <summary>Redondeo del importe de las rutas actuales (un solo medio): el mismo que aplicaba Postgres al persistir en
/// <c>numeric(14,2)</c> (mitad lejos de cero), para que un importe con más de dos decimales se comporte como antes.</summary>
internal static class ImporteDeUnSoloMedio
{
    public static decimal Redondear(decimal importe) => Math.Round(importe, 2, MidpointRounding.AwayFromZero);
}

// ---- Comandos del servicio con líneas por medio (E2-S3a) ---------------------------------------
// Forma interna del servicio: el importe del pago es la suma de sus líneas. Las solicitudes HTTP de arriba
// conservan su forma `{idMedioPago, importe}` y se adaptan a UNA línea con `Desde`.

/// <summary>Alta del POS con líneas por medio. Equivale a <see cref="SolicitudDePago"/> con <c>Lineas</c> en lugar
/// de medio e importe. <see cref="ValidarMedios"/> hace que el servidor valide cada medio (existe y no es cuenta
/// corriente); las rutas /api/gastos lo dejan en <c>false</c> porque el POS instalado nunca lo exigió.</summary>
public sealed record ComandoDePago(
    int IdPuntoVenta,
    CategoriaPago Categoria,
    int? IdProveedor,
    int? IdArea,
    string Concepto,
    string? Detalle,
    IReadOnlyList<LineaDePago> Lineas,
    string? NumeroFactura,
    int? IdComprobanteCompra = null,
    OrigenFondosPago OrigenFondos = OrigenFondosPago.CajaTurno,
    bool ValidarMedios = false)
{
    public static ComandoDePago Desde(SolicitudDePago s) => new(
        s.IdPuntoVenta, s.Categoria, s.IdProveedor, s.IdArea, s.Concepto, s.Detalle,
        [new LineaDePago(s.IdMedioPago, ImporteDeUnSoloMedio.Redondear(s.Importe), null)],
        s.NumeroFactura, s.IdComprobanteCompra, s.OrigenFondos);
}

/// <summary>Alta administrativa con líneas por medio. Equivale a <see cref="SolicitudDePagoDeAdministracion"/>.</summary>
public sealed record ComandoDePagoDeAdministracion(
    DateOnly Fecha,
    int IdEmpresa,
    int? IdPuntoVenta,
    CategoriaPago Categoria,
    int? IdProveedor,
    int? IdArea,
    string Concepto,
    string? Detalle,
    IReadOnlyList<LineaDePago> Lineas,
    string? NumeroFactura,
    int? IdComprobanteCompra = null)
{
    public static ComandoDePagoDeAdministracion Desde(SolicitudDePagoDeAdministracion s) => new(
        s.Fecha, s.IdEmpresa, s.IdPuntoVenta, s.Categoria, s.IdProveedor, s.IdArea, s.Concepto, s.Detalle,
        [new LineaDePago(s.IdMedioPago, ImporteDeUnSoloMedio.Redondear(s.Importe), null)],
        s.NumeroFactura, s.IdComprobanteCompra);
}

/// <summary>Edición con líneas por medio: reemplaza el conjunto de líneas del pago. <see cref="DeUnSoloMedio"/> marca
/// el camino de las rutas actuales, que solo conocen un medio: sobre un pago con varios ítems vivos responde
/// <c>409 gasto_con_varios_medios</c> en vez de colapsarlos, y conserva la referencia del único ítem.</summary>
public sealed record ComandoDeEdicionDePago(
    CategoriaPago Categoria,
    int? IdProveedor,
    int? IdArea,
    string Concepto,
    string? Detalle,
    IReadOnlyList<LineaDePago> Lineas,
    string? NumeroFactura,
    bool DeUnSoloMedio = false)
{
    public static ComandoDeEdicionDePago Desde(SolicitudDeEdicionDePago s) => new(
        s.Categoria, s.IdProveedor, s.IdArea, s.Concepto, s.Detalle,
        [new LineaDePago(s.IdMedioPago, ImporteDeUnSoloMedio.Redondear(s.Importe), null)],
        s.NumeroFactura, DeUnSoloMedio: true);
}

/// <summary>Pago de una compra con líneas por medio. Equivale a <see cref="SolicitudDePagoDeCompra"/>.</summary>
public sealed record ComandoDePagoDeCompra(DateOnly Fecha, IReadOnlyList<LineaDePago> Lineas, string? Concepto)
{
    public static ComandoDePagoDeCompra Desde(SolicitudDePagoDeCompra s) =>
        new(s.Fecha, [new LineaDePago(s.IdMedioPago, ImporteDeUnSoloMedio.Redondear(s.Importe), null)], s.Concepto);
}

// ---- Contrato HTTP /api/pagos con líneas por medio (E2-S4) ---------------------------------------
// Contrato nuevo, a la par de /api/gastos (que queda igual como adaptador de un solo medio). El importe del pago es la
// suma de sus líneas, así que ninguna de estas solicitudes lleva `idMedioPago` ni `importe` sueltos.

/// <summary>Cuerpo de <c>POST /api/pagos</c>. Mismos campos que <see cref="SolicitudDePago"/> con <see cref="Lineas"/> en lugar
/// de medio e importe. Cada línea se valida contra el servidor: el medio tiene que existir y no ser de cuenta corriente.</summary>
public sealed record SolicitudDePagoConLineas(
    int IdPuntoVenta,
    CategoriaPago Categoria,
    int? IdProveedor,
    int? IdArea,
    string Concepto,
    string? Detalle,
    string? NumeroFactura,
    IReadOnlyList<LineaDePago> Lineas,
    int? IdComprobanteCompra = null,
    OrigenFondosPago OrigenFondos = OrigenFondosPago.CajaTurno)
{
    public ComandoDePago AComando() => new(
        IdPuntoVenta, Categoria, IdProveedor, IdArea, Concepto, Detalle, Lineas, NumeroFactura,
        IdComprobanteCompra, OrigenFondos, ValidarMedios: true);
}

/// <summary>Cuerpo de <c>POST /api/pagos/administracion</c>. Mismos campos que <see cref="SolicitudDePagoDeAdministracion"/>
/// con <see cref="Lineas"/> en lugar de medio e importe.</summary>
public sealed record SolicitudDePagoDeAdministracionConLineas(
    DateOnly Fecha,
    int IdEmpresa,
    int? IdPuntoVenta,
    CategoriaPago Categoria,
    int? IdProveedor,
    int? IdArea,
    string Concepto,
    string? Detalle,
    string? NumeroFactura,
    IReadOnlyList<LineaDePago> Lineas,
    int? IdComprobanteCompra = null)
{
    public ComandoDePagoDeAdministracion AComando() => new(
        Fecha, IdEmpresa, IdPuntoVenta, Categoria, IdProveedor, IdArea, Concepto, Detalle, Lineas, NumeroFactura,
        IdComprobanteCompra);
}

/// <summary>Cuerpo de <c>PUT /api/pagos/{id}</c> y <c>PUT /api/pagos/administracion/{id}</c>: reemplaza el conjunto de líneas
/// del pago. A diferencia de <c>PUT /api/gastos/{id}</c>, también edita un pago con varios medios.</summary>
public sealed record SolicitudDeEdicionDePagoConLineas(
    CategoriaPago Categoria,
    int? IdProveedor,
    int? IdArea,
    string Concepto,
    string? Detalle,
    string? NumeroFactura,
    IReadOnlyList<LineaDePago> Lineas)
{
    public ComandoDeEdicionDePago AComando() => new(
        Categoria, IdProveedor, IdArea, Concepto, Detalle, Lineas, NumeroFactura, DeUnSoloMedio: false);
}

/// <summary>Cuerpo de <c>POST /api/pagos/de-compra/{idCompra}</c>. Mismas reglas que <see cref="SolicitudDePagoDeCompra"/>
/// con <see cref="Lineas"/> en lugar de medio e importe.</summary>
public sealed record SolicitudDePagoDeCompraConLineas(DateOnly Fecha, IReadOnlyList<LineaDePago> Lineas, string? Concepto)
{
    public ComandoDePagoDeCompra AComando() => new(Fecha, Lineas, Concepto);
}

/// <summary>Pago recién persistido del contrato nuevo: sin el medio ni el nombre de la cabecera, el reparto vive en
/// <see cref="Lineas"/>.</summary>
public sealed record PagoConLineas(
    int Id,
    int? IdTurnoCaja,
    int? IdPuntoVenta,
    DateTimeOffset Fecha,
    CategoriaPago Categoria,
    int? IdProveedor,
    int? IdArea,
    string Concepto,
    string? Detalle,
    string? NumeroFactura,
    decimal Importe,
    int IdEmpleado,
    int? IdComprobanteCompra,
    OrigenFondosPago OrigenFondos,
    IReadOnlyList<LineaDePagoListada> Lineas)
{
    public static PagoConLineas Desde(PagoRegistrado p, IReadOnlyList<LineaDePagoListada> lineas) => new(
        p.Id, p.IdTurnoCaja, p.IdPuntoVenta, p.Fecha, p.Categoria, p.IdProveedor, p.IdArea, p.Concepto, p.Detalle,
        p.NumeroFactura, p.Importe, p.IdEmpleado, p.IdComprobanteCompra, p.OrigenFondos, lineas);
}

/// <summary>Respuesta de <c>POST /api/pagos/de-compra/{idCompra}</c>: el pago bajo la clave <c>pago</c> (la ruta de compras
/// conserva <c>gasto</c>) y el estado de pago de la compra.</summary>
public sealed record ResultadoDePagoDeCompraConLineas(PagoConLineas Pago, decimal Pagado, decimal SaldoPendiente);
