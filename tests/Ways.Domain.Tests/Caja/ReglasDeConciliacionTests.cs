using Ways.Domain.Caja;
using Ways.Domain.Common;

namespace Ways.Domain.Tests.Caja;

public class ReglasDeConciliacionTests
{
    private static readonly DateTimeOffset Marca = new(2026, 10, 31, 12, 0, 0, TimeSpan.Zero);

    private static ErrorDominio Falla(Action accion) => Assert.Throws<ErrorDominio>(accion);

    [Fact]
    public void UnaFechaIgualALaMarcaEstaCongeladaYUnaPosteriorQuedaLibre()
    {
        Assert.True(ReglasDeConciliacion.EstaCongelado(Marca, Marca));
        Assert.True(ReglasDeConciliacion.EstaCongelado(Marca.AddTicks(-1), Marca));
        Assert.False(ReglasDeConciliacion.EstaCongelado(Marca.AddTicks(1), Marca));
    }

    [Fact]
    public void SinMarcaNadaEstaCongelado()
    {
        Assert.False(ReglasDeConciliacion.EstaCongelado(DateTimeOffset.MinValue.AddDays(1), null));
    }

    [Fact]
    public void LaComparacionNoDependeDelOffsetDeLaFecha()
    {
        var mismoInstanteConOtroOffset = Marca.ToOffset(TimeSpan.FromHours(-3));

        Assert.True(ReglasDeConciliacion.EstaCongelado(mismoInstanteConOtroOffset, Marca));
    }

    [Fact]
    public void SinHastaSeRechazaConSuCodigo()
    {
        var error = Falla(() => ReglasDeConciliacion.ExigirHasta(null));

        Assert.Equal(("hasta_requerido", 400), (error.Codigo, error.EstadoHttp));
    }

    [Fact]
    public void ElHastaSeNormalizaAUtc()
    {
        var local = new DateTimeOffset(2026, 10, 31, 9, 0, 0, TimeSpan.FromHours(-3));

        Assert.Equal(TimeSpan.Zero, ReglasDeConciliacion.ExigirHasta(local).Offset);
    }

    [Fact]
    public void UnaMarcaFuturaSeRechazaYUnaIgualAhoraSeAcepta()
    {
        var error = Falla(() => ReglasDeConciliacion.ExigirMarcaValida(Marca.AddTicks(1), Marca, null));

        Assert.Equal(("conciliacion_futura", 409), (error.Codigo, error.EstadoHttp));
        ReglasDeConciliacion.ExigirMarcaValida(Marca, Marca, null);
    }

    [Fact]
    public void UnaMarcaIgualOAnteriorALaVigenteSeRechazaYUnaPosteriorSeAcepta()
    {
        var ahora = Marca.AddDays(1);

        var igual = Falla(() => ReglasDeConciliacion.ExigirMarcaValida(Marca, ahora, Marca));
        var anterior = Falla(() => ReglasDeConciliacion.ExigirMarcaValida(Marca.AddHours(-1), ahora, Marca));

        Assert.Equal("conciliacion_anterior", igual.Codigo);
        Assert.Equal("conciliacion_anterior", anterior.Codigo);
        Assert.Equal(409, igual.EstadoHttp);
        ReglasDeConciliacion.ExigirMarcaValida(Marca.AddTicks(1), ahora, Marca);
    }

    [Fact]
    public void LaNotaSeRecortaYUnaVaciaQuedaNula()
    {
        Assert.Equal("Revisado con el contador", ReglasDeConciliacion.NormalizarNota("  Revisado con el contador "));
        Assert.Null(ReglasDeConciliacion.NormalizarNota("   "));
        Assert.Null(ReglasDeConciliacion.NormalizarNota(null));
    }

    [Fact]
    public void UnaNotaDeMasDeQuinientosCaracteresSeRechazaYUnaDeExactamenteQuinientosSeAcepta()
    {
        var exacta = new string('a', ReglasDeConciliacion.LargoMaximoDeNota);

        var error = Falla(() => ReglasDeConciliacion.NormalizarNota(exacta + "a"));

        Assert.Equal(("nota_muy_larga", 400), (error.Codigo, error.EstadoHttp));
        Assert.Equal(exacta, ReglasDeConciliacion.NormalizarNota(exacta));
    }

    [Fact]
    public void ElErrorDePeriodoConciliadoNombraLaMarca()
    {
        var error = ReglasDeConciliacion.PeriodoConciliado(Marca);

        Assert.Equal(("periodo_conciliado", 409), (error.Codigo, error.EstadoHttp));
        Assert.Contains("31/10/2026 12:00 UTC", error.Message);
    }
}
