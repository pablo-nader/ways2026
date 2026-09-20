namespace Ways.IntegrationTests;

/// <summary>
/// <c>mutation-proof-tests</c> regla 1 — la cláusula bajo prueba es la parte variable de ocho
/// dígitos de <see cref="DatosDePrueba.NumeroExternoUnico"/>
/// (<c>Random.Shared.Next(0, 100_000_000):D8</c>). Es la que evita el 409 <c>compra_duplicada</c>
/// intermitente contra <c>ux_comprobantes_compra_numero_externo</c> cuando la suite corre
/// completa; el generador anterior recortaba el interpolado a ocho caracteres y dejaba sólo tres
/// hexadecimales (4096 valores) dentro del ámbito (tenant, proveedor, tipo).
/// No toca la base: corre sin el fixture de Testcontainers.
/// </summary>
public class DatosDePruebaTests
{
    private const int Muestras = 5_000;

    [Fact]
    public void ElNumeroExternoConservaElFormatoVisibleDeOchoDigitos()
    {
        foreach (var numero in Generar())
        {
            Assert.Equal(13, numero.Length);
            Assert.Equal("0001-", numero[..5]);
            Assert.True(numero[5..].All(char.IsAsciiDigit), numero);
        }
    }

    [Fact]
    public void ElNumeroExternoRecorreElEspacioCompletoDeCienMillones()
    {
        // 5000 extracciones sobre 10^8 esperan 0,125 colisiones, así que el margen de 5 vuelve la
        // prueba determinista; un espacio angostado a 10^4 daría ~3935 distintos y fallaría.
        var distintos = Generar().Distinct().Count();

        Assert.True(distintos >= Muestras - 5, $"Sólo {distintos} valores distintos de {Muestras}.");
    }

    private static string[] Generar() =>
        Enumerable.Range(0, Muestras).Select(_ => DatosDePrueba.NumeroExternoUnico()).ToArray();
}
