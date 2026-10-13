using System.Text.RegularExpressions;
using Ways.Application.Tests.Infraestructura;

namespace Ways.Application.Tests.Fiscal;

/// <summary>
/// Facturación por medio de pago, T4: pinea por texto lo que ningún test de comportamiento puede matar de forma
/// determinista. El lost update del acumulado exige carreras reales; acá se fija el orden de los locks, la lectura
/// única de saldos bajo el lock, que el acumulado solo se escriba con SQL crudo (<c>SaveChanges</c> rechaza la tabla) y
/// que el endpoint no le pase a la decisión el token de la solicitud (un corte del cliente no es observable de forma
/// determinista por HTTP).
///
/// Cada test verifica el texto del archivo, no el comportamiento, y su nombre dice solo eso. Que la decisión nunca haga
/// fallar la venta y que una decisión fallida la complete la venta siguiente lo prueban los tests de
/// <c>DecisionDeFacturacionAlIngerirTests</c> (<c>UnaDecisionQueFallaNoFallaLaVentaYSeCompletaEnElReenvio</c> y
/// <c>LaVentaCuyaDecisionFalloSeDecideEnLaSiguienteVentaDelPuntoDeVentaConSuPropioAcumuladoYUnaSolaVez</c>).
/// </summary>
public class ServicioDeDecisionDeFacturacionEstructuralTests
{
    private static string Fuente(string ruta) =>
        File.ReadAllText(Path.Combine(RaizDelRepositorio.Resolver(), "src", Path.Combine(ruta.Split('/')))).Replace("\r\n", "\n");

    private static string Servicio => Fuente("Ways.Application/Fiscal/ServicioDeDecisionDeFacturacion.cs");

    private static string Entre(string fuente, string desde, string hasta)
    {
        var inicio = fuente.IndexOf(desde, StringComparison.Ordinal);
        Assert.True(inicio >= 0, $"No se encontró '{desde}'.");
        var fin = fuente.IndexOf(hasta, inicio + desde.Length, StringComparison.Ordinal);
        Assert.True(fin > inicio, $"No se encontró '{hasta}' después de '{desde}'.");
        return fuente[inicio..fin];
    }

    private static string CuerpoDeDecidir() =>
        Entre(Servicio, "public async Task<EstadoFiscalDeVenta> DecidirAsync(", "// ---- Lecturas");

    [Fact]
    public void ElTextoDeDecidirAsyncLlamaALosLocksYLasEscriturasEnElOrdenDocumentado()
    {
        var cuerpo = CuerpoDeDecidir();
        string[] orden =
        [
            "BloquearVentaAsync(",
            "LeerDecisionAsync(",
            "db.CertificadosFiscales",
            "LeerPagosAsync(",
            "AsegurarAcumuladoAsync(",
            "BloquearAcumuladosAsync(",
            "PoliticaDeFacturacionPorMedio.Decidir(",
            "AplicarAportesAsync(",
            "InsertarDecisionAsync(",
            "MarcarDecididaAsync(",
            "CommitAsync("
        ];

        // Cada marca se busca después de la anterior: un token que aparece más de una vez (MarcarDecididaAsync y
        // CommitAsync también cierran las ramas tempranas) no puede satisfacer el orden con una ocurrencia equivocada.
        var desde = 0;
        foreach (var marca in orden)
        {
            var posicion = cuerpo.IndexOf(marca, desde, StringComparison.Ordinal);
            Assert.True(posicion >= 0, $"'{marca}' falta o está fuera de orden en DecidirAsync.");
            desde = posicion + marca.Length;
        }
    }

    [Fact]
    public void ElTextoDelServicioPideLosLocksDeFilaYLasSentenciasDeAcumuladoDocumentadas()
    {
        Assert.Equal(1, Contar(CuerpoDeDecidir(), "BloquearAcumuladosAsync("));

        var fuente = Servicio;
        Assert.Contains("ORDER BY id_medio_pago FOR UPDATE", fuente, StringComparison.Ordinal);
        Assert.Contains("FOR UPDATE OF c", fuente, StringComparison.Ordinal);
        Assert.Contains("ON CONFLICT (id_empresa, id_medio_pago, periodo) DO NOTHING", fuente, StringComparison.Ordinal);
    }

    [Fact]
    public void ElTextoDeLaFilaDeDecisionEsUnInsertSimpleSinOnConflict()
    {
        var insercion = Entre(Servicio, "private static async Task<int> InsertarDecisionAsync(", "private static async Task MarcarDecididaAsync");
        Assert.Contains("INSERT INTO facturaciones_de_venta", insercion, StringComparison.Ordinal);
        Assert.DoesNotContain("ON CONFLICT", insercion, StringComparison.Ordinal);
    }

