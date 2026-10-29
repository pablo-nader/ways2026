using Ways.Application.Articulos;
using Ways.Domain.Common;

namespace Ways.Application.Tests.Articulos;

/// <summary>Contrato del orden de <c>GET /api/articulos/grilla</c>: lista cerrada, nunca
/// aceptar-y-ignorar. La parte de SQL se prueba contra Postgres en <c>ArticulosGrillaOrdenEndpointTests</c>.</summary>
public class ArticulosOrdenDeGrillaTests
{
    [Theory]
    [InlineData("codigo", ColumnaDeOrdenDeGrilla.Codigo)]
    [InlineData("nombre", ColumnaDeOrdenDeGrilla.Nombre)]
    [InlineData("costo", ColumnaDeOrdenDeGrilla.Costo)]
    [InlineData("precio", ColumnaDeOrdenDeGrilla.Precio)]
    [InlineData("proveedor", ColumnaDeOrdenDeGrilla.Proveedor)]
    [InlineData("disponibilidad", ColumnaDeOrdenDeGrilla.Disponibilidad)]
    [InlineData("estado", ColumnaDeOrdenDeGrilla.Estado)]
    public void CadaColumnaDeLaGrillaTieneSuClave(string clave, ColumnaDeOrdenDeGrilla esperada)
    {
        var orden = OrdenDeGrillaDeArticulos.Interpretar(clave, null, puedeVerCostos: true);

        Assert.Equal(new OrdenDeGrillaDeArticulos(esperada, false), orden);
    }

    [Fact]
    public void SinOrdenNiDireccionNoHayOrdenPedido()
    {
        Assert.Null(OrdenDeGrillaDeArticulos.Interpretar(null, null, puedeVerCostos: true));
    }

    [Theory]
    [InlineData(null, false)]
    [InlineData("ascendente", false)]
    [InlineData("descendente", true)]
    public void LaDireccionOmitidaEsAscendente(string? direccion, bool descendente)
    {
        var orden = OrdenDeGrillaDeArticulos.Interpretar("nombre", direccion, puedeVerCostos: true);

        Assert.Equal(descendente, orden!.Value.Descendente);
    }

    [Theory]
    [InlineData("acciones")]
    [InlineData("Nombre")]
    [InlineData("")]
    [InlineData("nombre; drop table articulos")]
    public void UnaClaveDesconocidaSeRechazaConCuatrocientos(string clave)
    {
        var error = Assert.Throws<ErrorDominio>(() => OrdenDeGrillaDeArticulos.Interpretar(clave, null, puedeVerCostos: true));

        Assert.Equal("orden_invalido", error.Codigo);
        Assert.Equal(400, error.EstadoHttp);
    }

    [Theory]
    [InlineData("asc")]
    [InlineData("Descendente")]
    [InlineData("")]
    public void UnaDireccionDesconocidaSeRechaza(string direccion)
    {
        var error = Assert.Throws<ErrorDominio>(() => OrdenDeGrillaDeArticulos.Interpretar("nombre", direccion, puedeVerCostos: true));

        Assert.Equal("direccion_invalida", error.Codigo);
        Assert.Equal(400, error.EstadoHttp);
    }

    [Fact]
    public void UnaDireccionSinOrdenSeRechaza()
    {
        var error = Assert.Throws<ErrorDominio>(() => OrdenDeGrillaDeArticulos.Interpretar(null, "descendente", puedeVerCostos: true));

        Assert.Equal("direccion_sin_orden", error.Codigo);
    }

    [Fact]
    public void QuienNoVeCostosNoPuedeOrdenarPorCosto()
    {
        var error = Assert.Throws<ErrorDominio>(() => OrdenDeGrillaDeArticulos.Interpretar("costo", "ascendente", puedeVerCostos: false));

        Assert.Equal("orden_no_permitido", error.Codigo);
        Assert.Equal(400, error.EstadoHttp);
    }

    [Fact]
    public void QuienNoVeCostosSiPuedeOrdenarPorLasDemasColumnas()
    {
        foreach (var clave in new[] { "codigo", "nombre", "precio", "proveedor", "disponibilidad", "estado" })
        {
            Assert.NotNull(OrdenDeGrillaDeArticulos.Interpretar(clave, null, puedeVerCostos: false));
        }
    }
}
