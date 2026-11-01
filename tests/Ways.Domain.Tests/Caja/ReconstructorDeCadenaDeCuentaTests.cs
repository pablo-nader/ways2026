using Ways.Domain.Caja;

namespace Ways.Domain.Tests.Caja;

/// <summary>Recalculo de la cadena de una cuenta: puro, sin base de datos.</summary>
public class ReconstructorDeCadenaDeCuentaTests
{
    [Fact]
    public void UnTramoVacioNoDevuelveSaldos()
    {
        Assert.Empty(ReconstructorDeCadenaDeCuenta.Recalcular(500m, []));
    }

    [Fact]
    public void LaPrimeraFilaParteDelSaldoAnteriorYCadaUnaDelFinalDeLaAnterior()
    {
        var saldos = ReconstructorDeCadenaDeCuenta.Recalcular(
            1000m, [new(100m, 0m), new(0m, 30m), new(0m, 0.5m), new(7.25m, 0m)]);

        Assert.Equal(
            [new SaldosDeFila(1000m, 1100m), new(1100m, 1070m), new(1070m, 1069.5m), new(1069.5m, 1076.75m)], saldos);
    }

    [Fact]
    public void UnAjusteConservaSuDiferenciaYCambiaElSaldoSobreElQueSeAplica()
    {
        // El ajuste original llevó el saldo de 100 a 90 (egreso 10). Con 5000 acreditados antes, parte de 5100.
        var saldos = ReconstructorDeCadenaDeCuenta.Recalcular(100m + 5000m, [new(0m, 10m)]);

        Assert.Equal([new SaldosDeFila(5100m, 5090m)], saldos);
    }

    [Fact]
    public void ElSaldoPuedeCruzarCeroSinCambiarLasDiferencias()
    {
        var saldos = ReconstructorDeCadenaDeCuenta.Recalcular(10m, [new(0m, 25m), new(5m, 0m)]);

        Assert.Equal([new SaldosDeFila(10m, -15m), new(-15m, -10m)], saldos);
    }
}
