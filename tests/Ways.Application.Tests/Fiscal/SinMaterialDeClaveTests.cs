using Ways.Application.Tests.Infraestructura;

namespace Ways.Application.Tests.Fiscal;

/// <summary>
/// stage-19a-slice2 (task 2.21, design D7/proposal decisión 12, verify criterion 7, target 35):
/// barrido de repositorio — ni <c>src/</c> ni <c>tests/</c> pueden contener material de clave
/// real. El certificado de prueba lo generan los TESTS en runtime (<see cref="CertificadoDePrueba"/>)
/// y nunca toca el disco del repo, así que este scan no tiene ningún falso positivo legítimo que
/// excluir (a diferencia de <c>SobreSoapAislamientoTests</c>, acá no hay ningún archivo
/// "autorizado").
/// </summary>
public class SinMaterialDeClaveTests
{
    /// <summary>Este propio archivo tiene que citar los marcadores como texto literal para
    /// definirlos — se excluye del contenido escaneado (no de la carpeta: sigue viviendo bajo
    /// <c>tests/</c> y sigue sujeto al chequeo de extensión).</summary>
    private const string ArchivoDelPropioTest = "SinMaterialDeClaveTests.cs";

    private static readonly string[] ExtensionesDeClave = [".pfx", ".p12", ".pem", ".key", ".cer", ".crt"];

    private static readonly string[] MarcadoresDeClave =
    [
        "-----BEGIN PRIVATE KEY-----",
        "-----BEGIN RSA PRIVATE KEY-----",
        "-----BEGIN ENCRYPTED PRIVATE KEY-----",
        "-----BEGIN EC PRIVATE KEY-----",
    ];

    private static readonly string[] ExtensionesDeTexto =
        [".cs", ".csproj", ".json", ".xml", ".md", ".config", ".sql", ".yaml", ".yml"];

    /// <summary>Carpetas de build que el scan no tiene que atravesar: no son fuente, y en
    /// <c>bin/</c> en particular puede haber binarios grandes — leerlos con
    /// <see cref="File.ReadAllText(string)"/> es costo real, no solo ruido de falsos
    /// positivos.</summary>
    private static readonly string[] CarpetasExcluidas = ["bin", "obj"];

    [Fact]
    public void NingunArchivoDeSrcOTestsEsOContieneMaterialDeClave()
    {
        var raiz = RaizDelRepositorio.Resolver();

        foreach (var carpeta in new[] { "src", "tests" })
        {
            var directorio = Path.Combine(raiz, carpeta);

            foreach (var archivo in Directory.EnumerateFiles(directorio, "*", SearchOption.AllDirectories))
            {
                if (EstaBajoCarpetaExcluida(directorio, archivo))
                {
                    continue;
                }

                var extension = Path.GetExtension(archivo);

                Assert.DoesNotContain(
                    extension, ExtensionesDeClave, StringComparer.OrdinalIgnoreCase);

                if (!ExtensionesDeTexto.Contains(extension, StringComparer.OrdinalIgnoreCase)
                    || Path.GetFileName(archivo) == ArchivoDelPropioTest)
                {
                    continue;
                }

                var contenido = File.ReadAllText(archivo);
                foreach (var marcador in MarcadoresDeClave)
                {
                    Assert.DoesNotContain(marcador, contenido, StringComparison.Ordinal);
                }
            }
        }
    }

    private static bool EstaBajoCarpetaExcluida(string raizDeBusqueda, string archivo)
    {
        var relativo = Path.GetRelativePath(raizDeBusqueda, archivo);
        var segmentos = relativo.Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        return segmentos.Any(segmento => CarpetasExcluidas.Contains(segmento, StringComparer.OrdinalIgnoreCase));
    }
}
