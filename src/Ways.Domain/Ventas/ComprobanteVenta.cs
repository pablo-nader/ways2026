using Ways.Domain.Common;

namespace Ways.Domain.Ventas;

/// <summary>
/// Comprobante de venta (doc 10 §4, design: Table Shapes — write path A). Operativa-scoped
/// (<c>id_tenant</c> + <c>id_punto_venta</c>, doc 09). <see cref="IdTurnoCaja"/> siempre
/// <c>NULL</c> en esta etapa (proposal decisión 1: no hay concepto de turno abierto todavía —
/// stage 6 lo cierra). Entidad dedicada con auditoría completa (<see cref="EntidadTenant"/>): a
/// diferencia de <c>numeraciones_comprobante</c>/<c>ofertas_listas</c>, tiene identidad propia
/// (<see cref="Id"/>) y es el padre referenciado por <see cref="ItemComprobanteVenta"/>/
/// <see cref="PagoComprobante"/> — mismo criterio que <c>Oferta</c>/<c>Cliente</c>, no el de las
/// junctions PK-only.
///
/// Inmutable una vez emitido (doc 10 principio 6): el único camino de escritura posterior a la
/// emisión es la transición de <see cref="Estado"/> a <see cref="EstadoComprobante.Anulado"/>
/// (Slice 5, <c>ServicioDeVentas.AnularAsync</c>, <c>UPDATE ... WHERE estado = 'emitido'</c>
/// condicional) — nunca una edición de ítems/pagos/totales.
/// </summary>
public class ComprobanteVenta : EntidadTenant
{
    public int Id { get; set; }

    public int IdTipoComprobante { get; set; }

    /// <summary>Asignado por <c>AsignadorDeNumeroComprobante</c> (Slice 2) — nunca client-side.
    /// Visible como <c>NumeroDeComprobante.Formatear(IdPuntoVenta, Numero)</c>.</summary>
    public long Numero { get; set; }

    public DateTimeOffset Fecha { get; set; }

    public int IdPuntoVenta { get; set; }

    /// <summary>Resuelto server-side desde el turno abierto del punto de venta (stage 6,
    /// <c>ServicioDeVentas.EmitirAsync</c>) — la promesa de esta etapa ya se cumple: toda venta
    /// nueva lo lleva poblado. <c>NULL</c> permanece solo en los comprobantes emitidos en stage 5,
    /// antes de que <c>turnos_caja</c> existiera (decisión 8: sin backfill).</summary>
    public int? IdTurnoCaja { get; set; }

    /// <summary><c>IContextoDeUsuario.UsuarioId</c> (design decisión 11) — quien opera la venta
    /// ES el usuario autenticado; no existe una tabla <c>empleados</c> separada todavía.</summary>
    public int IdEmpleado { get; set; }

    public int IdCliente { get; set; }

    /// <summary>Solo poblado en un NCX que referencia el TX que corrige (spec: Devoluciones As
    /// NCX Comprobantes) — <c>ReglaDeComprobantes.ValidarComprobanteAsociado</c> rechaza un TX
    /// con este campo seteado.</summary>
    public int? IdComprobanteAsociado { get; set; }

    /// <summary>stage-17-presupuestos-y-remitos (proposal §G): el presupuesto convertido en
    /// esta venta, si la hay. <c>NULL</c> en el 100% del tráfico previo a esta etapa —
    /// permanentemente legítimo, no toda venta viene de un presupuesto. La unicidad de
    /// <c>(id_presupuesto_origen, id_tenant)</c> (<c>ux_comprobantes_venta_presupuesto_origen</c>,
    /// PARCIAL) es la garantía de base de que a lo sumo un comprobante liga a cada presupuesto —
    /// escrito por <c>EscriturasDePresupuesto.MarcarConvertidoAsync</c> dentro de la misma
    /// transacción de venta (slice 3), nunca editado después.</summary>
    public int? IdPresupuestoOrigen { get; set; }

    public decimal Subtotal { get; set; }
    public decimal DescuentoTotal { get; set; }
    public decimal Total { get; set; }

    /// <summary><c>NULL</c> mientras <c>tipos_comprobante.discrimina_iva = false</c> (TX/NCX de
    /// esta etapa nunca discriminan IVA — no son fiscales).</summary>
    public decimal? NetoGravado { get; set; }
    public decimal? IvaTotal { get; set; }

    public string? DireccionEntrega { get; set; }
    public string? Observaciones { get; set; }

    public EstadoComprobante Estado { get; set; } = EstadoComprobante.Emitido;
}
