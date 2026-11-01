using Ways.Domain.Caja;
using Ways.Domain.Catalogos;

namespace Ways.Domain.Tests.Caja;

/// <summary>Reparto de lo facturado por cubo, puro y sin base de datos. IDs: 1 = efectivo (ancla), 2 = tarjeta,
/// 3 = cuenta corriente, 4 = otro medio de efectivo que no es el ancla.</summary>
public class ProrrateoDeFacturadoPorMedioTests
{
    private const int Efectivo = 1;
    private const int Tarjeta = 2;
    private const int CuentaCorriente = 3;
    private const int OtroEfectivo = 4;

    private static readonly IReadOnlyDictionary<int, ComportamientoMedioPago> Catalogo =
        new Dictionary<int, ComportamientoMedioPago>
        {
            [Efectivo] = ComportamientoMedioPago.Efectivo,
            [Tarjeta] = ComportamientoMedioPago.Electronico,
            [CuentaCorriente] = ComportamientoMedioPago.CuentaCorriente,
            [OtroEfectivo] = ComportamientoMedioPago.Efectivo,
        };

    private static CobroNetoDeVenta C(int idMedioPago, decimal neto) => new(idMedioPago, neto);

    private static FacturadoPorCubo Repartir(decimal total, params CobroNetoDeVenta[] cobros) =>
        ProrrateoDeFacturadoPorMedio.Repartir(total, cobros, Catalogo, Efectivo);

    [Fact]
    public void UnaVentaPagadaExactamenteSeReparteComoSeCobro()
    {
        var r = Repartir(1000m, C(Efectivo, 400m), C(Tarjeta, 350m), C(CuentaCorriente, 250m));

        Assert.Equal(new FacturadoPorCubo(400m, 350m, 250m), r);
    }

    [Fact]
    public void LaVentaCobradaDeMenosDentroDeLaToleranciaSigueSumandoSuTotal()
    {
        var r = Repartir(1000m, C(Efectivo, 495m), C(Tarjeta, 495m));

        Assert.Equal(1000m, r.Total);
        Assert.Equal(new FacturadoPorCubo(500m, 500m, 0m), r);
    }

    [Fact]
    public void LaVentaSobrepagadaSinVueltoSigueSumandoSuTotal()
    {
        var r = Repartir(1000m, C(Efectivo, 1000m), C(Tarjeta, 1000m));

        Assert.Equal(new FacturadoPorCubo(500m, 500m, 0m), r);
    }

    [Fact]
    public void ElMedioDeEfectivoQueNoEsElAnclaCuentaComoDigital()
    {
        var r = Repartir(100m, C(OtroEfectivo, 100m));

        Assert.Equal(new FacturadoPorCubo(0m, 100m, 0m), r);
    }

    [Fact]
    public void ElRestoDelRedondeoVaAlCuboDeMayorParticipacion()
    {
        // 100 * 1/3 = 33,33 en cada cubo; sobra 0,01 y el empate lo gana efectivo.
        var empate = Repartir(100m, C(Efectivo, 10m), C(Tarjeta, 10m), C(CuentaCorriente, 10m));
        Assert.Equal(new FacturadoPorCubo(33.34m, 33.33m, 33.33m), empate);

        // 10 * 1/3 y 2/3 -> 3,33 + 6,67 = 10,00; con mayoria digital el resto va a digitales.
        var mayoriaDigital = Repartir(100m, C(Efectivo, 1m), C(Tarjeta, 1m), C(CuentaCorriente, 1m), C(Tarjeta, 1m));
        Assert.Equal(100m, mayoriaDigital.Total);
        Assert.Equal(new FacturadoPorCubo(25m, 50m, 25m), mayoriaDigital);

        var conResto = Repartir(10m, C(Efectivo, 1m), C(Tarjeta, 2m));
        Assert.Equal(new FacturadoPorCubo(3.33m, 6.67m, 0m), conResto);
        Assert.Equal(10m, conResto.Total);
    }

    [Fact]
    public void UnaNotaDeCreditoReflejaLasProporcionesDeLaVentaOriginalConSuSigno()
    {
        var r = Repartir(-300m, C(Efectivo, 100m), C(Tarjeta, 200m));

        Assert.Equal(new FacturadoPorCubo(-100m, -200m, 0m), r);
    }

    [Fact]
    public void UnaNotaDeCreditoNegativaConRestoMantieneLaSumaExacta()
    {
        var r = Repartir(-10m, C(Efectivo, 1m), C(Tarjeta, 2m));

        Assert.Equal(-10m, r.Total);
        Assert.Equal(new FacturadoPorCubo(-3.33m, -6.67m, 0m), r);
    }

    [Fact]
    public void SinCobrosTodoVaAEfectivo()
    {
        Assert.Equal(new FacturadoPorCubo(-80m, 0m, 0m), Repartir(-80m));
        Assert.Equal(new FacturadoPorCubo(50m, 0m, 0m), Repartir(50m, C(Tarjeta, 0m)));
    }

    [Fact]
    public void ElVueltoYaNeteadoPuedeDejarElEfectivoEnCero()
    {
        var r = Repartir(100m, C(Efectivo, 0m), C(Tarjeta, 100m));

        Assert.Equal(new FacturadoPorCubo(0m, 100m, 0m), r);
    }
}
