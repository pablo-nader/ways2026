using Ways.Domain.Common;
using Ways.Domain.Fiscal;

namespace Ways.Domain.Ventas;

/// <summary>
/// Comprobante de venta (doc 10 §4, design: Table Shapes — write path A). Operativa-scoped
/// (<c>id_tenant</c> + <c>id_punto_venta</c>, doc 09). <see cref="IdTurnoCaja"/> siempre
/// <c>NULL</c> en esta etapa (proposal decisión 1: no hay concepto de turno abierto todavía —
/// stage 6 lo cierra). Entidad dedicada con auditoría completa (<see cref="EntidadTenant"/>): a
/// diferencia de <c>numeraciones_comprobante</c>/<c>ofertas_listas</c>, tiene identidad propia
/// (<see cref="Id"/>) y es el padre referenciado por <see cref="ItemComprobanteVenta"/>/
/// <see cref="Cobro"/> — mismo criterio que <c>Oferta</c>/<c>Cliente</c>, no el de las
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

    /// <summary>La venta (ticket) que este comprobante factura: solo lo tiene el espejo fiscal. <c>NULL</c> en
    /// todo comprobante que es una venta por derecho propio. Un espejo es siempre fiscal
    /// (<c>ck_comprobantes_venta_espejo_fiscal</c>) y una venta tiene a lo sumo uno
    /// (<c>ux_comprobantes_venta_venta_origen</c>). Los agregados de ventas lo excluyen con
    /// <see cref="ConsultasDeComprobantesVenta.SinEspejosFiscales"/>.</summary>
    public int? IdVentaOrigen { get; set; }

    /// <summary>El cajero marcó el ticket para facturarlo sí o sí. Solo se guarda acá; la decisión de
    /// facturar la toma el servidor.</summary>
    public bool FacturaForzada { get; set; }

    /// <summary>Cuándo el servidor decidió si este ticket se factura (con o sin fila de decisión). <c>NULL</c> =
    /// decisión adeudada; un espejo fiscal no se decide y queda siempre en <c>NULL</c>. Es la marca de idempotencia: una
    /// venta que no se factura no tiene fila en <c>facturaciones_de_venta</c>, así que sin esta marca decidirla dos
    /// veces la contaría dos veces en el acumulado. La escribe <c>ServicioDeDecisionDeFacturacion</c>, con SQL crudo y
    /// bajo el lock de esta fila, y los escritores de comprobantes que nunca son un ticket (TXR, RC y la emisión fiscal
    /// directa) la fijan al crear la fila para que no figuren como adeudados. Ver también
    /// <see cref="FacturacionIntentos"/> y <see cref="FacturacionProximoIntentoEn"/>.</summary>
    public DateTimeOffset? FacturacionDecididaEn { get; set; }

    /// <summary>Cuántas veces falló un intento de decidir esta venta (la propia o la de un barrido). Solo crece: una
    /// decisión lograda deja el contador como historia y no lo limpia. La escribe <c>ServicioDeDecisionDeFacturacion</c>
    /// con SQL crudo, fuera de la transacción que falló.</summary>
    public int FacturacionIntentos { get; set; }

    /// <summary>Antes de este instante el barrido no vuelve a intentar esta venta (retroceso exponencial tras un fallo).
    /// <c>NULL</c> = sin fallos pendientes de espera. Solo restringe al barrido: la decisión de la venta recién emitida
    /// y la llamada directa a <c>DecidirAsync</c> no lo miran.</summary>
    public DateTimeOffset? FacturacionProximoIntentoEn { get; set; }

    public decimal Subtotal { get; set; }
    public decimal DescuentoTotal { get; set; }

    /// <summary>Suma de los descuentos manuales de las líneas (las de porcentaje negativo), en
    /// positivo para un TX y en negativo para un NCX, igual que <see cref="DescuentoTotal"/>.
    /// Separado de <see cref="RecargoManualTotal"/> a propósito: un recargo en una línea no puede
    /// esconder un descuento en otra.</summary>
    public decimal DescuentoManualTotal { get; set; }

    /// <summary>Suma de los recargos manuales de las líneas (las de porcentaje positivo). El total
    /// es <c>subtotal − descuento_total − descuento_manual_total + recargo_manual_total</c>.</summary>
    public decimal RecargoManualTotal { get; set; }

    public decimal Total { get; set; }

    /// <summary><c>NULL</c> mientras <c>tipos_comprobante.discrimina_iva = false</c> (TX/NCX de
    /// esta etapa nunca discriminan IVA — no son fiscales).</summary>
    public decimal? NetoGravado { get; set; }
    public decimal? IvaTotal { get; set; }

    public string? DireccionEntrega { get; set; }
    public string? Observaciones { get; set; }

    public EstadoComprobante Estado { get; set; } = EstadoComprobante.Emitido;

    /// <summary>stage-19a (proposal.md §D): las cuatro columnas fiscales son ADITIVAS y llegan
    /// juntas — <see cref="ResultadoFiscal"/> <c>NULL</c> significa "no es un comprobante
    /// fiscal", el estado del 100% del tráfico TX/NCX/TXR/RC, permanentemente legítimo.
    /// <c>ck_comprobantes_venta_fiscal_coherente</c> impone la coherencia entre las cuatro. Sin
    /// columna nueva para el número fiscal: <see cref="Numero"/> ya lo lleva, alimentado por la
    /// serie fiscal en vez de la interna — disjuntas por tipo de comprobante.</summary>
    public string? Cae { get; set; }

    /// <summary>Llega junto con <see cref="Cae"/> (mismo CHECK) — nunca <c>DEFAULT now()</c>,
    /// viene del propio CAE que devuelve ARCA.</summary>
    public DateOnly? CaeVencimiento { get; set; }

    public ResultadoFiscal? ResultadoFiscal { get; set; }

    /// <summary>Documento jsonb con <c>Observaciones[]</c>/<c>Errors[]</c> de ARCA
    /// (<c>[{ "codigo": int, "mensaje": string }]</c>) — precedente <c>Auditoria.cs:40-45</c>.
    /// Nunca la respuesta cruda: eso persistiría <c>Token</c>/<c>Sign</c>, una credencial
    /// portadora, en una tabla sin camino de cifrado.</summary>
    public string? ObservacionesFiscales { get; set; }

    /// <summary>Documento jsonb con el emisor y el receptor tal como se emitió el espejo fiscal (incluido el documento ARCA
    /// del receptor que viajó a ARCA y entró al QR). Se escribe una sola vez, en la transacción que inserta el espejo, y no
    /// se vuelve a tocar. <c>NULL</c> en toda venta y en los espejos anteriores a la columna: esos se leen de las filas
    /// actuales de empresa, cliente y punto de venta.</summary>
    public string? DatosLegalesFiscales { get; set; }
}
