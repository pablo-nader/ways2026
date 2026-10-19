using Ways.Domain.Reportes;

namespace Ways.Domain.Tests.Reportes;

/// <summary>Grilla 7×24 del reporte de ventas por hora: pura, sin base de datos.</summary>
public class GrillaDeVentasPorHoraTests
{
    // 2026-08-03 es lunes.
    private static readonly DateOnly Lunes = new(2026, 8, 3);

    private static CeldaDeVentasPorHora Celda(GrillaDeVentasPorHora grilla, int dia, int hora) =>
        grilla.Celdas.Single(c => c.DiaDeLaSemana == dia && c.Hora == hora);

    [Fact]
    public void LaGrillaSiempreTiene168CeldasOrdenadasPorDiaYHora()
    {
        var grilla = GrillaDeVentasPorHora.Construir(Lunes, Lunes, []);

        Assert.Equal(168, grilla.Celdas.Count);
        Assert.Equal(7, grilla.Dias.Count);
        Assert.Equal(Enumerable.Range(1, 7), grilla.Dias.Select(d => d.DiaDeLaSemana));
        Assert.Equal(
            Enumerable.Range(1, 7).SelectMany(d => Enumerable.Range(0, 24).Select(h => (d, h))),
            grilla.Celdas.Select(c => (c.DiaDeLaSemana, c.Hora)));
        Assert.All(grilla.Celdas, c =>
        {
            Assert.Equal(0m, c.Total);
            Assert.Equal(0, c.Tickets);
            Assert.Equal(0m, c.PromedioTotal);
            Assert.Equal(0m, c.PromedioTickets);
        });
    }

    [Fact]
    public void UnDiaSueltoCuentaUnaOcurrenciaSoloParaSuDiaDeLaSemana()
    {
        var grilla = GrillaDeVentasPorHora.Construir(Lunes, Lunes, []);

        Assert.Equal(1, grilla.Dias.Single(d => d.DiaDeLaSemana == 1).Ocurrencias);
        Assert.Equal(0, grilla.Dias.Where(d => d.DiaDeLaSemana != 1).Sum(d => d.Ocurrencias));
    }

    [Fact]
    public void LasOcurrenciasContabilizanElRangoInclusivoDeAmbosExtremos()
    {
        // Lunes 3/8 a lunes 17/8: 15 días => tres lunes y dos de cada otro día.
        var grilla = GrillaDeVentasPorHora.Construir(Lunes, Lunes.AddDays(14), []);

        Assert.Equal(3, grilla.Dias.Single(d => d.DiaDeLaSemana == 1).Ocurrencias);
        Assert.All(grilla.Dias.Where(d => d.DiaDeLaSemana != 1), d => Assert.Equal(2, d.Ocurrencias));
    }

    [Fact]
    public void ElDomingoEsElDia7()
    {
        var domingo = Lunes.AddDays(6);
        var grilla = GrillaDeVentasPorHora.Construir(domingo, domingo, []);

        Assert.Equal(1, grilla.Dias.Single(d => d.DiaDeLaSemana == 7).Ocurrencias);
    }

    [Fact]
    public void ElPromedioDivideLaSumaPorLasOcurrenciasAunqueUnDiaNoTengaVentas()
    {
        // Tres lunes en el rango, ventas solo en dos de ellos (100 + 300): 400 / 3.
        var grilla = GrillaDeVentasPorHora.Construir(
            Lunes, Lunes.AddDays(14), [new SumaDeHora(1, 10, 400m, 2)]);

        var celda = Celda(grilla, 1, 10);
        Assert.Equal(400m, celda.Total);
        Assert.Equal(2, celda.Tickets);
        Assert.Equal(133.33m, celda.PromedioTotal);
        Assert.Equal(0.67m, celda.PromedioTickets);
    }

    [Fact]
    public void ElPromedioRedondeaElPuntoMedioHaciaAfuera()
    {
        // Dos lunes: 1,25 / 2 = 0,625 => 0,63 (AwayFromZero), y simétrico en negativo.
        var grilla = GrillaDeVentasPorHora.Construir(
            Lunes, Lunes.AddDays(7), [new SumaDeHora(1, 9, 1.25m, 1), new SumaDeHora(1, 11, -1.25m, 1)]);

        Assert.Equal(0.63m, Celda(grilla, 1, 9).PromedioTotal);
        Assert.Equal(-0.63m, Celda(grilla, 1, 11).PromedioTotal);
    }

    [Fact]
    public void UnDiaSinOcurrenciasPromediaCeroAunqueLleguenSumas()
    {
        var grilla = GrillaDeVentasPorHora.Construir(Lunes, Lunes, [new SumaDeHora(2, 8, 50m, 1)]);

        var celda = Celda(grilla, 2, 8);
        Assert.Equal(50m, celda.Total);
        Assert.Equal(0m, celda.PromedioTotal);
        Assert.Equal(0m, celda.PromedioTickets);
    }

    [Fact]
    public void LosTotalesGeneralesSumanTodasLasSumas()
    {
        var grilla = GrillaDeVentasPorHora.Construir(
            Lunes, Lunes.AddDays(6),
            [new SumaDeHora(1, 10, 100m, 1), new SumaDeHora(3, 23, 250m, 2), new SumaDeHora(1, 10, -20m, 0)]);

        Assert.Equal(330m, grilla.Total);
        Assert.Equal(3, grilla.Tickets);
        Assert.Equal(80m, Celda(grilla, 1, 10).Total);
    }

    [Theory]
    [InlineData(0, 10)]
    [InlineData(8, 10)]
    [InlineData(1, -1)]
    [InlineData(1, 24)]
    public void RechazaUnaSumaFueraDeLaGrilla(int dia, int hora)
    {
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            GrillaDeVentasPorHora.Construir(Lunes, Lunes, [new SumaDeHora(dia, hora, 1m, 1)]));
    }
}
