namespace Ways.Domain.Gastos;

/// <summary>
/// De dónde salen los fondos que cubren un <see cref="Gasto"/> (doc 10 §5/§7). Enum nativo de
/// Postgres (<c>origen_fondos_gasto</c>). <see cref="CajaTurno"/> es el único origen que existe
/// hoy — todo gasto se descuenta del efectivo de un turno abierto
/// (<c>ck_gastos_caja_turno_requiere_turno</c>); <see cref="Tesoreria"/> aterriza el modelo para
/// una etapa futura donde un gasto puede pagarse directo desde el fondo de tesorería, sin pasar
/// por ningún turno.
/// </summary>
public enum OrigenFondosGasto
{
    CajaTurno,
    Tesoreria
}
