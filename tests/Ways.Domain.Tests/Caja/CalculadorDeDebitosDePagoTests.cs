using Ways.Domain.Caja;

namespace Ways.Domain.Tests.Caja;

/// <summary>
/// Diferencia entre lo que un pago debería tener debitado en cada (cuenta, medio) y lo que ya debitó. Pura, sin base de datos.
/// IDs sintéticos: cuentas 10 y 20; medios 1 y 2.
/// </summary>
public class CalculadorDeDebitosDePagoTests
{
    private static ImporteDeCuentaYMedio Importe(int cuenta, int medio, decimal importe) => new(cuenta, medio, importe);

    [Fact]
    public void UnAltaSinDebitosPreviosEgresaElTotalDeCadaCuentaYMedio()
    {
        var debitos = CalculadorDeDebitosDePago.Calcular(
            [Importe(10, 1, 300m), Importe(10, 1, 200m), Importe(20, 2, 80m)], []);

        Assert.Equal([new DebitoDePago(10, 1, 0m, 500m), new DebitoDePago(20, 2, 0m, 80m)], debitos);
    }

    [Fact]
    public void SubirElImporteEgresaSoloLaDiferencia()
    {
        var debitos = CalculadorDeDebitosDePago.Calcular([Importe(10, 1, 750m)], [Importe(10, 1, 500m)]);

        Assert.Equal([new DebitoDePago(10, 1, 0m, 250m)], debitos);
    }

    [Fact]
    public void BajarElImporteIngresaLaDiferencia()
    {
        var debitos = CalculadorDeDebitosDePago.Calcular([Importe(10, 1, 400m)], [Importe(10, 1, 500m)]);

        Assert.Equal([new DebitoDePago(10, 1, 100m, 0m)], debitos);
    }

    [Fact]
    public void LoYaDebitadoSeSumaEntreVariasFilasDeLaMismaCuentaYMedio()
    {
        var debitos = CalculadorDeDebitosDePago.Calcular(
            [Importe(10, 1, 500m)], [Importe(10, 1, 500m), Importe(10, 1, 250m), Importe(10, 1, -250m)]);

        Assert.Empty(debitos);
    }

    [Fact]
    public void CambiarDeMedioYDeCuentaDevuelveEnLaViejaYDebitaEnLaNueva()
    {
        var debitos = CalculadorDeDebitosDePago.Calcular([Importe(20, 2, 500m)], [Importe(10, 1, 500m)]);

        Assert.Equal([new DebitoDePago(10, 1, 500m, 0m), new DebitoDePago(20, 2, 0m, 500m)], debitos);
    }

    [Fact]
    public void CambiarDeMedioDentroDeLaMismaCuentaEscribeUnaFilaPorMedio()
    {
        var debitos = CalculadorDeDebitosDePago.Calcular([Importe(10, 2, 500m)], [Importe(10, 1, 500m)]);

        Assert.Equal([new DebitoDePago(10, 1, 500m, 0m), new DebitoDePago(10, 2, 0m, 500m)], debitos);
    }

    [Fact]
    public void UnaBajaSinObjetivoDevuelveTodoLoDebitado()
    {
        var debitos = CalculadorDeDebitosDePago.Calcular([], [Importe(10, 1, 500m), Importe(20, 2, 80m)]);

        Assert.Equal([new DebitoDePago(10, 1, 500m, 0m), new DebitoDePago(20, 2, 80m, 0m)], debitos);
    }

    [Fact]
    public void SinCambiosNoEscribeNada()
    {
        Assert.Empty(CalculadorDeDebitosDePago.Calcular([Importe(10, 1, 500m)], [Importe(10, 1, 500m)]));
        Assert.Empty(CalculadorDeDebitosDePago.Calcular([], []));
    }

    [Fact]
    public void ElResultadoSaleOrdenadoPorCuentaYMedio()
    {
        var debitos = CalculadorDeDebitosDePago.Calcular(
            [Importe(20, 1, 1m), Importe(10, 2, 2m), Importe(10, 1, 3m)], []);

        Assert.Equal([(10, 1), (10, 2), (20, 1)], debitos.Select(d => (d.IdCuenta, d.IdMedioPago)));
    }
}
