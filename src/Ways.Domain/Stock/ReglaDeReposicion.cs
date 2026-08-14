using Ways.Domain.Common;

namespace Ways.Domain.Stock;

/// <summary>
/// Estado de reposición de un artículo en un punto de venta, derivado en <see
/// cref="ReglaDeReposicion.Clasificar"/> — nunca persistido (proposal decisión 1: <c>minimo</c>
/// es el valor fijo que el dueño setea; este enum es una LECTURA sobre él, nunca un campo
/// propio). Wire values son los nombres de miembro de C# (<c>JsonStringEnumConverter</c>, sin
/// naming policy) — mismo criterio que <see cref="EstadoDeVencimiento"/> (stage-12).
/// </summary>
public enum EstadoDeReposicion
{
    SinMinimo,
    Bajo,
    Ok
}

/// <summary>
/// La regla de reposición, pura y sin base de datos (design decisión 1, patrón
/// <c>PoliticaDeRoles</c>/<c>ReglaDeLotes</c>): clasificación por umbral, sugerencia de compra,
/// consumo diario promedio, cobertura y la ventana de rotación con su resolución de zona
/// horaria. El write path de <c>PUT /api/stock/minimos</c> y los tres reportes de gestión
/// (existencias, reposición, rotación) la consumen igual — la regla se testea UNA vez acá,
/// nunca reimplementada (proposal, riesgo 2: "una cifra plausible y equivocada").
/// </summary>
public static class ReglaDeReposicion
{
    /// <summary>Proposal decisión 1(b): <c>minimo IS NULL</c> ⇒ no gestionado, nunca alerta —
    /// lo que vuelve el día uno silencioso en vez de catastrófico. El borde es <c>cantidad
    /// &lt;= minimo</c>, NUNCA <c>&lt;</c>: alcanzar el punto de pedido ES la señal, y así
    /// <c>minimo = 0</c> también es útil ("avisame cuando llegue a cero") en vez de inútil.
    /// </summary>
    public static EstadoDeReposicion Clasificar(decimal cantidad, decimal? minimo) =>
        minimo is null ? EstadoDeReposicion.SinMinimo
        : cantidad <= minimo.Value ? EstadoDeReposicion.Bajo
        : EstadoDeReposicion.Ok;

    /// <summary>Proposal decisión 1(c)/3/4: <c>null</c> (JAMÁS <c>0</c>) cuando no hay nivel
    /// objetivo (<paramref name="reposicion"/>) — un cero en "cuánto comprar" es una respuesta
    /// fabricada a una pregunta que el sistema no puede responder. <c>Math.Max(0, …)</c> evita
    /// una sugerencia negativa cuando <paramref name="cantidad"/> ya superó el objetivo. Sin
    /// término "en tránsito": ese sustraendo lo agrega la etapa 16.</summary>
    public static decimal? Sugerido(decimal cantidad, decimal? reposicion) =>
        reposicion is null ? null : Math.Max(0m, reposicion.Value - cantidad);

    /// <summary><paramref name="netoConsumido"/> <c>null</c> ⇒ ningún movimiento de consumo
    /// calificado en la ventana: no hay historia que promediar, y la respuesta honesta es "no
    /// sé" (<c>null</c>), nunca "cero" (proposal, riesgo 3). Un neto negativo (devoluciones
    /// superan a las ventas dentro de la ventana) se recorta a <c>0</c> — nunca <c>null</c>,
    /// porque SÍ hubo historia calificada, y nunca negativo, porque un consumo diario negativo
    /// no es una magnitud de negocio. <paramref name="diasVentana"/> &gt;= 1 lo garantiza el
    /// llamador vía <see cref="ExigirVentanaValida"/> — nunca una división por cero acá.
    /// </summary>
    public static decimal? ConsumoDiario(decimal? netoConsumido, int diasVentana) =>
        netoConsumido is null ? null : Math.Max(0m, netoConsumido.Value) / diasVentana;

