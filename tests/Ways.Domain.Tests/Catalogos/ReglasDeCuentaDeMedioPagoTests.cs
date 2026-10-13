using Ways.Domain.Catalogos;
using Ways.Domain.Common;

namespace Ways.Domain.Tests.Catalogos;

public class ReglasDeCuentaDeMedioPagoTests
{
    private static readonly CuentaDeMedioPago Guardada = new(7, 2m, 1m, 0.5m, 0m);

    private static CuentaEnviadaDeMedioPago Nada => new(false, null, null, null, null, null);

    private static ErrorDominio Falla(Func<object?> accion) => Assert.Throws<ErrorDominio>(() => accion());

    [Fact]
    public void ElMedioElectronicoSinNadaEnviadoConservaLoGuardado()
    {
        var resuelta = ReglasDeCuentaDeMedioPago.Resolver(ComportamientoMedioPago.Electronico, Guardada, Nada);

        Assert.Equal(Guardada, resuelta);
    }

    [Fact]
    public void LaCuentaEnviadaEnNullLaQuitaYLosPorcentajesEnCeroSeAplican()
    {
        var enviada = new CuentaEnviadaDeMedioPago(true, null, 0m, 0m, 0m, 0m);

        var resuelta = ReglasDeCuentaDeMedioPago.Resolver(ComportamientoMedioPago.Electronico, Guardada, enviada);

        Assert.Equal(CuentaDeMedioPago.Vacia, resuelta);
    }

    [Fact]
    public void UnPorcentajeEnviadoReemplazaSoloEseCampo()
    {
        var enviada = Nada with { Iva = 10.125m };

        var resuelta = ReglasDeCuentaDeMedioPago.Resolver(ComportamientoMedioPago.Electronico, Guardada, enviada);

        Assert.Equal(new CuentaDeMedioPago(7, 2m, 1m, 10.125m, 0m), resuelta);
    }

    [Fact]
    public void LaCuentaEnviadaConUnIdLaReemplaza()
    {
        var resuelta = ReglasDeCuentaDeMedioPago.Resolver(
            ComportamientoMedioPago.Electronico, Guardada, Nada with { IdCuentaPresente = true, IdCuenta = 9 });

        Assert.Equal(Guardada with { IdCuentaTesoreria = 9 }, resuelta);
    }

    [Theory]
    [InlineData("-0.001")]
    [InlineData("100.001")]
    [InlineData("0.0001")]
    [InlineData("12.3456")]
    public void UnPorcentajeFueraDeRangoOConMasDeTresDecimalesSeRechaza(string crudo)
    {
        var valor = decimal.Parse(crudo, System.Globalization.CultureInfo.InvariantCulture);

        foreach (var enviada in new[]
                 {
                     Nada with { Comision = valor },
                     Nada with { Iibb = valor },
                     Nada with { Iva = valor },
                     Nada with { Ganancias = valor }
                 })
        {
            var error = Falla(() => ReglasDeCuentaDeMedioPago.Resolver(
                ComportamientoMedioPago.Electronico, CuentaDeMedioPago.Vacia, enviada));

            Assert.Equal("porcentaje_cuenta_invalido", error.Codigo);
            Assert.Equal(400, error.EstadoHttp);
        }
    }

    [Theory]
    [InlineData("0")]
    [InlineData("100")]
    [InlineData("0.001")]
    [InlineData("99.999")]
    public void LosExtremosYLosTresDecimalesSeAdmiten(string crudo)
    {
        var valor = decimal.Parse(crudo, System.Globalization.CultureInfo.InvariantCulture);

        var resuelta = ReglasDeCuentaDeMedioPago.Resolver(
            ComportamientoMedioPago.Electronico, CuentaDeMedioPago.Vacia, Nada with { Comision = valor });

        Assert.Equal(valor, resuelta.Comision);
    }

    [Fact]
    public void LaSumaDeLosCuatroPuedeSerExactamenteCien()
    {
        var enviada = new CuentaEnviadaDeMedioPago(false, null, 40m, 30m, 20m, 10m);

        var resuelta = ReglasDeCuentaDeMedioPago.Resolver(
            ComportamientoMedioPago.Electronico, CuentaDeMedioPago.Vacia, enviada);

        Assert.Equal(100m, resuelta.Comision + resuelta.RetencionIibb + resuelta.RetencionIva + resuelta.RetencionGanancias);
    }

    [Fact]
    public void LaSumaQueSuperaCienSeRechazaIncluyendoLoGuardado()
    {
        // Lo guardado en IIBB e IVA suma 1,5: con 99 enviado en comisión la suma resultante es 100,5.
        var error = Falla(() => ReglasDeCuentaDeMedioPago.Resolver(
            ComportamientoMedioPago.Electronico, Guardada with { Comision = 0m }, Nada with { Comision = 99m }));

        Assert.Equal("porcentajes_cuenta_suma_invalida", error.Codigo);
        Assert.Equal(400, error.EstadoHttp);
    }

    [Theory]
    [InlineData(ComportamientoMedioPago.Efectivo)]
    [InlineData(ComportamientoMedioPago.CuentaCorriente)]
    public void UnMedioNoElectronicoRechazaUnaCuentaEnviada(ComportamientoMedioPago comportamiento)
    {
        var error = Falla(() => ReglasDeCuentaDeMedioPago.Resolver(
            comportamiento, CuentaDeMedioPago.Vacia, Nada with { IdCuentaPresente = true, IdCuenta = 3 }));

        Assert.Equal("cuenta_solo_electronico", error.Codigo);
        Assert.Equal(400, error.EstadoHttp);
    }

