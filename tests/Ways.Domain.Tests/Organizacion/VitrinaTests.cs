using Ways.Domain.Common;
using Ways.Domain.Organizacion;

namespace Ways.Domain.Tests.Organizacion;

public class VitrinaTests
{
    private static readonly byte[] CabeceraPng = [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A, 0x00, 0x00];
    private static readonly byte[] CabeceraJpeg = [0xFF, 0xD8, 0xFF, 0xE0, 0x00, 0x10];

    private static byte[] CabeceraWebp() =>
        [.. "RIFF"u8, 0x24, 0x00, 0x00, 0x00, .. "WEBP"u8, .. "VP8 "u8];

    // ---- texto ------------------------------------------------------------------------------------

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("\t\n")]
    public void UnTextoVacioONuloEsSinTexto(string? crudo)
    {
        Assert.Null(Vitrina.NormalizarTexto(crudo, 120, "El slogan"));
    }

    [Fact]
    public void ElTextoSeGuardaSinLosEspaciosDeLosExtremosYConservaLosInternos()
    {
        Assert.Equal("Todo\ncerca  tuyo", Vitrina.NormalizarTexto("  Todo\ncerca  tuyo \n", 120, "El slogan"));
    }

    [Fact]
    public void UnTextoDeExactamenteElMaximoSeAcepta()
    {
        var texto = new string('a', Vitrina.LargoMaximoDelTagline);

        Assert.Equal(texto, Vitrina.NormalizarTexto(texto, Vitrina.LargoMaximoDelTagline, "El slogan"));
    }

    [Fact]
    public void UnTextoUnCaracterMasLargoQueElMaximoDa400TextoDemasiadoLargoQueNombraElCampo()
    {
        var texto = new string('a', Vitrina.LargoMaximoDelTagline + 1);

        var error = Assert.Throws<ErrorDominio>(
            () => Vitrina.NormalizarTexto(texto, Vitrina.LargoMaximoDelTagline, "El slogan"));

        Assert.Equal("texto_demasiado_largo", error.Codigo);
        Assert.Equal(400, error.EstadoHttp);
        Assert.Contains("El slogan", error.Message);
        Assert.Contains("120", error.Message);
    }

    [Fact]
    public void ElLargoSeMideDespuesDeRecortarNoAntes()
    {
        var texto = " " + new string('a', Vitrina.LargoMaximoDelTagline) + " ";

        Assert.NotNull(Vitrina.NormalizarTexto(texto, Vitrina.LargoMaximoDelTagline, "El slogan"));
    }

    // ---- color ------------------------------------------------------------------------------------

    [Theory]
    [InlineData("#F7991C", "#f7991c")]
    [InlineData("#f7991c", "#f7991c")]
    [InlineData("  #00AaFf ", "#00aaff")]
    [InlineData("#000000", "#000000")]
    [InlineData("#ffffff", "#ffffff")]
    public void UnColorHexadecimalSeGuardaEnMinuscula(string crudo, string esperado)
    {
        Assert.Equal(esperado, Vitrina.NormalizarColor(crudo));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void UnColorVacioONuloEsSinColor(string? crudo)
    {
        Assert.Null(Vitrina.NormalizarColor(crudo));
    }

    [Theory]
    [InlineData("F7991C")]
    [InlineData("#F7991")]
    [InlineData("#F7991CC")]
    [InlineData("#GGGGGG")]
    [InlineData("#fff")]
    [InlineData("rgb(247,153,28)")]
    [InlineData("orange")]
    [InlineData("##f7991c")]
    [InlineData("#f7 991c")]
    [InlineData("#f7991é")]
    public void UnColorQueNoEsRrggbbDa400ColorInvalido(string crudo)
    {
        var error = Assert.Throws<ErrorDominio>(() => Vitrina.NormalizarColor(crudo));

        Assert.Equal("color_invalido", error.Codigo);
        Assert.Equal(400, error.EstadoHttp);
    }

    // ---- tema -------------------------------------------------------------------------------------

    [Theory]
    [InlineData("claro", "claro")]
    [InlineData("oscuro", "oscuro")]
    [InlineData("  CLARO ", "claro")]
    [InlineData("Oscuro", "oscuro")]
    public void ElTemaSeNormalizaAMinuscula(string crudo, string esperado)
    {
        Assert.Equal(esperado, Vitrina.NormalizarTema(crudo));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("gris")]
    [InlineData("dark")]
    [InlineData("claro oscuro")]
    public void UnTemaQueNoEsClaroNiOscuroDa400TemaInvalido(string? crudo)
    {
        var error = Assert.Throws<ErrorDominio>(() => Vitrina.NormalizarTema(crudo));

        Assert.Equal("tema_invalido", error.Codigo);
        Assert.Equal(400, error.EstadoHttp);
    }

    [Fact]
    public void UnaVitrinaNuevaNaceConElTemaOscuro()
    {
        Assert.Equal(Vitrina.TemaOscuro, new Vitrina().Tema);
    }

    // ---- logo: tipo por los primeros bytes --------------------------------------------------------

    [Fact]
    public void ElTipoDelLogoSaleDelContenidoDeLosTresFormatosAdmitidos()
    {
        Assert.Equal(Vitrina.TipoPng, Vitrina.DetectarTipoDeLogo(CabeceraPng));
        Assert.Equal(Vitrina.TipoJpeg, Vitrina.DetectarTipoDeLogo(CabeceraJpeg));
        Assert.Equal(Vitrina.TipoWebp, Vitrina.DetectarTipoDeLogo(CabeceraWebp()));
    }

    [Fact]
    public void UnSvgNuncaEsUnLogoAunqueSeaUnaImagenValida()
    {
        var svg = "<svg xmlns=\"http://www.w3.org/2000/svg\"><script>alert(1)</script></svg>"u8.ToArray();
        var svgConDeclaracion = "<?xml version=\"1.0\"?><svg xmlns=\"http://www.w3.org/2000/svg\"/>"u8.ToArray();

        Assert.Null(Vitrina.DetectarTipoDeLogo(svg));
        Assert.Null(Vitrina.DetectarTipoDeLogo(svgConDeclaracion));
    }

    [Fact]
    public void UnRiffQueNoEsWebpNoEsUnLogo()
    {
        byte[] wav = [.. "RIFF"u8, 0x24, 0x00, 0x00, 0x00, .. "WAVE"u8, .. "fmt "u8];

        Assert.Null(Vitrina.DetectarTipoDeLogo(wav));
    }

    [Fact]
    public void UnWebpTruncadoAntesDeLaMarcaNoEsUnLogo()
    {
        byte[] corto = [.. "RIFF"u8, 0x24, 0x00, 0x00, 0x00, (byte)'W', (byte)'E', (byte)'B'];

        Assert.Null(Vitrina.DetectarTipoDeLogo(corto));
    }

    [Theory]
    [InlineData(new byte[] { })]
    [InlineData(new byte[] { 0x89 })]
    [InlineData(new byte[] { 0x89, 0x50, 0x4E, 0x47 })]
    [InlineData(new byte[] { 0xFF, 0xD8 })]
    [InlineData(new byte[] { 0x47, 0x49, 0x46, 0x38, 0x39, 0x61 })]
    [InlineData(new byte[] { 0x25, 0x50, 0x44, 0x46, 0x2D })]
    public void ContenidoQueNoEmpiezaConUnaFirmaCompletaNoEsUnLogo(byte[] contenido)
    {
        Assert.Null(Vitrina.DetectarTipoDeLogo(contenido));
    }

    // ---- logo: validación completa ----------------------------------------------------------------

    [Fact]
    public void UnLogoValidoDevuelveElTipoQueDiceElContenido()
    {
        Assert.Equal(Vitrina.TipoPng, Vitrina.ValidarLogo(CabeceraPng, Vitrina.TipoPng));
        Assert.Equal(Vitrina.TipoJpeg, Vitrina.ValidarLogo(CabeceraJpeg, Vitrina.TipoJpeg));
        Assert.Equal(Vitrina.TipoWebp, Vitrina.ValidarLogo(CabeceraWebp(), Vitrina.TipoWebp));
    }

    [Fact]
    public void SinTipoDeclaradoMandaElContenido()
    {
        Assert.Equal(Vitrina.TipoJpeg, Vitrina.ValidarLogo(CabeceraJpeg, tipoDeclarado: null));
    }

    [Theory]
    [InlineData("image/jpeg")]
    [InlineData("image/webp")]
    [InlineData("image/svg+xml")]
    [InlineData("text/plain")]
    public void UnTipoDeclaradoQueNoCoincideConElContenidoDa415(string declarado)
    {
        var error = Assert.Throws<ErrorDominio>(() => Vitrina.ValidarLogo(CabeceraPng, declarado));

        Assert.Equal("logo_no_admitido", error.Codigo);
        Assert.Equal(415, error.EstadoHttp);
    }

    [Fact]
    public void ContenidoQueNoEsUnaImagenAdmitidaDa415AunConTipoDeclaradoValido()
    {
        var error = Assert.Throws<ErrorDominio>(
            () => Vitrina.ValidarLogo("<svg/>"u8, Vitrina.TipoPng));

        Assert.Equal("logo_no_admitido", error.Codigo);
        Assert.Equal(415, error.EstadoHttp);
    }

    [Fact]
    public void UnLogoVacioDa415()
    {
        var error = Assert.Throws<ErrorDominio>(() => Vitrina.ValidarLogo([], Vitrina.TipoPng));

        Assert.Equal("logo_no_admitido", error.Codigo);
    }

    [Fact]
    public void UnLogoDeExactamenteUnMegabyteSeAcepta()
    {
        var logo = new byte[Vitrina.TamanoMaximoDelLogo];
        CabeceraPng.CopyTo(logo, 0);

        Assert.Equal(Vitrina.TipoPng, Vitrina.ValidarLogo(logo, Vitrina.TipoPng));
    }

    [Fact]
    public void UnLogoUnByteMasGrandeQueUnMegabyteDa413AntesQueElTipo()
    {
        var logo = new byte[Vitrina.TamanoMaximoDelLogo + 1];
        CabeceraPng.CopyTo(logo, 0);

        var error = Assert.Throws<ErrorDominio>(() => Vitrina.ValidarLogo(logo, Vitrina.TipoPng));

        Assert.Equal("logo_demasiado_grande", error.Codigo);
        Assert.Equal(413, error.EstadoHttp);

        // El tamaño gana aunque además el tipo declarado no coincida.
        var conOtroTipo = Assert.Throws<ErrorDominio>(() => Vitrina.ValidarLogo(logo, Vitrina.TipoJpeg));
        Assert.Equal("logo_demasiado_grande", conOtroTipo.Codigo);
    }
}
