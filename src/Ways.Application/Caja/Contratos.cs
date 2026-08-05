using Ways.Domain.Caja;
using Ways.Domain.Gastos;

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

/// <summary>Un ticket límite del turno (legacy D6: "primer y último ticket") — número visible,
/// fecha de emisión y el código del tipo de comprobante (<c>TX</c>, <c>RC</c>, …) del
/// <see cref="Ways.Domain.Ventas.ComprobanteVenta"/> correspondiente. <see cref="Codigo"/> es
/// necesario porque cada tipo numera su propia serie independiente (stage-7-cuenta-corriente,
/// design decisión 7): sin él, "primer ticket #1" es ambiguo entre una TX y una RC que arrancan
/// las dos en 1. Nunca aparece para un comprobante anulado (spec: Anulados Are Excluded From The
/// Derivation, mismo criterio aplicado acá).</summary>
public sealed record TicketLimite(long Numero, DateTimeOffset Fecha, string Codigo);

/// <summary>Ingresos de un área dentro del turno (legacy D6, primer bloque: "por área") —
/// agrupa <see cref="Ways.Domain.Ventas.ItemComprobanteVenta.Total"/> por
/// <see cref="Ways.Domain.Ventas.ItemComprobanteVenta.IdArea"/>, el snapshot inmutable del
/// ítem (nunca re-derivado de <c>articulos</c>, doc 10 principio 6) — mismo criterio de
/// snapshot que ya rige <c>ItemEmitido</c>.</summary>
public sealed record IngresoPorArea(int IdArea, string NombreArea, decimal Total);

/// <summary>Egresos de una categoría de gasto dentro del turno (legacy D6, segundo bloque: "por
/// tipo").</summary>
public sealed record EgresoPorCategoria(CategoriaGasto Categoria, decimal Total);

/// <summary>Egresos de un área dentro del turno (legacy D6, segundo bloque: "por área") — agrupa
/// <see cref="Ways.Domain.Gastos.Gasto.Importe"/> por <see cref="Ways.Domain.Gastos.Gasto.IdArea"/>,
/// que a diferencia de <see cref="Ways.Domain.Ventas.ItemComprobanteVenta.IdArea"/> es NULLABLE:
/// <see cref="IdArea"/> null representa el bucket "Sin área" (gastos sin área declarada), nunca
/// descartados.</summary>
public sealed record EgresoPorArea(int? IdArea, string NombreArea, decimal Total);

/// <summary>Egresos del turno (legacy D6, segundo bloque: "por área y por tipo") — gastos
/// agrupados por categoría y por área más el total de retiros físicos (<c>movimientos_caja</c>
/// tipo <see cref="TipoMovimientoCaja.Retiro"/>); nunca incluye <see
/// cref="TipoMovimientoCaja.Refuerzo"/> ni <see cref="TipoMovimientoCaja.AperturaCajon"/>, que no
/// son egresos. Con este bloque, D6 queda completo salvo "saldo" (uno de los medios de pago del
/// primer bloque de D6, Ingresos), que depende de la etapa 7 y todavía no existe.</summary>
public sealed record EgresosDeTurno(
    IReadOnlyList<EgresoPorCategoria> PorCategoria, IReadOnlyList<EgresoPorArea> PorArea, decimal Retiros);

/// <summary>Respuesta de <c>GET /api/caja/turnos/{id}/resumen</c> (D6 parity, design: API
/// Surface) — de solo lectura, nunca escribe nada. <see cref="Medios"/> es la MISMA derivación
/// que el cierre va a persistir (spec: Resumen Parcial Uses The Same Derivation As Cierre,
/// invariante intacto); <see cref="CantidadTickets"/>/<see cref="PrimerTicket"/>/<see
/// cref="UltimoTicket"/>/<see cref="IngresosPorArea"/>/<see cref="Egresos"/> son contenido de
/// reporte agregado ADITIVO (follow-up de la etapa 6, "Resumen parcial D6-content enrichment")
/// — nunca alimentan <c>CalculadorDeArqueo</c> ni ninguna escritura.</summary>
public sealed record ResumenDeTurno(
    int IdTurnoCaja,
    int IdMedioAncla,
    IReadOnlyList<LineaDeResumen> Medios,
    int CantidadTickets,
    TicketLimite? PrimerTicket,
    TicketLimite? UltimoTicket,
    IReadOnlyList<IngresoPorArea> IngresosPorArea,
    EgresosDeTurno Egresos);

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
