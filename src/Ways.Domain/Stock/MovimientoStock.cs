namespace Ways.Domain.Stock;

/// <summary>
/// Ledger de movimientos de stock (doc 10 §6, design: Table Shapes — write path B): la tabla
/// que reconstruye y audita <see cref="Stock.Cantidad"/> (doc 10 principio 7). Append-only por
/// contrato — ningún endpoint actualiza ni elimina una fila, jamás.
///
/// A propósito NO hereda de <see cref="Common.EntidadBase"/>/<see cref="Common.EntidadTenant"/>
/// aunque tiene identidad propia (<see cref="Id"/>): un ledger append-only no tiene
/// <c>updated_at</c>/<c>deleted_at</c> con sentido (nunca se edita ni se da de baja una fila
/// escrita), así que design nombra su única columna de fecha <c>creado_el</c> (no
/// <c>created_at</c>) para marcar la diferencia — mismo criterio de "columna manual" que
/// <see cref="Ventas.NumeracionComprobante"/>/<see cref="Ways.Domain.Stock.Stock"/>, con filtro
/// de tenant escrito a mano en <c>WaysDbContext.AplicarFiltroDeTenantEnMovimientoStock</c>.
///
/// <see cref="IdComprobanteCompra"/> aterriza en stage-8 Slice 1 (design: Table Shapes — D, la
/// FK diferida de doc-10:457-465): columna + FK compuesta juntas, en la misma migración que crea
/// <c>comprobantes_compra</c> — nunca escrita fuera de <c>ServicioDeCompras.ConfirmarAsync</c>/
/// <c>AnularAsync</c> (Slice 2).
/// </summary>
public class MovimientoStock
{
    public int Id { get; set; }
    public int IdTenant { get; set; }

    public int IdArticulo { get; set; }
    public int IdPuntoVenta { get; set; }

    /// <summary>Con signo: venta negativa, ajuste/anulación según corresponda (design: The Sale
    /// Transaction — <c>movimientos_stock (cantidad = −item.cantidad, motivo = venta)</c>).
    /// Nunca cero — <c>ck_movimientos_stock_cantidad_no_cero</c> lo respalda a nivel esquema.</summary>
    public decimal Cantidad { get; set; }

    public MotivoStock Motivo { get; set; }

    /// <summary>Poblado solo cuando <see cref="Motivo"/> es <see cref="MotivoStock.Venta"/> o
    /// <see cref="MotivoStock.Anulacion"/> (design: The Sale Transaction).</summary>
    public int? IdComprobanteVenta { get; set; }

    /// <summary>Poblado solo cuando <see cref="Motivo"/> es <see cref="MotivoStock.Compra"/> o
    /// la <see cref="MotivoStock.Anulacion"/> que la revierte (design: Table Shapes — D;
    /// Transactions — CONFIRMAR COMPRA / ANULAR COMPRA).</summary>
    public int? IdComprobanteCompra { get; set; }

    /// <summary>Transferencias entre locales (doc 10 §6) — columna creada en stage 5, escrita
    /// recién en stage-8 Slice 3 (<c>ServicioDeStock.TransferirAsync</c>): las dos filas
    /// espejadas de una transferencia llevan acá el destino (design decisión 5 del proposal).</summary>
    public int? IdPuntoVentaDestino { get; set; }

    /// <summary>Etapa 12 (proposal decisión 5, gate §C): dimensión de lote del movimiento, con
    /// FK compuesta <c>fk_movimientos_stock_lote</c> sobre <c>(id_lote, id_articulo, id_tenant)</c>
    /// contra la clave alterna de <see cref="Lote"/> — Postgres garantiza a nivel de esquema que
    /// el lote de un movimiento pertenece a su mismo artículo. <c>NOT NULL</c> exigido para
    /// movimientos de un artículo lot-effective y siempre <c>NULL</c> para uno que no lo es —
    /// invariante cruzado entre tablas (no una CHECK), probado con un test de integración
    /// dedicado (design decisión 5). Columna creada en esta slice, escrita recién a partir de las
    /// slices 4-12 (ningún escritor nuevo en esta slice: schema + seed gate).</summary>
    public int? IdLote { get; set; }

    public int IdEmpleado { get; set; }

    public string? Observaciones { get; set; }

    public DateTimeOffset CreadoEl { get; set; }
}
