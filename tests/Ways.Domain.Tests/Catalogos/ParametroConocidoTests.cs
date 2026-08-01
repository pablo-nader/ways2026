using Ways.Domain.Catalogos;
using Ways.Domain.Common;

namespace Ways.Domain.Tests.Catalogos;

public class ParametroConocidoTests
{
    [Theory]
    [InlineData("tolerancia_pago")]
    [InlineData("vuelto_maximo")]
    [InlineData("importe_adicional_recarga")]
    [InlineData("slots_tickets_espera")]
    public void LasCuatroClavesDeDoc10EstanRegistradas(string clave)
    {
        var conocido = ParametroConocido.Buscar(clave);

        Assert.Equal(clave, conocido.Clave);
    }

    [Fact]
    public void UnaClaveDesconocidaTiraErrorDeDominio400()
    {
        var error = Assert.Throws<ErrorDominio>(() => ParametroConocido.Buscar("no_existe"));

        Assert.Equal("parametro_desconocido", error.Codigo);
        Assert.Equal(400, error.EstadoHttp);
    }

    [Fact]
    public void LaBusquedaEsInsensibleAMayusculas()
    {
        var conocido = ParametroConocido.Buscar("TOLERANCIA_PAGO");

        Assert.Same(ParametroConocido.ToleranciaPago, conocido);
    }
}
