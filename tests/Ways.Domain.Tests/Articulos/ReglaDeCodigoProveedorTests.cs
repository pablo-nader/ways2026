using Ways.Domain.Articulos;
using Ways.Domain.Common;

namespace Ways.Domain.Tests.Articulos;

public class ReglaDeCodigoProveedorTests
{
    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void UnValorNuloOEnBlancoEquivaleASinCodigo(string? valor)
    {
        Assert.Null(ReglaDeCodigoProveedor.NormalizarOpcional(valor, "codigo_proveedor"));
    }

    [Fact]
    public void ElCodigoSeRecortaPeroConservaLasMayusculas()
    {
        Assert.Equal("Ab-12", ReglaDeCodigoProveedor.NormalizarOpcional("  Ab-12 ", "codigo_proveedor"));
    }

    [Fact]
    public void UnCodigoDeExactamenteLaLongitudMaximaEsValido()
    {
        var codigo = new string('x', ReglaDeCodigoProveedor.LongitudMaxima);

        Assert.Equal(codigo, ReglaDeCodigoProveedor.NormalizarRequerido(codigo, "codigo"));
    }

    [Fact]
    public void UnCodigoQueSuperaLaLongitudMaximaDespuesDeRecortarEsRechazado()
    {
        var codigo = " " + new string('x', ReglaDeCodigoProveedor.LongitudMaxima + 1) + " ";

        var error = Assert.Throws<ErrorDominio>(() => ReglaDeCodigoProveedor.NormalizarRequerido(codigo, "codigo"));

        Assert.Equal("codigo_muy_largo", error.Codigo);
        Assert.Equal(400, error.EstadoHttp);
    }

    [Fact]
    public void UnCodigoRequeridoEnBlancoEsRechazadoConElNombreDelCampo()
    {
        var error = Assert.Throws<ErrorDominio>(() => ReglaDeCodigoProveedor.NormalizarRequerido("  ", "codigo"));

        Assert.Equal("codigo_requerido", error.Codigo);
        Assert.Equal(400, error.EstadoHttp);
    }

    [Fact]
    public void UnCodigoSinProveedorEsRechazado()
    {
        var error = Assert.Throws<ErrorDominio>(() => ReglaDeCodigoProveedor.ExigirProveedor("ABC", idProveedor: null));

        Assert.Equal("proveedor_habitual_requerido", error.Codigo);
        Assert.Equal(400, error.EstadoHttp);
    }

    [Fact]
    public void SinCodigoNoSeExigeProveedor()
    {
        Assert.Null(Record.Exception(() => ReglaDeCodigoProveedor.ExigirProveedor(null, idProveedor: null)));
    }

    [Fact]
    public void UnCodigoConProveedorEsValido()
    {
        Assert.Null(Record.Exception(() => ReglaDeCodigoProveedor.ExigirProveedor("ABC", idProveedor: 7)));
    }
}
