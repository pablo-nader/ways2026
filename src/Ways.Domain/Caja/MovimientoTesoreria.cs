namespace Ways.Domain.Caja;

/// <summary>
/// Ledger encadenado de tesorería (doc 10 §7, design: Table Shapes — write path D; ex
/// <c>cajaz</c> del legacy): el cierre escribe automáticamente una fila
/// <see cref="TipoMovimientoTesoreria.RetiroCaja"/> por turno (design: The Cierre Transaction),
/// encadenando <see cref="Inicio"/> desde el <see cref="Final"/> de la última fila del mismo
/// punto de venta.
///
/// A propósito NO hereda de <see cref="Common.EntidadBase"/>/<see cref="Common.EntidadTenant"/>
/// — mismo criterio que <see cref="Ways.Domain.Stock.MovimientoStock"/>, con filtro de tenant
/// escrito a mano en <c>WaysDbContext.AplicarFiltroDeTenantEnMovimientoTesoreria</c>.
/// </summary>
public class MovimientoTesoreria
{
    public int Id { get; set; }
    public int IdTenant { get; set; }

    public int IdPuntoVenta { get; set; }
    public DateTimeOffset Fecha { get; set; }
    public TipoMovimientoTesoreria Tipo { get; set; }

    /// <summary>Turno que originó la fila — único escritor de esta etapa es el cierre, siempre
    /// la puebla. Nullable en el esquema para admitir tesorería sin turno (futuras entradas
    /// manuales, decisión 4, fuera de alcance hoy).</summary>
    public int? IdTurnoCaja { get; set; }

    public required string Concepto { get; set; }

    public decimal Inicio { get; set; }
    public decimal Ingreso { get; set; }
    public decimal Egreso { get; set; }

    /// <summary><c>inicio + ingreso − egreso</c> (design decisión 6, backstop de esquema
    /// <c>ck_movimientos_tesoreria_cadena</c>) — a diferencia de
    /// <see cref="ArqueoTurno.Diferencia"/>, se calcula en C# y se inserta: la CHECK es defensa
    /// en profundidad, no la fuente de verdad.</summary>
    public decimal Final { get; set; }

    public int IdEmpleado { get; set; }
}
