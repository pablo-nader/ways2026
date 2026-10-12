using Ways.Domain.Articulos;
using Ways.Domain.Common;

namespace Ways.Domain.Tests.Articulos;

public class ImagenDeArticuloTests
{
    private static readonly byte[] CabeceraPng = [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A, 0x00, 0x00];
    private static readonly byte[] CabeceraJpeg = [0xFF, 0xD8, 0xFF, 0xE0, 0x00, 0x10];

    private static byte[] CabeceraWebp() =>
        [.. "RIFF"u8, 0x24, 0x00, 0x00, 0x00, .. "WEBP"u8, .. "VP8 "u8];

    [Fact]
    public void LaImagenValidaDevuelveElTipoQueDiceElContenido()
    {
        Assert.Equal(ValidadorDeImagen.TipoPng, ImagenDeArticulo.Validar(CabeceraPng, ValidadorDeImagen.TipoPng));
        Assert.Equal(ValidadorDeImagen.TipoJpeg, ImagenDeArticulo.Validar(CabeceraJpeg, ValidadorDeImagen.TipoJpeg));
        Assert.Equal(ValidadorDeImagen.TipoWebp, ImagenDeArticulo.Validar(CabeceraWebp(), ValidadorDeImagen.TipoWebp));
    }

    [Fact]
    public void ElTamanoMaximoDeLaImagenEsElDelValidadorCompartidoYElDelCheckDeLaBase()
    {
        Assert.Equal(1_048_576, ImagenDeArticulo.TamanoMaximo);
        Assert.Equal(ValidadorDeImagen.TamanoMaximo, ImagenDeArticulo.TamanoMaximo);
    }

    [Fact]
    public void UnaImagenDeExactamenteUnMegabyteSeAcepta()
    {
        var imagen = new byte[ImagenDeArticulo.TamanoMaximo];
        CabeceraPng.CopyTo(imagen, 0);

        Assert.Equal(ValidadorDeImagen.TipoPng, ImagenDeArticulo.Validar(imagen, ValidadorDeImagen.TipoPng));
    }

    [Fact]
    public void UnaImagenUnByteMasGrandeDa413ImagenDemasiadoGrandeAntesQueElTipo()
    {
        var imagen = new byte[ImagenDeArticulo.TamanoMaximo + 1];
        CabeceraPng.CopyTo(imagen, 0);

        var error = Assert.Throws<ErrorDominio>(() => ImagenDeArticulo.Validar(imagen, ValidadorDeImagen.TipoPng));

        Assert.Equal("imagen_demasiado_grande", error.Codigo);
        Assert.Equal(413, error.EstadoHttp);

        // El tamaño gana aunque además el tipo declarado no coincida.
        var conOtroTipo = Assert.Throws<ErrorDominio>(() => ImagenDeArticulo.Validar(imagen, ValidadorDeImagen.TipoJpeg));
        Assert.Equal("imagen_demasiado_grande", conOtroTipo.Codigo);
    }

    [Theory]
    [InlineData("image/jpeg")]
    [InlineData("image/svg+xml")]
    [InlineData("text/plain")]
    public void UnTipoDeclaradoQueNoCoincideConElContenidoDa415ImagenNoAdmitida(string declarado)
    {
        var error = Assert.Throws<ErrorDominio>(() => ImagenDeArticulo.Validar(CabeceraPng, declarado));

        Assert.Equal("imagen_no_admitida", error.Codigo);
        Assert.Equal(415, error.EstadoHttp);
    }

    [Fact]
    public void UnSvgNuncaEsUnaImagenDeArticuloAunDeclaradaComoPng()
    {
        var svg = "<svg xmlns=\"http://www.w3.org/2000/svg\"><script>alert(1)</script></svg>"u8.ToArray();

        var error = Assert.Throws<ErrorDominio>(() => ImagenDeArticulo.Validar(svg, ValidadorDeImagen.TipoPng));

        Assert.Equal("imagen_no_admitida", error.Codigo);
        Assert.Equal(415, error.EstadoHttp);
    }

    [Fact]
    public void UnaImagenVaciaDa415()
    {
        var error = Assert.Throws<ErrorDominio>(() => ImagenDeArticulo.Validar([], ValidadorDeImagen.TipoPng));

        Assert.Equal("imagen_no_admitida", error.Codigo);
    }

    [Fact]
    public void LaVersionSonLosSegundosUnixDeLaUltimaCarga()
    {
        var cargada = new DateTimeOffset(2026, 10, 11, 12, 30, 45, 900, TimeSpan.Zero);

        Assert.Equal(cargada.ToUnixTimeSeconds(), ImagenDeArticulo.Version(cargada));
        Assert.Equal(1_791_721_845L, ImagenDeArticulo.Version(cargada));
    }
}
