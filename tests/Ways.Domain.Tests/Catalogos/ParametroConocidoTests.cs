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
    [InlineData("zona_horaria")]
    [InlineData("comision_porcentaje")]
    [InlineData("lotes_habilitado")]
    [InlineData("dias_alerta_vencimiento")]
    public void LasOchoClavesConocidasEstanRegistradas(string clave)
    {
        var conocido = ParametroConocido.Buscar(clave);

        Assert.Equal(clave, conocido.Clave);
    }

    [Fact]
    public void LotesHabilitadoDeclaraElDefaultEnFalse()
    {
        Assert.Equal(typeof(bool), ParametroConocido.LotesHabilitado.TipoClr);
        Assert.Equal("false", ParametroConocido.LotesHabilitado.ValorPorDefecto);
    }

    [Fact]
    public void DiasAlertaVencimientoDeclaraElDefaultEn30()
    {
        Assert.Equal(typeof(int), ParametroConocido.DiasAlertaVencimiento.TipoClr);
        Assert.Equal("30", ParametroConocido.DiasAlertaVencimiento.ValorPorDefecto);
    }

    [Fact]
    public void ZonaHorariaDeclaraElDefaultComoStringJsonQuoteado()
    {
        Assert.Equal(typeof(string), ParametroConocido.ZonaHoraria.TipoClr);
        Assert.Equal("\"America/Argentina/Buenos_Aires\"", ParametroConocido.ZonaHoraria.ValorPorDefecto);
    }

    [Fact]
    public void ComisionPorcentajeDeclaraElDefaultEnCero()
    {
        Assert.Equal(typeof(decimal), ParametroConocido.ComisionPorcentaje.TipoClr);
        Assert.Equal("0", ParametroConocido.ComisionPorcentaje.ValorPorDefecto);
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
