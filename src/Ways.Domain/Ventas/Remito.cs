using Ways.Domain.Common;

namespace Ways.Domain.Ventas;

/// <summary>
/// Remito (doc 10 §4-adjacent, proposal: Modelo de datos propuesto — §E). Operativa-scoped
/// (<c>id_tenant</c> + <c>id_punto_venta</c>, doc 09) — la misma categoría que
/// <see cref="Presupuesto"/>/<see cref="ComprobanteVenta"/>: la mercadería sale bajo su propio
/// documento, la consolidación (slice 6) recién crea el <c>comprobantes_venta</c> que la
/// factura (N remitos → 1, proposal decisión 6 del explore).
///
/// <c>EntidadBase</c>: SÍ — mismo razonamiento que <see cref="Presupuesto"/> (proposal §E): un
/// remito es mutable durante <see cref="EstadoRemito.Borrador"/> (replace-set completo bajo
/// <c>SELECT … FOR UPDATE</c>), se edita de nuevo en <c>emitir</c>/<c>anular</c>/la
/// consolidación — hereda <see cref="EntidadTenant"/> con el filtro de tenant estándar y
/// <c>EstamparTenant()</c>, sin filtro clonado.
/// </summary>
public class Remito : EntidadTenant
{
    public int Id { get; set; }

    public int IdPuntoVenta { get; set; }

    public int IdCliente { get; set; }

    /// <summary>Quién lo creó — <c>IContextoDeUsuario.UsuarioId</c>, FK simple (proposal §E, FK
    /// 14), mismo criterio que <c>Presupuesto.IdEmpleado</c>/<c>ComprobanteVenta.IdEmpleado</c>.</summary>
    public int IdEmpleado { get; set; }

    /// <summary>Correlativo propio por punto de venta, serie <c>'REM'</c> — <c>NULL</c> mientras
    /// <see cref="EstadoRemito.Borrador"/>, asignado únicamente al <c>emitir</c>
    /// (<c>AsignadorDeNumeroComprobante</c>, slice 5).</summary>
    public long? Numero { get; set; }

    /// <summary><c>IRelojDelSistema.Ahora</c> al crear el borrador — sin <c>DEFAULT now()</c> en
    /// la columna (proposal §E, mismo criterio que <c>Presupuesto.FechaEmision</c>).</summary>
    public DateTimeOffset FechaEmision { get; set; }

    /// <summary>Se estampa junto con <see cref="Numero"/> en el mismo <c>UPDATE</c> del
    /// <c>emitir</c> (proposal §E, <c>ck_remitos_salida_completa</c>) — cuándo salió la
    /// mercadería.</summary>
    public DateTimeOffset? FechaSalida { get; set; }

    public string? DireccionEntrega { get; set; }

    public string? Observaciones { get; set; }

    public decimal Subtotal { get; set; }
    public decimal DescuentoTotal { get; set; }
    public decimal Total { get; set; }

    public EstadoRemito Estado { get; set; } = EstadoRemito.Borrador;

    /// <summary>La factura consolidada (proposal §E, decisión 6/7 del explore): NULL salvo en
    /// <see cref="EstadoRemito.Facturado"/> — <c>ck_remitos_facturacion</c> exige que estado y
    /// link vayan JUNTOS, en ambas direcciones (el desligue de la anulación de un <c>TXR</c> los
    /// limpia a la vez, slice 6).</summary>
    public int? IdComprobanteVenta { get; set; }
}
