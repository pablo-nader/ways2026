using Ways.Domain.Common;
using Ways.Domain.MercadoPago;
using Ways.Domain.Organizacion;

namespace Ways.Domain.Tests.MercadoPago;

public class LocalParaMercadoPagoTests
{
    private static DireccionDeSucursal Completa() =>
        new("Mitre", "1234", "Rosario", "Santa Fe", "2000", -32.944243m, -60.650539m, "Frente a la plaza");

    [Theory]
    [InlineData("Av. 9 de Julio", "Av. 9 de Julio")]
    [InlineData("Córdoba Ñandú", "Cordoba Nandu")]
    [InlineData("Café über", "Cafe uber")]
    [InlineData("Local #1 (centro) & Cía", "Local 1 centro Cia")]
    [InlineData("Sucursal-Centro, norte.", "Sucursal-Centro, norte.")]
    [InlineData("  varios   espacios\t y\ntabs  ", "varios espacios y tabs")]
    [InlineData("S/N", "SN")]
    [InlineData("日本語 Tienda", "Tienda")]
    public void ElTextoSeLimpiaParaMercadoPago(string entrada, string esperado) =>
        Assert.Equal(esperado, TextoParaMercadoPago.Sanear(entrada, 200));

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("###")]
    [InlineData("日本語")]
    public void UnTextoSinNingunCaracterAdmitidoQuedaVacio(string? entrada) =>
        Assert.Equal(string.Empty, TextoParaMercadoPago.Sanear(entrada, 200));

    [Fact]
    public void ElTextoSeCortaAlLargoMaximoSinDejarUnEspacioAlFinal()
    {
        Assert.Equal("abcde", TextoParaMercadoPago.Sanear("abcdefgh", 5));
        Assert.Equal("abcd", TextoParaMercadoPago.Sanear("abcd efgh", 5));
    }

    [Fact]
    public void ElExternalIdEsAlfanumericoYEstablePorSucursal()
    {
        Assert.Equal("WAYSS42", LocalParaMercadoPago.ExternalIdDe(42));
        Assert.Matches("^[A-Za-z0-9]{1,60}$", LocalParaMercadoPago.ExternalIdDe(int.MaxValue));
    }

    [Fact]
    public void UnaDireccionCompletaArmaElLocalConElTextoLimpio()
    {
        var direccion = Completa() with { Calle = "Av. Córdoba", Provincia = "Ciudad Autónoma de Buenos Aires" };

        var local = LocalParaMercadoPago.Armar("Sucursal Ñ #1", direccion);

        Assert.Equal("Sucursal N 1", local.Nombre);
        Assert.Equal("Av. Cordoba", local.Calle);
        Assert.Equal("1234", local.Altura);
        Assert.Equal("Rosario", local.Ciudad);
        Assert.Equal("Ciudad Autonoma de Buenos Aires", local.Provincia);
        Assert.Equal(-32.944243m, local.Latitud);
        Assert.Equal(-60.650539m, local.Longitud);
        Assert.Equal("Frente a la plaza", local.Referencia);
    }

    [Fact]
    public void LaReferenciaEsOpcionalYUnaReferenciaSinCaracteresAdmitidosSeOmite()
    {
        Assert.Null(LocalParaMercadoPago.Armar("Centro", Completa() with { Referencia = null }).Referencia);
        Assert.Null(LocalParaMercadoPago.Armar("Centro", Completa() with { Referencia = "###" }).Referencia);
    }

    [Fact]
    public void ElCodigoPostalNoEsRequisitoDeMercadoPago() =>
        Assert.Empty(LocalParaMercadoPago.CamposFaltantes("Centro", Completa() with { CodigoPostal = null }));

    [Fact]
    public void UnaDireccionCompletaNoTieneCamposFaltantes() =>
        Assert.Empty(LocalParaMercadoPago.CamposFaltantes("Centro", Completa()));

    [Fact]
    public void UnaDireccionVaciaFaltaEnTodosLosCamposRequeridosEnOrden() =>
        Assert.Equal(
            ["calle", "altura", "ciudad", "provincia", "latitud", "longitud"],
            LocalParaMercadoPago.CamposFaltantes("Centro", DireccionDeSucursal.Vacia));

    [Theory]
    [InlineData("calle")]
    [InlineData("altura")]
    [InlineData("ciudad")]
    [InlineData("provincia")]
    [InlineData("latitud")]
    [InlineData("longitud")]
    public void CadaCampoRequeridoPorSiSoloFaltaSeNombra(string campo)
    {
        var direccion = campo switch
        {
            "calle" => Completa() with { Calle = null },
            "altura" => Completa() with { Altura = null },
            "ciudad" => Completa() with { Ciudad = null },
            "provincia" => Completa() with { Provincia = null },
            "latitud" => Completa() with { Latitud = null },
            _ => Completa() with { Longitud = null }
        };

        Assert.Equal([campo], LocalParaMercadoPago.CamposFaltantes("Centro", direccion));
    }

    [Fact]
    public void UnCampoQueSoloTieneCaracteresNoAdmitidosCuentaComoFaltante()
    {
        Assert.Equal(["calle"], LocalParaMercadoPago.CamposFaltantes("Centro", Completa() with { Calle = "###" }));
        Assert.Equal(["nombre"], LocalParaMercadoPago.CamposFaltantes("(((", Completa()));
    }

    [Fact]
    public void ArmarConUnaDireccionIncompletaLanza400ConLosCamposFaltantes()
    {
        var error = Assert.Throws<ErrorDominio>(() =>
            LocalParaMercadoPago.Armar("Centro", Completa() with { Altura = null, Latitud = null, Longitud = null }));

        Assert.Equal("direccion_incompleta_para_mercadopago", error.Codigo);
        Assert.Equal(400, error.EstadoHttp);
        Assert.Contains("altura", error.Message);
        Assert.Contains("latitud", error.Message);
        Assert.Contains("longitud", error.Message);
        Assert.DoesNotContain("calle", error.Message);
    }
}
