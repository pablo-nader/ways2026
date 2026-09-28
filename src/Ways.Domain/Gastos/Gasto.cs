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

    /// <summary>FK compuesta a <see cref="Ways.Domain.Organizacion.Empresa"/> — resuelta
    /// server-side desde <see cref="IdPuntoVenta"/> (<c>PuntoVenta.IdEmpresa</c>), nunca input de
    /// cliente. Backfill de <c>GastosOrigenFondosYTesoreriaPorEmpresa</c> para las filas
    /// preexistentes.</summary>
    public int IdEmpresa { get; set; }

    /// <summary>Punto de venta de origen (FK compuesta). Nullable en el esquema desde
    /// <c>GastosOrigenFondosYTesoreriaPorEmpresa</c>, pero <c>ServicioDeGastos.RegistrarAsync</c>
    /// siempre lo puebla hoy (viene de <see cref="SolicitudDeGasto"/>) — CajaTurno y Tesoreria
    /// nacen por igual contra un punto de venta puntual.</summary>
    public int? IdPuntoVenta { get; set; }

    /// <summary>Resuelto server-side del turno abierto — nunca input de cliente (spec: Gasto
    /// Requires An Open Turno). Nullable en el esquema: solo <see cref="OrigenFondos"/> = <see
    /// cref="OrigenFondosGasto.CajaTurno"/> lo EXIGE (<c>ck_gastos_caja_turno_requiere_turno</c>).
    /// stage-gastos-origen-fondos-pos (PR2): un gasto de origen <see cref="OrigenFondosGasto.Tesoreria"/>
    /// también lo trae poblado — sigue atado al turno abierto para trazabilidad — pero la CHECK no
    /// lo exige porque ese origen no descuenta el cajón.</summary>
    public int? IdTurnoCaja { get; set; }

    public int IdEmpleado { get; set; }

    public CategoriaGasto Categoria { get; set; }

    /// <summary>De dónde salen los fondos (doc 10 §5/§7). <c>ServicioDeGastos.RegistrarAsync</c>
    /// persiste el valor que trae <c>SolicitudDeGasto.OrigenFondos</c> (default <see
    /// cref="OrigenFondosGasto.CajaTurno"/> por retrocompatibilidad) — <see
    /// cref="OrigenFondosGasto.Tesoreria"/> excluye el gasto de todo arqueo (<see
    /// cref="Ways.Application.Caja.LectorDeMovimientosDelTurno"/>) y le escribe su propio <see
    /// cref="Ways.Domain.Caja.MovimientoTesoreria"/>.</summary>
    public OrigenFondosGasto OrigenFondos { get; set; }

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
