namespace Ways.Domain.Reportes;

/// <summary>Suma cruda de un casillero (día de la semana × hora local) del período. El
/// <paramref name="DiaDeLaSemana"/> va de 1 (lunes) a 7 (domingo).</summary>
public readonly record struct SumaDeHora(int DiaDeLaSemana, int Hora, decimal Total, int Tickets);

/// <summary>Cuántas veces cae un día de la semana dentro del rango inclusivo del reporte.</summary>
public sealed record OcurrenciasDeDia(int DiaDeLaSemana, int Ocurrencias);

/// <summary>Un casillero de la grilla: sumas del período y promedios por ocurrencia del día.</summary>
public sealed record CeldaDeVentasPorHora(
    int DiaDeLaSemana, int Hora, decimal Total, int Tickets, decimal PromedioTotal, decimal PromedioTickets);

/// <summary>
/// Grilla completa de 7 días × 24 horas del reporte de ventas por hora, pura y sin base de datos
/// como <see cref="RangoDeReporte"/>. Los casilleros sin ventas salen en cero y el promedio divide
/// por las ocurrencias del día de la semana en el rango, tengan o no ventas.
/// </summary>
public sealed class GrillaDeVentasPorHora
{
    private const int DiasPorSemana = 7;
    private const int HorasPorDia = 24;
    private const int DecimalesDelPromedio = 2;

    public IReadOnlyList<OcurrenciasDeDia> Dias { get; }
    public IReadOnlyList<CeldaDeVentasPorHora> Celdas { get; }
    public decimal Total { get; }
    public int Tickets { get; }

    private GrillaDeVentasPorHora(
        IReadOnlyList<OcurrenciasDeDia> dias, IReadOnlyList<CeldaDeVentasPorHora> celdas, decimal total, int tickets)
    {
        Dias = dias;
        Celdas = celdas;
        Total = total;
        Tickets = tickets;
    }

    /// <summary>Varias sumas del mismo casillero se acumulan. Una suma fuera de 1..7 / 0..23 es un
    /// error de programación del llamador, no un dato del usuario.</summary>
    public static GrillaDeVentasPorHora Construir(DateOnly desde, DateOnly hasta, IEnumerable<SumaDeHora> sumas)
    {
        var ocurrencias = new int[DiasPorSemana + 1];
        for (var fecha = desde; fecha <= hasta; fecha = fecha.AddDays(1))
        {
            ocurrencias[DiaDeLaSemanaIso(fecha)]++;
        }

        var totales = new decimal[DiasPorSemana + 1, HorasPorDia];
        var tickets = new int[DiasPorSemana + 1, HorasPorDia];
        foreach (var suma in sumas)
        {
            ArgumentOutOfRangeException.ThrowIfLessThan(suma.DiaDeLaSemana, 1, nameof(sumas));
            ArgumentOutOfRangeException.ThrowIfGreaterThan(suma.DiaDeLaSemana, DiasPorSemana, nameof(sumas));
            ArgumentOutOfRangeException.ThrowIfNegative(suma.Hora, nameof(sumas));
            ArgumentOutOfRangeException.ThrowIfGreaterThanOrEqual(suma.Hora, HorasPorDia, nameof(sumas));

            totales[suma.DiaDeLaSemana, suma.Hora] += suma.Total;
            tickets[suma.DiaDeLaSemana, suma.Hora] += suma.Tickets;
        }

        var dias = new List<OcurrenciasDeDia>(DiasPorSemana);
        var celdas = new List<CeldaDeVentasPorHora>(DiasPorSemana * HorasPorDia);
        for (var dia = 1; dia <= DiasPorSemana; dia++)
        {
            dias.Add(new OcurrenciasDeDia(dia, ocurrencias[dia]));
            for (var hora = 0; hora < HorasPorDia; hora++)
            {
                var total = totales[dia, hora];
                var cantidad = tickets[dia, hora];
                celdas.Add(new CeldaDeVentasPorHora(
                    dia, hora, total, cantidad,
                    Promediar(total, ocurrencias[dia]),
                    Promediar(cantidad, ocurrencias[dia])));
            }
        }

        return new GrillaDeVentasPorHora(dias, celdas, celdas.Sum(c => c.Total), celdas.Sum(c => c.Tickets));
    }

    /// <summary>Lunes = 1 … domingo = 7.</summary>
    public static int DiaDeLaSemanaIso(DateOnly fecha) => ((int)fecha.DayOfWeek + 6) % 7 + 1;

    private static decimal Promediar(decimal suma, int ocurrencias) =>
        ocurrencias == 0
            ? 0m
            : Math.Round(suma / ocurrencias, DecimalesDelPromedio, MidpointRounding.AwayFromZero);
}
