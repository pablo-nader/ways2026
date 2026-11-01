namespace Ways.Domain.Caja;

/// <summary>
/// Ledger de movimientos físicos de caja fuera de la venta (doc 10 §7, design: Table Shapes —
/// write path B): retiros, refuerzos y apertura de cajón (F12 del legacy). Los refuerzos y las
/// aperturas de cajón son append-only; un retiro manual lo puede corregir o dar de baja un Admin
/// (<c>ServicioDeRetiros</c>): el importe y el motivo se actualizan en el lugar, con la auditoría
/// llevando el valor anterior, y la baja es lógica (<see cref="DeletedAt"/>).
///
/// A propósito NO hereda de <see cref="Common.EntidadBase"/>/<see cref="Common.EntidadTenant"/>:
/// no tiene <c>created_at</c>/<c>updated_at</c>, su fecha es <c>creado_el</c>. Los filtros de tenant
/// y de baja lógica están escritos a mano en <c>WaysDbContext.AplicarFiltroDeTenantEnMovimientoCaja</c>.
/// </summary>
public class MovimientoCaja
{
    public int Id { get; set; }
    public int IdTenant { get; set; }

    public int IdTurnoCaja { get; set; }

    public TipoMovimientoCaja Tipo { get; set; }

    /// <summary>Siempre <c>0</c> para <see cref="TipoMovimientoCaja.AperturaCajon"/>, siempre
    /// <c>&gt; 0</c> para los demás (design decisión 8, <c>ck_movimientos_caja_importe</c>).</summary>
    public decimal Importe { get; set; }

    /// <summary>Obligatorio y con longitud mínima uniformada para los tres tipos por igual
    /// (design decisión 8, <c>ck_movimientos_caja_motivo_minimo</c>): mover dinero físico o
    /// abrir el cajón merecen una razón registrada.</summary>
    public required string Motivo { get; set; }

    public int IdEmpleado { get; set; }

    public DateTimeOffset CreadoEl { get; set; }

    /// <summary>El retiro que inserta el cierre por retiro (<c>ServicioDeTurnos.CerrarPorRetiroAsync</c>). No se edita ni se
    /// elimina: es parte del cierre.</summary>
    public bool EsRetiroDeCierre { get; set; }

    public DateTimeOffset? DeletedAt { get; set; }
}
