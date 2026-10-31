namespace Ways.Domain.Pagos;

/// <summary>
/// De dónde salen los fondos que cubren un <see cref="Pago"/> (doc 10 §5/§7). Enum nativo de
/// Postgres (<c>origen_fondos_pago</c>). <see cref="CajaTurno"/> descuenta el pago del efectivo
/// del cajón del turno abierto (<c>ck_pagos_caja_turno_requiere_turno</c> lo exige). <see
/// cref="Tesoreria"/> (stage-gastos-origen-fondos-pos, PR2) paga el pago directo desde el fondo de
/// tesorería de la empresa — el pago sigue atado a un turno abierto para trazabilidad, pero NO
/// descuenta nada del cajón: <c>ServicioDePagos.EscribirMovimientoDeTesoreriaAsync</c> escribe su
/// propio <see cref="Ways.Domain.Caja.MovimientoTesoreria"/> en la misma transacción.
/// </summary>
public enum OrigenFondosPago
{
    CajaTurno,
    Tesoreria
}
