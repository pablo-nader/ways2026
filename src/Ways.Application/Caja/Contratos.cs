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

// ---- Slice 4: derivación, resumen y cierre (design: The Cierre Transaction; Interfaces/Contracts) ----

/// <summary>Un conteo declarado por el cajero — <c>(id_medio_pago, importe_declarado)</c>, el
/// ÚNICO dato que el cliente envía (spec: Cierre Payload Carries Only Declared Counts).</summary>
public readonly record struct ConteoDeclarado(int IdMedioPago, decimal ImporteDeclarado);

/// <summary>Cuerpo de <c>POST /api/caja/turnos/{id}/cierre</c> (design: API Surface;
/// Interfaces/Contracts) — sin ningún campo de total, subtotal o esperado (spec: No Request
/// Shape Accepts A Total): <c>ImporteEsperado</c> SIEMPRE lo deriva el servidor.</summary>
public sealed record SolicitudDeCierre(IReadOnlyList<ConteoDeclarado> Conteos, string? Observaciones);

/// <summary>Una línea de <see cref="ResumenDeTurno"/> — proyección de
/// <see cref="Ways.Domain.Caja.LineaDeArqueo"/>, la misma derivación que el cierre va a
/// persistir (spec: Resumen Parcial Uses The Same Derivation As Cierre).</summary>
public sealed record LineaDeResumen(int IdMedioPago, decimal ImporteEsperado);

/// <summary>Respuesta de <c>GET /api/caja/turnos/{id}/resumen</c> (D6 parity, design: API
/// Surface) — de solo lectura, nunca escribe nada.</summary>
public sealed record ResumenDeTurno(int IdTurnoCaja, int IdMedioAncla, IReadOnlyList<LineaDeResumen> Medios);

/// <summary>Una fila ya persistida de <see cref="ArqueoTurno"/> — con <c>Diferencia</c> incluida
/// (columna <c>GENERATED ALWAYS</c>, design decisión 6).</summary>
public sealed record LineaDeArqueoResumen(
    int IdMedioPago, decimal ImporteEsperado, decimal ImporteDeclarado, decimal Diferencia);

/// <summary>Respuesta de <c>POST /api/caja/turnos/{id}/cierre</c> y de
/// <c>GET /api/caja/turnos/{id}</c> (design: API Surface — "Turno + its arqueos_turno, the
/// Z-report payload"): mismos campos planos que <see cref="TurnoResumen"/> más
/// <see cref="Arqueos"/> — la deserialización de <see cref="TurnoResumen"/> sobre este mismo JSON
/// sigue funcionando (System.Text.Json ignora propiedades no mapeadas), así que las pruebas de
/// Slice 2 contra <c>GET …/{id}</c> quedan intactas.</summary>
public sealed record TurnoConArqueos(
    int Id,
    int IdPuntoVenta,
    int IdEmpleadoApertura,
    int? IdEmpleadoCierre,
    DateTimeOffset FechaApertura,
    DateTimeOffset? FechaCierre,
    decimal FondoInicial,
    EstadoTurno Estado,
    string? Observaciones,
    IReadOnlyList<LineaDeArqueoResumen> Arqueos);
