namespace Ways.Domain.Caja;

/// <summary>
/// Estado de un <see cref="TurnoCaja"/> (doc 10 §7). Enum nativo de Postgres
/// (<c>estado_turno</c>). Única transición válida: <see cref="Abierto"/> → <see cref="Cerrado"/>
/// (design decisión 10) — nunca al revés, no hay reapertura.
/// </summary>
public enum EstadoTurno
{
    Abierto,
    Cerrado
}
