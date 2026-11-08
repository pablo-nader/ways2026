using Ways.Application.Pagos;
using Ways.Domain.Common;
using Ways.Domain.Pagos;

namespace Ways.Application.Tests.Pagos;

public class ReglaDeLineasDePagoDePersonalTests
{
    private static PlanDeLineasDePago Plan(params decimal[] importes) =>
        ReglaDeLineasDePago.Planificar(importes.Select(i => (LineaDePago?)new LineaDePago(1, i, null)).ToList());

    [Fact]
    public void LasLineasQueSumanElImporteDelPagoPasan()
    {
        ReglaDeLineasDePagoDePersonal.ExigirMismoTotal(Plan(700m, 300.25m), 1000.25m);
    }

    [Theory]
    [InlineData(1000.24)]
    [InlineData(1000.26)]
    public void UnTotalDistintoSeRechazaYElMensajeDiceCuantoTieneQueSumar(double importeDelPago)
    {
        var error = Assert.Throws<ErrorDominio>(
            () => ReglaDeLineasDePagoDePersonal.ExigirMismoTotal(Plan(700m, 300.25m), (decimal)importeDelPago));

        Assert.Equal("importe_distinto_del_pago", error.Codigo);
        Assert.Equal(400, error.EstadoHttp);
        Assert.Contains(((decimal)importeDelPago).ToString("0.00", System.Globalization.CultureInfo.InvariantCulture), error.Message);
        Assert.Contains("1000.25", error.Message);
    }
}
