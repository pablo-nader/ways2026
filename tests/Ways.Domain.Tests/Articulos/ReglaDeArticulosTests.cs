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
                disponibleParaTodasNuevo: false, cantidadDeFilasSubset: 0));

        Assert.Equal("subset_de_empresas_requerido", error.Codigo);
        Assert.Equal(400, error.EstadoHttp);
    }

    [Fact]
    public void RestringirDisponibilidadConAlMenosUnaFilaDeSubsetEsPermitido()
    {
        var excepcion = Record.Exception(() =>
            ReglaDeArticulos.ValidarRestriccionDeDisponibilidad(
                disponibleParaTodasNuevo: false, cantidadDeFilasSubset: 1));

        Assert.Null(excepcion);
    }

    [Fact]
    public void MantenerDisponibleParaTodasSinSubsetEsPermitido()
    {
        var excepcion = Record.Exception(() =>
            ReglaDeArticulos.ValidarRestriccionDeDisponibilidad(
                disponibleParaTodasNuevo: true, cantidadDeFilasSubset: 0));

        Assert.Null(excepcion);
    }

    /// <summary>judgment-day ronda 1 (root cause de un par de CRITICAL): la regla valida el
    /// ESTADO RESULTANTE, no la transición. Antes de este fix, esta misma llamada (con el
    /// parámetro <c>disponibleParaTodasActual: false</c> que ya no existe) NO lanzaba —
    /// "mantener restringido sin cambiar nunca exige subset de nuevo" era el comportamiento
    /// viejo, y era exactamente el bug: un artículo ya restringido que se guarda otra vez sin
    /// ninguna fila de subset (false -&gt; false, count 0) tiene que rechazarse igual que una
    /// restricción nueva, porque el estado resultante sigue siendo "restringido sin ninguna
    /// empresa visible".</summary>
    [Fact]
    public void MantenerRestringidoSinFilasDeSubsetEsRechazado()
    {
        var error = Assert.Throws<ErrorDominio>(() =>
            ReglaDeArticulos.ValidarRestriccionDeDisponibilidad(
                disponibleParaTodasNuevo: false, cantidadDeFilasSubset: 0));

        Assert.Equal("subset_de_empresas_requerido", error.Codigo);
        Assert.Equal(400, error.EstadoHttp);
    }
}
