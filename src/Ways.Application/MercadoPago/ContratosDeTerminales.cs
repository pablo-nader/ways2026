namespace Ways.Application.MercadoPago;

/// <summary>
/// Cuerpo de <c>POST /api/puntos-venta/{id}/mercadopago/terminal</c>. Un solo campo, obligatorio: <c>TIPO__SERIE</c>, como lo devuelve
/// el listado de terminales (<c>^[A-Z0-9]+__[A-Za-z0-9]+$</c>, hasta 80 caracteres). Lo valida el servicio después del 404 del punto de
/// venta, no el binder: un cuerpo sin el campo responde el mismo 400 <c>terminal_point_invalida</c>.
/// </summary>
public sealed record SolicitudDeTerminalMercadoPago(string? IdTerminal);

/// <summary>Una terminal Point vinculada (en la app de Mercado Pago) a la caja del punto de venta. <see cref="Asignada"/> dice si es la
/// que Ways tiene guardada para el punto de venta.</summary>
public sealed record TerminalDelPuntoDeVenta(string Id, string? ModoOperativo, bool Asignada);

/// <summary>Las terminales de la caja del punto de venta. <see cref="Instrucciones"/> viene solo cuando la lista está vacía: dice cómo
/// vincular la terminal en la app de Mercado Pago, el único lugar donde se puede. <see cref="IdTerminalAsignada"/> es la que Ways tiene
/// guardada (nula si ninguna) y <see cref="AsignadaVigente"/> dice si todavía figura entre las terminales de la caja: falso con una
/// asignación guardada es una asignación vieja (la terminal se desvinculó en la app) que conviene quitar.</summary>
public sealed record ListadoDeTerminalesDelPuntoDeVenta(
    IReadOnlyList<TerminalDelPuntoDeVenta> Terminales, string? Instrucciones, string? IdTerminalAsignada, bool AsignadaVigente);

/// <summary>Resultado de asignar o quitar la terminal. <see cref="ModoCambiado"/> es verdadero si Ways cambió el modo de la terminal a
/// <c>PDV</c> en esta llamada: Mercado Pago pide reiniciarla para que lo tome.</summary>
public sealed record AsignacionDeTerminalMercadoPago(string? IdTerminalPoint, DateTimeOffset? FechaTerminal, bool ModoCambiado);

/// <summary>Cuerpo de <c>PATCH /terminals/v1/setup</c>: <c>{"terminals":[{"id","operating_mode"}]}</c>.</summary>
public sealed record SolicitudDeModoDeTerminalMercadoPago(IReadOnlyList<ModoDeTerminalMercadoPago> Terminals);

public sealed record ModoDeTerminalMercadoPago(string Id, string OperatingMode);

/// <summary>La respuesta del <c>PATCH /terminals/v1/setup</c> repite las terminales; Ways no usa nada de ella.</summary>
public sealed record RespuestaDeModoDeTerminalMercadoPago(IReadOnlyList<ModoDeTerminalMercadoPago>? Terminals);
