using System.Text.RegularExpressions;
using Ways.Domain.Dispositivos;

namespace Ways.Domain.Tests.Dispositivos;

public partial class TokenDeDispositivoTests
{
    [GeneratedRegex("^[0-9a-f]{64}$")]
    private static partial Regex HashHexMinuscula64();

    [GeneratedRegex("^[A-Za-z0-9_-]+$")]
    private static partial Regex Base64UrlSinPadding();

    [Fact]
    public void GenerarNuevoDevuelveUnSecretoBase64UrlSinPaddingNiCaracteresDeCookieInvalidos()
    {
        var (secreto, _) = TokenDeDispositivo.GenerarNuevo();

        Assert.False(string.IsNullOrEmpty(secreto));
        Assert.DoesNotContain('+', secreto);
        Assert.DoesNotContain('/', secreto);
        Assert.DoesNotContain('=', secreto);
        Assert.Matches(Base64UrlSinPadding(), secreto);
    }

    [Fact]
    public void GenerarNuevoDevuelveUnHashDeSesentaYCuatroHexMinuscula()
    {
        var (_, hash) = TokenDeDispositivo.GenerarNuevo();

        Assert.Equal(64, hash.Length);
        Assert.Matches(HashHexMinuscula64(), hash);
    }

    [Fact]
    public void GenerarNuevoNuncaRepiteElMismoSecretoEntreDosLlamadas()
    {
        // No es una prueba de mutación (no hay una clave puntual que mutar): es la propiedad
        // estructural de RandomNumberGenerator sobre 32 bytes — 256 bits de entropía hacen una
        // colisión en dos llamadas prácticamente irrepresentable.
        var (secretoA, hashA) = TokenDeDispositivo.GenerarNuevo();
        var (secretoB, hashB) = TokenDeDispositivo.GenerarNuevo();

        Assert.NotEqual(secretoA, secretoB);
        Assert.NotEqual(hashA, hashB);
    }

    [Fact]
    public void HashearEsDeterministicoParaElMismoSecreto()
    {
        var (secreto, hashOriginal) = TokenDeDispositivo.GenerarNuevo();

        var hashRecalculado = TokenDeDispositivo.Hashear(secreto);

        Assert.Equal(hashOriginal, hashRecalculado);
    }

    [Fact]
    public void HashearDeDosSecretosDistintosProduceHashesDistintos()
    {
        var hashA = TokenDeDispositivo.Hashear("secreto-a");
        var hashB = TokenDeDispositivo.Hashear("secreto-b");

        Assert.NotEqual(hashA, hashB);
    }
}
