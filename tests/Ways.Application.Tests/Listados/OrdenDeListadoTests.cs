using Ways.Application.Auditoria;
using Ways.Application.Caja;
using Ways.Application.Listados;
using Ways.Application.Reportes;
using Ways.Domain.Common;

namespace Ways.Application.Tests.Listados;

/// <summary>Contrato del orden de los listados paginados en el servidor (auditoría, tesorería y reporte
/// de artículos): lista cerrada, nunca aceptar-y-ignorar. La parte de SQL se prueba contra Postgres en
/// los tests de integración de cada endpoint.</summary>
public class OrdenDeListadoTests
{
    private enum Columna
    {
        Uno,
        Dos,
    }

    private static readonly IReadOnlyDictionary<string, Columna> Claves =
        new Dictionary<string, Columna>(StringComparer.Ordinal) { ["uno"] = Columna.Uno, ["dos"] = Columna.Dos };

    [Fact]
    public void SinOrdenNiDireccionNoHayOrdenPedido()
    {
        Assert.Null(OrdenDeListado.Interpretar(null, null, Claves));
    }

    [Theory]
    [InlineData(null, false)]
    [InlineData("ascendente", false)]
    [InlineData("descendente", true)]
    public void LaDireccionOmitidaEsAscendente(string? direccion, bool descendente)
    {
        var orden = OrdenDeListado.Interpretar("uno", direccion, Claves);

        Assert.Equal(new OrdenDeListado<Columna>(Columna.Uno, descendente), orden);
    }

    [Theory]
    [InlineData("tres")]
    [InlineData("Uno")]
    [InlineData("")]
    [InlineData("uno; drop table x")]
    public void UnaClaveDesconocidaSeRechazaConCuatrocientos(string clave)
    {
        var error = Assert.Throws<ErrorDominio>(() => OrdenDeListado.Interpretar(clave, null, Claves));

        Assert.Equal("orden_invalido", error.Codigo);
        Assert.Equal(400, error.EstadoHttp);
    }

    [Theory]
    [InlineData("asc")]
    [InlineData("Descendente")]
    [InlineData("")]
    public void UnaDireccionDesconocidaSeRechaza(string direccion)
    {
        var error = Assert.Throws<ErrorDominio>(() => OrdenDeListado.Interpretar("uno", direccion, Claves));

        Assert.Equal("direccion_invalida", error.Codigo);
        Assert.Equal(400, error.EstadoHttp);
    }

    [Fact]
    public void UnaDireccionSinOrdenSeRechaza()
    {
        var error = Assert.Throws<ErrorDominio>(() => OrdenDeListado.Interpretar(null, "descendente", Claves));

        Assert.Equal("direccion_sin_orden", error.Codigo);
        Assert.Equal(400, error.EstadoHttp);
    }

    [Fact]
    public void UnaColumnaQueElRolNoVeSeRechazaYLasDemasPasan()
    {
        var error = Assert.Throws<ErrorDominio>(
            () => OrdenDeListado.Interpretar("dos", null, Claves, columna => columna != Columna.Dos));

        Assert.Equal("orden_no_permitido", error.Codigo);
        Assert.Equal(400, error.EstadoHttp);
        Assert.NotNull(OrdenDeListado.Interpretar("uno", null, Claves, columna => columna != Columna.Dos));
    }

    /// <summary>Cada columna de datos de la pantalla tiene su clave, ni una más ni una menos: agregar una
    /// columna a la pantalla sin su clave (o al revés) rompe acá.</summary>
    [Fact]
    public void CadaListadoExponeExactamenteLasClavesDeSusColumnas()
    {
        AssertClaves(
            ServicioDeConsultaDeAuditoria.ClavesDeOrden.Keys, "fecha", "accion", "entidad", "idEntidad", "actor", "puntoVenta");
        AssertClaves(
            ServicioDeReportesDeArticulos.ClavesDeOrdenDeArticulos.Keys,
            "codigo", "nombre", "area", "categoria", "marca", "grupo", "proveedor", "estado");
    }

    private static void AssertClaves(IEnumerable<string> claves, params string[] esperadas) =>
        Assert.Equal(esperadas.Order(StringComparer.Ordinal), claves.Order(StringComparer.Ordinal));

    [Fact]
    public void CadaClaveDeCadaListadoSeInterpretaALaColumnaQueNombra()
    {
        foreach (var (clave, columna) in ServicioDeConsultaDeAuditoria.ClavesDeOrden)
        {
            Assert.Equal(columna, OrdenDeListado.Interpretar(clave, null, ServicioDeConsultaDeAuditoria.ClavesDeOrden)!.Value.Columna);
        }

        Assert.Equal(
            ColumnaDeOrdenDeArticulosDeReporte.Proveedor,
            OrdenDeListado.Interpretar("proveedor", null, ServicioDeReportesDeArticulos.ClavesDeOrdenDeArticulos)!.Value.Columna);
    }
}
