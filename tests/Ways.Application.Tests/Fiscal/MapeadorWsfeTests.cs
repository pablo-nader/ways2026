using System.Globalization;
using Ways.Application.Fiscal;
using Ways.Application.Tests.Infraestructura;
using Ways.Infrastructure.Fiscal;

namespace Ways.Application.Tests.Fiscal;

/// <summary>
/// stage-19a-slice3 (tasks 3.6-3.8, design D2/D3, targets 37-39): <see cref="MapeadorWsfe"/> es
/// puro — sin <c>HttpClient</c>, sin TA real, sin circuito — así que el golden del sobre
/// <c>FECAESolicitar</c> compara bytes contra una función, mismo criterio que
/// <c>SobreSoapTests</c>/<c>GeneradorDeTraTests</c> en la slice 2.
/// </summary>
public class MapeadorWsfeTests
{
    private static readonly TicketDeAcceso Ticket =
        new("TOKEN_WSFE_DE_PRUEBA", "SIGN_WSFE_DE_PRUEBA", new DateTimeOffset(2026, 1, 15, 22, 0, 0, TimeSpan.Zero));

    private const string Cuit = "20111111111";
    private static readonly ClaveDeSerie Serie = new(PtoVta: 3, CbteTipo: 6);

    private static readonly SolicitudDeCae SolicitudDeReferencia = new(
        Serie: Serie,
        CbteDesde: 105,
        CbteHasta: 105,
        Concepto: 1,
        DocTipo: 99,
        DocNro: 0,
        CbteFch: new DateOnly(2026, 1, 15),
        ImpTotal: 121.00m,
        ImpTotConc: 0.00m,
        ImpNeto: 100.00m,
        ImpOpEx: 0.00m,
        ImpIVA: 21.00m,
        ImpTrib: 0.00m,
        CondicionIVAReceptorId: 5,
        Iva: [new ItemIvaFiscal(5, 100.00m, 21.00m)]);

    [Fact]
    public void ElSobreFecaeSolicitarCoincideByteAByteConElGoldenTranscripto()
    {
        var raiz = RaizDelRepositorio.Resolver();
        var golden = File.ReadAllText(Path.Combine(
            raiz, "tests", "Ways.Application.Tests", "Fiscal", "Fixtures", "Wsfe",
            "FecaeSolicitarRequestGolden.xml"));

        var sobre = MapeadorWsfe.ConstruirFecaeSolicitar(Ticket, Cuit, SolicitudDeReferencia);

        Assert.Equal(golden, sobre); // target 37
    }

    /// <summary>target 38: un mutante que use la cultura ACTUAL del hilo en vez de
    /// <see cref="CultureInfo.InvariantCulture"/> pasaría igual bajo la cultura por defecto de CI
    /// (que suele ser invariante/en-US) — el kill real exige cambiar la cultura actual a una que
    /// use <c>,</c> como separador decimal y confirmar que el sobre SIGUE usando <c>.</c>.</summary>
    [Fact]
    public void ElFormatoDeMonedaEsInvariantCultureAunBajoUnaCulturaActualConComoDecimal()
    {
        var culturaOriginal = CultureInfo.CurrentCulture;
        try
        {
            CultureInfo.CurrentCulture = new CultureInfo("es-AR");

            var sobre = MapeadorWsfe.ConstruirFecaeSolicitar(Ticket, Cuit, SolicitudDeReferencia);

            Assert.Contains("<ImpTotal>121.00</ImpTotal>", sobre, StringComparison.Ordinal);
            Assert.Contains("<ImpNeto>100.00</ImpNeto>", sobre, StringComparison.Ordinal);
            Assert.Contains("<CbteFch>20260115</CbteFch>", sobre, StringComparison.Ordinal);
            Assert.DoesNotContain("121,00", sobre, StringComparison.Ordinal);
        }
        finally
        {
            CultureInfo.CurrentCulture = culturaOriginal;
        }
    }

    /// <summary>target 39: <c>Concepto = 1</c> (productos) nunca emite
    /// <c>FchServDesde</c>/<c>FchServHasta</c>/<c>FchVtoPago</c> — ni siquiera vacíos.</summary>
    [Fact]
    public void LosElementosOpcionalesDeConcepto1SeOmitenNuncaSeEmitenVacios()
    {
        var sobre = MapeadorWsfe.ConstruirFecaeSolicitar(Ticket, Cuit, SolicitudDeReferencia);

        Assert.DoesNotContain("FchServDesde", sobre, StringComparison.Ordinal);
        Assert.DoesNotContain("FchServHasta", sobre, StringComparison.Ordinal);
        Assert.DoesNotContain("FchVtoPago", sobre, StringComparison.Ordinal);
    }

    [Fact]
    public void LosElementosOpcionalesSeEmitenCuandoSeProveenParaConcepto2Y3()
    {
        var solicitudDeServicio = SolicitudDeReferencia with
        {
            Concepto = 2,
            FchServDesde = new DateOnly(2026, 1, 1),
            FchServHasta = new DateOnly(2026, 1, 31),
            FchVtoPago = new DateOnly(2026, 2, 10)
        };

        var sobre = MapeadorWsfe.ConstruirFecaeSolicitar(Ticket, Cuit, solicitudDeServicio);

        Assert.Contains("<FchServDesde>20260101</FchServDesde>", sobre, StringComparison.Ordinal);
        Assert.Contains("<FchServHasta>20260131</FchServHasta>", sobre, StringComparison.Ordinal);
        Assert.Contains("<FchVtoPago>20260210</FchVtoPago>", sobre, StringComparison.Ordinal);
    }
}
