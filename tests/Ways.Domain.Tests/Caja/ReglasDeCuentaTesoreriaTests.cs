using Ways.Domain.Caja;
using Ways.Domain.Common;

namespace Ways.Domain.Tests.Caja;

public class ReglasDeCuentaTesoreriaTests
{
    [Fact]
    public void ElNombreSeRecortaYSeDevuelve()
    {
        Assert.Equal("Mercado Pago", ReglasDeCuentaTesoreria.NormalizarNombre("  Mercado Pago \t"));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void UnNombreVacioOSoloEspaciosEsRequerido(string? nombre)
    {
        var error = Assert.Throws<ErrorDominio>(() => ReglasDeCuentaTesoreria.NormalizarNombre(nombre));

        Assert.Equal("nombre_requerido", error.Codigo);
        Assert.Equal(400, error.EstadoHttp);
    }

    [Fact]
    public void ElNombreDeExactamenteElLargoMaximoSeAcepta()
    {
        var nombre = new string('a', ReglasDeCuentaTesoreria.LargoMaximoDeNombre);

        Assert.Equal(nombre, ReglasDeCuentaTesoreria.NormalizarNombre(nombre));
    }

    [Fact]
    public void ElLargoSeMideDespuesDeRecortar()
    {
        var nombre = " " + new string('a', ReglasDeCuentaTesoreria.LargoMaximoDeNombre) + " ";

        Assert.Equal(ReglasDeCuentaTesoreria.LargoMaximoDeNombre, ReglasDeCuentaTesoreria.NormalizarNombre(nombre).Length);
    }

    [Fact]
    public void UnNombreUnCaracterMasLargoQueElMaximoSeRechaza()
    {
        var nombre = new string('a', ReglasDeCuentaTesoreria.LargoMaximoDeNombre + 1);

        var error = Assert.Throws<ErrorDominio>(() => ReglasDeCuentaTesoreria.NormalizarNombre(nombre));

        Assert.Equal("nombre_muy_largo", error.Codigo);
        Assert.Equal(400, error.EstadoHttp);
    }

    [Theory]
    [InlineData("0")]
    [InlineData("1500.25")]
    [InlineData("-1500.25")]
    [InlineData("999999999999.99")]
    [InlineData("-999999999999.99")]
    public void ElSaldoInicialDentroDeLaColumnaConHastaDosDecimalesSeAcepta(string texto)
    {
        var saldo = decimal.Parse(texto, System.Globalization.CultureInfo.InvariantCulture);

        ReglasDeCuentaTesoreria.ExigirSaldoInicialValido(saldo);
    }

    [Theory]
    [InlineData("0.001")]
    [InlineData("10.005")]
    [InlineData("1000000000000")]
    [InlineData("-1000000000000")]
    public void UnSaldoInicialConTresDecimalesOFueraDeLaColumnaSeRechaza(string texto)
    {
        var saldo = decimal.Parse(texto, System.Globalization.CultureInfo.InvariantCulture);

        var error = Assert.Throws<ErrorDominio>(() => ReglasDeCuentaTesoreria.ExigirSaldoInicialValido(saldo));

        Assert.Equal("saldo_inicial_invalido", error.Codigo);
        Assert.Equal(400, error.EstadoHttp);
    }
}
