using Ways.Domain.Caja;

namespace Ways.Application.Caja;

/// <summary>Cuerpo de <c>POST /api/caja/turnos</c> (design: API Surface) — sin campo de
/// empleado a propósito, mismo criterio que <c>Ways.Application.Ventas.SolicitudDeVenta</c>
/// (design decisión 11): <c>id_empleado_apertura</c> siempre sale de
/// <c>IContextoDeUsuario.UsuarioId</c>, nunca de este contrato.</summary>
public sealed record SolicitudDeApertura(int IdPuntoVenta, decimal FondoInicial, string? Observaciones);

/// <summary>Cuerpo de <c>POST /api/caja/turnos/{id}/movimientos</c> (design: API Surface) — el
/// turno lo identifica la ruta, nunca este cuerpo (spec: idTurnoCaja is not an accepted request
/// field, sobre los campos del contrato).</summary>
public sealed record SolicitudDeMovimiento(TipoMovimientoCaja Tipo, decimal Importe, string? Motivo);

/// <summary>Proyección de <see cref="TurnoCaja"/> — respuesta de apertura, <c>GET …/abierto</c>
/// y <c>GET …/{id}</c> (Slice 4 le agrega <c>Arqueos</c> cuando el cierre exista).</summary>
public sealed record TurnoResumen(
    int Id,
    int IdPuntoVenta,
    int IdEmpleadoApertura,
    int? IdEmpleadoCierre,
    DateTimeOffset FechaApertura,
    DateTimeOffset? FechaCierre,
    decimal FondoInicial,
    EstadoTurno Estado,
    string? Observaciones);

/// <summary>Fila de <c>GET /api/caja/turnos</c> (historial paginado) — sin
/// observaciones/empleados, mismo criterio que <c>Ways.Application.Ventas.ComprobanteListado</c>.
/// </summary>
public sealed record TurnoListado(
    int Id, int IdPuntoVenta, DateTimeOffset FechaApertura, DateTimeOffset? FechaCierre, EstadoTurno Estado);

/// <summary>Página de resultados de <c>GET /api/caja/turnos</c> — mismo shape que
/// <c>Ways.Application.Ventas.PaginaDeVentas</c>.</summary>
public sealed record PaginaDeTurnos(IReadOnlyList<TurnoListado> Items, int Total, int Pagina, int Tamanio);

/// <summary>Proyección de <see cref="MovimientoCaja"/> ya persistido.</summary>
public sealed record MovimientoRegistrado(
    int Id, int IdTurnoCaja, TipoMovimientoCaja Tipo, decimal Importe, string Motivo, int IdEmpleado, DateTimeOffset CreadoEl);
