using Ways.Domain.CuentaCorriente;

namespace Ways.Application.CuentaCorriente;

/// <summary>
/// Cuerpo de <c>POST /api/clientes/{id}/cuenta-corriente/pagos</c> (design: API Surface;
/// Interfaces/Contracts). Sin ningún campo de importe propio a propósito (design decisión 6):
/// <c>importeAplicado</c> lo deriva <see cref="Domain.CuentaCorriente.ValidadorDePagoACuenta"/> a
/// partir de <see cref="Pagos"/> — mismo criterio que <c>SolicitudDeVenta</c> nunca mandando un
/// total ya calculado.
/// </summary>
public sealed record SolicitudDePagoACuenta(
    int IdPuntoVenta, IReadOnlyList<PagoDeCuenta>? Pagos, string? Observaciones);

/// <summary>Un medio de pago de la RC — mismo shape que
/// <see cref="Ventas.PagoDeVenta"/>, redeclarado acá porque una RC no es un checkout (design
/// decisión 1: no reusa <c>ServicioDeVentas</c>).</summary>
public sealed record PagoDeCuenta(int IdMedioPago, decimal Importe, string? Referencia, decimal Vuelto);

/// <summary>
/// Cuerpo de <c>POST /api/clientes/{id}/cuenta-corriente/reliquidacion</c> (design: API Surface).
/// <see cref="IdPuntoVenta"/> es provenance, no autoridad (design: Open Questions — la
/// reliquidación no tiene turno del que derivarlo, así que viaja en el request, validado
/// tenant-scoped como cualquier otro id de punto de venta) — se persiste en el movimiento
/// <c>ActualizacionPrecios</c> resultante.</summary>
public sealed record SolicitudDeReliquidacion(int IdPuntoVenta);

/// <summary>
/// Cuerpo de <c>POST /api/clientes/{id}/cuenta-corriente/ajustes</c> (design: API Surface;
/// Transactions — AJUSTE MANUAL). <see cref="Importe"/> viaja con signo, decidido por el llamador
/// (spec: Ajuste Requires A Detalle — "importe MAY be positive or negative"); <see cref="Detalle"/>
/// es obligatorio (<see cref="Domain.CuentaCorriente.ReglaDeAjusteDeCuenta"/>).
/// </summary>
public sealed record SolicitudDeAjuste(int IdPuntoVenta, decimal Importe, string? Detalle);

/// <summary>
/// Fila del ledger proyectada para el estado de cuenta (design decisión 9: <c>saldo_resultante</c>
/// es la ÚNICA fuente del saldo corriente, nunca re-derivado). <see cref="Etiqueta"/> es
/// <c>null</c> para todo tipo salvo <see cref="TipoMovimientoCc.Ajuste"/> — ahí distingue manual de
/// contramovimiento (design decisión 8/9; <see cref="Domain.CuentaCorriente.CalculadorDeEstadoDeCuenta"/>).
/// </summary>
public sealed record MovimientoDeCuentaCorriente(
    int Id, DateTimeOffset Fecha, TipoMovimientoCc Tipo, decimal Importe, decimal SaldoResultante,
    string? Detalle, int? IdComprobanteVenta, EtiquetaDeAjuste? Etiqueta);

/// <summary>Header de estado de cuenta (design decisión 9: "un único GET devuelve header + page",
/// leído de la MISMA fila de <c>clientes</c> que pidió el operador — no puede desalinearse del
/// ledger que está mirando). <see cref="Disponibilidad"/> es <c>null</c> cuando
/// <see cref="CreditoIlimitado"/> (nunca un número fabricado).</summary>
public sealed record EstadoDeCuentaHeader(decimal Saldo, decimal LimiteCredito, bool CreditoIlimitado, decimal? Disponibilidad);

/// <summary>Respuesta completa de <c>GET /api/clientes/{id}/cuenta-corriente</c> — header +
/// movimientos en un único payload (design decisión 9). <see cref="Historico"/>/<see cref="Desde"/>/
/// <see cref="Hasta"/> reflejan la ventana EFECTIVA aplicada (spec: Default Last Month Filter,
/// Desde/Hasta, And Histórico), no lo pedido crudo — así el cliente HTTP sabe qué ventana ve sin
/// tener que recalcular el default.</summary>
public sealed record EstadoDeCuenta(
    EstadoDeCuentaHeader Header, IReadOnlyList<MovimientoDeCuentaCorriente> Movimientos, bool Historico,
    DateTimeOffset? Desde, DateTimeOffset? Hasta);
