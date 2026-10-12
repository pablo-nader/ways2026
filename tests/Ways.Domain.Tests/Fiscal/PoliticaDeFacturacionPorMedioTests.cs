using Ways.Domain.Fiscal;

namespace Ways.Domain.Tests.Fiscal;

public class PoliticaDeFacturacionPorMedioTests
{
    private const int Efectivo = 1;
    private const int Transferencia = 2;
    private const int Tarjeta = 3;

    private static PagoDeTicket Pago(int medio, decimal importe, decimal porcentaje) =>
        new(medio, importe, porcentaje);

    private static Dictionary<int, decimal> Saldos(params (int Medio, decimal Saldo)[] saldos) =>
        saldos.ToDictionary(s => s.Medio, s => s.Saldo);

    private static decimal AporteDe(DecisionDeFacturacion decision, int medio) =>
        decision.Aportes.Single(a => a.IdMedioPago == medio).Aporte;

    // Aplica los aportes al saldo, como lo hará el acumulador persistido.
    private static void Aplicar(Dictionary<int, decimal> saldos, IEnumerable<AporteDeSaldo> aportes)
    {
        foreach (var a in aportes)
        {
            saldos[a.IdMedioPago] = saldos.GetValueOrDefault(a.IdMedioPago) + a.Aporte;
        }
    }

    [Fact]
    public void LaFacturaForzadaFacturaAunConTodosLosMediosAlCeroPorCiento()
    {
        var decision = PoliticaDeFacturacionPorMedio.Decidir(
            [Pago(Efectivo, 100m, 0m), Pago(Tarjeta, 40m, 0m)], Saldos(), forzada: true);

        Assert.True(decision.Facturar);
        Assert.Equal(MotivoDeFacturacion.Forzada, decision.Motivo);
    }

    [Fact]
    public void LaFacturaForzadaDebitaElSaldoDeCadaMedio()
    {
        var decision = PoliticaDeFacturacionPorMedio.Decidir(
            [Pago(Efectivo, 100m, 10m), Pago(Tarjeta, 40m, 0m)],
            Saldos((Efectivo, 25m)), forzada: true);

        Assert.Equal(10m - 100m, AporteDe(decision, Efectivo));
        Assert.Equal(-40m, AporteDe(decision, Tarjeta));
    }

