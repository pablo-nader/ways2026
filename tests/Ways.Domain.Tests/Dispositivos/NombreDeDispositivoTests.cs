using Ways.Domain.Common;
using Ways.Domain.Dispositivos;

namespace Ways.Domain.Tests.Dispositivos;

public class NombreDeDispositivoTests
{
    [Fact]
    public void NormalizarRecortaEspaciosAlrededor()
    {
        Assert.Equal("Caja 1", NombreDeDispositivo.Normalizar("  Caja 1  "));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void NormalizarRechazaUnNombreVacioOSoloEspacios(string? valor)
    {
        var error = Assert.Throws<ErrorDominio>(() => NombreDeDispositivo.Normalizar(valor));
        Assert.Equal("nombre_requerido", error.Codigo);
        Assert.Equal(400, error.EstadoHttp);
    }

    [Fact]
    public void NormalizarAceptaExactamenteElLargoMaximo()
    {
        var nombre = new string('a', NombreDeDispositivo.LargoMaximo);

        Assert.Equal(nombre, NombreDeDispositivo.Normalizar(nombre));
    }

    [Fact]
    public void NormalizarRechazaUnNombreMasLargoQueElMaximo()
    {
        var nombre = new string('a', NombreDeDispositivo.LargoMaximo + 1);

        var error = Assert.Throws<ErrorDominio>(() => NombreDeDispositivo.Normalizar(nombre));
        Assert.Equal("nombre_muy_largo", error.Codigo);
        Assert.Equal(400, error.EstadoHttp);
    }
}
