using Ways.Domain.Common;

namespace Ways.Domain.Compras;

/// <summary>
/// Línea de un <see cref="ComprobanteCompra"/> (doc 10 §5, design: Table Shapes — B). Child
/// scope: <c>id_tenant</c> únicamente, sin FK propia a <c>puntos_venta</c> — se deriva del
/// comprobante padre, mismo criterio que <c>ItemComprobanteVenta</c>.
///
/// A diferencia de <c>ItemComprobanteVenta</c>, <see cref="IdArticulo"/> es deliberadamente
/// <c>NOT NULL</c> (design: Table Shapes — B): una línea de compra sin artículo no puede mover
/// stock ni actualizar costo — sería un gasto, y los gastos ya existen como concepto separado.
///
/// Mientras el comprobante está en <see cref="EstadoCompra.Borrador"/> las filas se reemplazan
/// físicamente (<c>DELETE</c> + <c>INSERT</c>, design decisión 2, <c>ServicioDeCompras.
/// ActualizarBorradorAsync</c>) — no hay edición incremental de una fila existente.
/// </summary>
public class ItemComprobanteCompra : EntidadTenant
{
    public int Id { get; set; }

    public int IdComprobanteCompra { get; set; }

    /// <summary>Asignado por el servidor en cada replace-set (design decisión 2) — nunca input
    /// de cliente.</summary>
    public int Orden { get; set; }

    public int IdArticulo { get; set; }

    /// <summary>Snapshot al momento del guardado del borrador.</summary>
    public required string Descripcion { get; set; }

    /// <summary>Derivado (design: Compra Arithmetic): <c>unidades + (bultos ?? 0) ×
    /// (unidadesPorBulto ?? 0)</c>, nunca input directo de cliente (design decisión 3).</summary>
    public decimal Cantidad { get; set; }

    /// <summary>Inputs conservados para auditoría (doc-10:391) — no participan en ningún cálculo
    /// posterior a la derivación de <see cref="Cantidad"/>.</summary>
    public decimal? Bultos { get; set; }
    public decimal? UnidadesPorBulto { get; set; }

    /// <summary><c>numeric(14,4)</c>: costos con más precisión que los precios de venta
    /// (doc-10:392) — la CHECK permite <c>0</c> a propósito (líneas de bonificación son reales,
    /// design decisión 4).</summary>
    public decimal CostoUnitario { get; set; }

    /// <summary>Importe de línea (misma semántica que <c>ItemComprobanteVenta.Descuento</c>).</summary>
    public decimal Descuento { get; set; }

    public int IdAlicuotaIva { get; set; }

    /// <summary>Snapshot — informativo cuando el tipo no discrimina IVA.</summary>
    public decimal PorcentajeIva { get; set; }

    /// <summary>Derivado: <c>bruto − descuento</c> (design: Compra Arithmetic).</summary>
    public decimal Total { get; set; }

    /// <summary>Si esta línea pisa <c>articulos.costo_nominal</c> al confirmar (doc-10:396) —
    /// combinado con <c>CostoUnitario &gt; 0</c> (design decisión 4).</summary>
    public bool ActualizaCosto { get; set; } = true;

    /// <summary>Sugerencia calculada al guardar el borrador (design: Compra Arithmetic) —
    /// nunca aplicada por la confirmación (design decisión 3). <c>NULL</c> solo si el artículo
    /// no tiene margen configurado.</summary>
    public decimal? PrecioSugerido { get; set; }

    /// <summary>Etapa 12 (proposal gate §G): input de lote a nivel de borrador — capturado tal
    /// cual mientras la compra es <see cref="EstadoCompra.Borrador"/>, sin resolver contra
    /// <c>lotes</c> todavía (las líneas de borrador se reemplazan físicamente en cada edición,
    /// resolver temprano ensuciaría <c>lotes</c> con filas de borradores que nunca confirman).
    /// <c>NULL</c> si la línea no lleva código de lote (artículo no lot-effective, o el operador
    /// todavía no lo cargó).</summary>
    public string? CodigoLote { get; set; }

    /// <summary>Input de vencimiento a nivel de borrador, acompaña a <see cref="CodigoLote"/> —
    /// <c>ck_items_comprobante_compra_lote_input</c> exige que si hay código también haya fecha
    /// (un código sin vencimiento nunca puede resolver a una fila válida de <c>lotes</c>).</summary>
    public DateOnly? FechaVencimiento { get; set; }

    /// <summary>Resuelto (get-or-create) recién al confirmar, contra
    /// <c>ux_lotes_articulo_codigo</c> (slice 5, <c>ServicioDeCompras.EjecutarConfirmarAsync</c>).
    /// <c>NULL</c> mientras la compra es borrador y para artículos que no son lot-effective.
    /// Snapshot desde ese momento — es lo que hace exacta la anulación.</summary>
    public int? IdLote { get; set; }
}
