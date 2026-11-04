using Ways.Domain.Caja;
using Ways.Domain.Common;

namespace Ways.Domain.Tests.Caja;

public class ReglaDeEdicionDeRetirosTests
{
    private static readonly DateTimeOffset Ahora = new(2026, 11, 1, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public void CambiarElMotivoDelRetiroDeCierreSeRechazaCon400()
    {
        var error = Assert.Throws<ErrorDominio>(() =>
            ReglaDeEdicionDeRetiros.ExigirMotivoDeCierreIntacto(true, "Retiro de cierre de turno", "Otro motivo"));

        Assert.Equal("motivo_de_cierre_no_editable", error.Codigo);
        Assert.Equal(400, error.EstadoHttp);
    }

    [Fact]
    public void ElRetiroDeCierreConservaSuMotivo() =>
        ReglaDeEdicionDeRetiros.ExigirMotivoDeCierreIntacto(true, "Retiro de cierre de turno", "Retiro de cierre de turno");

    [Fact]
    public void UnRetiroManualCambiaDeMotivoLibremente() =>
        ReglaDeEdicionDeRetiros.ExigirMotivoDeCierreIntacto(false, "Retiro para el banco", "Otro motivo");

    [Fact]
    public void SubirElImporteAcreditaLaDiferenciaALaTesoreria() =>
        Assert.Equal((30m, 0m), ReglaDeEdicionDeRetiros.AjusteDeTesoreria(100m, 130m));

    [Fact]
    public void BajarElImporteDebitaLaDiferenciaDeLaTesoreria() =>
        Assert.Equal((0m, 40m), ReglaDeEdicionDeRetiros.AjusteDeTesoreria(100m, 60m));

    [Fact]
    public void LaBajaDebitaElImporteEntero() =>
        Assert.Equal((0m, 100m), ReglaDeEdicionDeRetiros.AjusteDeTesoreria(100m, null));

    [Fact]
    public void SinCambioDeImporteNoHayAjuste() => Assert.Null(ReglaDeEdicionDeRetiros.AjusteDeTesoreria(100m, 100m));

    [Fact]
    public void ConElTurnoCerradoCuentaLaFechaDelCierre()
    {
        var cierre = Ahora.AddDays(-3);

        Assert.Equal(cierre, ReglaDeEdicionDeRetiros.FechaQueCuentaParaElLibro(cierre, Ahora));
    }

    [Fact]
    public void ConElTurnoAbiertoCuentaElMomentoDeLaEdicion() =>
        Assert.Equal(Ahora, ReglaDeEdicionDeRetiros.FechaQueCuentaParaElLibro(null, Ahora));
}
