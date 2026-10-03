using Ways.Application.Precios;
using Ways.Domain.Articulos;
using Ways.Domain.Common;

namespace Ways.Application.Tests.Precios;

/// <summary>
/// El contrato del <c>alcance</c> de la API de precios: cada valor de <see cref="AlcanceDeFamilia"/>
/// (y su ausencia) tiene un destino —un <see cref="ModoDeAlcanceDeFamilia"/>— y el cuarto modo,
/// <see cref="ModoDeAlcanceDeFamilia.FamiliaSiCorresponde"/>, es de los llamadores internos y no se
/// alcanza desde la API. Sin base de datos: <see cref="ServicioDePrecios.ModoDeLaSolicitud"/> es una
/// función pura.
/// </summary>
public class ServicioDePreciosAlcanceTests
{
    [Theory]
    [InlineData(null, ModoDeAlcanceDeFamilia.ExigirDecision)]
    [InlineData(AlcanceDeFamilia.Familia, ModoDeAlcanceDeFamilia.Familia)]
    [InlineData(AlcanceDeFamilia.SoloEste, ModoDeAlcanceDeFamilia.SoloEste)]
    public void CadaAlcanceDeLaApiTieneSuModo(AlcanceDeFamilia? alcance, ModoDeAlcanceDeFamilia esperado)
    {
        Assert.Equal(esperado, ServicioDePrecios.ModoDeLaSolicitud(alcance));
    }

    /// <summary>El conversor JSON del servidor acepta también el ordinal del enum: un número fuera
    /// de rango llega al servicio como un valor no definido, y no puede caer en silencio en ninguno
    /// de los destinos.</summary>
    [Theory]
    [InlineData(2)]
    [InlineData(99)]
    [InlineData(-1)]
    public void UnAlcanceFueraDelEnumSeRechazaConAlcanceInvalido(int ordinal)
    {
        var error = Assert.Throws<ErrorDominio>(() => ServicioDePrecios.ModoDeLaSolicitud((AlcanceDeFamilia)ordinal));

        Assert.Equal("alcance_invalido", error.Codigo);
        Assert.Equal(400, error.EstadoHttp);
    }

    [Fact]
    public void LaApiNuncaPideFamiliaSiCorresponde()
    {
        var valoresDeLaApi = new AlcanceDeFamilia?[] { null, AlcanceDeFamilia.Familia, AlcanceDeFamilia.SoloEste };

        Assert.DoesNotContain(
            valoresDeLaApi.Select(ServicioDePrecios.ModoDeLaSolicitud),
            modo => modo == ModoDeAlcanceDeFamilia.FamiliaSiCorresponde);
    }

    /// <summary>Ambos contratos de alta aceptan el alcance, opcional, sin valor por defecto. Un
    /// cliente que no lo manda sigue siendo válido para un artículo sin familia.</summary>
    [Fact]
    public void LosDosContratosDeAltaAceptanUnAlcanceOpcionalSinValorPorDefecto()
    {
        var alta = new AltaPrecio(IdListaPrecio: 1, Precio: 10m);
        var programado = new ProgramarPrecio(IdListaPrecio: 1, Precio: 10m, VigenteDesde: DateTimeOffset.UnixEpoch);

        Assert.Null(alta.Alcance);
        Assert.Null(programado.Alcance);
        Assert.Equal(AlcanceDeFamilia.SoloEste, (alta with { Alcance = AlcanceDeFamilia.SoloEste }).Alcance);
        Assert.Equal(AlcanceDeFamilia.Familia, (programado with { Alcance = AlcanceDeFamilia.Familia }).Alcance);
    }
}
