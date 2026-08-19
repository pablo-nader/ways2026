using Ways.Domain.Common;

namespace Ways.Domain.Compras;

/// <summary>
/// Comprobante de compra (doc 10 §5, design: Table Shapes — A). Operativa-scoped (<c>id_tenant</c>
/// + <c>id_punto_venta</c>, doc 09), misma base que <see cref="Ways.Domain.Ventas.ComprobanteVenta"/>
/// (<see cref="EntidadTenant"/>) — <see cref="UpdatedAt"/> es genuinamente significativo acá: un
/// <see cref="Estado"/> <see cref="EstadoCompra.Borrador"/> es el único documento mutable del
/// sistema (design decisión 2). <see cref="EntidadBase.DeletedAt"/> nunca se escribe — no existe
/// endpoint de borrado, el registro transiciona de estado, nunca se elimina.
///
/// <see cref="NumeroExterno"/>/<see cref="FechaComprobante"/> quedan <c>NULL</c> mientras
/// <see cref="Estado"/> es <see cref="EstadoCompra.Borrador"/> (doc-10:374-375) —
/// <c>ck_comprobantes_compra_confirmada_completa</c> es el backstop de esquema de esa regla.
/// <see cref="FechaRecepcion"/> lo escribe únicamente <c>ServicioDeCompras.ConfirmarAsync</c>
/// (Slice 2) desde <c>IRelojDelSistema.Ahora</c> — nunca input de cliente.
/// </summary>
public class ComprobanteCompra : EntidadTenant
{
    public int Id { get; set; }

    public int IdProveedor { get; set; }

    /// <summary><c>clase = compra</c> se exige en el servicio (Slice 2), no por esquema — el
    /// mismo criterio que <c>ComprobanteVenta.IdTipoComprobante</c> con <c>clase = venta</c>.</summary>
    public int IdTipoComprobante { get; set; }

    /// <summary>El número del proveedor (doc-10:374-375) — acá no hay correlativo propio, a
    /// diferencia de <c>ComprobanteVenta.Numero</c>. <c>NULL</c> mientras <c>Borrador</c>.</summary>
    public string? NumeroExterno { get; set; }

    /// <summary>La fecha de la factura del proveedor — <c>NULL</c> mientras <c>Borrador</c>.</summary>
    public DateOnly? FechaComprobante { get; set; }

    /// <summary>Escrito por la confirmación desde <c>IRelojDelSistema.Ahora</c> — nunca input de
    /// cliente (design: Transactions — CONFIRMAR COMPRA).</summary>
    public DateTimeOffset? FechaRecepcion { get; set; }

    /// <summary>El local donde entra la mercadería (doc-10:378).</summary>
    public int IdPuntoVenta { get; set; }

    /// <summary><c>IContextoDeUsuario.UsuarioId</c> — mismo criterio que
    /// <c>ComprobanteVenta.IdEmpleado</c> (FK simple, deviación documentada).</summary>
    public int IdEmpleado { get; set; }

    public decimal Subtotal { get; set; }
    public decimal DescuentoTotal { get; set; }
    public decimal Total { get; set; }

    /// <summary><c>NULL</c> cuando <c>tipos_comprobante.discrimina_iva = false</c> (design:
    /// Compra Arithmetic) — misma postura que <c>ComprobanteVenta.IvaTotal</c>.</summary>
    public decimal? IvaTotal { get; set; }

    public string? Observaciones { get; set; }

    public EstadoCompra Estado { get; set; } = EstadoCompra.Borrador;

    /// <summary>Etapa 16 (proposal: Modelo de datos propuesto — §D): la orden de compra que esta
    /// recepción cubre. <c>NULL</c> = 100% del tráfico anterior a esta etapa, un estado
    /// permanentemente legítimo — no toda compra viene de una OC. Seteable mientras
    /// <see cref="EstadoCompra.Borrador"/> (<c>ExigirOrdenLigableAsync</c>, slice 3); congelado
    /// una vez que el comprobante deja de ser borrador.</summary>
    public int? IdOrdenCompra { get; set; }
}
