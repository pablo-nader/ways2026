using System.Security.Cryptography;
using System.Text;
using Ways.Domain.MercadoPago;

namespace Ways.Domain.Tests.MercadoPago;

public class FirmaDeWebhookMercadoPagoTests
{
    private const string Secreto = "secreto-de-prueba-del-webhook";
    private const string IdDeSolicitud = "9f1c2e3d-aaaa-bbbb-cccc-000000000001";
    private static readonly DateTimeOffset Ahora = new(2026, 11, 1, 12, 0, 0, TimeSpan.Zero);

    /// <summary>Firma por una vía independiente de la implementación: el manifiesto escrito a mano y un HMAC propio.</summary>
    private static string Hex(string manifiesto, string secreto = Secreto) =>
        Convert.ToHexString(new HMACSHA256(Encoding.UTF8.GetBytes(secreto)).ComputeHash(Encoding.UTF8.GetBytes(manifiesto))).ToLowerInvariant();

    private static string Encabezado(string idEnManifiesto, long ts, string requestId = IdDeSolicitud, string secreto = Secreto) =>
        $"ts={ts},v1={Hex($"id:{idEnManifiesto};request-id:{requestId};ts:{ts};", secreto)}";

    private static long EnSegundos(TimeSpan desplazamiento) => (Ahora + desplazamiento).ToUnixTimeSeconds();

    private static ResultadoDeFirmaDeWebhook Verificar(string? encabezado, string? idDelDato = "ord01abc", string? requestId = IdDeSolicitud, string? secreto = Secreto) =>
        FirmaDeWebhookMercadoPago.Verificar(encabezado, requestId, idDelDato, secreto, Ahora);

    [Fact]
    public void UnaFirmaValidaEnSegundosSeAcepta() =>
        Assert.Equal(ResultadoDeFirmaDeWebhook.Valida, Verificar(Encabezado("ord01abc", EnSegundos(TimeSpan.Zero))));

    [Fact]
    public void UnaFirmaValidaEnMilisegundosSeAcepta()
    {
        var ts = (Ahora - TimeSpan.FromMinutes(1)).ToUnixTimeMilliseconds();

        Assert.Equal(ResultadoDeFirmaDeWebhook.Valida, Verificar(Encabezado("ord01abc", ts)));
    }

    [Fact]
    public void ElSecretoEquivocadoSeRechaza() =>
        Assert.Equal(
            ResultadoDeFirmaDeWebhook.Invalida,
            Verificar(Encabezado("ord01abc", EnSegundos(TimeSpan.Zero), secreto: "otro-secreto")));

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void SinSecretoConfiguradoNuncaHayFirmaValida(string? secreto) =>
        Assert.Equal(
            ResultadoDeFirmaDeWebhook.Invalida,
            Verificar(Encabezado("ord01abc", EnSegundos(TimeSpan.Zero), secreto: secreto ?? string.Empty), secreto: secreto));

    [Fact]
    public void UnIdDelDatoAlteradoSeRechaza() =>
        Assert.Equal(
            ResultadoDeFirmaDeWebhook.Invalida,
            Verificar(Encabezado("ord01abc", EnSegundos(TimeSpan.Zero)), idDelDato: "ord01abd"));

    [Fact]
    public void UnRequestIdAlteradoSeRechaza() =>
        Assert.Equal(
            ResultadoDeFirmaDeWebhook.Invalida,
            Verificar(Encabezado("ord01abc", EnSegundos(TimeSpan.Zero)), requestId: "otro-request-id"));

