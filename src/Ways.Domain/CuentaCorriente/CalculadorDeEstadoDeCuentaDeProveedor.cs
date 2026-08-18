namespace Ways.Domain.CuentaCorriente;

/// <summary>
/// Puro, sin DB (design.md:131-134, task 4.1) — la única derivación del estado de cuenta de
/// proveedores que no vive ya en <see cref="Ways.Application.Compras.ServicioDeSaldoDeProveedor"/>:
/// etiquetar un <see cref="TipoMovimientoCcProveedor.Ajuste"/> por su origen. Espeja
/// <see cref="CalculadorDeEstadoDeCuenta.EtiquetarAjuste"/> (cliente, stage 7) — misma derivación
/// estructural, sin columna nueva: el contramovimiento de anulación (stage-15 Slice 2) siempre
/// lleva <c>id_comprobante_compra</c>; el ajuste manual (Slice 5) nunca lo lleva.
/// <see cref="Ways.Application.Compras.ServicioDeSaldoDeProveedor"/>'s <c>ResolverEstadoPago</c>
/// (por-compra, fórmula OD7) se REUSA sin tocar — no se duplica acá.
/// </summary>
public static class CalculadorDeEstadoDeCuentaDeProveedor
{
    /// <summary>Aplica solo a filas <see cref="TipoMovimientoCcProveedor.Ajuste"/> — el llamador es
    /// responsable de no invocarla sobre otro <see cref="TipoMovimientoCcProveedor"/>.</summary>
    public static EtiquetaDeAjuste EtiquetarAjuste(int? idComprobanteCompra) =>
        idComprobanteCompra is null ? EtiquetaDeAjuste.Manual : EtiquetaDeAjuste.AnulacionContramovimiento;
}
