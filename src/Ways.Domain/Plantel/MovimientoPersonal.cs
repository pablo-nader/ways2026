namespace Ways.Domain.Plantel;

/// <summary>
/// Tipo de un <see cref="MovimientoPersonal"/>. Enum nativo de Postgres
/// (<c>tipo_movimiento_personal</c>); Npgsql lo mapea por nombre, no por posición.
/// <see cref="Pago"/> está declarado para el registro de pagos, que todavía no tiene escritor.
/// </summary>
public enum TipoMovimientoPersonal
{
    Horas,
    Pago,
    Ajuste
}

/// <summary>
/// Libro de cuenta corriente del personal: la tabla que respalda <see cref="Personal.Saldo"/>
/// (doc 10, principio 7). Inmutable una vez insertado; un error se corrige con un ajuste. No
/// hereda de <see cref="Common.EntidadBase"/>: un libro append-only no tiene <c>updated_at</c> ni
/// <c>deleted_at</c>. El filtro de tenant está escrito a mano en <c>WaysDbContext</c> e
/// <c>IdTenant</c> se escribe explícito.
/// </summary>
public class MovimientoPersonal
{
    public int Id { get; set; }
    public int IdTenant { get; set; }
    public int IdPersonal { get; set; }

    /// <summary>El usuario que registró el movimiento (en este modelo, <c>id_empleado</c> siempre es
    /// el usuario que opera, nunca el personal).</summary>
    public int IdEmpleado { get; set; }

    /// <summary>El día trabajado en una carga de horas; la fecha del movimiento en los demás tipos.</summary>
    public DateOnly Fecha { get; set; }

    public TipoMovimientoPersonal Tipo { get; set; }

    /// <summary>Solo en <see cref="TipoMovimientoPersonal.Horas"/>.</summary>
    public decimal? Horas { get; set; }

    /// <summary>Valor hora congelado de la carga; solo en <see cref="TipoMovimientoPersonal.Horas"/>.</summary>
    public decimal? ValorHora { get; set; }

    /// <summary>Con signo: positivo aumenta lo que la empresa le debe al empleado, negativo lo reduce.</summary>
    public decimal Importe { get; set; }

    /// <summary>Foto de <c>personal.saldo</c> al insertar; nunca se recalcula.</summary>
    public decimal SaldoResultante { get; set; }

    public string? Detalle { get; set; }

    public DateTimeOffset CreatedAt { get; set; }
}
