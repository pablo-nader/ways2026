using Ways.Domain.Caja;
using Ways.Domain.Catalogos;

namespace Ways.Domain.Tests.Caja;

/// <summary>
/// Subtotales del cierre por clasificación del medio, puros y sin base de datos. IDs sintéticos:
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

    private static (ResultadoDeCierrePorRetiro Resultado, TotalesDeVentasPorComportamiento Totales) Cerrar(
        InsumosDeArqueo insumos, int idAncla, IReadOnlyDictionary<int, ComportamientoMedioPago> catalogo)
    {
        var arqueables = CalculadorDeArqueo.Calcular(insumos, idAncla);
        var resultado = CalculadorDeCierrePorRetiro.Calcular(insumos, idAncla, arqueables);
        return (resultado, ClasificadorDeVentasPorMedio.Clasificar(resultado.VentasPorMedio, catalogo, idAncla));
    }

    [Fact]
    public void DigitalesMasEfectivoSumanElTotalConMediosMixtosVueltoCeroVentasYMedioDeBaja()
    {
        var insumos = Insumos(
            Actividad(IdEfectivo, ComportamientoMedioPago.Efectivo, 1000m, vueltos: 50m),
            Actividad(IdTarjeta, ComportamientoMedioPago.Electronico, 500m),
            Actividad(IdTransferencia, ComportamientoMedioPago.Electronico, 250.25m),
            Actividad(IdCuentaCorriente, ComportamientoMedioPago.CuentaCorriente, 300m),
            Actividad(IdSinVentas, ComportamientoMedioPago.Electronico, 0m),
            Actividad(IdDeBaja, ComportamientoMedioPago.Electronico, 80m));

        var (resultado, totales) = Cerrar(insumos, IdEfectivo, Catalogo);

        Assert.Equal(950m, totales.TotalEfectivo);
        Assert.Equal(830.25m, totales.TotalDigitales);
        Assert.Equal(1780.25m, resultado.TotalVentas);
        Assert.Equal(resultado.TotalVentas, totales.TotalDigitales + totales.TotalEfectivo);
        Assert.Equal(resultado.VentasEnEfectivoNetas, totales.TotalEfectivo);
        Assert.Equal(300m, resultado.VentasACuentaCorriente);
        Assert.Equal(resultado.VentasPorMedio.Count, totales.Lineas.Count);
        Assert.DoesNotContain(totales.Lineas, l => l.IdMedioPago == IdCuentaCorriente);
        Assert.Equal(
            ComportamientoMedioPago.Electronico, totales.Lineas.Single(l => l.IdMedioPago == IdSinVentas).Comportamiento);
    }

    [Fact]
    public void LaVentaACuentaCorrienteCobradaEnEfectivoSeInformaAparteSinDuplicarElTotal()
    {
        var insumos = Insumos(
            Actividad(IdEfectivo, ComportamientoMedioPago.Efectivo, 1000m),
            Actividad(IdCuentaCorriente, ComportamientoMedioPago.CuentaCorriente, 1000m));

        var (resultado, totales) = Cerrar(insumos, IdEfectivo, Catalogo);

        Assert.Equal(1000m, totales.TotalEfectivo);
        Assert.Equal(0m, totales.TotalDigitales);
        Assert.Equal(1000m, resultado.TotalVentas);
        Assert.Equal(1000m, resultado.VentasACuentaCorriente);
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
        Assert.Equal(
            ComportamientoMedioPago.Efectivo, totales.Lineas.Single(l => l.IdMedioPago == IdEfectivo).Comportamiento);
        Assert.Equal(
            ComportamientoMedioPago.Electronico, totales.Lineas.Single(l => l.IdMedioPago == IdTarjeta).Comportamiento);
    }

    /// <summary>Con el ancla hoy en cuenta corriente y sin pagos, no tiene línea: el vuelto de otro
    /// medio no se netea en ninguna línea, <c>TotalEfectivo</c> es 0 y <c>VentasEnEfectivoNetas</c>
    /// es negativo. El total sigue siendo digitales + efectivo.</summary>
    [Fact]
    public void ConElAnclaSinLineaElEfectivoEsCeroAunqueVentasEnEfectivoNetasSeaNegativo()
    {
        var catalogo = new Dictionary<int, ComportamientoMedioPago>(Catalogo)
        {
            [IdEfectivo] = ComportamientoMedioPago.CuentaCorriente
        };
        var insumos = Insumos(
            new ActividadDeMedio(IdEfectivo, ComportamientoMedioPago.CuentaCorriente, 0m, 0m, 0m, false),
            Actividad(IdTarjeta, ComportamientoMedioPago.Electronico, 500m, vueltos: 20m));

        var (resultado, totales) = Cerrar(insumos, IdEfectivo, catalogo);

        Assert.Equal(-20m, resultado.VentasEnEfectivoNetas);
        Assert.Equal(0m, totales.TotalEfectivo);
        Assert.Equal(500m, totales.TotalDigitales);
        Assert.Equal(resultado.TotalVentas, totales.TotalDigitales + totales.TotalEfectivo);
    }

    /// <summary>Fija que los medios no efectivo siguen el catálogo ACTUAL: el mismo medio con las
    /// mismas ventas pasa de digital a cuenta corriente informada aparte si se reclasifica.</summary>
    [Fact]
    public void ReclasificarUnMedioElectronicoACuentaCorrienteDespuesDeLasVentasMueveSuImporteFueraDeLosDigitales()
    {
        InsumosDeArqueo Con(ComportamientoMedioPago comportamiento) => Insumos(
            Actividad(IdEfectivo, ComportamientoMedioPago.Efectivo, 100m),
            Actividad(IdTarjeta, comportamiento, 500m));
        IReadOnlyDictionary<int, ComportamientoMedioPago> CatalogoCon(ComportamientoMedioPago comportamiento) =>
            new Dictionary<int, ComportamientoMedioPago>(Catalogo) { [IdTarjeta] = comportamiento };

        var (antes, totalesAntes) = Cerrar(
            Con(ComportamientoMedioPago.Electronico), IdEfectivo, CatalogoCon(ComportamientoMedioPago.Electronico));
        var (despues, totalesDespues) = Cerrar(
            Con(ComportamientoMedioPago.CuentaCorriente), IdEfectivo, CatalogoCon(ComportamientoMedioPago.CuentaCorriente));

        Assert.Equal(500m, totalesAntes.TotalDigitales);
        Assert.Equal(0m, antes.VentasACuentaCorriente);
        Assert.Equal(0m, totalesDespues.TotalDigitales);
        Assert.Equal(500m, despues.VentasACuentaCorriente);
        Assert.Equal(100m, despues.TotalVentas);
    }

    [Fact]
    public void UnaLineaDeUnMedioDeCuentaCorrienteNoEsClasificable()
    {
        var ventas = new[] { new LineaDeVentaPorMedio(IdCuentaCorriente, 123m) };

        Assert.Throws<InvalidOperationException>(
            () => ClasificadorDeVentasPorMedio.Clasificar(ventas, Catalogo, IdEfectivo));
    }

    [Fact]
    public void SinVentasLosSubtotalesSonCero()
    {
        var totales = ClasificadorDeVentasPorMedio.Clasificar([], Catalogo, IdEfectivo);

        Assert.Empty(totales.Lineas);
        Assert.Equal(0m, totales.TotalDigitales + totales.TotalEfectivo);
    }
}
