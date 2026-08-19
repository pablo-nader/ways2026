using Ways.Domain.Common;

namespace Ways.Domain.Ventas;

/// <summary>
/// Presupuesto (doc 10 §4-adjacent, proposal: Modelo de datos propuesto — §C). Operativa-scoped
/// (<c>id_tenant</c> + <c>id_punto_venta</c>, doc 09) — la misma categoría que
/// <see cref="ComprobanteVenta"/>, su documento hermano: el checkout queda byte-idéntico por
/// construcción porque un presupuesto vive en su propia tabla, nunca en
/// <c>comprobantes_venta</c> (proposal decisión 1, precedente <c>OrdenCompra</c>/stage-16).
///
/// <c>EntidadBase</c>: SÍ — un presupuesto es mutable durante <see cref="EstadoPresupuesto.Borrador"/>
/// (replace-set completo bajo <c>SELECT … FOR UPDATE</c>), se edita de nuevo en
/// <c>enviar</c>/<c>anular</c>, y un borrador abandonado usa la baja lógica ordinaria de
/// cualquier documento mutable de este repo — hereda <see cref="EntidadTenant"/> exactamente
/// como <see cref="ComprobanteVenta"/>/<see cref="Compras.OrdenCompra"/>, con el filtro de
/// tenant estándar y <c>EstamparTenant()</c> — sin filtro clonado, sin escritura explícita de
/// <c>IdTenant</c>.
/// </summary>
public class Presupuesto : EntidadTenant
{
    public int Id { get; set; }

    public int IdPuntoVenta { get; set; }

    /// <summary>Consumidor Final por defecto, como la venta (proposal §C).</summary>
    public int IdCliente { get; set; }

    /// <summary>Quién lo creó — <c>IContextoDeUsuario.UsuarioId</c>, FK simple (proposal §C, FK
    /// 4), mismo criterio que <c>ComprobanteVenta.IdEmpleado</c>/<c>OrdenCompra.IdEmpleado</c>.</summary>
    public int IdEmpleado { get; set; }

    /// <summary>Correlativo propio por punto de venta, serie <c>'PRES'</c> — <c>NULL</c> mientras
    /// <see cref="EstadoPresupuesto.Borrador"/>, asignado únicamente al <c>enviar</c>
    /// (<c>AsignadorDeNumeroComprobante</c>, slice 2). <c>bigint</c>, no <c>int</c> — mismo tipo
    /// que <c>numeraciones_comprobante.proximo_numero</c> (proposal §C).</summary>
    public long? Numero { get; set; }

    /// <summary><c>IRelojDelSistema.Ahora</c> — sin <c>DEFAULT now()</c> en la columna (proposal
    /// §C): un default de base defeatearía <c>RelojFijo</c> en tests silenciosamente.</summary>
    public DateTimeOffset FechaEmision { get; set; }

    /// <summary>Se estampa junto con <see cref="Numero"/> y <see cref="Vencimiento"/> en el
    /// mismo <c>UPDATE</c> del <c>enviar</c> (proposal §C, <c>ck_presupuestos_envio_completo</c>).</summary>
    public DateTimeOffset? FechaEnvio { get; set; }

    /// <summary><c>NOT NULL</c> desde <see cref="EstadoPresupuesto.Enviado"/> (CHECK 1, proposal
    /// §C — decisión 3). <c>date</c>, no <c>timestamptz</c>: es una fecha declarada, no un
    /// instante — solo tiene sentido dentro de una zona horaria (la del punto de venta, resuelta
    /// server-side).</summary>
    public DateOnly? Vencimiento { get; set; }

    public string? Observaciones { get; set; }

    public decimal Subtotal { get; set; }
    public decimal DescuentoTotal { get; set; }
    public decimal Total { get; set; }

    public EstadoPresupuesto Estado { get; set; } = EstadoPresupuesto.Borrador;
}
