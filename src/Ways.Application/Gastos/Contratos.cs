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
