using Ways.Domain.Common;
using Ways.Domain.Compras;

namespace Ways.Domain.Tests.Compras;

/// <summary>
/// Desglose de IVA por alícuota de <see cref="CalculadorDeCompra"/> y override del IVA impreso —
/// pura, sin base de datos.
/// </summary>
public class CalculadorDeCompraAlicuotasTests
{
    private static readonly IReadOnlyDictionary<int, (decimal? MargenGrupo, decimal? MargenProveedor)> SinMargenes =
        new Dictionary<int, (decimal?, decimal?)>();

    private static LineaDeCompra Linea(int orden, decimal total, int idAlicuota, decimal porcentaje) =>
        new(orden, 1, "item de prueba", 1m, null, null, total, 0m, idAlicuota, porcentaje, true);

    [Fact]
    public void ElDesgloseAgrupaElNetoPorAlicuotaYCalculaElIvaDeCadaUna()
    {
        var resultado = CalculadorDeCompra.Calcular(
            [
                Linea(1, 100m, idAlicuota: 1, porcentaje: 21m),
                Linea(2, 200m, idAlicuota: 1, porcentaje: 21m),
                Linea(3, 100m, idAlicuota: 2, porcentaje: 10.5m)
            ],
            discriminaIva: true, SinMargenes);

        Assert.Equal(2, resultado.Alicuotas.Count);
        Assert.Equal(new AlicuotaCalculada(1, 21m, 300m, 63m), resultado.Alicuotas[0]);
        Assert.Equal(new AlicuotaCalculada(2, 10.5m, 100m, 10.5m), resultado.Alicuotas[1]);
        Assert.Equal(73.5m, resultado.IvaTotal);
        Assert.Equal(473.5m, resultado.Total);
    }

    [Fact]
    public void ElIvaSeRedondeaUnaSolaVezPorAlicuotaYNoPorLinea()
    {
        // Tres líneas de 0.50 al 21%: por línea serían 0.11 x 3 = 0.33; sobre el neto de la
        // alícuota (1.50) es 0.315 -> 0.32, que es lo que imprime una factura.
        var resultado = CalculadorDeCompra.Calcular(
            [
                Linea(1, 0.5m, idAlicuota: 1, porcentaje: 21m),
                Linea(2, 0.5m, idAlicuota: 1, porcentaje: 21m),
                Linea(3, 0.5m, idAlicuota: 1, porcentaje: 21m)
            ],
            discriminaIva: true, SinMargenes);

        Assert.Equal(0.32m, resultado.Alicuotas.Single().Iva);
        Assert.Equal(0.32m, resultado.IvaTotal);
    }

    [Fact]
    public void ExentoYNoGravadoAparecenEnElDesgloseConIvaCero()
    {
        var resultado = CalculadorDeCompra.Calcular(
            [
                Linea(1, 100m, idAlicuota: 1, porcentaje: 21m),
                Linea(2, 50m, idAlicuota: 5, porcentaje: 0m),
                Linea(3, 30m, idAlicuota: 6, porcentaje: 0m)
            ],
            discriminaIva: true, SinMargenes);

        Assert.Equal(new AlicuotaCalculada(5, 0m, 50m, 0m), resultado.Alicuotas[1]);
        Assert.Equal(new AlicuotaCalculada(6, 0m, 30m, 0m), resultado.Alicuotas[2]);
        Assert.Equal(21m, resultado.IvaTotal);
        Assert.Equal(201m, resultado.Total);
    }

    [Fact]
    public void SinDiscriminarIvaNoHayDesgloseNiIvaTotal()
    {
        var resultado = CalculadorDeCompra.Calcular(
            [Linea(1, 100m, idAlicuota: 1, porcentaje: 21m)], discriminaIva: false, SinMargenes);

        Assert.Empty(resultado.Alicuotas);
        Assert.Null(resultado.IvaTotal);
        Assert.Equal(100m, resultado.Total);
    }

    [Fact]
    public void ElIvaImpresoReemplazaAlCalculadoYRecomputaElIvaTotalYElTotal()
    {
        var resultado = CalculadorDeCompra.Calcular(
            [
                Linea(1, 300m, idAlicuota: 1, porcentaje: 21m),
                Linea(2, 100m, idAlicuota: 2, porcentaje: 10.5m)
            ],
            discriminaIva: true, SinMargenes, new Dictionary<int, decimal> { [1] = 63.5m });

        // Solo la alícuota 1 trae override: la 2 conserva el calculado.
        Assert.Equal(63.5m, resultado.Alicuotas[0].Iva);
        Assert.Equal(10.5m, resultado.Alicuotas[1].Iva);
        Assert.Equal(74m, resultado.IvaTotal);
        Assert.Equal(474m, resultado.Total);
    }

