using System.Text.Json.Serialization;
using Ways.Domain.Pagos;

namespace Ways.Application.Pagos;

/// <summary>Proyección de <see cref="Pago"/> ya persistido — resultado interno de las altas y
/// ediciones del servicio.</summary>
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

/// <summary>Fila de <c>GET /api/pagos</c> (historial paginado) — mismo criterio de shape
/// reducido que <c>Ways.Application.Caja.TurnoListado</c>. <c>IdPuntoVenta</c> nullable: un pago
/// puede no llevar punto de venta puntual (histórico pre-empresa). <see cref="OrigenFondos"/>
/// también aparece acá (y en <c>LectorDeLineasDelTurno.LeerPagosAsync</c>, mismo DTO): esta fila
/// es informativa (Z-report/historial), nunca un total de arqueo — el origen viaja para que la UI
/// pueda etiquetar "Caja general" sin filtrar nada.
///
/// <see cref="IdTurnoCaja"/>/<see cref="TurnoAbierto"/> le dicen a la UI si el POS todavía puede
/// editar el pago (<c>PUT /api/pagos/{id}</c> exige el turno abierto); los campos editables
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

/// <summary>Página de resultados de <c>GET /api/pagos</c> — mismo shape que
/// <c>Ways.Application.Caja.PaginaDeTurnos</c>.</summary>
public sealed record PaginaDePagos(IReadOnlyList<PagoListado> Items, int Total, int Pagina, int Tamanio);

// ---- Pagos de administración (stage-gastos-admin-retroactivos, PR3) -----------------------
//
// Owner's use case 2: el admin registra, en cualquier momento (incluida una fecha RETROACTIVA),
// un pago que nunca pasó por un turno — pagado directo de la tesorería (caja general) de la
// empresa. Contrato propio, no una extensión del alta del POS: la forma es distinta a
// propósito (IdEmpresa + Fecha de negocio en vez de IdPuntoVenta obligatorio resolviendo turno)
// — mezclar los dos en un solo record con campos condicionales sería más confuso que dos
// contratos chicos, mismo criterio que separar SolicitudDeCompra de SolicitudDeAjuste.

/// <summary>Fila de <c>GET /api/pagos/administracion</c> — proyección más rica que
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
    IReadOnlyList<LineaDePagoListada> Lineas,
    decimal Aplicado = 0m,
    decimal? Disponible = null,
    IReadOnlyList<AplicacionDePagoListada>? Aplicaciones = null);

/// <summary>Una aplicación viva de un pago a una compra, tal como la ve el historial de administración. El número es el
/// externo de la compra (<c>null</c> si no lo tiene).</summary>
public sealed record AplicacionDePagoListada(
    int IdAplicacion, int IdComprobanteCompra, string? NumeroCompra, decimal Importe, DateTimeOffset Fecha);

/// <summary>Página de resultados de <c>GET /api/pagos/administracion</c> — mismo shape que
/// <see cref="PaginaDePagos"/>.</summary>
public sealed record PaginaDePagosDeAdministracion(
    IReadOnlyList<PagoDeAdministracionListado> Items, int Total, int Pagina, int Tamanio);

// ---- Vinculación posterior a una compra (stage-gasto-a-compra, PR4) ------------------------
//
// Owner requirement: cualquier pago (POS o admin) puede ligarse a una compra DESPUÉS de creado,
// no solo al momento del alta (a diferencia de SolicitudDePagoConLineas.IdComprobanteCompra/
// SolicitudDePagoDeAdministracionConLineas.IdComprobanteCompra, que solo aplican en el alta).

/// <summary>Cuerpo de <c>POST /api/pagos/{id}/vincular-compra</c> — un solo campo a propósito,
/// mismo criterio minimalista que <c>SolicitudDeAplicarPrecios</c> para un comando puntual.</summary>
public sealed record SolicitudDeVincularCompra(int IdComprobanteCompra);

// ---- Pago de una compra desde su detalle o su cuenta corriente --------------------------------

/// <summary>Cuerpo de <c>POST /api/compras/{id}/pagos</c>. La empresa, el punto de venta, el
/// proveedor y la categoría salen de la compra, nunca del cliente: un pago es siempre un pago de
/// categoría proveedor, de origen tesorería, imputado a esa compra. <see cref="Fecha"/> es la fecha
/// de negocio (puede ser retroactiva, nunca futura, mismas reglas que
/// <see cref="SolicitudDePagoDeAdministracionConLineas.Fecha"/>). <see cref="Concepto"/> es opcional: sin él
/// queda <c>Pago &lt;tipo&gt; &lt;número&gt;</c>.</summary>
public sealed record SolicitudDePagoDeCompra(DateOnly Fecha, decimal Importe, int IdMedioPago, string? Concepto);

