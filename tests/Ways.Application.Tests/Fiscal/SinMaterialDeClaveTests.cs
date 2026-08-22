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

    [Fact]
    public void NingunArchivoDeSrcOTestsEsOContieneMaterialDeClave()
    {
        var raiz = ResolverRaizDelRepositorio();

        foreach (var carpeta in new[] { "src", "tests" })
        {
            var directorio = Path.Combine(raiz, carpeta);

            foreach (var archivo in Directory.EnumerateFiles(directorio, "*", SearchOption.AllDirectories))
            {
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

    private static string ResolverRaizDelRepositorio()
    {
        var directorio = AppContext.BaseDirectory;

        while (directorio is not null && !File.Exists(Path.Combine(directorio, "Ways.slnx")))
        {
            directorio = Path.GetDirectoryName(directorio.TrimEnd(Path.DirectorySeparatorChar));
        }

        return directorio ?? throw new InvalidOperationException("No se encontró la raíz del repositorio (Ways.slnx).");
    }
}