    [Theory]
    [InlineData(64.0)]
    [InlineData(62.0)]
    public void ElIvaImpresoADiferenciaExactaDeLaToleranciaSeAcepta(double impreso)
    {
        var resultado = CalculadorDeCompra.Calcular(
            [Linea(1, 300m, idAlicuota: 1, porcentaje: 21m)],
            discriminaIva: true, SinMargenes, new Dictionary<int, decimal> { [1] = (decimal)impreso });

        Assert.Equal((decimal)impreso, resultado.IvaTotal);
    }

    [Theory]
    [InlineData(64.01)]
    [InlineData(61.99)]
    [InlineData(-1.0)]
    public void ElIvaImpresoQueSePasaDeLaToleranciaOEsNegativoSeRechaza(double impreso)
    {
        var error = Assert.Throws<ErrorDominio>(() => CalculadorDeCompra.Calcular(
            [Linea(1, 300m, idAlicuota: 1, porcentaje: 21m)],
            discriminaIva: true, SinMargenes, new Dictionary<int, decimal> { [1] = (decimal)impreso }));

        Assert.Equal("iva_impreso_fuera_de_tolerancia", error.Codigo);
        Assert.Equal(400, error.EstadoHttp);
    }

    [Fact]
    public void ElIvaImpresoNegativoSeRechazaAunqueEsteDentroDeLaTolerancia()
    {
        // IVA calculado 0.21: -0.50 dista 0.71, dentro de la tolerancia, pero un IVA negativo no existe.
        var error = Assert.Throws<ErrorDominio>(() => CalculadorDeCompra.Calcular(
            [Linea(1, 1m, idAlicuota: 1, porcentaje: 21m)],
            discriminaIva: true, SinMargenes, new Dictionary<int, decimal> { [1] = -0.5m }));

        Assert.Equal("iva_impreso_fuera_de_tolerancia", error.Codigo);
    }

    [Fact]
    public void ElIvaImpresoDeUnaAlicuotaQueNoEstaEnLasLineasSeRechaza()
    {
        var error = Assert.Throws<ErrorDominio>(() => CalculadorDeCompra.Calcular(
            [Linea(1, 300m, idAlicuota: 1, porcentaje: 21m)],
            discriminaIva: true, SinMargenes, new Dictionary<int, decimal> { [9] = 5m }));

        Assert.Equal("iva_impreso_alicuota_desconocida", error.Codigo);
        Assert.Equal(400, error.EstadoHttp);
    }

    [Fact]
    public void ElIvaImpresoEnUnComprobanteQueNoDiscriminaSeRechazaEnVezDeDescartarse()
    {
        var error = Assert.Throws<ErrorDominio>(() => CalculadorDeCompra.Calcular(
            [Linea(1, 300m, idAlicuota: 1, porcentaje: 21m)],
            discriminaIva: false, SinMargenes, new Dictionary<int, decimal> { [1] = 63m }));

        Assert.Equal("iva_impreso_sin_discriminar", error.Codigo);
        Assert.Equal(400, error.EstadoHttp);
    }

    [Fact]
    public void UnIvaImpresoVacioEnUnComprobanteQueNoDiscriminaEsInocuo()
    {
        var resultado = CalculadorDeCompra.Calcular(
            [Linea(1, 100m, idAlicuota: 1, porcentaje: 21m)],
            discriminaIva: false, SinMargenes, new Dictionary<int, decimal>());

        Assert.Null(resultado.IvaTotal);
    }

    [Fact]
    public void ElCostoEfectivoDeUnArticuloNoCambiaPorElOverrideDeRedondeo()
    {
        var sinOverride = CalculadorDeCompra.Calcular(
            [Linea(1, 300m, idAlicuota: 1, porcentaje: 21m)], discriminaIva: true, SinMargenes);
        var conOverride = CalculadorDeCompra.Calcular(
            [Linea(1, 300m, idAlicuota: 1, porcentaje: 21m)],
            discriminaIva: true, SinMargenes, new Dictionary<int, decimal> { [1] = 63.5m });

        Assert.Equal(sinOverride.Items[0].CostoEfectivo, conOverride.Items[0].CostoEfectivo);
    }
}
