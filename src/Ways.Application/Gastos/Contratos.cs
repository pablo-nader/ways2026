using Ways.Domain.Gastos;

namespace Ways.Application.Gastos;

/// <summary>Cuerpo de <c>POST /api/gastos</c> (design: API Surface) — sin <c>idTurnoCaja</c> a
/// propósito, mismo criterio que <c>Ways.Application.Caja.SolicitudDeApertura</c>: el turno
/// siempre lo resuelve el servidor desde <see cref="IdPuntoVenta"/> (spec: Gasto Requires An
/// Open Turno, Gasto succeeds with an open turno), nunca este contrato.
///
/// <see cref="IdComprobanteCompra"/> aterriza en stage-8 Slice 4 (design decisión 7) — opcional,
/// al final para no romper ningún call site posicional existente. Cuando viene seteado,
/// <see cref="Categoria"/> tiene que ser <see cref="CategoriaGasto.Proveedor"/> (spec: gastos / A
/// Comprobante Compra Link Requires Categoria Proveedor) e <see cref="IdProveedor"/> es opcional:
/// se deriva de la compra cuando falta, se exige que coincida cuando viene.
///
/// <see cref="OrigenFondos"/> aterriza en stage-gastos-origen-fondos-pos (PR2) — último campo,
/// opcional con default <see cref="OrigenFondosGasto.CajaTurno"/> a propósito: retrocompatibilidad
/// con clientes existentes (incluida la cola offline del POS) que todavía no lo mandan nunca deben
/// perder el comportamiento de hoy.</summary>
public sealed record SolicitudDeGasto(
    int IdPuntoVenta,
    CategoriaGasto Categoria,
    int? IdProveedor,
    int? IdArea,
    string Concepto,
    string? Detalle,
    int IdMedioPago,
    string? NumeroFactura,
    decimal Importe,
    int? IdComprobanteCompra = null,
    OrigenFondosGasto OrigenFondos = OrigenFondosGasto.CajaTurno);

/// <summary>Proyección de <see cref="Gasto"/> ya persistido — respuesta de <c>POST
/// /api/gastos</c>.</summary>
public sealed record GastoRegistrado(
    int Id,
    int? IdTurnoCaja,
    int? IdPuntoVenta,
    DateTimeOffset Fecha,
    CategoriaGasto Categoria,
    int? IdProveedor,
    int? IdArea,
    string Concepto,
    string? Detalle,
    int IdMedioPago,
    string? NumeroFactura,
    decimal Importe,
    int IdEmpleado,
    int? IdComprobanteCompra,
    OrigenFondosGasto OrigenFondos);

/// <summary>Fila de <c>GET /api/gastos</c> (historial paginado) — mismo criterio de shape
/// reducido que <c>Ways.Application.Caja.TurnoListado</c>. <c>IdPuntoVenta</c> nullable: un gasto
/// puede no llevar punto de venta puntual (histórico pre-empresa). <see cref="OrigenFondos"/>
/// también aparece acá (y en <c>LectorDeLineasDelTurno.LeerGastosAsync</c>, mismo DTO): esta fila
/// es informativa (Z-report/historial), nunca un total de arqueo — el origen viaja para que la UI
/// pueda etiquetar "Caja general" sin filtrar nada.</summary>
public sealed record GastoListado(
    int Id,
    int? IdPuntoVenta,
    DateTimeOffset Fecha,
    CategoriaGasto Categoria,
    int IdMedioPago,
    decimal Importe,
    OrigenFondosGasto OrigenFondos);

/// <summary>Página de resultados de <c>GET /api/gastos</c> — mismo shape que
/// <c>Ways.Application.Caja.PaginaDeTurnos</c>.</summary>
public sealed record PaginaDeGastos(IReadOnlyList<GastoListado> Items, int Total, int Pagina, int Tamanio);

// ---- Gastos de administración (stage-gastos-admin-retroactivos, PR3) -----------------------
//
// Owner's use case 2: el admin registra, en cualquier momento (incluida una fecha RETROACTIVA),
// un gasto que nunca pasó por un turno — pagado directo de la tesorería (caja general) de la
// empresa. Contrato propio, no una extensión de SolicitudDeGasto: la forma es distinta a
// propósito (IdEmpresa + Fecha de negocio en vez de IdPuntoVenta obligatorio resolviendo turno)
// — mezclar los dos en un solo record con campos condicionales sería más confuso que dos
// contratos chicos, mismo criterio que separar SolicitudDeCompra de SolicitudDeAjuste.

/// <summary>Cuerpo de <c>POST /api/gastos/administracion</c>. <see cref="Fecha"/> es la fecha de
/// NEGOCIO elegida por el admin — puede ser retroactiva (spec: owner's use case 2), nunca futura
/// respecto del "hoy" local de <see cref="IdEmpresa"/>/<see cref="IdPuntoVenta"/>
/// (<c>ServicioDeGastos.ExigirFechaNoFutura</c>) — mismo shape <c>DateOnly</c> que
/// <c>SolicitudDeCompra.FechaComprobante</c>: la hora la sigue poniendo el servidor
/// (<c>reloj.Ahora</c>) para los movimientos de ledger, la fecha de negocio es lo único que el
/// admin controla. <see cref="IdEmpresa"/> reemplaza a <c>IdPuntoVenta</c> como ancla del gasto
/// (la tesorería es un fondo POR EMPRESA, doc 10 §7) — <see cref="IdPuntoVenta"/> es opcional,
/// solo trazabilidad, nunca resuelve un turno (no hay turno en este camino:
/// <see cref="Ways.Application.Gastos.ServicioDeGastos.RegistrarDeAdministracionAsync"/> nunca
/// llama a <c>ServicioDeTurnos</c>). <see cref="IdComprobanteCompra"/>/<see cref="IdProveedor"/>
/// siguen las mismas reglas de ligadura que <c>SolicitudDeGasto</c> (categoría proveedor
/// obligatoria, proveedor derivado/validado contra la compra).</summary>
public sealed record SolicitudDeGastoDeAdministracion(
    DateOnly Fecha,
    int IdEmpresa,
    int? IdPuntoVenta,
    CategoriaGasto Categoria,
    int? IdProveedor,
    int? IdArea,
    string Concepto,
    string? Detalle,
    int IdMedioPago,
    string? NumeroFactura,
    decimal Importe,
    int? IdComprobanteCompra = null);

/// <summary>Fila de <c>GET /api/gastos/administracion</c> — proyección más rica que
/// <see cref="GastoListado"/> (pensada para el historial operativo del turno, esta es la
/// pantalla de gestión completa): nombres resueltos de proveedor/área/medio de pago vía LEFT
/// JOIN (dangling-fk-read-models — un catálogo dado de baja lógica nunca puede tirar la fila ni
/// romper el listado, el nombre simplemente sale <c>null</c>) y el número de compra ligada para
/// trazabilidad.</summary>
public sealed record GastoDeAdministracionListado(
    int Id,
    DateTimeOffset Fecha,
    int IdEmpresa,
    int? IdPuntoVenta,
    int? IdTurnoCaja,
    CategoriaGasto Categoria,
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
    OrigenFondosGasto OrigenFondos,
    int? IdComprobanteCompra);

/// <summary>Página de resultados de <c>GET /api/gastos/administracion</c> — mismo shape que
/// <see cref="PaginaDeGastos"/>.</summary>
public sealed record PaginaDeGastosDeAdministracion(
    IReadOnlyList<GastoDeAdministracionListado> Items, int Total, int Pagina, int Tamanio);
