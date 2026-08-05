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
