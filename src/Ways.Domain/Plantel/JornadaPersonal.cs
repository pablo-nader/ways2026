namespace Ways.Domain.Plantel;

/// <summary>
/// Marca un día de un personal como confirmado y guarda su total de horas (doc 10 §8c). Distingue «confirmado
/// en 0 h» (la fila existe con <see cref="Horas"/> = 0) de «sin cargar» (no hay fila: la grilla semanal lo
/// precarga con el horario habitual). La verdad del dinero es el libro <see cref="MovimientoPersonal"/>:
/// después de cada escritura <see cref="Horas"/> es igual a la suma de las horas de sus asientos de ese día.
/// Es mutable (el total cambia al editar el día) pero no se da de baja, así que no hereda
/// <see cref="Common.EntidadBase"/>; el filtro de tenant está escrito a mano en <c>WaysDbContext</c>,
/// <c>IdTenant</c> se escribe explícito y solo <c>EscriturasDePersonal</c> la escribe, con SQL crudo.
/// </summary>
public class JornadaPersonal
{
    public int Id { get; set; }
    public int IdTenant { get; set; }
    public int IdPersonal { get; set; }

    /// <summary>El usuario que confirmó el día por última vez.</summary>
    public int IdEmpleado { get; set; }

    public DateOnly Fecha { get; set; }

    /// <summary>Total confirmado del día, de 0 a 24.</summary>
    public decimal Horas { get; set; }

    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }
}
