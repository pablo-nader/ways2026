using Ways.Domain.Common;

namespace Ways.Domain.Compras;

/// <summary>
/// Orden de compra (doc 10 §5-adjacent, proposal: Modelo de datos propuesto — §B). Operativa-scoped
/// (<c>id_tenant</c> + <c>id_punto_venta</c>, doc 09), misma categoría que
/// <see cref="ComprobanteCompra"/>, su documento hermano.
///
/// <c>EntidadBase</c>: SÍ — a diferencia de <c>movimientos_cuenta_corriente_proveedor</c>/
/// <c>Auditoria</c> (ledgers append-only de las etapas 14/15), una OC es mutable durante
/// <see cref="EstadoOrdenCompra.Borrador"/> (replace-set completo), se edita de nuevo en
/// <c>enviar</c>/<c>cerrar</c>/<c>anular</c>, y un borrador abandonado usa la baja lógica
/// ordinaria de cualquier documento mutable de este repo — hereda <see cref="EntidadTenant"/>
/// exactamente como <see cref="ComprobanteCompra"/>, con el filtro de tenant estándar y
/// <c>EstamparTenant()</c> — sin filtro clonado, sin escritura explícita de <c>IdTenant</c>
/// (proposal §B, gate aprobado).
/// </summary>
public class OrdenCompra : EntidadTenant
{
    public int Id { get; set; }

    /// <summary>A qué local llega la mercadería (proposal §B).</summary>
    public int IdPuntoVenta { get; set; }

    public int IdProveedor { get; set; }

    /// <summary>Quién la creó — <c>IContextoDeUsuario.UsuarioId</c>, FK simple (proposal §B, FK
    /// 4), mismo criterio que <c>ComprobanteCompra.IdEmpleado</c>.</summary>
    public int IdEmpleado { get; set; }

    /// <summary>Correlativo propio por punto de venta, serie <c>'OC'</c> — <c>NULL</c> mientras
    /// <see cref="EstadoOrdenCompra.Borrador"/>, asignado únicamente al <c>enviar</c>
    /// (<c>AsignadorDeNumeroComprobante</c>, slice 2). <c>bigint</c>, no <c>int</c> — mismo tipo
    /// que <c>numeraciones_comprobante.proximo_numero</c> (proposal §B).</summary>
    public long? Numero { get; set; }

    /// <summary><c>IRelojDelSistema.Ahora</c> — sin <c>DEFAULT now()</c> en la columna (proposal
    /// §B): un default de base defeatearía <c>RelojFijo</c> en tests silenciosamente.</summary>
    public DateTimeOffset FechaEmision { get; set; }

    /// <summary>Cuándo salió al proveedor — se estampa junto con <see cref="Numero"/> en el mismo
    /// <c>UPDATE</c> del <c>enviar</c> (proposal §B, <c>ck_ordenes_compra_envio_completo</c>).</summary>
    public DateTimeOffset? FechaEnvio { get; set; }

    /// <summary>ETA declarada — insumo del tránsito diferido (proposal §B). <c>date</c>, no
    /// <c>timestamptz</c>: es una fecha declarada por el proveedor, no un instante.</summary>
    public DateOnly? FechaEsperada { get; set; }

    /// <summary>Solo con <see cref="EstadoOrdenCompra.Cerrada"/> (proposal §B,
    /// <c>ck_ordenes_compra_cierre</c>) — escrita tanto por el cierre automático de la proyección
    /// como por <c>POST /{id}/cerrar</c> (manual).</summary>
    public DateTimeOffset? FechaCierre { get; set; }

    /// <summary><c>NOT NULL</c> ⇒ cierre MANUAL, nunca revertido por la proyección (design
    /// decisión 5, proposal §B) — el mismo discriminador manual/automático que el precedente de
    /// <c>apertura</c> de la etapa 15.</summary>
    public int? IdEmpleadoCierre { get; set; }

    public string? Observaciones { get; set; }

    public EstadoOrdenCompra Estado { get; set; } = EstadoOrdenCompra.Borrador;
}
