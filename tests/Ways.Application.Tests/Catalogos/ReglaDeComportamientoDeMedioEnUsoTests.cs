using Ways.Application.Catalogos;
using Ways.Domain.Catalogos;
using Ways.Domain.Common;

namespace Ways.Application.Tests.Catalogos;

/// <summary>Un medio que figura en cobros no entra ni sale de cuenta corriente; los demás cambios pasan.</summary>
public class ReglaDeComportamientoDeMedioEnUsoTests
{
    [Theory]
    [InlineData(ComportamientoMedioPago.CuentaCorriente, ComportamientoMedioPago.Efectivo)]
    [InlineData(ComportamientoMedioPago.CuentaCorriente, ComportamientoMedioPago.Electronico)]
    [InlineData(ComportamientoMedioPago.Efectivo, ComportamientoMedioPago.CuentaCorriente)]
    [InlineData(ComportamientoMedioPago.Electronico, ComportamientoMedioPago.CuentaCorriente)]
    public void EntrarOSalirDeCuentaCorrienteConCobrosSeRechazaCon409(
        ComportamientoMedioPago anterior, ComportamientoMedioPago nuevo)
    {
        var error = Assert.Throws<ErrorDominio>(() => ReglaDeComportamientoDeMedioEnUso.Exigir(anterior, nuevo, true));

        Assert.Equal("medio_pago_comportamiento_en_uso", error.Codigo);
        Assert.Equal(409, error.EstadoHttp);
    }

    [Theory]
    [InlineData(ComportamientoMedioPago.Efectivo, ComportamientoMedioPago.Electronico)]
    [InlineData(ComportamientoMedioPago.Electronico, ComportamientoMedioPago.Efectivo)]
    [InlineData(ComportamientoMedioPago.CuentaCorriente, ComportamientoMedioPago.CuentaCorriente)]
    public void UnCambioQueNoTocaLaCuentaCorrientePasaAunqueHayaCobros(
        ComportamientoMedioPago anterior, ComportamientoMedioPago nuevo)
    {
        ReglaDeComportamientoDeMedioEnUso.Exigir(anterior, nuevo, true);
    }

    [Fact]
    public void SinCobrosElMedioPuedeEntrarYSalirDeCuentaCorriente()
    {
        ReglaDeComportamientoDeMedioEnUso.Exigir(ComportamientoMedioPago.CuentaCorriente, ComportamientoMedioPago.Efectivo, false);
        ReglaDeComportamientoDeMedioEnUso.Exigir(ComportamientoMedioPago.Efectivo, ComportamientoMedioPago.CuentaCorriente, false);
    }
}
