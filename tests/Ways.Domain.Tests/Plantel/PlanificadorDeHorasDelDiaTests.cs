using Ways.Domain.Common;
using Ways.Domain.Plantel;

namespace Ways.Domain.Tests.Plantel;

public class PlanificadorDeHorasDelDiaTests
{
    private static PlanificadorDeHorasDelDia.AsientoExistente Capa(decimal horas, decimal valorHora) => new(horas, valorHora);

    private static IReadOnlyList<PlanificadorDeHorasDelDia.AsientoPlanificado> Planificar(
        decimal deseadas, decimal vigente, params PlanificadorDeHorasDelDia.AsientoExistente[] existentes) =>
        PlanificadorDeHorasDelDia.Planificar(existentes, deseadas, vigente);

    [Fact]
    public void UnDiaSinAsientosCargaElTotalAlValorVigente()
    {
        var plan = Planificar(8m, 1000m);

        var asiento = Assert.Single(plan);
        Assert.Equal(8m, asiento.Horas);
        Assert.Equal(1000m, asiento.ValorHora);
        Assert.Equal(8000m, asiento.Importe);
    }

    [Fact]
    public void ElMismoTotalNoEscribeNada()
    {
        Assert.Empty(Planificar(8m, 1200m, Capa(5m, 1000m), Capa(3m, 1100m)));
    }

    [Fact]
    public void SubirElTotalEscribeSoloLaDiferenciaAlValorVigente()
    {
        var plan = Planificar(10m, 1500m, Capa(8m, 1000m));

        var asiento = Assert.Single(plan);
        Assert.Equal(2m, asiento.Horas);
        Assert.Equal(1500m, asiento.ValorHora);
        Assert.Equal(3000m, asiento.Importe);
    }

    [Fact]
    public void BajarConsumeLaUltimaCapaPrimeroConSuPropioValorHora()
    {
        var plan = Planificar(6m, 2000m, Capa(5m, 1000m), Capa(3m, 1100m));

        Assert.Collection(plan, a =>
        {
            Assert.Equal(-2m, a.Horas);
            Assert.Equal(1100m, a.ValorHora);
            Assert.Equal(-2200m, a.Importe);
        });
    }

    [Fact]
    public void BajarMasQueLaUltimaCapaPasaALaAnterior()
    {
        var plan = Planificar(3m, 2000m, Capa(5m, 1000m), Capa(3m, 1100m));

        Assert.Collection(
            plan,
            a =>
            {
                Assert.Equal(-3m, a.Horas);
                Assert.Equal(1100m, a.ValorHora);
                Assert.Equal(-3300m, a.Importe);
            },
            a =>
            {
                Assert.Equal(-2m, a.Horas);
                Assert.Equal(1000m, a.ValorHora);
                Assert.Equal(-2000m, a.Importe);
            });
    }

    [Fact]
    public void VolverACeroRevierteTodasLasCapasYElImporteNetoEsCero()
    {
        var existentes = new[] { Capa(5m, 1000.50m), Capa(3m, 1100m) };
        var plan = PlanificadorDeHorasDelDia.Planificar(existentes, 0m, 2000m);

        Assert.Equal(2, plan.Count);
        var acreditado = existentes.Sum(e => Math.Round(e.Horas * e.ValorHora, 2, MidpointRounding.AwayFromZero));
        Assert.Equal(-acreditado, plan.Sum(a => a.Importe));
    }

    [Fact]
    public void UnaBajaPreviaSeAplicaALaUltimaCapaAntesDeCalcularElRestante()
    {
        // 5 h a 1000 y 3 h a 1100; una baja previa de 2 h (a 1100) deja 5 h a 1000 y 1 h a 1100.
        var plan = Planificar(
            4m, 2000m, Capa(5m, 1000m), Capa(3m, 1100m), Capa(-2m, 1100m));

        Assert.Collection(
            plan,
            a =>
            {
                Assert.Equal(-1m, a.Horas);
                Assert.Equal(1100m, a.ValorHora);
            },
            a =>
            {
                Assert.Equal(-1m, a.Horas);
                Assert.Equal(1000m, a.ValorHora);
            });
    }

    [Fact]
    public void BajarYVolverASubirAcreditaLaCapaNuevaAlValorVigente()
    {
        // 8 h a 1000, bajada a 5 h (-3 a 1000) y subida a 7 h: la capa nueva usa el valor vigente.
        var plan = Planificar(7m, 1300m, Capa(8m, 1000m), Capa(-3m, 1000m));

        var asiento = Assert.Single(plan);
        Assert.Equal(2m, asiento.Horas);
        Assert.Equal(1300m, asiento.ValorHora);
        Assert.Equal(2600m, asiento.Importe);
    }

    [Fact]
    public void LaCapaNuevaSeConsumeAntesQueLaViejaEnEdicionesRepetidas()
    {
        // 4 h a 1000, +4 h a 2000 (total 8): bajar a 6 sale de la capa a 2000; bajar a 3 consume 2 de 2000 y 1 de 1000.
        var primera = Planificar(6m, 3000m, Capa(4m, 1000m), Capa(4m, 2000m));
        Assert.Collection(primera, a =>
        {
            Assert.Equal(-2m, a.Horas);
            Assert.Equal(2000m, a.ValorHora);
        });

        var segunda = Planificar(3m, 3000m, Capa(4m, 1000m), Capa(4m, 2000m), Capa(-2m, 2000m));
        Assert.Collection(
            segunda,
            a =>
            {
                Assert.Equal(-2m, a.Horas);
                Assert.Equal(2000m, a.ValorHora);
            },
            a =>
            {
                Assert.Equal(-1m, a.Horas);
                Assert.Equal(1000m, a.ValorHora);
            });
    }

    [Fact]
    public void UnDiaHeredadoPorEncimaDeVeinticuatroSeReduceAlTope()
    {
        var plan = Planificar(24m, 1000m, Capa(20m, 1000m), Capa(10m, 1200m));

        var asiento = Assert.Single(plan);
        Assert.Equal(-6m, asiento.Horas);
        Assert.Equal(1200m, asiento.ValorHora);
        Assert.Equal(-7200m, asiento.Importe);
    }

    [Fact]
    public void ElValorHoraCeroGeneraImporteCero()
    {
        var plan = Planificar(2m, 1000m, Capa(4m, 0m));

        var asiento = Assert.Single(plan);
        Assert.Equal(-2m, asiento.Horas);
        Assert.Equal(0m, asiento.Importe);
    }

    [Fact]
    public void ElImporteSeRedondeaAlejandoseDeCeroTambienEnLasBajas()
    {
        var plan = Planificar(0m, 1000m, Capa(0.01m, 0.50m));

        var asiento = Assert.Single(plan);
        Assert.Equal(-0.01m, asiento.Importe);
    }

    [Theory]
    [InlineData(-0.01)]
    [InlineData(24.01)]
    [InlineData(8.123)]
    public void UnTotalFueraDeRangoOConMasDeDosDecimalesSeRechaza(double horas)
    {
        var error = Assert.Throws<ErrorDominio>(() => Planificar((decimal)horas, 1000m));

        Assert.Equal("horas_invalidas", error.Codigo);
    }

    [Fact]
    public void ElTopeDeVeinticuatroSeAcepta()
    {
        Assert.Equal(24m, Assert.Single(Planificar(24m, 10m)).Horas);
    }

    [Fact]
    public void UnLibroConBajasQueNoTienenCapaSeRechazaComoInconsistente()
    {
        Assert.Throws<InvalidOperationException>(() => Planificar(1m, 1000m, Capa(2m, 1000m), Capa(-3m, 1000m)));
    }
}
