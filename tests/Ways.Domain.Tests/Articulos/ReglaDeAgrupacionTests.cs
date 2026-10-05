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

    /// <summary>Al agregar a una familia que ya existe la referencia todavía no se conoce, y puede ser uno de los ids
    /// pedidos: se admite UNO más que el tope. Los ids salen sin repetir y ascendentes, y los inválidos se conservan.</summary>
    [Fact]
    public void AlAgregarSeAdmiteUnIdMasQueElTopePorqueLaReferenciaDeLaFamiliaPuedeSerUnoDeEllos()
    {
        var conUnoMas = Enumerable.Range(1, ReglaDeAgrupacion.MaximoDeArticulosPorPedido + 1).Reverse().ToList();

        var ids = ReglaDeAgrupacion.DestinosAlAgregar([.. conUnoMas, .. conUnoMas]);

        Assert.Equal(Enumerable.Range(1, ReglaDeAgrupacion.MaximoDeArticulosPorPedido + 1), ids);
        Assert.Equal([-3, 0, 4], ReglaDeAgrupacion.DestinosAlAgregar([4, 0, -3, 4]));
        Assert.Empty(ReglaDeAgrupacion.DestinosAlAgregar(null));
    }

    [Fact]
    public void AlAgregarDosIdsMasQueElTopeSeRechazanConUn400()
    {
        var pedido = Enumerable.Range(1, ReglaDeAgrupacion.MaximoDeArticulosPorPedido + 2).ToList();

        var error = Assert.Throws<ErrorDominio>(() => ReglaDeAgrupacion.DestinosAlAgregar(pedido));

        Assert.Equal(("demasiados_articulos", 400), (error.Codigo, error.EstadoHttp));
        Assert.Equal("Un pedido agrupa como máximo 100 artículos además del de referencia.", error.Message);
    }

    /// <summary>El tope exacto, sobre los destinos que quedan sin la referencia: cien se aceptan y uno más da el mismo 400 y
    /// el mismo mensaje que el tope de <see cref="ReglaDeAgrupacion.Destinos"/>.</summary>
    [Fact]
    public void ElTopeExactoSobreLosDestinosAdmiteElMaximoYRechazaUnoMas()
    {
        ReglaDeAgrupacion.ExigirTope([.. Enumerable.Range(1, ReglaDeAgrupacion.MaximoDeArticulosPorPedido)]);

        var error = Assert.Throws<ErrorDominio>(
            () => ReglaDeAgrupacion.ExigirTope([.. Enumerable.Range(1, ReglaDeAgrupacion.MaximoDeArticulosPorPedido + 1)]));

        Assert.Equal(("demasiados_articulos", 400), (error.Codigo, error.EstadoHttp));
        Assert.Equal("Un pedido agrupa como máximo 100 artículos además del de referencia.", error.Message);
    }

    /// <summary>El tope de pares acota los locks que el pedido sostiene: mil pares se aceptan, de cualquier forma que se
    /// compongan, y mil uno se rechazan con el mensaje exacto, que nombra los dos factores.</summary>
    [Theory]
    [InlineData(100, 10)]
    [InlineData(50, 20)]
    [InlineData(1, 1000)]
    [InlineData(0, 5000)]
    [InlineData(7, 0)]
    public void LosParesHastaElMaximoSeAceptan(int destinos, int listasFijas)
    {
        ReglaDeAgrupacion.ExigirParesAcotados(destinos, listasFijas);

        Assert.Null(ReglaDeAgrupacion.MensajeSiExcedeLosPares(destinos, listasFijas));
    }

    [Theory]
    [InlineData(91, 11, 1001)]
    [InlineData(100, 11, 1100)]
    [InlineData(143, 7, 1001)]
    public void LosParesPorEncimaDelMaximoSeRechazanConUn400YSuMensajeExacto(int destinos, int listasFijas, int pares)
    {
        var mensaje =
            $"Un pedido admite como máximo 1000 pares de artículo y lista de precios fija, y este tiene {pares} " +
            $"({destinos} artículos × {listasFijas} listas fijas). Hay que agrupar los artículos en varios pedidos.";

        var error = Assert.Throws<ErrorDominio>(() => ReglaDeAgrupacion.ExigirParesAcotados(destinos, listasFijas));

        Assert.Equal(("demasiados_articulos", 400, mensaje), (error.Codigo, error.EstadoHttp, error.Message));
        Assert.Equal(mensaje, ReglaDeAgrupacion.MensajeSiExcedeLosPares(destinos, listasFijas));
    }

    /// <summary>El producto se calcula en 64 bits: dos factores grandes no desbordan y se rechazan en vez de dar la vuelta.</summary>
    [Fact]
    public void ElProductoDeLosParesNoDesbordaEnteros()
    {
        Assert.NotNull(ReglaDeAgrupacion.MensajeSiExcedeLosPares(int.MaxValue, 2));
        Assert.NotNull(ReglaDeAgrupacion.MensajeSiExcedeLosPares(65_536, 65_536));
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
