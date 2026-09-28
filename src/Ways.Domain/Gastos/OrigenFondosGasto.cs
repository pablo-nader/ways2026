namespace Ways.Domain.Gastos;

/// <summary>
/// De dónde salen los fondos que cubren un <see cref="Gasto"/> (doc 10 §5/§7). Enum nativo de
/// Postgres (<c>origen_fondos_gasto</c>). <see cref="CajaTurno"/> descuenta el gasto del efectivo
/// del cajón del turno abierto (<c>ck_gastos_caja_turno_requiere_turno</c> lo exige). <see
/// cref="Tesoreria"/> (stage-gastos-origen-fondos-pos, PR2) paga el gasto directo desde el fondo de
/// tesorería de la empresa — el gasto sigue atado a un turno abierto para trazabilidad, pero NO
/// descuenta nada del cajón: <c>ServicioDeGastos.EscribirMovimientoDeTesoreriaAsync</c> escribe su
/// propio <see cref="Ways.Domain.Caja.MovimientoTesoreria"/> en la misma transacción.
/// </summary>
public enum OrigenFondosGasto
{
    CajaTurno,
    Tesoreria
}
