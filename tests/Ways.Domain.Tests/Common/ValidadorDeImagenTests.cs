using Ways.Domain.Common;

namespace Ways.Domain.Tests.Common;

/// <summary>
/// La regla compartida por el logo de la vitrina y la imagen de los artículos. Los códigos y los mensajes de
/// los rechazos son del llamador: cada recurso conserva los suyos. Los casos del logo (<c>VitrinaTests</c>) y
/// los de la imagen (<c>ImagenDeArticuloTests</c>) pasan por acá; estos fijan lo que no depende de ninguno.
/// </summary>
public class ValidadorDeImagenTests
{
    private static readonly ErroresDeImagen Errores = new(
        "tamano_x", "Mensaje de tamaño.", "tipo_x", "Mensaje de tipo.");

    private static readonly byte[] CabeceraPng = [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A];

    [Fact]
    public void LosRechazosUsanLosCodigosYLosMensajesDeQuienLlama()
    {
        var grande = Assert.Throws<ErrorDominio>(
            () => ValidadorDeImagen.Validar(new byte[ValidadorDeImagen.TamanoMaximo + 1], null, Errores));
        var tipo = Assert.Throws<ErrorDominio>(() => ValidadorDeImagen.Validar("<svg/>"u8, null, Errores));

        Assert.Equal(("tamano_x", "Mensaje de tamaño.", 413), (grande.Codigo, grande.Message, grande.EstadoHttp));
        Assert.Equal(("tipo_x", "Mensaje de tipo.", 415), (tipo.Codigo, tipo.Message, tipo.EstadoHttp));
    }

    [Theory]
    [InlineData("image/png", true)]
    [InlineData("image/jpeg", true)]
    [InlineData("image/webp", true)]
    [InlineData("image/gif", false)]
    [InlineData("image/svg+xml", false)]
    [InlineData("IMAGE/PNG", false)]
    [InlineData("", false)]
    [InlineData(null, false)]
    public void SoloPngJpegYWebpSonTiposAdmitidos(string? tipo, bool admitido)
    {
        Assert.Equal(admitido, ValidadorDeImagen.EsTipoAdmitido(tipo));
    }

    [Fact]
    public void ElTipoDeclaradoSeComparaExactoConElQueDiceElContenido()
    {
        Assert.Equal(ValidadorDeImagen.TipoPng, ValidadorDeImagen.Validar(CabeceraPng, "image/png", Errores));

        var error = Assert.Throws<ErrorDominio>(() => ValidadorDeImagen.Validar(CabeceraPng, "IMAGE/PNG", Errores));
        Assert.Equal("tipo_x", error.Codigo);
    }

    [Fact]
    public void ElTopeEsUnMegabyteExacto()
    {
        Assert.Equal(1024 * 1024, ValidadorDeImagen.TamanoMaximo);
    }
}
