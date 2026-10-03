using Ways.Domain.Caja;
using Ways.Domain.Catalogos;

namespace Ways.Domain.Tests.Caja;

public class RecalculadorDeArqueoTests
{
    private const int Efectivo = 1;
    private const int Tarjeta = 2;
    private const int Transferencia = 3;
    private const int CuentaCorriente = 4;

    private static readonly IReadOnlyList<ActividadDeMedio> Catalogo =
    [
        new(Efectivo, ComportamientoMedioPago.Efectivo, 0m, 0m, 0m, false),
        new(Tarjeta, ComportamientoMedioPago.Electronico, 0m, 0m, 0m, false),
        new(Transferencia, ComportamientoMedioPago.Electronico, 0m, 0m, 0m, false),
        new(CuentaCorriente, ComportamientoMedioPago.CuentaCorriente, 0m, 0m, 0m, false)
    ];

    [Fact]
    public void UnaFilaCuyoEsperadoCambiaGuardaElOriginalDelCierre()
    {
        var cambios = RecalculadorDeArqueo.Planificar(
            [new LineaDeArqueo(Efectivo, 900m)], Catalogo, [new ArqueoPersistido(Efectivo, 800m, null)]);

        Assert.Equal([new CambioDeArqueo(Efectivo, 900m, 800m, EsNueva: false)], cambios);
    }

    [Fact]
    public void UnSegundoRecalculoNuncaPisaElOriginal()
    {
        var cambios = RecalculadorDeArqueo.Planificar(
            [new LineaDeArqueo(Efectivo, 950m)], Catalogo, [new ArqueoPersistido(Efectivo, 900m, 800m)]);

        Assert.Equal([new CambioDeArqueo(Efectivo, 950m, 800m, EsNueva: false)], cambios);
    }

    [Fact]
    public void UnaFilaSinCambioNoApareceEnElPlan()
    {
        var cambios = RecalculadorDeArqueo.Planificar(
            [new LineaDeArqueo(Efectivo, 800m), new LineaDeArqueo(Tarjeta, 300m)], Catalogo,
            [new ArqueoPersistido(Efectivo, 800m, null), new ArqueoPersistido(Tarjeta, 250m, null)]);

        Assert.Equal([new CambioDeArqueo(Tarjeta, 300m, 250m, EsNueva: false)], cambios);
    }

    [Fact]
    public void UnMedioArqueableSinFilaNaceConOriginalCero()
    {
        var cambios = RecalculadorDeArqueo.Planificar(
            [new LineaDeArqueo(Efectivo, 800m), new LineaDeArqueo(Transferencia, -40m)], Catalogo,
            [new ArqueoPersistido(Efectivo, 800m, null)]);

        Assert.Equal([new CambioDeArqueo(Transferencia, -40m, 0m, EsNueva: true)], cambios);
    }

    [Fact]
    public void UnMedioQueSeQuedaSinActividadConservaSuFilaConEsperadoCero()
    {
        var cambios = RecalculadorDeArqueo.Planificar(
            [new LineaDeArqueo(Efectivo, 800m)], Catalogo,
            [new ArqueoPersistido(Efectivo, 800m, null), new ArqueoPersistido(Tarjeta, -60m, null)]);

        Assert.Equal([new CambioDeArqueo(Tarjeta, 0m, -60m, EsNueva: false)], cambios);
    }

    [Fact]
    public void UnaFilaDeUnMedioQueYaNoEsVisibleOEsCuentaCorrienteQuedaComoEsta()
    {
        var cambios = RecalculadorDeArqueo.Planificar(
            [new LineaDeArqueo(Efectivo, 800m)], Catalogo,
            [
                new ArqueoPersistido(Efectivo, 800m, null),
                new ArqueoPersistido(CuentaCorriente, 70m, null),
                new ArqueoPersistido(99, 55m, null)
            ]);

        Assert.Empty(cambios);
    }

    [Fact]
    public void ElPlanSaleOrdenadoPorMedio()
    {
        var cambios = RecalculadorDeArqueo.Planificar(
            [new LineaDeArqueo(Transferencia, 10m), new LineaDeArqueo(Efectivo, 700m)], Catalogo,
            [new ArqueoPersistido(Efectivo, 800m, null)]);

        Assert.Equal([Efectivo, Transferencia], cambios.Select(c => c.IdMedioPago));
    }
}
