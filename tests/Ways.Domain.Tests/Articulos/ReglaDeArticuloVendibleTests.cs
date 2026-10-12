using Ways.Domain.Articulos;
using Ways.Domain.Combos;
using Ways.Domain.Common;
using Ways.Domain.Ofertas;

namespace Ways.Domain.Tests.Articulos;

/// <summary>
/// "Disponible a la venta" y "disponible online" nacen en <c>true</c> (todo está en la tienda y se vende salvo que se
/// destilde), y la regla pura que rechaza un artículo de uso interno nombra al primero que no se vende, en el orden
/// recibido.
/// </summary>
public class ReglaDeArticuloVendibleTests
{
    private static Articulo Crear(string codigo, bool disponibleVenta = true) => new()
    {
        CodigoInterno = codigo,
        Nombre = $"Artículo {codigo}",
        DisponibleVenta = disponibleVenta
    };

    [Fact]
    public void UnArticuloNuevoEstaDisponibleOnlineYALaVenta()
    {
        var articulo = new Articulo { CodigoInterno = "A", Nombre = "A" };

        Assert.True(articulo.DisponibleOnline);
        Assert.True(articulo.DisponibleVenta);
    }

    [Fact]
    public void UnaOfertaYUnComboNuevosEstanDisponiblesOnline()
    {
        Assert.True(new Oferta { Nombre = "Oferta" }.DisponibleOnline);
        Assert.True(new Combo { Nombre = "Combo" }.DisponibleOnline);
    }

    [Fact]
    public void LosArticulosVendiblesPasanYLaListaVaciaTambien()
    {
        ReglaDeArticuloVendible.Exigir([Crear("A"), Crear("B")]);
        ReglaDeArticuloVendible.Exigir([]);
    }

    [Fact]
    public void UnArticuloDeUsoInternoSeRechazaConConflictoYSuCodigo()
    {
        var error = Assert.Throws<ErrorDominio>(
            () => ReglaDeArticuloVendible.Exigir([Crear("A"), Crear("INT-1", disponibleVenta: false), Crear("B")]));

        Assert.Equal("articulo_no_vendible", error.Codigo);
        Assert.Equal(409, error.EstadoHttp);
        Assert.Contains("INT-1", error.Message);
    }

    [Fact]
    public void ConVariosNoVendiblesSeNombraElPrimeroEnElOrdenRecibido()
    {
        var error = Assert.Throws<ErrorDominio>(
            () => ReglaDeArticuloVendible.Exigir([Crear("OK"), Crear("PRIMERO", false), Crear("SEGUNDO", false)]));

        Assert.Contains("PRIMERO", error.Message);
        Assert.DoesNotContain("SEGUNDO", error.Message);
    }

    [Fact]
    public void LaDisponibilidadOnlineNoAfectaLaRegla()
    {
        var articulo = Crear("FUERA-DE-LA-TIENDA");
        articulo.DisponibleOnline = false;

        ReglaDeArticuloVendible.Exigir([articulo]);

        Assert.True(ReglaDeArticuloVendible.EsVendible(articulo));
    }
}
