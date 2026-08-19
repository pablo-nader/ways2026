namespace Ways.Domain.Ventas;

/// <summary>
/// La regla de expiración/convertibilidad de un <see cref="Presupuesto"/>, pura y sin base de
/// datos (design decisión 11, patrón <c>ReglaDeLotes</c>/<c>PoliticaDeRoles</c>). <c>hoy</c>
/// SIEMPRE llega resuelto en la zona horaria del punto de venta — esta clase no conoce relojes
/// ni zonas, el llamador (<c>ServicioDePresupuestos</c>/<c>EscriturasDePresupuesto</c>) resuelve
/// la zona antes de invocarla.
/// </summary>
public static class ReglaDePresupuestos
{
    /// <summary>Vencido = <c>enviado</c> Y su <c>vencimiento</c> quedó estrictamente antes de
    /// <paramref name="hoy"/> — un presupuesto es convertible EN el día de su vencimiento
    /// (design decisión 11: mismo operador que <c>ReglaDeLotes.EstaVencido</c>, <c>&lt;</c>, no
    /// <c>&lt;=</c>). Falso para todo estado que no sea <see cref="EstadoPresupuesto.Enviado"/>
    /// — un borrador nunca está "vencido" (todavía no tiene fecha), y un convertido/anulado ya
    /// salió del ciclo de vencimiento.</summary>
    public static bool EstaVencido(EstadoPresupuesto estado, DateOnly? vencimiento, DateOnly hoy) =>
        estado is EstadoPresupuesto.Enviado && vencimiento is { } v && v < hoy;

    /// <summary>Convertible = <c>enviado</c> Y no vencido. Usado como pre-chequeo en la fase
    /// decide del checkout (design: Transactions, rama del snapshot p3) — NUNCA la autoridad: la
    /// autoridad es el <c>UPDATE</c> guardado de <c>EscriturasDePresupuesto.MarcarConvertidoAsync</c>,
    /// que repite el mismo chequeo de forma atómica dentro de la transacción.</summary>
    public static bool EsConvertible(EstadoPresupuesto estado, DateOnly? vencimiento, DateOnly hoy) =>
        estado is EstadoPresupuesto.Enviado && !EstaVencido(estado, vencimiento, hoy);
}
