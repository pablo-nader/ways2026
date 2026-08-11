namespace Ways.Application.Exportacion;

/// <summary>Tipo de dato de una columna exportable. Determina cómo <c>IExportadorDeTabla</c>
/// escribe el valor (número/fecha reales, nunca texto pre-formateado) y qué formato de
/// número aplica a nivel columna.</summary>
public enum TipoDeColumna
{
    Texto,
    Entero,
    Decimal,
    Moneda,
    Cantidad,
    Fecha,
    FechaHora
}

/// <summary>
/// Una celda tipada de <see cref="TablaExportable"/>. El valor viaja boxeado a propósito: el
/// <see cref="TipoDeColumna"/> es la única fuente de verdad sobre cómo interpretarlo, así el
/// adaptador que finalmente escribe el archivo (hoy <c>ExportadorXlsx</c>) nunca decide un
/// formato por su cuenta — decisión 1 del design de la etapa 11 ("una columna de importe que
/// Excel no puede sumar porque en realidad es texto" es la falla que este tipo evita).
/// <c>Valor == null</c> siempre se traduce a celda vacía, nunca a <c>0</c> ni a <c>"-"</c>.
/// </summary>
public readonly record struct Celda(TipoDeColumna Tipo, object? Valor)
{
    public static Celda Texto(string? v) => new(TipoDeColumna.Texto, v);

    public static Celda Entero(int? v) => new(TipoDeColumna.Entero, v);

    public static Celda Decimal(decimal? v) => new(TipoDeColumna.Decimal, v);

    public static Celda Moneda(decimal? v) => new(TipoDeColumna.Moneda, v);

    public static Celda Cantidad(decimal? v) => new(TipoDeColumna.Cantidad, v);

    public static Celda Fecha(DateOnly? v) => new(TipoDeColumna.Fecha, v);

    /// <summary>Convierte el instante a la hora local de <paramref name="zona"/> y descarta el
    /// offset antes de guardarlo — Excel no tiene noción de zona horaria; escribir el instante
    /// crudo le daría al archivo una segunda oportunidad de correr el día que el servidor ya
    /// fijó (ADR-6 de la etapa 10, aplicado acá a archivos).</summary>
    public static Celda FechaHora(DateTimeOffset? v, TimeZoneInfo zona) =>
        new(TipoDeColumna.FechaHora, v is null ? null : TimeZoneInfo.ConvertTime(v.Value, zona).DateTime);
}