    /// <summary>Consumo diario promedio × <paramref name="diasCoberturaObjetivo"/>, redondeado
    /// a 3 decimales — la precisión de <c>numeric(12,3)</c> que <c>stock.minimo</c>/<c>
    /// reposicion</c> ya usan. <c>null</c> se propaga desde <paramref name="consumoDiario"/>
    /// (sin historia ⇒ sin sugerencia, nunca una sugerencia de cero).</summary>
    public static decimal? MinimoSugerido(decimal? consumoDiario, int diasCoberturaObjetivo) =>
        consumoDiario is null
            ? null
            : Math.Round(consumoDiario.Value * diasCoberturaObjetivo, 3, MidpointRounding.AwayFromZero);

    /// <summary><c>null</c> cuando el consumo diario es <c>null</c> (sin historia) O <c>0</c>
    /// (el artículo no rota) — "infinito" no es un número de días, y tampoco lo es <c>0</c>:
    /// ninguna de las dos respuestas es honesta, así que ninguna se devuelve.</summary>
    public static decimal? DiasDeCobertura(decimal cantidad, decimal? consumoDiario) =>
        consumoDiario is null or 0m ? null : cantidad / consumoDiario.Value;

    /// <summary>
    /// Proposal decisión 7 / design decisión 7: la ventana <c>[hoy - (dias-1) .. hoy]</c>,
    /// resuelta en <paramref name="zona"/> y devuelta como instantes UTC — <c>
    /// HastaUtcExclusivo</c> es la medianoche local del día SIGUIENTE a <paramref
    /// name="hoy"/>, exclusiva (mismo criterio que <c>RangoDeReporte.HastaUtcExclusivo</c>).
    ///
    /// <see cref="TimeZoneInfo.GetUtcOffset(DateTime)"/> — nunca <c>ConvertTimeToUtc</c>, que
    /// TIRA <c>ArgumentException</c> sobre un local inválido — ya resuelve los dos bordes que
    /// un naive <c>ConvertTimeToUtc</c> revienta o contesta mal, sin código especial acá: una
    /// medianoche local INVÁLIDA (una zona que salta hacia adelante exactamente a las 24:00)
    /// devuelve el offset ANTERIOR a la transición, así que el instante calculado avanza
    /// exactamente al instante del salto; una medianoche AMBIGUA (una zona que retrocede el
    /// reloj exactamente a las 24:00) devuelve el offset ESTÁNDAR por diseño de la BCL. El
    /// mismo patrón que <c>RangoDeReporte.InstanteLocal</c> ya prueba en producción.
    /// </summary>
    public static (DateTimeOffset DesdeUtc, DateTimeOffset HastaUtcExclusivo) VentanaDeRotacion(
        DateOnly hoy, int dias, TimeZoneInfo zona)
    {
        var desde = hoy.AddDays(-(dias - 1));
        var hastaExclusivo = hoy.AddDays(1);

        return (InstanteLocal(desde, zona), InstanteLocal(hastaExclusivo, zona));
    }

    private static DateTimeOffset InstanteLocal(DateOnly fecha, TimeZoneInfo zona)
    {
        var local = fecha.ToDateTime(TimeOnly.MinValue, DateTimeKind.Unspecified);
        var offset = zona.GetUtcOffset(local);

        return new DateTimeOffset(local, offset).ToUniversalTime();
    }

    /// <summary>400 <paramref name="codigo"/> (<c>dias_rotacion_invalido</c> / <c>
    /// dias_cobertura_invalido</c>, según el llamador) — un parámetro <c>&lt;= 0</c> dividiría
    /// por cero en <see cref="ConsumoDiario"/> o produciría una ventana invertida en <see
    /// cref="VentanaDeRotacion"/>. Refinamiento sobre el proposal (decisión de sdd-design).
    /// </summary>
    public static int ExigirVentanaValida(int dias, string codigo)
    {
        if (dias <= 0)
        {
            throw new ErrorDominio(
                codigo, $"El parámetro de días tiene que ser mayor a cero (recibido: {dias}).", 400);
        }

        return dias;
    }
}
