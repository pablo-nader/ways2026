namespace Ways.Domain.Caja;

/// <summary>
/// Libro encadenado de una <see cref="CuentaTesoreria"/> (doc 10 §7): cada fila parte del
/// <see cref="Final"/> de la anterior de la MISMA cuenta. A propósito NO hereda de
/// <see cref="Common.EntidadBase"/>/<see cref="Common.EntidadTenant"/>, mismo criterio que
/// <see cref="MovimientoTesoreria"/>, con filtro de tenant escrito a mano en
/// <c>WaysDbContext.AplicarFiltroDeTenantEnMovimientoCuenta</c>.
/// </summary>
public class MovimientoCuenta
{
    public int Id { get; set; }
    public int IdTenant { get; set; }
    public int IdCuentaTesoreria { get; set; }
    public DateTimeOffset Fecha { get; set; }
    public TipoMovimientoCuenta Tipo { get; set; }

    /// <summary>Turno cuyo cierre originó la fila; <c>null</c> para movimientos sin turno.</summary>
    public int? IdTurnoCaja { get; set; }

    /// <summary>Medio de pago que originó la fila; <c>null</c> para movimientos sin medio.</summary>
    public int? IdMedioPago { get; set; }

    public required string Concepto { get; set; }

    public decimal Inicio { get; set; }
    public decimal Ingreso { get; set; }
    public decimal Egreso { get; set; }

    /// <summary><c>inicio + ingreso − egreso</c>, calculado en C# e insertado; la CHECK
    /// <c>ck_movimientos_cuenta_cadena</c> es defensa en profundidad.</summary>
    public decimal Final { get; set; }

    public int IdEmpleado { get; set; }

    /// <summary>Base sobre la que se calculó <see cref="PorcentajeAplicado"/>, congelada en la fila.</summary>
    public decimal? BaseBruta { get; set; }

    /// <summary>Porcentaje vigente al escribir la fila, congelado para que un cambio posterior en el
    /// medio de pago no reescriba la historia.</summary>
    public decimal? PorcentajeAplicado { get; set; }
}
