using Ways.Domain.Clientes;
using Ways.Domain.Common;

namespace Ways.Domain.Tests.Clientes;

public class ReglaDeClientesTests
{
    [Fact]
    public void NumeroUnoEsConsumidorFinal()
    {
        Assert.True(ReglaDeClientes.EsConsumidorFinal(1));
    }

    [Theory]
    [InlineData(2)]
    [InlineData(100)]
    [InlineData(0)]
    public void CualquierOtroNumeroNoEsConsumidorFinal(int numero)
    {
        Assert.False(ReglaDeClientes.EsConsumidorFinal(numero));
    }

    [Fact]
    public void ValidarNoConsumidorFinalRechazaElNumeroUno()
    {
        var error = Assert.Throws<ErrorDominio>(() => ReglaDeClientes.ValidarNoConsumidorFinal(1));

        Assert.Equal("consumidor_final_protegido", error.Codigo);
        Assert.Equal(409, error.EstadoHttp);
    }

    [Theory]
    [InlineData(2)]
    [InlineData(50)]
    public void ValidarNoConsumidorFinalPermiteCualquierOtroNumero(int numero)
    {
        var excepcion = Record.Exception(() => ReglaDeClientes.ValidarNoConsumidorFinal(numero));

        Assert.Null(excepcion);
    }
}
