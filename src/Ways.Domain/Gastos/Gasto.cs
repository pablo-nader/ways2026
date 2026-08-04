using Ways.Domain.Common;

namespace Ways.Domain.Gastos;

/// <summary>
/// Gasto operativo capturado contra un turno abierto (doc 10 §5/§7, design: Table Shapes —
/// write path C). Entidad de tenant (documento de autoría de usuario, no un ledger derivado) —
/// gana <c>updated_at</c>/baja lógica igual que <c>Cliente</c>/<c>Proveedor</c>, a diferencia de
/// los ledgers append-only de esta misma etapa.
///
/// <c>id_comprobante_compra</c> NO existe todavía (proposal decisión 1, design: Table Shapes —
/// write path C): <c>comprobantes_compra</c> no existe, así que la columna se difiere a la
/// etapa 8 (que la crea junto con su FK, mismo patrón que
/// <see cref="Ways.Domain.Stock.MovimientoStock"/> con su propio FK diferido de compra) en vez
/// de dejar un <c>int?</c> sin constraint.
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
}
