namespace Ways.Domain.Pagos;

/// <summary>
/// De dónde nace una <see cref="AplicacionPago"/> (doc 10 §5). Enum nativo de Postgres
/// (<c>origen_aplicacion_pago</c>). <see cref="PagoDeCompra"/> es el pago que se carga ya contra una compra;
/// <see cref="Aplicacion"/> es la que el usuario hace después sobre un crédito; <see cref="Migracion"/> es la que
/// reconstruyó el backfill a partir de <c>pagos.id_comprobante_compra</c>.
/// </summary>
public enum OrigenAplicacionPago
{
    PagoDeCompra,
    Aplicacion,
    Migracion
}