    [Fact]
    public void ElTextoDelServicioNoEscribeConElChangeTrackerYLimpiaElTrackerAlEmpezarCadaIntento()
    {
        var fuente = Servicio;
        Assert.DoesNotContain("SaveChangesAsync(", fuente, StringComparison.Ordinal);
        Assert.DoesNotMatch(new Regex(@"\bdb\.\w+\.Add\(", RegexOptions.None, TimeSpan.FromSeconds(5)), fuente);
        Assert.DoesNotContain("FabricaDeEstrategiaSinReintento", fuente, StringComparison.Ordinal);

        Assert.Matches(
            new Regex(
                @"estrategia\.ExecuteAsync\(async _ =>\s*\{\s*db\.ChangeTracker\.Clear\(\);",
                RegexOptions.None,
                TimeSpan.FromSeconds(5)),
            CuerpoDeDecidir());
    }

    [Fact]
    public void ElTextoDeDecidirTrasLaVentaYDelBarridoTieneUnCatchGeneralYNoDeclaraElTokenDeLaSolicitud()
    {
        var fuente = Servicio;
        var decidirTras = Entre(fuente, "public async Task<EstadoFiscalDeVenta> DecidirTrasLaVentaAsync(", "public async Task SanearDecisionesDebidasAsync(");
        var barrido = Entre(fuente, "public async Task SanearDecisionesDebidasAsync(", "private async Task RegistrarFalloAsync(");
        var decidirYSanear = Entre(fuente, "public async Task<EstadoFiscalDeVenta> DecidirYSanearAsync(", "public async Task<EstadoFiscalDeVenta> DecidirTrasLaVentaAsync(");

        Assert.Contains("catch (Exception ex)", decidirTras, StringComparison.Ordinal);
        Assert.Contains("catch (Exception ex)", barrido, StringComparison.Ordinal);
        Assert.Contains("DecidirAsync(idComprobanteVenta, presupuesto.Token)", decidirTras, StringComparison.Ordinal);
        Assert.Contains("new CancellationTokenSource(PresupuestoDeBarrido)", barrido, StringComparison.Ordinal);

        // Las firmas públicas no tienen un CancellationToken que el endpoint pudiera llenar con el de la solicitud.
        foreach (var firma in new[] { decidirTras, barrido, decidirYSanear })
        {
            var linea = firma[..firma.IndexOf(')')];
            Assert.DoesNotContain("CancellationToken", linea, StringComparison.Ordinal);
        }
    }

    [Fact]
    public void ElTextoDelEndpointLlamaALaDecisionConBarridoDespuesDeEmitirSinPasarElTokenDeLaSolicitud()
    {
        var endpoint = Fuente("Ways.Api/Endpoints/VentasEndpoints.cs");
        var emision = endpoint.IndexOf("await servicio.EmitirAsync(solicitud, ct)", StringComparison.Ordinal);
        Assert.True(emision >= 0, "No se encontró la emisión de la venta.");
        var decision = endpoint.IndexOf("decision.DecidirYSanearAsync(", emision, StringComparison.Ordinal);
        Assert.True(decision > emision, "La decisión corre después de emitir la venta.");

        var llamadas = Regex.Matches(
            endpoint[decision..],
            @"decision\.(DecidirTrasLaVentaAsync|SanearDecisionesDebidasAsync|DecidirYSanearAsync)\(([^)]*)\)",
            RegexOptions.None,
            TimeSpan.FromSeconds(5));
        Assert.Single(llamadas);
        Assert.All(llamadas, l => Assert.DoesNotMatch(new Regex(@"\bct\b", RegexOptions.None, TimeSpan.FromSeconds(5)), l.Groups[2].Value));
    }

    [Fact]
    public void ElTextoDeDecidirYSanearSaltaElBarridoCuandoLaDecisionTerminoSinDecidirYLoCorreDespuesDeDecidir()
    {
        var cuerpo = Entre(Servicio, "public async Task<EstadoFiscalDeVenta> DecidirYSanearAsync(", "public async Task<EstadoFiscalDeVenta> DecidirTrasLaVentaAsync(");
        var decidir = cuerpo.IndexOf("DecidirTrasLaVentaAsync(idComprobanteVenta)", StringComparison.Ordinal);
        var condicion = cuerpo.IndexOf("estado.Estado != EstadosFiscalesDeVenta.SinDecidir", StringComparison.Ordinal);
        var barrido = cuerpo.IndexOf("SanearDecisionesDebidasAsync(idPuntoVenta, idComprobanteVenta)", StringComparison.Ordinal);
        Assert.True(decidir >= 0 && condicion > decidir && barrido > condicion, "Decidir, condición y barrido, en ese orden.");
    }

    private static int Contar(string texto, string marca)
    {
        var cantidad = 0;
        for (var i = texto.IndexOf(marca, StringComparison.Ordinal); i >= 0; i = texto.IndexOf(marca, i + 1, StringComparison.Ordinal))
        {
            cantidad++;
        }

        return cantidad;
    }
}
