using Ways.Domain.Common;

namespace Ways.Domain.Gastos;

/// <summary>
/// Gasto operativo capturado contra un turno abierto (doc 10 §5/§7, design: Table Shapes —
/// write path C). Entidad de tenant (documento de autoría de usuario, no un ledger derivado) —
/// gana <c>updated_at</c>/baja lógica igual que <c>Cliente</c>/<c>Proveedor</c>, a diferencia de
/// los ledgers append-only de esta misma etapa.
///
/// <see cref="IdComprobanteCompra"/> aterriza en stage-8 Slice 1 (design: Table Shapes — D):
/// columna + FK compuesta juntas, mismo patrón que <see cref="Ways.Domain.Stock.MovimientoStock.IdComprobanteCompra"/>.
/// Poblado por <c>ServicioDeGastos</c> (Slice 4) bajo el guard <c>SELECT ... FOR SHARE</c> sobre
/// el header de la compra (design decisión 7).
/// </summary>
public class Gasto : EntidadTenant
{
    public int Id { get; set; }

    public DateTimeOffset Fecha { get; set; }

    public int IdPuntoVenta { get; set; }

    /// <summary>Resuelto server-side del turno abierto — nunca input de cliente (spec: Gasto
    /// Requires An Open Turno).</summary>
    public int IdTurnoCaja { get; set; }

    public int IdEmpleado { get; set; }

    public CategoriaGasto Categoria { get; set; }

    public int? IdProveedor { get; set; }
    public int? IdArea { get; set; }

    public required string Concepto { get; set; }
    public string? Detalle { get; set; }

    public int IdMedioPago { get; set; }

    public string? NumeroFactura { get; set; }

    /// <summary>Siempre <c>&gt; 0</c> (spec: Importe Must Be Positive, <c>ck_gastos_importe_positivo</c>).</summary>
    public decimal Importe { get; set; }

    /// <summary>Vincula el gasto a la compra que paga (design decisión 7) — solo válido cuando
    /// <see cref="Categoria"/> es <see cref="CategoriaGasto.Proveedor"/> y la compra está
    /// <c>confirmada</c>; el vínculo es historia, nunca bloquea la anulación de la compra
    /// (design decisión 6, la regla invertida).</summary>
    public int? IdComprobanteCompra { get; set; }
}
