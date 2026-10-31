using Ways.Domain.MercadoPago;

namespace Ways.Domain.Tests.MercadoPago;

public class PkceTests
{
    [Fact]
    public void ElDesafioS256DelVectorDelRfc7636EsElEsperado()
    {
        // Apéndice B de RFC 7636.
        Assert.Equal(
            "E9Melhoa2OwvFrEMTJguCHaoeK1t8URWbuGJSstw-cM",
            Pkce.DesafioS256("dBjftJeZ4CVP-mB92K27uhbUJU1p1r_wW1gFWFOEjXk"));
    }

    [Fact]
    public void ElVerificadorGeneradoTiene43CaracteresDelAlfabetoUrlSeguroYNoSeRepite()
    {
        var verificadores = Enumerable.Range(0, 20).Select(_ => Pkce.GenerarVerificador()).ToList();

        Assert.All(verificadores, v =>
        {
            Assert.Equal(43, v.Length);
            Assert.Matches("^[A-Za-z0-9_-]+$", v);
        });
        Assert.Equal(verificadores.Count, verificadores.Distinct().Count());
    }
}