/// <summary>Respuesta de <c>POST /api/compras/{id}/pagos</c>: el pago creado y el estado de pago de
/// la compra después de este pago, leído bajo el mismo lock que lo validó.</summary>
public sealed record ResultadoDePagoDeCompra([property: JsonPropertyName("gasto")] PagoRegistrado Pago, decimal Pagado, decimal SaldoPendiente);

/// <summary>El pago de sueldo recién persistido junto con sus ítems: el desglose del libro de personal lista una
/// línea por ítem.</summary>
public sealed record PagoDeSueldoPersistido(Pago Pago, IReadOnlyList<ItemPago> Items);

/// <summary>Redondeo del importe de <c>POST /api/compras/{id}/pagos</c> (un solo medio): el mismo que aplicaba Postgres al persistir en
/// <c>numeric(14,2)</c> (mitad lejos de cero), para que un importe con más de dos decimales se comporte como antes.</summary>
internal static class ImporteDeUnSoloMedio
{
    public static decimal Redondear(decimal importe) => Math.Round(importe, 2, MidpointRounding.AwayFromZero);
}

// ---- Comandos del servicio con líneas por medio (E2-S3a) ---------------------------------------
// Forma interna del servicio: el importe del pago es la suma de sus líneas. Las solicitudes de /api/pagos se adaptan con
// `AComando`; solo `POST /api/compras/{id}/pagos` conserva la forma `{idMedioPago, importe}` y se adapta a UNA línea con `Desde`.

/// <summary>Alta del POS con líneas por medio. El servidor valida cada medio (existe y no es cuenta corriente).</summary>
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
    OrigenFondosPago OrigenFondos = OrigenFondosPago.CajaTurno);

/// <summary>Alta administrativa con líneas por medio. <see cref="Fecha"/> es la fecha de NEGOCIO elegida por el admin: puede
/// ser retroactiva, nunca futura respecto del "hoy" local de <see cref="IdEmpresa"/>/<see cref="IdPuntoVenta"/>.</summary>
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
    int? IdComprobanteCompra = null);

/// <summary>Edición con líneas por medio: reemplaza el conjunto de líneas del pago.</summary>
public sealed record ComandoDeEdicionDePago(
    CategoriaPago Categoria,
    int? IdProveedor,
    int? IdArea,
    string Concepto,
    string? Detalle,
    IReadOnlyList<LineaDePago> Lineas,
    string? NumeroFactura);

/// <summary>Pago de una compra con líneas por medio. Equivale a <see cref="SolicitudDePagoDeCompra"/>.</summary>
public sealed record ComandoDePagoDeCompra(DateOnly Fecha, IReadOnlyList<LineaDePago> Lineas, string? Concepto)
{
    public static ComandoDePagoDeCompra Desde(SolicitudDePagoDeCompra s) =>
        new(s.Fecha, [new LineaDePago(s.IdMedioPago, ImporteDeUnSoloMedio.Redondear(s.Importe), null)], s.Concepto);
}

// ---- Contrato HTTP /api/pagos con líneas por medio (E2-S4) ---------------------------------------
// El importe del pago es la suma de sus líneas, así que ninguna de estas solicitudes lleva `idMedioPago` ni `importe` sueltos.

/// <summary>Cuerpo de <c>POST /api/pagos</c>. Sin <c>idTurnoCaja</c> a propósito: el turno siempre lo resuelve el servidor desde
/// <see cref="IdPuntoVenta"/> (mismo criterio que <c>Ways.Application.Caja.SolicitudDeApertura</c>). Si viene
/// <see cref="IdComprobanteCompra"/>, <see cref="Categoria"/> tiene que ser <see cref="CategoriaPago.Proveedor"/> e
/// <see cref="IdProveedor"/> se deriva de la compra cuando falta y se exige que coincida cuando viene. El importe es la suma de
/// las <see cref="Lineas"/>; cada línea se valida contra el servidor: el medio tiene que existir y no ser de cuenta corriente.</summary>
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
        IdComprobanteCompra, OrigenFondos);
}

