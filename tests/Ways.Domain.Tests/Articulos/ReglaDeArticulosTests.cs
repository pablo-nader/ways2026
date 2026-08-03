using Ways.Domain.Articulos;
using Ways.Domain.Common;

namespace Ways.Domain.Tests.Articulos;

public class ReglaDeArticulosTests
{
    [Fact]
    public void RestringirDisponibilidadSinFilasDeSubsetEsRechazado()
    {
        var error = Assert.Throws<ErrorDominio>(() =>
            ReglaDeArticulos.ValidarRestriccionDeDisponibilidad(
                disponibleParaTodasActual: true, disponibleParaTodasNuevo: false, cantidadDeFilasSubset: 0));

        Assert.Equal("disponibilidad_restriccion_sin_subset", error.Codigo);
        Assert.Equal(400, error.EstadoHttp);
    }

    [Fact]
    public void RestringirDisponibilidadConAlMenosUnaFilaDeSubsetEsPermitido()
    {
        var excepcion = Record.Exception(() =>
            ReglaDeArticulos.ValidarRestriccionDeDisponibilidad(
                disponibleParaTodasActual: true, disponibleParaTodasNuevo: false, cantidadDeFilasSubset: 1));

        Assert.Null(excepcion);
    }

    [Fact]
    public void MantenerDisponibleParaTodasSinCambiarNuncaExigeSubset()
    {
        var excepcion = Record.Exception(() =>
            ReglaDeArticulos.ValidarRestriccionDeDisponibilidad(
                disponibleParaTodasActual: true, disponibleParaTodasNuevo: true, cantidadDeFilasSubset: 0));

        Assert.Null(excepcion);
    }

    [Fact]
    public void MantenerRestringidoSinCambiarNuncaExigeSubsetDeNuevo()
    {
        // El pasaje true -> false es lo único que dispara la regla; una fila ya restringida
        // que se guarda de nuevo (false -> false) no es una "restricción" nueva.
        var excepcion = Record.Exception(() =>
            ReglaDeArticulos.ValidarRestriccionDeDisponibilidad(
                disponibleParaTodasActual: false, disponibleParaTodasNuevo: false, cantidadDeFilasSubset: 0));

        Assert.Null(excepcion);
    }

    [Fact]
    public void AmpliarDeRestringidoATodasNuncaExigeSubset()
    {
        var excepcion = Record.Exception(() =>
            ReglaDeArticulos.ValidarRestriccionDeDisponibilidad(
                disponibleParaTodasActual: false, disponibleParaTodasNuevo: true, cantidadDeFilasSubset: 0));

        Assert.Null(excepcion);
    }
}