    [Fact]
    public void LaForzadaTienePrioridadSobreElMotivoDelMedioAlCien()
    {
        var decision = PoliticaDeFacturacionPorMedio.Decidir(
            [Pago(Transferencia, 100m, 100m)], Saldos(), forzada: true);

        Assert.Equal(MotivoDeFacturacion.Forzada, decision.Motivo);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-500)]
    [InlineData(500)]
    public void UnMedioAlCienPorCientoSiempreFacturaSinImportarElSaldo(int saldoInicial)
    {
        var decision = PoliticaDeFacturacionPorMedio.Decidir(
            [Pago(Transferencia, 100m, 100m)], Saldos((Transferencia, saldoInicial)), forzada: false);

        Assert.True(decision.Facturar);
        Assert.Equal(MotivoDeFacturacion.MedioExigeFactura, decision.Motivo);
    }

    [Fact]
    public void UnTicketSoloDeMedioAlCienPorCientoDejaElSaldoSinCambios()
    {
        var decision = PoliticaDeFacturacionPorMedio.Decidir(
            [Pago(Transferencia, 250m, 100m)], Saldos((Transferencia, -30m)), forzada: false);

        Assert.Equal(0m, AporteDe(decision, Transferencia));
    }

    [Fact]
    public void UnMedioAlCienPorCientoFacturaDentroDeUnTicketMixto()
    {
        var decision = PoliticaDeFacturacionPorMedio.Decidir(
            [Pago(Transferencia, 100m, 100m), Pago(Efectivo, 50m, 10m)],
            Saldos((Transferencia, -80m), (Efectivo, -400m)), forzada: false);

        Assert.True(decision.Facturar);
        Assert.Equal(MotivoDeFacturacion.MedioExigeFactura, decision.Motivo);
    }

    [Fact]
    public void LosMediosAlCeroPorCientoNuncaFacturanYNoMuevenElSaldo()
    {
        var saldos = Saldos((Efectivo, 0m));

        for (var i = 0; i < 500; i++)
        {
            var decision = PoliticaDeFacturacionPorMedio.Decidir(
                [Pago(Efectivo, 100m + i, 0m)], saldos, forzada: false);

            Assert.False(decision.Facturar);
            Assert.Equal(MotivoDeFacturacion.NoRequerida, decision.Motivo);
            Assert.Equal(0m, AporteDe(decision, Efectivo));
            Aplicar(saldos, decision.Aportes);
        }

        Assert.Equal(0m, saldos[Efectivo]);
    }

    [Fact]
    public void UnTicketMixtoDeTransferenciaAlCienYEfectivoAlDiezSeFacturaEntero()
    {
        var decision = PoliticaDeFacturacionPorMedio.Decidir(
            [Pago(Transferencia, 100m, 100m), Pago(Efectivo, 200m, 10m)], Saldos(), forzada: false);

        Assert.True(decision.Facturar);
        // Efectivo devenga el 10 % de 200 (20) y se factura completo (200).
        Assert.Equal(20m - 200m, AporteDe(decision, Efectivo));
        Assert.Equal(0m, AporteDe(decision, Transferencia));
    }

    [Fact]
    public void SiSoloUnMedioParcialAlcanzaSuCupoSeFacturaEnteroYSeDebitanAmbos()
    {
        // Efectivo: 45 + 10 = 55 > 50 (cupo alcanzado). Tarjeta: 0 + 5 = 5, lejos de su cupo.
        var decision = PoliticaDeFacturacionPorMedio.Decidir(
            [Pago(Efectivo, 100m, 10m), Pago(Tarjeta, 100m, 5m)],
            Saldos((Efectivo, 45m)), forzada: false);

        Assert.True(decision.Facturar);
        Assert.Equal(MotivoDeFacturacion.CupoAlcanzado, decision.Motivo);
        Assert.Equal(10m - 100m, AporteDe(decision, Efectivo));
        Assert.Equal(5m - 100m, AporteDe(decision, Tarjeta));
    }

    [Fact]
    public void SinCupoAlcanzadoNoFacturaYAcumulaElDevengado()
    {
        var decision = PoliticaDeFacturacionPorMedio.Decidir(
            [Pago(Efectivo, 100m, 10m), Pago(Tarjeta, 100m, 5m), Pago(Transferencia, 80m, 0m)],
            Saldos(), forzada: false);

        Assert.False(decision.Facturar);
        Assert.Equal(MotivoDeFacturacion.NoRequerida, decision.Motivo);
        Assert.Equal(10m, AporteDe(decision, Efectivo));
        Assert.Equal(5m, AporteDe(decision, Tarjeta));
        Assert.Equal(0m, AporteDe(decision, Transferencia));
    }

    [Fact]
    public void ExactamenteLaMitadDelImporteNoFactura()
    {
        // 40 + 10 = 50 = importe / 2.
        var decision = PoliticaDeFacturacionPorMedio.Decidir(
            [Pago(Efectivo, 100m, 10m)], Saldos((Efectivo, 40m)), forzada: false);

        Assert.False(decision.Facturar);
        Assert.Equal(10m, AporteDe(decision, Efectivo));
    }

    [Fact]
    public void ApenasPorEncimaDeLaMitadDelImporteFactura()
    {
        var decision = PoliticaDeFacturacionPorMedio.Decidir(
            [Pago(Efectivo, 100m, 10m)], Saldos((Efectivo, 40.01m)), forzada: false);

        Assert.True(decision.Facturar);
        Assert.Equal(MotivoDeFacturacion.CupoAlcanzado, decision.Motivo);
    }

    [Fact]
    public void UnMedioAlCeroPorCientoNoDisparaPorUnSaldoAltoQueYaTenia()
    {
        var decision = PoliticaDeFacturacionPorMedio.Decidir(
            [Pago(Efectivo, 100m, 0m)], Saldos((Efectivo, 900m)), forzada: false);

        Assert.False(decision.Facturar);
    }

    [Fact]
    public void ElMismoMedioDosVecesEnUnTicketSeAgrega()
    {
        // Agregado: importe 100, devengo 10, saldo 40,5 -> 50,5 > 50; un único aporte por medio.
        var decision = PoliticaDeFacturacionPorMedio.Decidir(
            [Pago(Efectivo, 60m, 10m), Pago(Efectivo, 40m, 10m)],
            Saldos((Efectivo, 40.5m)), forzada: false);

        Assert.True(decision.Facturar);
        var aporte = Assert.Single(decision.Aportes);
        Assert.Equal(10m - 100m, aporte.Aporte);
    }

    [Fact]
    public void UnMedioSinSaldoPrevioCuentaComoCero()
    {
        var decision = PoliticaDeFacturacionPorMedio.Decidir(
            [Pago(Efectivo, 100m, 10m)], Saldos(), forzada: false);

        Assert.Equal(10m, AporteDe(decision, Efectivo));
    }

    [Fact]
    public void MilTicketsIgualesDeEfectivoAlDiezPorCientoFacturanElDiezPorCiento()
    {
        var saldos = Saldos();
        decimal vendido = 0m, facturado = 0m, maxAbsSaldo = 0m;

        for (var i = 0; i < 1000; i++)
        {
            var decision = PoliticaDeFacturacionPorMedio.Decidir(
                [Pago(Efectivo, 100m, 10m)], saldos, forzada: false);
            vendido += 100m;
            if (decision.Facturar)
            {
                facturado += 100m;
            }

            Aplicar(saldos, decision.Aportes);
            maxAbsSaldo = Math.Max(maxAbsSaldo, Math.Abs(saldos[Efectivo]));
        }

        // Tolerancia: el desvío (facturado - 10 % de lo vendido) es el saldo, acotado por el importe (100).
        Assert.InRange(facturado - vendido * 0.10m, -100m, 100m);
        Assert.Equal(10_000m, facturado);
        Assert.True(maxAbsSaldo <= 100m, $"El saldo máximo fue {maxAbsSaldo}.");
    }

    [Fact]
    public void UnaSecuenciaVariadaConvergeAlPorcentajeYElSaldoQuedaAcotado()
    {
        var azar = new Random(20261012);
        var saldos = Saldos();
        decimal vendido = 0m, facturado = 0m, mayorImporte = 0m, maxAbsSaldo = 0m;

        for (var i = 0; i < 2000; i++)
        {
            var importe = azar.Next(500, 50001) / 100m;
            mayorImporte = Math.Max(mayorImporte, importe);
            var decision = PoliticaDeFacturacionPorMedio.Decidir(
                [Pago(Efectivo, importe, 25m)], saldos, forzada: false);
            vendido += importe;
            if (decision.Facturar)
            {
                facturado += importe;
            }

            Aplicar(saldos, decision.Aportes);
            maxAbsSaldo = Math.Max(maxAbsSaldo, Math.Abs(saldos[Efectivo]));
        }

        // Identidad: saldo = 25 % de lo vendido - lo facturado.
        Assert.Equal(vendido * 0.25m - facturado, saldos[Efectivo]);
        // Tolerancia: el saldo (el desvío) nunca supera el mayor importe de la secuencia.
        Assert.True(maxAbsSaldo <= mayorImporte, $"Saldo máximo {maxAbsSaldo} > mayor importe {mayorImporte}.");
        Assert.InRange(facturado / vendido, 0.25m - mayorImporte / vendido, 0.25m + mayorImporte / vendido);
    }

    [Fact]
    public void AplicarLosAportesYLuegoSuReversionDevuelveLosSaldosOriginales()
    {
        var saldos = Saldos((Efectivo, 37.5m), (Tarjeta, -12m), (Transferencia, 0m));
        var original = new Dictionary<int, decimal>(saldos);
        var decision = PoliticaDeFacturacionPorMedio.Decidir(
            [Pago(Efectivo, 100m, 10m), Pago(Tarjeta, 80m, 5m), Pago(Transferencia, 20m, 100m)],
            saldos, forzada: false);

        Aplicar(saldos, decision.Aportes);
        Assert.NotEqual(original, saldos);
        Aplicar(saldos, PoliticaDeFacturacionPorMedio.Revertir(decision.Aportes));

        Assert.Equal(original, saldos);
    }

    [Fact]
    public void RevertirNiegaCadaAporteYConservaElMedio()
    {
        var revertido = PoliticaDeFacturacionPorMedio.Revertir(
            [new AporteDeSaldo(Efectivo, -90m), new AporteDeSaldo(Tarjeta, 5m)]);

        Assert.Equal([new AporteDeSaldo(Efectivo, 90m), new AporteDeSaldo(Tarjeta, -5m)], revertido);
    }

    [Fact]
    public void LosAportesSalenOrdenadosPorMedio()
    {
        var decision = PoliticaDeFacturacionPorMedio.Decidir(
            [Pago(Tarjeta, 10m, 0m), Pago(Efectivo, 10m, 0m), Pago(Transferencia, 10m, 0m)],
            Saldos(), forzada: false);

        Assert.Equal([Efectivo, Transferencia, Tarjeta], decision.Aportes.Select(a => a.IdMedioPago));
    }

    [Fact]
    public void UnaListaDePagosVaciaSeRechaza()
    {
        Assert.Throws<ArgumentException>(() =>
            PoliticaDeFacturacionPorMedio.Decidir([], Saldos(), forzada: false));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public void UnImporteNoPositivoSeRechaza(int importe)
    {
        Assert.Throws<ArgumentException>(() =>
            PoliticaDeFacturacionPorMedio.Decidir(
                [Pago(Efectivo, importe, 10m)], Saldos(), forzada: false));
    }

    [Theory]
    [InlineData(-0.01)]
    [InlineData(100.01)]
    public void UnPorcentajeFueraDeRangoSeRechaza(double porcentaje)
    {
        Assert.Throws<ArgumentException>(() =>
            PoliticaDeFacturacionPorMedio.Decidir(
                [Pago(Efectivo, 100m, (decimal)porcentaje)], Saldos(), forzada: false));
    }

    [Fact]
    public void UnMismoMedioConDosPorcentajesDistintosSeRechaza()
    {
        Assert.Throws<ArgumentException>(() =>
            PoliticaDeFacturacionPorMedio.Decidir(
                [Pago(Efectivo, 50m, 10m), Pago(Efectivo, 50m, 20m)], Saldos(), forzada: false));
    }
}
