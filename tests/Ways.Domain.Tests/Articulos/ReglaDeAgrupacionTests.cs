using Ways.Domain.Articulos;
using Ways.Domain.Common;

namespace Ways.Domain.Tests.Articulos;

/// <summary>
/// Las reglas de forma del pedido de agrupar (<see cref="ReglaDeAgrupacion"/>): qué ids se alinean de verdad y cuántos
/// admite un pedido. Función pura: el servicio las aplica antes de abrir ninguna transacción.
/// </summary>
public class ReglaDeAgrupacionTests
{
    [Fact]
    public void UnaListaAusenteOVaciaNoTieneDestinos()
    {
        Assert.Empty(ReglaDeAgrupacion.Destinos(null, idArticuloReferencia: 7));
        Assert.Empty(ReglaDeAgrupacion.Destinos([], idArticuloReferencia: 7));
        Assert.Empty(ReglaDeAgrupacion.Destinos(null, idArticuloReferencia: null));
    }

    /// <summary>Los ids repetidos cuentan una vez y salen ascendentes, en el orden en que los toman los locks de fila.</summary>
    [Fact]
    public void LosDestinosSalenSinRepetirYAscendentes()
    {
        Assert.Equal([2, 5, 9], ReglaDeAgrupacion.Destinos([9, 2, 5, 9, 2], idArticuloReferencia: null));
    }

    /// <summary>La referencia ya es el modelo: si viene en el pedido, una o varias veces, no es un destino.</summary>
    [Fact]
    public void LaReferenciaNoEsUnDestinoAunqueVengaEnElPedido()
    {
        Assert.Equal([2, 9], ReglaDeAgrupacion.Destinos([5, 9, 5, 2], idArticuloReferencia: 5));
    }

    /// <summary>Un id que no es de ningún artículo posible no se descarta acá: es un rechazo del servicio, que es quien
    /// los busca, y descartarlo callaría un pedido mal armado.</summary>
    [Fact]
    public void LosIdsInvalidosSeConservanParaQueElServicioLosRechace()
    {
        Assert.Equal([-3, 0, 4], ReglaDeAgrupacion.Destinos([4, 0, -3], idArticuloReferencia: 7));
    }

    [Fact]
    public void ElTopeAdmiteExactamenteElMaximoDeDestinos()
    {
        var pedido = Enumerable.Range(1, ReglaDeAgrupacion.MaximoDeArticulosPorPedido).ToList();

        Assert.Equal(ReglaDeAgrupacion.MaximoDeArticulosPorPedido, ReglaDeAgrupacion.Destinos(pedido, idArticuloReferencia: null).Count);
    }

    [Fact]
    public void UnDestinoMasQueElMaximoSeRechazaConUn400()
    {
        var pedido = Enumerable.Range(1, ReglaDeAgrupacion.MaximoDeArticulosPorPedido + 1).ToList();

        var error = Assert.Throws<ErrorDominio>(() => ReglaDeAgrupacion.Destinos(pedido, idArticuloReferencia: null));

        Assert.Equal(("demasiados_articulos", 400), (error.Codigo, error.EstadoHttp));
    }

    /// <summary>El tope cuenta los destinos y no el pedido crudo: los repetidos y la referencia no suman.</summary>
    [Fact]
    public void ElTopeNoCuentaLosRepetidosNiLaReferencia()
    {
        var destinos = Enumerable.Range(1, ReglaDeAgrupacion.MaximoDeArticulosPorPedido).ToList();
        var pedido = new List<int>([.. destinos, .. destinos, 5000]);

        Assert.Equal(destinos, ReglaDeAgrupacion.Destinos(pedido, idArticuloReferencia: 5000));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public void UnaReferenciaQueNoEsUnIdPosibleSeRechazaConUn400(int idArticuloReferencia)
    {
        var error = Assert.Throws<ErrorDominio>(() => ReglaDeAgrupacion.ExigirReferencia(idArticuloReferencia));

        Assert.Equal(("id_articulo_referencia_requerido", 400), (error.Codigo, error.EstadoHttp));
    }

    [Fact]
    public void UnaReferenciaConUnIdPosibleSeAcepta()
    {
        ReglaDeAgrupacion.ExigirReferencia(1);
    }
}