    [Fact]
    public void UnTsAlteradoSeRechazaAunqueSigaEnLaVentana()
    {
        var ts = EnSegundos(TimeSpan.Zero);
        var encabezado = Encabezado("ord01abc", ts).Replace($"ts={ts}", $"ts={ts + 1}", StringComparison.Ordinal);

        Assert.Equal(ResultadoDeFirmaDeWebhook.Invalida, Verificar(encabezado));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void SinEncabezadoSeRechaza(string? encabezado) =>
        Assert.Equal(ResultadoDeFirmaDeWebhook.Invalida, Verificar(encabezado));

    [Fact]
    public void SinRequestIdSeRechazaAunqueLaFirmaSeaDeUnManifiestoSinEl()
    {
        var ts = EnSegundos(TimeSpan.Zero);
        var sinRequestId = $"ts={ts},v1={Hex($"id:ord01abc;ts:{ts};")}";

        Assert.Equal(ResultadoDeFirmaDeWebhook.Invalida, Verificar(sinRequestId, requestId: null));
        Assert.Equal(ResultadoDeFirmaDeWebhook.Invalida, Verificar(sinRequestId, requestId: ""));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    public void SinIdDelDatoSeRechaza(string? idDelDato) =>
        Assert.Equal(
            ResultadoDeFirmaDeWebhook.Invalida,
            Verificar(Encabezado("ord01abc", EnSegundos(TimeSpan.Zero)), idDelDato: idDelDato));

    [Theory]
    [InlineData("v1=abc")]
    [InlineData("ts=1")]
    [InlineData("ts=,v1=")]
    [InlineData("basura")]
    [InlineData("ts=abc,v1=00")]
    [InlineData("ts=-5,v1=00")]
    public void UnEncabezadoIncompletoOIlegibleSeRechaza(string encabezado) =>
        Assert.Equal(ResultadoDeFirmaDeWebhook.Invalida, Verificar(encabezado));

    [Fact]
    public void UnaPartePorEncabezadoRepetidaSeRechaza()
    {
        var ts = EnSegundos(TimeSpan.Zero);
        var valido = Encabezado("ord01abc", ts);

        Assert.Equal(ResultadoDeFirmaDeWebhook.Invalida, Verificar($"{valido},ts={ts}"));
        Assert.Equal(ResultadoDeFirmaDeWebhook.Invalida, Verificar($"{valido},v1={Hex("otro")}"));
    }

    [Fact]
    public void LaFirmaEnMayusculasHexadecimalesSeAcepta()
    {
        var ts = EnSegundos(TimeSpan.Zero);
        var encabezado = $"ts={ts},v1={Hex($"id:ord01abc;request-id:{IdDeSolicitud};ts:{ts};").ToUpperInvariant()}";

        Assert.Equal(ResultadoDeFirmaDeWebhook.Valida, Verificar(encabezado));
    }

    [Theory]
    [InlineData("zz")]
    [InlineData("0102")]
    public void UnaFirmaQueNoEsHexadecimalDeLargoCompletoSeRechaza(string v1) =>
        Assert.Equal(ResultadoDeFirmaDeWebhook.Invalida, Verificar($"ts={EnSegundos(TimeSpan.Zero)},v1={v1}"));

    [Fact]
    public void LosEspaciosAlrededorDeLasPartesSeToleran()
    {
        var ts = EnSegundos(TimeSpan.Zero);
        var encabezado = $" ts = {ts} , v1 = {Hex($"id:ord01abc;request-id:{IdDeSolicitud};ts:{ts};")} ";

        Assert.Equal(ResultadoDeFirmaDeWebhook.Valida, Verificar(encabezado));
    }

    // Regla de Mercado Pago: un data.id alfanumérico entra al manifiesto en minúsculas; uno con otros caracteres, tal cual.
    [Fact]
    public void ElIdAlfanumericoSeFirmaEnMinusculas()
    {
        var ts = EnSegundos(TimeSpan.Zero);

        Assert.Equal(ResultadoDeFirmaDeWebhook.Valida, Verificar(Encabezado("ord01abc", ts), idDelDato: "ORD01ABC"));
        Assert.Equal(ResultadoDeFirmaDeWebhook.Invalida, Verificar(Encabezado("ORD01ABC", ts), idDelDato: "ORD01ABC"));
    }

    [Fact]
    public void ElIdConOtrosCaracteresSeFirmaTalCual()
    {
        var ts = EnSegundos(TimeSpan.Zero);

        Assert.Equal(ResultadoDeFirmaDeWebhook.Valida, Verificar(Encabezado("ORD-01_ABC", ts), idDelDato: "ORD-01_ABC"));
        Assert.Equal(ResultadoDeFirmaDeWebhook.Invalida, Verificar(Encabezado("ord-01_abc", ts), idDelDato: "ORD-01_ABC"));
    }

    [Fact]
    public void ElIdNumericoSeFirmaTalCual() =>
        Assert.Equal(
            ResultadoDeFirmaDeWebhook.Valida,
            Verificar(Encabezado("123456", EnSegundos(TimeSpan.Zero)), idDelDato: "123456"));

    // Ventana: hasta 10 minutos atrás y 2 adelante, inclusive.
    [Theory]
    [InlineData(-600, ResultadoDeFirmaDeWebhook.Valida)]
    [InlineData(-601, ResultadoDeFirmaDeWebhook.FueraDeVentana)]
    [InlineData(120, ResultadoDeFirmaDeWebhook.Valida)]
    [InlineData(121, ResultadoDeFirmaDeWebhook.FueraDeVentana)]
    public void ElTsTieneQueEstarDentroDeLaVentanaEnSegundos(int desplazamientoEnSegundos, ResultadoDeFirmaDeWebhook esperado) =>
        Assert.Equal(esperado, Verificar(Encabezado("ord01abc", EnSegundos(TimeSpan.FromSeconds(desplazamientoEnSegundos)))));

    [Theory]
    [InlineData(-600, ResultadoDeFirmaDeWebhook.Valida)]
    [InlineData(-601, ResultadoDeFirmaDeWebhook.FueraDeVentana)]
    [InlineData(120, ResultadoDeFirmaDeWebhook.Valida)]
    [InlineData(121, ResultadoDeFirmaDeWebhook.FueraDeVentana)]
    public void ElTsTieneQueEstarDentroDeLaVentanaEnMilisegundos(int desplazamientoEnSegundos, ResultadoDeFirmaDeWebhook esperado) =>
        Assert.Equal(
            esperado,
            Verificar(Encabezado("ord01abc", (Ahora + TimeSpan.FromSeconds(desplazamientoEnSegundos)).ToUnixTimeMilliseconds())));

    [Fact]
    public void UnTsFueraDeRangoNoRompeYSeRechaza()
    {
        var absurdo = "99999999999999999";

        Assert.Equal(
            ResultadoDeFirmaDeWebhook.FueraDeVentana,
            Verificar($"ts={absurdo},v1={Hex($"id:ord01abc;request-id:{IdDeSolicitud};ts:{absurdo};")}"));
    }

    [Fact]
    public void ElManifiestoTieneElFormatoDeMercadoPago() =>
        Assert.Equal("id:ord01abc;request-id:req-1;ts:1700000000;", FirmaDeWebhookMercadoPago.ArmarManifiesto("ORD01abc", "req-1", "1700000000"));
}
