namespace Ways.Domain.Ventas;

/// <summary>
/// De dónde nace una <see cref="AplicacionCobro"/> (doc 10 §8a). Enum nativo de Postgres (<c>origen_aplicacion_cobro</c>).
/// <see cref="Manual"/> es la que alguien hizo desde el sistema (al registrar el recibo o después); <see cref="Historial"/>
/// es la que la migración <c>AplicarCobrosHistoricosPorAntiguedad</c> armó con los recibos anteriores, aplicando cada uno a
/// las ventas más viejas del mismo cliente. Las dos se desaplican igual. Si alguien suma un importe a mano a una aplicación
/// histórica, esa fila pasa a <see cref="Manual"/> y el Down de la migración (que borra solo lo histórico) ya no la toca.
/// </summary>
public enum OrigenAplicacionCobro
{
    Manual,
    Historial
}
