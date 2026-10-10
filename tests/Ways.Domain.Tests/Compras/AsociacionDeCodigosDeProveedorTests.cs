using Ways.Domain.Compras;

namespace Ways.Domain.Tests.Compras;

public class AsociacionDeCodigosDeProveedorTests
{
    private static LineaConCodigoDeProveedor Linea(int orden, int? idArticulo, string? codigo) =>
        new(orden, idArticulo, codigo);

    [Fact]
    public void UnaLineaConArticuloYCodigoEsCandidata()
    {
        var candidatas = AsociacionDeCodigosDeProveedor.Seleccionar([Linea(1, 10, "ABC")]);

        Assert.Equal([new AsociacionCandidata(10, "ABC")], candidatas);
    }

    [Fact]
    public void UnConceptoConCodigoNuncaAsocia()
    {
        var candidatas = AsociacionDeCodigosDeProveedor.Seleccionar([Linea(1, null, "ABC"), Linea(2, 10, "DEF")]);

        Assert.Equal([new AsociacionCandidata(10, "DEF")], candidatas);
    }

    [Fact]
    public void UnaLineaSinCodigoNoEsCandidata()
    {
        Assert.Empty(AsociacionDeCodigosDeProveedor.Seleccionar([Linea(1, 10, null), Linea(2, 11, null)]));
    }

    [Fact]
    public void ConElMismoCodigoEnDosArticulosGanaLaLineaDeMenorOrdenAunqueLlegueDespues()
    {
        var candidatas = AsociacionDeCodigosDeProveedor.Seleccionar([Linea(2, 11, "ABC"), Linea(1, 10, "ABC")]);

        Assert.Equal([new AsociacionCandidata(10, "ABC")], candidatas);
    }

    [Fact]
    public void ElMismoCodigoConOtrasMayusculasCuentaComoRepetido()
    {
        var candidatas = AsociacionDeCodigosDeProveedor.Seleccionar([Linea(1, 10, "abc"), Linea(2, 11, "ABC")]);

        Assert.Equal([new AsociacionCandidata(10, "abc")], candidatas);
    }

    [Fact]
    public void ElMismoCodigoEnLasMismasLineasDelMismoArticuloSeIntentaUnaSolaVez()
    {
        var candidatas = AsociacionDeCodigosDeProveedor.Seleccionar([Linea(1, 10, "ABC"), Linea(2, 10, "ABC")]);

        Assert.Single(candidatas);
    }

    [Fact]
    public void UnConceptoConUnCodigoNoBloqueaElMismoCodigoDeUnaLineaPosteriorConArticulo()
    {
        var candidatas = AsociacionDeCodigosDeProveedor.Seleccionar([Linea(1, null, "ABC"), Linea(2, 10, "ABC")]);

        Assert.Equal([new AsociacionCandidata(10, "ABC")], candidatas);
    }

    [Fact]
    public void LosCandidatosSalenOrdenadosPorCodigoParaTomarLosLocksSiempreEnElMismoOrden()
    {
        var candidatas = AsociacionDeCodigosDeProveedor.Seleccionar(
            [Linea(1, 10, "ZZZ"), Linea(2, 11, "aaa"), Linea(3, 12, "MMM")]);

        Assert.Equal(["aaa", "MMM", "ZZZ"], candidatas.Select(c => c.Codigo));
    }
}