    [Theory]
    [InlineData(ComportamientoMedioPago.Efectivo)]
    [InlineData(ComportamientoMedioPago.CuentaCorriente)]
    public void UnMedioNoElectronicoRechazaCualquierPorcentajeEnviadoDistintoDeCero(ComportamientoMedioPago comportamiento)
    {
        foreach (var enviada in new[]
                 {
                     Nada with { Comision = 0.001m },
                     Nada with { Iibb = 1m },
                     Nada with { Iva = 1m },
                     Nada with { Ganancias = 1m }
                 })
        {
            var error = Falla(() => ReglasDeCuentaDeMedioPago.Resolver(comportamiento, CuentaDeMedioPago.Vacia, enviada));

            Assert.Equal("cuenta_solo_electronico", error.Codigo);
        }
    }

    [Fact]
    public void UnMedioNoElectronicoAceptaCuentaNullYPorcentajesEnCeroEnviadosAExpresion()
    {
        var enviada = new CuentaEnviadaDeMedioPago(true, null, 0m, 0m, 0m, 0m);

        var resuelta = ReglasDeCuentaDeMedioPago.Resolver(ComportamientoMedioPago.Efectivo, Guardada, enviada);

        Assert.Equal(CuentaDeMedioPago.Vacia, resuelta);
    }

    [Theory]
    [InlineData(ComportamientoMedioPago.Efectivo)]
    [InlineData(ComportamientoMedioPago.CuentaCorriente)]
    public void UnMedioQuePasaAUnComportamientoNoElectronicoSinEnviarNadaLimpiaLoGuardado(
        ComportamientoMedioPago comportamiento)
    {
        var resuelta = ReglasDeCuentaDeMedioPago.Resolver(comportamiento, Guardada, Nada);

        Assert.Equal(CuentaDeMedioPago.Vacia, resuelta);
    }

    [Fact]
    public void UnPorcentajeInvalidoSeRechazaAunqueElMedioNoSeaElectronico()
    {
        var error = Falla(() => ReglasDeCuentaDeMedioPago.Resolver(
            ComportamientoMedioPago.Efectivo, CuentaDeMedioPago.Vacia, Nada with { Comision = 150m }));

        Assert.Equal("porcentaje_cuenta_invalido", error.Codigo);
    }

    [Fact]
    public void LaCuentaDeLaMismaEmpresaActivaSeAsigna()
    {
        ReglasDeCuentaDeMedioPago.ExigirCuentaAsignable(
            idEmpresaDelMedio: 5, new CuentaAsignable(5, Activa: true), asignacionNueva: true, idUnicaEmpresaDelTenant: null);
    }

    [Fact]
    public void LaCuentaDeOtraEmpresaQueLaDelMedioSeRechaza()
    {
        var error = Falla(() =>
        {
            ReglasDeCuentaDeMedioPago.ExigirCuentaAsignable(
                5, new CuentaAsignable(6, Activa: true), asignacionNueva: false, idUnicaEmpresaDelTenant: 5);
            return null;
        });

        Assert.Equal("cuenta_tesoreria_de_otra_empresa", error.Codigo);
        Assert.Equal(400, error.EstadoHttp);
    }

    [Fact]
    public void UnMedioDeTodoElTenantSoloTomaCuentaSiElTenantTieneUnaSolaEmpresa()
    {
        var error = Falla(() =>
        {
            ReglasDeCuentaDeMedioPago.ExigirCuentaAsignable(
                null, new CuentaAsignable(5, Activa: true), asignacionNueva: true, idUnicaEmpresaDelTenant: null);
            return null;
        });

        Assert.Equal("cuenta_tesoreria_requiere_empresa", error.Codigo);
        Assert.Equal(400, error.EstadoHttp);
    }

    [Fact]
    public void UnMedioDeTodoElTenantTomaLaCuentaDeLaUnicaEmpresa()
    {
        ReglasDeCuentaDeMedioPago.ExigirCuentaAsignable(
            null, new CuentaAsignable(5, Activa: true), asignacionNueva: true, idUnicaEmpresaDelTenant: 5);
    }

    [Fact]
    public void UnMedioDeTodoElTenantRechazaLaCuentaQueNoEsDeLaUnicaEmpresa()
    {
        var error = Falla(() =>
        {
            ReglasDeCuentaDeMedioPago.ExigirCuentaAsignable(
                null, new CuentaAsignable(6, Activa: true), asignacionNueva: true, idUnicaEmpresaDelTenant: 5);
            return null;
        });

        Assert.Equal("cuenta_tesoreria_de_otra_empresa", error.Codigo);
    }

    [Fact]
    public void UnaCuentaInactivaNoSeAsignaDeNuevo()
    {
        var error = Falla(() =>
        {
            ReglasDeCuentaDeMedioPago.ExigirCuentaAsignable(
                5, new CuentaAsignable(5, Activa: false), asignacionNueva: true, idUnicaEmpresaDelTenant: null);
            return null;
        });

        Assert.Equal("cuenta_tesoreria_inactiva", error.Codigo);
        Assert.Equal(400, error.EstadoHttp);
    }

    [Fact]
    public void UnaCuentaInactivaYaAsignadaSeConserva()
    {
        ReglasDeCuentaDeMedioPago.ExigirCuentaAsignable(
            5, new CuentaAsignable(5, Activa: false), asignacionNueva: false, idUnicaEmpresaDelTenant: null);
    }
}
