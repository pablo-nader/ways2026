using Ways.Domain.Common;

namespace Ways.Domain.Ventas;

/// <summary>
/// Línea de un <see cref="ComprobanteVenta"/> (doc 10 §4, design: Table Shapes — write path A).
/// Child scope: <c>id_tenant</c> únicamente, sin <c>id_punto_venta</c> propio — derivable del
/// padre, duplicarlo invitaría a drift sin que ninguna query lo necesite (mismo criterio que
/// <c>ofertas_listas</c>). Con auditoría completa (<see cref="EntidadTenant"/>), a diferencia de
/// las junctions PK-only: tiene identidad propia y nunca se edita, pero sigue la convención
/// general de <c>created_at</c>/<c>updated_at</c>/<c>deleted_at</c> (doc 10 principio 3) salvo
/// que el design la exima explícitamente (no es el caso acá).
///
/// <b>Snapshot inmutable</b> (doc 10 principio 6, spec: Snapshot Immutability of Items):
/// <see cref="Descripcion"/>, <see cref="CodigoBarra"/>, <see cref="IdArea"/>,
/// <see cref="PrecioUnitario"/>, <see cref="IdListaPrecio"/>, <see cref="IdOferta"/>,
/// <see cref="IdAlicuotaIva"/>/<see cref="PorcentajeIva"/>, <see cref="CostoUnitario"/>/
/// <see cref="CostoEsEstimado"/> se copian al emitir y nunca se re-derivan de
/// <c>articulos</c>/<c>precios</c>/<c>ofertas</c> en una reimpresión. Ningún endpoint de edición
/// existe — la única mutación del comprobante padre es la anulación.
/// </summary>
public class ItemComprobanteVenta : EntidadTenant
{
    public int Id { get; set; }

    public int IdComprobanteVenta { get; set; }

    public int Orden { get; set; }

    /// <summary><c>NULL</c> solo en líneas de concepto libre (doc 10 §4) — stage 5 no construye
    /// ese camino todavía, la columna queda lista.</summary>
    public int? IdArticulo { get; set; }

    public required string Descripcion { get; set; }
    public string? CodigoBarra { get; set; }

    public int IdArea { get; set; }

    /// <summary>Con qué lista se vendió — snapshot, no re-derivable.</summary>
    public int IdListaPrecio { get; set; }

    /// <summary>Si una oferta tocó esta línea (design decisión 3: el total es siempre el
    /// re-resuelto server-side, nunca el que mostró el cliente).</summary>
    public int? IdOferta { get; set; }

    public int IdAlicuotaIva { get; set; }
    public decimal PorcentajeIva { get; set; }

    /// <summary>Con signo (design decisión 4): positiva en TX, negativa en NCX —
    /// <c>ReglaDeComprobantes.ValidarSignoDeLineas</c> lo exige contra
    /// <c>tipos_comprobante.signo</c>.</summary>
    public decimal Cantidad { get; set; }

    public decimal PrecioUnitario { get; set; }
    public decimal Descuento { get; set; }

    /// <summary><c>cantidad × precio_unitario − descuento</c> (<c>CalculadorDeTotales</c>,
    /// redondeo <c>MidpointRounding.AwayFromZero</c>).</summary>
    public decimal Total { get; set; }

    /// <summary>Snapshot de <c>articulos.costo_nominal</c> al emitir, por unidad, sin signo
    /// (igual que <see cref="PrecioUnitario"/>: el signo vive en <see cref="Cantidad"/>) y con
    /// IVA incluido (stage 9, decisión 1). <c>NULL</c> = costo desconocido; nunca se colapsa a
    /// cero. Jamás se expone en <c>ItemEmitido</c>/<c>ComprobanteEmitido</c>.</summary>
    public decimal? CostoUnitario { get; set; }

    /// <summary><c>true</c> únicamente en filas completadas por el backfill de la migración
    /// <c>CostoCongeladoEnVentaEtapa9</c> (stage 9, decisión 2): una aproximación, no un costo
    /// real capturado al emitir.</summary>
    public bool CostoEsEstimado { get; set; }
}
