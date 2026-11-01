namespace Ways.Domain.Catalogos;

/// <summary>
/// Nivel al que pertenece una clave de <see cref="ParametroConocido"/>. Una clave de
/// <see cref="Caja"/> se resuelve punto de venta → empresa → default; una de
/// <see cref="Sucursal"/> (la que comparten todas las cajas de un mismo local: zona horaria, lotes,
/// días de alerta) se resuelve sucursal → empresa → default, y las filas por punto de venta no cuentan.
/// </summary>
public enum AlcanceDeParametro
{
    Caja = 0,
    Sucursal = 1
}
