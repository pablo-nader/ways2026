namespace Ways.Application.Fiscal;

/// <summary>La fecha que ARCA registra (<c>CbteFch</c>) y la que muestra el libro IVA es el día de
/// negocio de la empresa, no el día UTC: entre las 21:00 y las 24:00 en Argentina el día UTC ya es
/// el siguiente.</summary>
public static class FechaFiscal
{
    public static DateOnly DeInstante(DateTimeOffset instante, TimeZoneInfo zona) =>
        DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(instante, zona).DateTime);
}
