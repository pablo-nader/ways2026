namespace Ways.Domain.CuentaCorriente;

/// <summary>
/// Distingue un <see cref="MovimientoCuentaCorriente"/> <see cref="TipoMovimientoCc.Ajuste"/> por
/// su origen (design decisión 8/9): un ajuste manual (esta capability, Slice 4) nunca lleva
/// <c>id_comprobante_venta</c>; el contramovimiento de anulación (stage 5) siempre lo lleva —
/// ninguna columna nueva codifica la diferencia, la etiqueta se DERIVA (spec:
/// ajustes-de-cuenta-corriente / Ajuste Is Distinct From The Anulación Contramovimiento).
/// </summary>
public enum EtiquetaDeAjuste
{
    Manual,
    AnulacionContramovimiento
}

/// <summary>
/// Pura, sin DB (design decisión 9, mismo listón que <see cref="ReliquidadorDeConsumos"/>): la
/// derivación de <c>disponibilidad</c> del header de estado de cuenta y la etiqueta estructural de
/// un movimiento <see cref="TipoMovimientoCc.Ajuste"/>.
/// </summary>
public static class CalculadorDeEstadoDeCuenta
{
    /// <summary><c>credito_ilimitado</c> ⇒ <c>null</c>, NUNCA un número fabricado (design decisión
    /// 9, pinned: "disponibilidad is decimal?, NULL ⇒ ilimitado") — el llamador (web, Slice 5) es
    /// quien decide cómo rotular <c>null</c> en pantalla.</summary>
    public static decimal? CalcularDisponibilidad(decimal saldo, decimal limiteCredito, bool creditoIlimitado) =>
        creditoIlimitado ? null : limiteCredito - saldo;

    /// <summary>Aplica solo a filas <see cref="TipoMovimientoCc.Ajuste"/> — el llamador es
    /// responsable de no invocarla sobre otro <see cref="TipoMovimientoCc"/> (design: "no new
    /// column", la predicate ES <c>id_comprobante_venta IS NULL</c>).</summary>
    public static EtiquetaDeAjuste EtiquetarAjuste(int? idComprobanteVenta) =>
        idComprobanteVenta is null ? EtiquetaDeAjuste.Manual : EtiquetaDeAjuste.AnulacionContramovimiento;
}
