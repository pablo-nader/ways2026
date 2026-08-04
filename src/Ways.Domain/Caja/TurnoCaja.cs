using Ways.Domain.Common;

namespace Ways.Domain.Caja;

/// <summary>
/// Turno de caja (doc 10 §7, design: Table Shapes — write path A): serializa cada camino que
/// toca la caja física de un punto de venta (venta, anulación, gasto, movimiento de caja) — el
/// turno abierto es el lock de primer orden de toda la etapa (design: lock-order invariant, The
/// Cierre Transaction). Se abre con <see cref="FondoInicial"/> y se cierra una sola vez (nunca
/// hay reapertura, design decisión 10): <see cref="Estado"/> solo transiciona abierto → cerrado.
///
/// Mutable (hereda <see cref="EntidadTenant"/>, a diferencia de los ledgers append-only de esta
/// misma etapa): la fila se abre y después se cierra —dos escrituras sobre la misma fila—, así
/// que <c>updated_at</c> tiene sentido acá, a diferencia de <see cref="ArqueoTurno"/>/
/// <see cref="MovimientoCaja"/>/<see cref="MovimientoTesoreria"/>.
/// </summary>
public class TurnoCaja : EntidadTenant
{
    public int Id { get; set; }

    public int IdPuntoVenta { get; set; }

    public int IdEmpleadoApertura { get; set; }

    /// <summary>Poblado solo al cerrar (<c>ck_turnos_caja_cierre_consistente</c>).</summary>
    public int? IdEmpleadoCierre { get; set; }

    public DateTimeOffset FechaApertura { get; set; }

    /// <summary>Poblado solo al cerrar (<c>ck_turnos_caja_cierre_consistente</c>).</summary>
    public DateTimeOffset? FechaCierre { get; set; }

    public decimal FondoInicial { get; set; }

    public EstadoTurno Estado { get; set; } = EstadoTurno.Abierto;

    public string? Observaciones { get; set; }
}