/// <summary>Cuerpo de <c>POST /api/pagos/administracion</c>. <see cref="Fecha"/> es la fecha de NEGOCIO elegida por el admin
/// (puede ser retroactiva, nunca futura); <see cref="IdPuntoVenta"/> es opcional y solo da trazabilidad, nunca resuelve un turno.
/// El importe es la suma de las <see cref="Lineas"/>.</summary>
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
/// del pago. Edita también un pago con varios medios.</summary>
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
        Categoria, IdProveedor, IdArea, Concepto, Detalle, Lineas, NumeroFactura);
}

/// <summary>Cuerpo de <c>POST /api/pagos/de-compra/{idCompra}</c>. Mismas reglas que <see cref="SolicitudDePagoDeCompra"/>
/// con <see cref="Lineas"/> en lugar de medio e importe, y a diferencia de aquella admite pagar de más (el excedente queda a
/// favor en el pago nuevo) y aplicar pagos ya existentes. Todo campo se usa o se rechaza: <c>fecha</c> se valida siempre (no
/// futura) y es la fecha de negocio del pago nuevo y de las aplicaciones que crea el pedido, <c>concepto</c> es del pago
/// nuevo (sin <c>lineas</c> es <c>400 concepto_sin_pago_nuevo</c>), <c>lineas</c> (0 a 20) son el pago nuevo y
/// <c>aplicaciones</c> (0 a 20) son pagos existentes cuyo disponible se aplica a esta compra; al menos una de las dos
/// listas no puede estar vacía. Con <c>lineas</c>, la compra tiene que seguir con saldo después de aplicar lo existente
/// (<c>409 compra_sin_saldo_pendiente</c>). <c>cerrarEnCero</c> (por defecto <c>false</c>) cierra a mano la diferencia entre el
/// saldo y lo que paga el pedido (aplicaciones + líneas) si no supera la tolerancia de redondeo de la compra: lo que falta se
/// perdona (la compra queda Pagada) y lo que sobra del pago nuevo se absorbe en vez de quedar a favor; pasada la tolerancia es
/// <c>409 redondeo_excede_tolerancia</c> y no se escribe nada.</summary>
public sealed record SolicitudDePagoDeCompraConLineas(
    DateOnly Fecha,
    IReadOnlyList<LineaDePago>? Lineas,
    string? Concepto,
    IReadOnlyList<AplicacionDePagoExistente>? Aplicaciones = null,
    bool CerrarEnCero = false)
{
    public ComandoDePagoDeCompraConAplicaciones AComando() => new(Fecha, Lineas, Concepto, Aplicaciones, CerrarEnCero);
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
/// conserva <c>gasto</c>) y el estado de pago de la compra. <c>pago</c> es <c>null</c> cuando el pedido no trajo líneas (solo
/// aplicó pagos existentes); <c>aplicaciones</c> son todas las aplicaciones vivas a la compra después de la operación;
/// <c>disponibleDelPago</c> es lo que del pago nuevo quedó a favor (<c>null</c> sin pago nuevo). <c>redondeo</c> es la diferencia
/// que cerró <c>cerrarEnCero</c> (<c>null</c> si no se pidió o no había diferencia): positiva si faltaba pagar y se perdonó (la
/// compra quedó Pagada), negativa si el pago nuevo pagó de más y el excedente se absorbió (disponible 0).</summary>
public sealed record ResultadoDePagoDeCompraConLineas(
    PagoConLineas? Pago,
    decimal Pagado,
    decimal SaldoPendiente,
    IReadOnlyList<AplicacionALaCompraListada> Aplicaciones,
    decimal? DisponibleDelPago,
    decimal? Redondeo = null);

/// <summary>Una aplicación viva de un pago a la compra, tal como la devuelve el pago de una compra.</summary>
public sealed record AplicacionALaCompraListada(int IdAplicacion, int IdPago, decimal Importe, DateTimeOffset Fecha);

/// <summary>Resultado interno de pagar una compra con aplicaciones: el pago nuevo (si hubo líneas) y el estado de la compra.</summary>
public sealed record ResultadoDePagoDeCompraConAplicaciones(
    PagoRegistrado? Pago,
    decimal Pagado,
    decimal SaldoPendiente,
    IReadOnlyList<AplicacionALaCompraListada> Aplicaciones,
    decimal? DisponibleDelPago,
    decimal? Redondeo = null);
