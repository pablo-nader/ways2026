namespace Ways.Domain.CuentaCorriente;

/// <summary>
/// Tipo de un <see cref="MovimientoCuentaCorrienteProveedor"/> (doc 10 §8-adjacent). Enum
/// nativo de Postgres (<c>tipo_movimiento_cc_proveedor</c>), mismo criterio que
/// <see cref="TipoMovimientoCc"/>. El orden de los miembros ES el orden de valores declarado
/// en la migración (gate §A, proposal.md:499-508): un escritor por valor —
/// <see cref="Apertura"/> ← la migración `CuentaCorrienteDeProveedoresEtapa15`;
/// <see cref="Compra"/> ← <c>ServicioDeCompras.ConfirmarAsync</c> (slice 2);
/// <see cref="Pago"/> ← <c>ServicioDePagos.InsertarPagoAsync</c> (slice 3);
/// <see cref="Ajuste"/> ← <c>ServicioDeCompras.AnularAsync</c> (contramovimiento, slice 2) y el
/// ajuste manual (slice 5); <see cref="Redondeo"/> ← el cierre en cero de una compra o de un pago
/// (<c>ServicioDePagos</c>, E4): perdona o absorbe una diferencia de hasta la tolerancia configurada, con el signo de la
/// deuda que deja de existir (negativo si falta pagar de una compra, positivo si sobra de un pago) y exactamente UN
/// destino — la compra o el pago —; su reversa es otro movimiento <see cref="Redondeo"/> de signo opuesto. Ningún valor
/// especulativo.
/// </summary>
public enum TipoMovimientoCcProveedor
{
    Apertura,
    Compra,
    Pago,
    Ajuste,
    Redondeo
}
