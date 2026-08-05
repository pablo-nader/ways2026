using Ways.Domain.Gastos;

namespace Ways.Application.Gastos;

/// <summary>Cuerpo de <c>POST /api/gastos</c> (design: API Surface) — sin <c>idTurnoCaja</c> a
/// propósito, mismo criterio que <c>Ways.Application.Caja.SolicitudDeApertura</c>: el turno
/// siempre lo resuelve el servidor desde <see cref="IdPuntoVenta"/> (spec: Gasto Requires An
/// Open Turno, Gasto succeeds with an open turno), nunca este contrato.</summary>
public sealed record SolicitudDeGasto(
    int IdPuntoVenta,
    CategoriaGasto Categoria,
    int? IdProveedor,
    int? IdArea,
    string Concepto,
    string? Detalle,
    int IdMedioPago,
    string? NumeroFactura,
    decimal Importe);

/// <summary>Proyección de <see cref="Gasto"/> ya persistido — respuesta de <c>POST
/// /api/gastos</c>.</summary>
public sealed record GastoRegistrado(
    int Id,
    int IdTurnoCaja,
    int IdPuntoVenta,
    DateTimeOffset Fecha,
    CategoriaGasto Categoria,
    int? IdProveedor,
    int? IdArea,
    string Concepto,
    string? Detalle,
    int IdMedioPago,
    string? NumeroFactura,
    decimal Importe,
    int IdEmpleado);

/// <summary>Fila de <c>GET /api/gastos</c> (historial paginado) — mismo criterio de shape
/// reducido que <c>Ways.Application.Caja.TurnoListado</c>.</summary>
public sealed record GastoListado(
    int Id,
    int IdPuntoVenta,
    DateTimeOffset Fecha,
    CategoriaGasto Categoria,
    int IdMedioPago,
    decimal Importe);

/// <summary>Página de resultados de <c>GET /api/gastos</c> — mismo shape que
/// <c>Ways.Application.Caja.PaginaDeTurnos</c>.</summary>
public sealed record PaginaDeGastos(IReadOnlyList<GastoListado> Items, int Total, int Pagina, int Tamanio);
