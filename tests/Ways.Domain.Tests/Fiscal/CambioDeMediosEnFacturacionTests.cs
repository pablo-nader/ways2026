using Ways.Domain.Fiscal;

namespace Ways.Domain.Tests.Fiscal;

public class CambioDeMediosEnFacturacionTests
{
    private const int Efectivo = 1;
    private const int Tarjeta = 2;
    private const int Transferencia = 3;

    [Fact]
    public void SinFacturarMueveElDevengoYLoVendidoDelMedioViejoAlNuevo()
    {
        PagoDeTicket[] anteriores = [new(Efectivo, 1000m, 10m)];
        PagoDeTicket[] nuevos = [new(Tarjeta, 1000m, 30m)];

        var cambio = CambioDeMediosEnFacturacion.Planificar(
            SituacionFiscalDelTicket.NoFacturado, PoliticaDeFacturacionPorMedio.Aportes(anteriores, false), anteriores, nuevos);

        Assert.Equal([new AporteDeSaldo(Tarjeta, 300m)], cambio.AportesNuevos);
        Assert.Equal(
            [new VariacionDeAcumulado(Efectivo, -100m, -1000m, 0m), new VariacionDeAcumulado(Tarjeta, 300m, 1000m, 0m)],
            cambio.Variaciones);
    }

    [Fact]
    public void FacturadoDeshaceLosAportesGuardadosYAplicaLosDelMedioNuevoConDebito()
    {
        // Decisión guardada con los aportes del momento de la venta (un porcentaje distinto del actual del medio viejo).
        AporteDeSaldo[] guardados = [new(Tarjeta, 1000m * 50m / 100m - 1000m)];
        PagoDeTicket[] anteriores = [new(Tarjeta, 1000m, 80m)];
        PagoDeTicket[] nuevos = [new(Transferencia, 600m, 100m), new(Efectivo, 400m, 0m)];

        var cambio = CambioDeMediosEnFacturacion.Planificar(SituacionFiscalDelTicket.Facturado, guardados, anteriores, nuevos);

        Assert.Equal([new AporteDeSaldo(Efectivo, -400m), new AporteDeSaldo(Transferencia, 0m)], cambio.AportesNuevos);
        Assert.Equal(
            [
                new VariacionDeAcumulado(Efectivo, -400m, 400m, 400m),
                new VariacionDeAcumulado(Tarjeta, 500m, -1000m, -1000m),
                new VariacionDeAcumulado(Transferencia, 0m, 600m, 600m)
            ],
            cambio.Variaciones);
    }

    [Fact]
    public void RechazadoMueveElSaldoQueDejoElRechazoYNoTocaLoFacturado()
    {
        AporteDeSaldo[] guardados = [new(Tarjeta, -700m)];
        PagoDeTicket[] anteriores = [new(Tarjeta, 1000m, 30m)];
        PagoDeTicket[] nuevos = [new(Efectivo, 1000m, 10m)];

        var cambio = CambioDeMediosEnFacturacion.Planificar(SituacionFiscalDelTicket.Rechazado, guardados, anteriores, nuevos);

        Assert.Equal([new AporteDeSaldo(Efectivo, -900m)], cambio.AportesNuevos);
        Assert.Equal(
            [new VariacionDeAcumulado(Efectivo, 100m, 1000m, 0m), new VariacionDeAcumulado(Tarjeta, -300m, -1000m, 0m)],
            cambio.Variaciones);
    }

    [Fact]
    public void UnMedioQueQuedaIgualNoSeMueve()
    {
        PagoDeTicket[] anteriores = [new(Efectivo, 500m, 10m), new(Tarjeta, 500m, 30m)];
        PagoDeTicket[] nuevos = [new(Efectivo, 500m, 10m), new(Transferencia, 500m, 30m)];

        var cambio = CambioDeMediosEnFacturacion.Planificar(
            SituacionFiscalDelTicket.NoFacturado, PoliticaDeFacturacionPorMedio.Aportes(anteriores, false), anteriores, nuevos);

        Assert.Equal([Tarjeta, Transferencia], cambio.Variaciones.Select(v => v.IdMedioPago));
    }

    [Fact]
    public void AplicarElCambioYDespuesRevertirLosAportesNuevosDejaElAcumuladoEnCero()
    {
        AporteDeSaldo[] guardados = PoliticaDeFacturacionPorMedio.Aportes([new(Tarjeta, 1000m, 40m)], true).ToArray();
        PagoDeTicket[] anteriores = [new(Tarjeta, 1000m, 40m)];
        PagoDeTicket[] nuevos = [new(Efectivo, 1000m, 0m)];

        var cambio = CambioDeMediosEnFacturacion.Planificar(SituacionFiscalDelTicket.Facturado, guardados, anteriores, nuevos);

        // Saldo por medio: lo aplicado por la decisión, más el cambio, menos la cancelación con los aportes nuevos.
        var saldo = guardados.ToDictionary(a => a.IdMedioPago, a => a.Aporte);
        foreach (var variacion in cambio.Variaciones)
        {
            saldo[variacion.IdMedioPago] = saldo.GetValueOrDefault(variacion.IdMedioPago) + variacion.Saldo;
        }

        foreach (var inverso in PoliticaDeFacturacionPorMedio.Revertir(cambio.AportesNuevos))
        {
            saldo[inverso.IdMedioPago] = saldo.GetValueOrDefault(inverso.IdMedioPago) + inverso.Aporte;
        }

        Assert.All(saldo.Values, s => Assert.Equal(0m, s));
    }
}
