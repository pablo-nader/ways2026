using Ways.Application.Tests.Infraestructura;

namespace Ways.Application.Tests.Fiscal;

/// <summary>
/// Facturación por medio de pago, T4: pinea por texto lo que ningún test de comportamiento puede matar de forma
/// determinista. El lost update del acumulado exige carreras reales; acá se fija el orden de los locks, la lectura
/// única de saldos bajo el lock y que el acumulado solo se escriba con SQL crudo (<c>SaveChanges</c> rechaza la tabla).
/// </summary>
public class ServicioDeDecisionDeFacturacionEstructuralTests
{
    private static string Fuente(string ruta) =>
        File.ReadAllText(Path.Combine(RaizDelRepositorio.Resolver(), "src", Path.Combine(ruta.Split('/')))).Replace("\r\n", "\n");

    private static string Servicio => Fuente("Ways.Application/Fiscal/ServicioDeDecisionDeFacturacion.cs");

    private static string CuerpoDeDecidir()
    {
        var fuente = Servicio;
        var inicio = fuente.IndexOf("public async Task<EstadoFiscalDeVenta> DecidirAsync(", StringComparison.Ordinal);
        Assert.True(inicio >= 0, "No se encontró DecidirAsync.");
        var fin = fuente.IndexOf("// ---- Lecturas", inicio, StringComparison.Ordinal);
        Assert.True(fin > inicio, "No se encontró el final de DecidirAsync.");
        return fuente[inicio..fin];
    }

    [Fact]
    public void LosLocksYLasEscriturasSiguenElOrdenDocumentado()
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

        var anterior = -1;
        foreach (var marca in orden)
        {
            var posicion = marca is "CommitAsync(" or "MarcarDecididaAsync(" ? cuerpo.LastIndexOf(marca, StringComparison.Ordinal) : cuerpo.IndexOf(marca, StringComparison.Ordinal);
            Assert.True(posicion > anterior, $"'{marca}' falta o está fuera de orden en DecidirAsync.");
            anterior = posicion;
        }
    }

    [Fact]
    public void LosSaldosSeLeenUnaSolaVezYBajoElLockEnOrdenDeMedio()
    {
        var cuerpo = CuerpoDeDecidir();
        Assert.Equal(1, Contar(cuerpo, "BloquearAcumuladosAsync("));

        var fuente = Servicio;
        Assert.Contains("ORDER BY id_medio_pago FOR UPDATE", fuente, StringComparison.Ordinal);
        Assert.Contains("FOR UPDATE OF c", fuente, StringComparison.Ordinal);
        Assert.Contains("ON CONFLICT (id_empresa, id_medio_pago, periodo) DO NOTHING", fuente, StringComparison.Ordinal);
        Assert.Contains("ON CONFLICT (id_comprobante_venta, id_tenant) DO NOTHING", fuente, StringComparison.Ordinal);
    }

    [Fact]
    public void ElServicioNuncaEscribeConElChangeTrackerYReintentaConElTrackerLimpio()
    {
        var fuente = Servicio;
        Assert.DoesNotContain("SaveChangesAsync(", fuente, StringComparison.Ordinal);
        Assert.DoesNotMatch(new System.Text.RegularExpressions.Regex(@"\bdb\.\w+\.Add\(", System.Text.RegularExpressions.RegexOptions.None, TimeSpan.FromSeconds(5)), fuente);
        Assert.DoesNotContain("FabricaDeEstrategiaSinReintento", fuente, StringComparison.Ordinal);

        Assert.Matches(
            new System.Text.RegularExpressions.Regex(
                @"estrategia\.ExecuteAsync\(async \(\) =>\s*\{\s*db\.ChangeTracker\.Clear\(\);",
                System.Text.RegularExpressions.RegexOptions.None,
                TimeSpan.FromSeconds(5)),
            CuerpoDeDecidir());
    }

    [Fact]
    public void LaDecisionNuncaHaceFallarLaVentaYElEndpointLaLlamaDespuesDelCommit()
    {
        Assert.Contains("catch (Exception ex)", Servicio, StringComparison.Ordinal);

        var endpoint = Fuente("Ways.Api/Endpoints/VentasEndpoints.cs");
        var emision = endpoint.IndexOf("await servicio.EmitirAsync(solicitud, ct)", StringComparison.Ordinal);
        var decision = endpoint.IndexOf("decision.DecidirTrasLaVentaAsync(", StringComparison.Ordinal);
        Assert.True(emision >= 0 && decision > emision, "La decisión corre después de emitir la venta.");
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
