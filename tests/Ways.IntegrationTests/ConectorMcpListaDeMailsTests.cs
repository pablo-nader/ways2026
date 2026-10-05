using Ways.Api.ConectorMcp;

namespace Ways.IntegrationTests;

/// <summary>Pruebas puras de <see cref="ListaDeMailsHabilitados"/>: no levantan la app ni Docker.</summary>
public class ConectorMcpListaDeMailsTests
{
    [Theory]
    [InlineData("a@ways.test,b@ways.test")]
    [InlineData("a@ways.test;b@ways.test")]
    [InlineData(" a@ways.test ; ,b@ways.test , ")]
    public void AceptaComaYPuntoYComaComoSeparadoresEIgnoraEspaciosYEntradasVacias(string valor)
    {
        var lista = new ListaDeMailsHabilitados(valor);

        Assert.Equal(2, lista.Cantidad);
        Assert.True(lista.Incluye("a@ways.test"));
        Assert.True(lista.Incluye("b@ways.test"));
    }

    [Fact]
    public void ComparaSinDistinguirMayusculasYRecortaElMailConsultado()
    {
        var lista = new ListaDeMailsHabilitados("Admin@Ways.Test");

        Assert.True(lista.Incluye("admin@ways.test"));
        Assert.True(lista.Incluye("  ADMIN@WAYS.TEST  "));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData(" , ; ")]
    public void UnValorVacioNoHabilitaANadie(string? valor)
    {
        var lista = new ListaDeMailsHabilitados(valor);

        Assert.Equal(0, lista.Cantidad);
        Assert.False(lista.Incluye("admin@ways.test"));
        Assert.False(lista.Incluye(string.Empty));
    }

    [Fact]
    public void SoloCoincideElMailCompleto()
    {
        var lista = new ListaDeMailsHabilitados("admin@ways.test");

        Assert.False(lista.Incluye("min@ways.test"));
        Assert.False(lista.Incluye("admin@ways.test.ar"));
        Assert.False(lista.Incluye(null));
    }
}
