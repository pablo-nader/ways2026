using Ways.Application.Tests.Infraestructura;

namespace Ways.Application.Tests.Fiscal;

/// <summary>
/// stage-19a-slice5 (task 5.22, verify criterion 8, target 76 — cross-slice, único target de las
/// 76 no atado a un slice individual): ningún archivo de configuración mergeado a <c>main</c> en
/// la sub-etapa completa (slices 1-5) puede llevar un hostname REAL de ARCA
/// (<c>wswhomo.afip.gov.ar</c>/<c>servicios1.afip.gov.ar</c>) como default — <c>Ways:Fiscal:UrlWsaa</c>/
/// <c>Ways:Fiscal:UrlWsfe</c> (<c>DependencyInjection.AgregarInfrastructure</c>) están AUSENTES a
/// propósito de todo <c>appsettings*.json</c> shipeado; 19b los carga recién cuando exista una
/// credencial real. Barrido del REPO completo (no solo <c>src/Ways.Api</c>): un hostname real
/// filtrado a cualquier <c>appsettings*.json</c>, incluido uno de test, igual violaría el criterio.
/// </summary>
public class ConfiguracionSinHostnameArcaTests
{
    private static readonly string[] HostnamesReales = ["wswhomo.afip.gov.ar", "servicios1.afip.gov.ar"];

    [Fact]
    public void NingunAppsettingsShippeadoContieneUnHostnameRealDeArca()
    {
        var raiz = RaizDelRepositorio.Resolver();
        var archivos = Directory.EnumerateFiles(raiz, "appsettings*.json", SearchOption.AllDirectories)
            .Where(a => !EstaBajoCarpetaExcluida(raiz, a))
            .ToList();

        // Guard del propio test: si algún día no hay NINGÚN appsettings*.json en el repo, esto es
        // un falso-verde (el scan "pasaría" sin haber mirado nada) — mutation-proof-tests regla 2.
        Assert.NotEmpty(archivos);

        foreach (var archivo in archivos)
        {
            var contenido = File.ReadAllText(archivo);
            foreach (var hostname in HostnamesReales)
            {
                Assert.DoesNotContain(hostname, contenido, StringComparison.OrdinalIgnoreCase);
            }
        }
    }

    private static bool EstaBajoCarpetaExcluida(string raizDeBusqueda, string archivo)
    {
        var relativo = Path.GetRelativePath(raizDeBusqueda, archivo);
        var segmentos = relativo.Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        return segmentos.Any(segmento =>
            string.Equals(segmento, "bin", StringComparison.OrdinalIgnoreCase)
            || string.Equals(segmento, "obj", StringComparison.OrdinalIgnoreCase)
            || string.Equals(segmento, "node_modules", StringComparison.OrdinalIgnoreCase));
    }
}
