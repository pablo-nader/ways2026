using Ways.Application.Exportacion;
using Ways.Domain.Common;

namespace Ways.Application.Tests.Exportacion;

/// <summary>
/// stage-11, Slice 1b (design decisión 9; spec exportacion-de-reportes: "Export Route Convention
/// And Policy Inheritance By Co-Location") — único valor legal en v1 es <c>"xlsx"</c>, cualquier
/// otro rechaza con el código de dominio que el spec fija.
/// </summary>
public class FormatoDeExportacionTests
{
    [Theory]
    [InlineData("pdf")]
    [InlineData("csv")]
    [InlineData("XLS")]
    public void UnFormatoNoSoportadoRechazaConElCodigoDeDominio(string valor)
    {
        var error = Assert.Throws<ErrorDominio>(() => FormatoDeExportacion.Parsear(valor));

        Assert.Equal("formato_no_soportado", error.Codigo);
        Assert.Equal(400, error.EstadoHttp);
    }

    [Fact]
    public void XlsxParseaSinLanzar()
    {
        var formato = FormatoDeExportacion.Parsear("xlsx");

        Assert.Equal("xlsx", formato);
    }

    [Fact]
    public void ElParseoEsInsensibleAMayusculas()
    {
        var formato = FormatoDeExportacion.Parsear("XLSX");

        Assert.Equal("xlsx", formato);
    }
}
