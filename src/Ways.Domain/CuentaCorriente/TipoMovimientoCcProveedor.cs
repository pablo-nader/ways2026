namespace Ways.Domain.CuentaCorriente;

/// <summary>
/// Tipo de un <see cref="MovimientoCuentaCorrienteProveedor"/> (doc 10 §8-adjacent). Enum
/// nativo de Postgres (<c>tipo_movimiento_cc_proveedor</c>), mismo criterio que
/// <see cref="TipoMovimientoCc"/>. El orden de los miembros ES el orden de valores declarado
/// en la migración (gate §A, proposal.md:499-508): un escritor por valor —
/// <see cref="Apertura"/> ← la migración `CuentaCorrienteDeProveedoresEtapa15`;
/// <see cref="Compra"/> ← <c>ServicioDeCompras.ConfirmarAsync</c> (slice 2);
/// <see cref="Pago"/> ← <c>ServicioDeGastos.InsertarGastoAsync</c> (slice 3);
/// <see cref="Ajuste"/> ← <c>ServicioDeCompras.AnularAsync</c> (contramovimiento, slice 2) y el
/// ajuste manual (slice 5). Ningún valor especulativo.
/// </summary>
public enum TipoMovimientoCcProveedor
{
    Apertura,
    Compra,
    Pago,
    Ajuste
}
