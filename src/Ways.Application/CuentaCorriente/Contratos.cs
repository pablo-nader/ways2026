using Ways.Application.Fiscal;
using Ways.Application.Ventas;
using Ways.Domain.CuentaCorriente;
using Ways.Domain.Ventas;

namespace Ways.Application.CuentaCorriente;

/// <summary>
/// Cuerpo de <c>POST /api/clientes/{id}/cuenta-corriente/pagos</c> (design: API Surface;
/// Interfaces/Contracts). Sin ningún campo de importe propio a propósito (design decisión 6):
/// <c>importeAplicado</c> lo deriva <see cref="Domain.CuentaCorriente.ValidadorDePagoACuenta"/> a
/// partir de <see cref="Pagos"/> — mismo criterio que <c>SolicitudDeVenta</c> nunca mandando un
/// total ya calculado.
/// </summary>
public sealed record SolicitudDePagoACuenta(
    int IdPuntoVenta, IReadOnlyList<PagoDeCuenta>? Pagos, string? Observaciones,
    IReadOnlyList<AplicacionDeCobroPedida?>? Aplicaciones = null);

/// <summary>
/// Cuerpo de <c>POST /api/cobros/{idRecibo}/aplicaciones</c>: las ventas a las que se aplica lo disponible del recibo
/// (<see cref="AplicacionDeCobroPedida"/>: <c>idComprobanteVenta</c> e <c>importe</c>). De 1 a
/// <see cref="ReglaDeAplicacionDeCobro.MaximoDeVentasPorPedido"/>, sin repetir ventas.
/// </summary>
public sealed record SolicitudDeAplicacionesDeCobro(IReadOnlyList<AplicacionDeCobroPedida?>? Aplicaciones);

/// <summary>
/// Respuesta de <c>POST /api/clientes/{id}/cuenta-corriente/pagos</c>: los mismos campos que
/// <see cref="ComprobanteEmitido"/> (la forma que leen los registros instalados) más las aplicaciones vivas del recibo
/// (<see cref="Aplicaciones"/>, vacía si el pedido no trajo ninguna). Un recibo no tiene ítems, presupuesto de origen ni
/// factura: esos campos viajan igual que siempre (<c>[]</c>, <c>null</c>, <c>false</c>, <c>null</c>).
/// </summary>
public sealed record PagoACuentaRegistrado(
    int Id,
    long Numero,
    string NumeroVisible,
    EstadoComprobante Estado,
    DateTimeOffset Fecha,
    int IdPuntoVenta,
    int IdCliente,
    int? IdComprobanteAsociado,
    decimal Subtotal,
    decimal DescuentoTotal,
    decimal DescuentoManualTotal,
    decimal RecargoManualTotal,
    decimal Total,
    string? DireccionEntrega,
    string? Observaciones,
    IReadOnlyList<ItemEmitido> Items,
    IReadOnlyList<PagoEmitido> Pagos,
    int? IdPresupuestoOrigen,
    bool FacturaForzada,
    EstadoFiscalDeVenta? Fiscal,
    IReadOnlyList<AplicacionDeCobroListada> Aplicaciones)
{
    public static PagoACuentaRegistrado De(
        ComprobanteEmitido c, IReadOnlyList<AplicacionDeCobroListada> aplicaciones) => new(
        c.Id, c.Numero, c.NumeroVisible, c.Estado, c.Fecha, c.IdPuntoVenta, c.IdCliente, c.IdComprobanteAsociado,
        c.Subtotal, c.DescuentoTotal, c.DescuentoManualTotal, c.RecargoManualTotal, c.Total, c.DireccionEntrega,
        c.Observaciones, c.Items, c.Pagos, c.IdPresupuestoOrigen, c.FacturaForzada, c.Fiscal, aplicaciones);
}

/// <summary>Una venta del cliente con saldo pendiente en cuenta corriente (<c>GET …/ventas-pendientes</c>):
/// <see cref="ParteEnCuentaCorriente"/> es lo que quedó a cuenta, <see cref="Cobrado"/> lo aplicado por recibos y
/// <see cref="SaldoPendiente"/> la diferencia (siempre mayor a cero acá).</summary>
public sealed record VentaPendienteDeCobro(
    int Id, string CodigoTipo, string? Letra, long Numero, string NumeroVisible, DateTimeOffset Fecha,
    decimal ParteEnCuentaCorriente, decimal Cobrado, decimal SaldoPendiente);

/// <summary>Una aplicación viva de un recibo, con el número visible de la venta a la que se aplicó.</summary>
public sealed record AplicacionDeReciboListada(
    int Id, int IdComprobanteVenta, string NumeroVisibleDeVenta, decimal Importe, DateTimeOffset Fecha);

/// <summary>Un recibo no anulado del cliente (<c>GET …/recibos</c>): lo cobrado neto de vuelto, lo aplicado a ventas y lo
/// disponible para aplicar, con sus aplicaciones vivas.</summary>
public sealed record ReciboDeCliente(
    int Id, long Numero, string NumeroVisible, DateTimeOffset Fecha, decimal NetoCobrado, decimal Aplicado,
    decimal Disponible, IReadOnlyList<AplicacionDeReciboListada> Aplicaciones);

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
