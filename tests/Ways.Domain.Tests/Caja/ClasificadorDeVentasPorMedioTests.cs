using Ways.Domain.Caja;
using Ways.Domain.Catalogos;

namespace Ways.Domain.Tests.Caja;

/// <summary>
/// Totales del cierre por clasificación del medio, puros y sin base de datos. IDs sintéticos:
/// 1 = efectivo (ancla), 2 = tarjeta, 3 = transferencia, 4 = cuenta corriente, 5 = medio sin
/// ventas, 6 = medio dado de baja que conserva ventas.
/// </summary>
public class ClasificadorDeVentasPorMedioTests
{
    private const int IdEfectivo = 1;
    private const int IdTarjeta = 2;
    private const int IdTransferencia = 3;
    private const int IdCuentaCorriente = 4;
    private const int IdSinVentas = 5;
    private const int IdDeBaja = 6;

    private static readonly IReadOnlyDictionary<int, ComportamientoMedioPago> Catalogo =
        new Dictionary<int, ComportamientoMedioPago>
        {
            [IdEfectivo] = ComportamientoMedioPago.Efectivo,
            [IdTarjeta] = ComportamientoMedioPago.Electronico,
            [IdTransferencia] = ComportamientoMedioPago.Electronico,
            [IdCuentaCorriente] = ComportamientoMedioPago.CuentaCorriente,
            [IdSinVentas] = ComportamientoMedioPago.Electronico,
            [IdDeBaja] = ComportamientoMedioPago.Electronico
        };

    private static InsumosDeArqueo Insumos(params ActividadDeMedio[] actividad) =>
        new(FondoInicial: 0m, Refuerzos: 0m, Retiros: 0m, Actividad: actividad);

    private static ActividadDeMedio Actividad(
        int id, ComportamientoMedioPago comportamiento, decimal pagos, decimal vueltos = 0m) =>
        new(id, comportamiento, pagos, vueltos, 0m, true);

    [Fact]
    public void LosTresSubtotalesSumanElTotalDeVentasConMediosMixtosVueltoCeroVentasYMedioDeBaja()
    {
        var insumos = Insumos(
            Actividad(IdEfectivo, ComportamientoMedioPago.Efectivo, 1000m, vueltos: 50m),
            Actividad(IdTarjeta, ComportamientoMedioPago.Electronico, 500m),
            Actividad(IdTransferencia, ComportamientoMedioPago.Electronico, 250.25m),
            Actividad(IdCuentaCorriente, ComportamientoMedioPago.CuentaCorriente, 300m),
            Actividad(IdSinVentas, ComportamientoMedioPago.Electronico, 0m),
            Actividad(IdDeBaja, ComportamientoMedioPago.Electronico, 80m));
        var arqueables = CalculadorDeArqueo.Calcular(insumos, IdEfectivo);
        var resultado = CalculadorDeCierrePorRetiro.Calcular(insumos, IdEfectivo, arqueables);

        var totales = ClasificadorDeVentasPorMedio.Clasificar(resultado.VentasPorMedio, Catalogo, IdEfectivo);

        Assert.Equal(950m, totales.TotalEfectivo);
        Assert.Equal(830.25m, totales.TotalDigitales);
        Assert.Equal(300m, totales.TotalCuentaCorriente);
        Assert.Equal(
            resultado.TotalVentas,
            totales.TotalDigitales + totales.TotalEfectivo + totales.TotalCuentaCorriente);
        Assert.Equal(resultado.VentasEnEfectivoNetas, totales.TotalEfectivo);
        Assert.Equal(resultado.VentasPorMedio.Count, totales.Lineas.Count);
        Assert.Equal(
            ComportamientoMedioPago.Electronico, totales.Lineas.Single(l => l.IdMedioPago == IdSinVentas).Comportamiento);
    }

    [Fact]
    public void ElEfectivoSigueAlAnclaFijadaAunqueElMedioYaNoSeaEfectivoEnElCatalogo()
    {
        var catalogo = new Dictionary<int, ComportamientoMedioPago>(Catalogo)
        {
            [IdEfectivo] = ComportamientoMedioPago.Electronico,
            [IdTarjeta] = ComportamientoMedioPago.Efectivo
        };
        var ventas = new[]
        {
            new LineaDeVentaPorMedio(IdEfectivo, 700m),
            new LineaDeVentaPorMedio(IdTarjeta, 200m)
        };

        var totales = ClasificadorDeVentasPorMedio.Clasificar(ventas, catalogo, IdEfectivo);

        Assert.Equal(700m, totales.TotalEfectivo);
        Assert.Equal(200m, totales.TotalDigitales);
        Assert.Equal(0m, totales.TotalCuentaCorriente);
        Assert.Equal(
            ComportamientoMedioPago.Efectivo, totales.Lineas.Single(l => l.IdMedioPago == IdEfectivo).Comportamiento);
        Assert.Equal(
            ComportamientoMedioPago.Electronico, totales.Lineas.Single(l => l.IdMedioPago == IdTarjeta).Comportamiento);
    }

    [Fact]
    public void UnMedioCuentaCorrienteNuncaEntraEnDigitalesNiEnEfectivo()
    {
        var ventas = new[] { new LineaDeVentaPorMedio(IdCuentaCorriente, 123m) };

        var totales = ClasificadorDeVentasPorMedio.Clasificar(ventas, Catalogo, IdEfectivo);

        Assert.Equal(0m, totales.TotalDigitales);
        Assert.Equal(0m, totales.TotalEfectivo);
        Assert.Equal(123m, totales.TotalCuentaCorriente);
    }

    [Fact]
    public void SinVentasLosTresSubtotalesSonCero()
    {
        var totales = ClasificadorDeVentasPorMedio.Clasificar([], Catalogo, IdEfectivo);

        Assert.Empty(totales.Lineas);
        Assert.Equal(0m, totales.TotalDigitales + totales.TotalEfectivo + totales.TotalCuentaCorriente);
    }
}
