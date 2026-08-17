namespace Ways.Application.Exportacion;

/// <summary>
/// Traduce los límites <c>desde</c>/<c>hasta</c> de un export a las fechas que se MUESTRAN: el
/// "Período" del encabezado y los dos extremos de <see cref="NombreDeArchivo"/>. El filtrado de
/// filas sigue usando el <see cref="DateTimeOffset"/> crudo — esto no lo toca.
///
/// El cliente manda cada límite como el día elegido en el picker expresado en su propio offset
/// local (<c>2026-08-16T00:00:00-03:00</c> / <c>2026-08-16T23:59:59.999-03:00</c>), así que el día
/// a mostrar es el del reloj que viaja EN el propio valor, no el del instante llevado a UTC:
/// <c>UtcDateTime</c> adelanta un día con offsets negativos (<c>23:59:59.999-03:00</c> cae en el
/// <c>02:59:59.999Z</c> del día siguiente, el caso de America/Argentina/Buenos_Aires) y atrasa uno
/// con offsets positivos (<c>00:00:00+05:30</c> cae en el <c>18:30Z</c> del día anterior).
///
/// Distinto de un instante del servidor (<c>reloj.Ahora</c>, <c>turno.FechaApertura</c>), que no
/// lleva intención de zona y por eso sí se convierte a la zona resuelta del punto de venta.
/// </summary>
public static class FechaDelRango
{
    public static DateOnly De(DateTimeOffset limite) => DateOnly.FromDateTime(limite.DateTime);

    public static (DateOnly Desde, DateOnly Hasta) De(DateTimeOffset desde, DateTimeOffset hasta) =>
        (De(desde), De(hasta));
}
