namespace Ways.Application.Tests.Exportacion;

/// <summary>
/// stage-11, Slice 1a (design decisión 4; spec exportacion-de-reportes: "Excel Library
/// Containment"): la librería XLSX solo puede nombrarse desde
/// <c>src/Ways.Infrastructure/Exportacion/ExportadorXlsx.cs</c>. Un scan de fuente, mismo idioma
/// que <see cref="Ways.IntegrationTests.VentasCheckoutTests.NingunLiteralDeToleranciaOVueltoHardcodeadoEnElCaminoDeCheckout"/>
/// — corre en la suite rápida sin DB porque el scan es sobre archivos, no sobre tipos cargados.
/// El raíz del scan es <c>src/</c> solamente: el código de test SÍ lee workbooks con la
/// librería, a propósito (decisión 8).
/// </summary>
public class ContencionDelExportadorTests
{
    private const string NombreDeLaLibreria = "ClosedXML";
    private const string ArchivoAutorizado = "ExportadorXlsx.cs";
    private const string ProyectoAutorizado = "Ways.Infrastructure.csproj";

    [Fact]
    public void SoloExportadorXlsxReferenciaLaLibreriaXlsxEnSrc()
    {
        var raiz = ResolverRaizDelRepositorio();
        var srcDir = Path.Combine(raiz, "src");

        var archivosQueNombranLaLibreria = Directory
            .EnumerateFiles(srcDir, "*.cs", SearchOption.AllDirectories)
            .Where(archivo => File.ReadAllText(archivo).Contains(NombreDeLaLibreria, StringComparison.Ordinal))
            .Select(Path.GetFileName)
            .ToList();

        Assert.Equal([ArchivoAutorizado], archivosQueNombranLaLibreria);
    }

    [Fact]
    public void SoloWaysInfrastructureCsprojReferenciaElPackageDeLaLibreria()
    {
        var raiz = ResolverRaizDelRepositorio();
        var srcDir = Path.Combine(raiz, "src");

        var csprojsQueReferencianElPackage = Directory
            .EnumerateFiles(srcDir, "*.csproj", SearchOption.AllDirectories)
            .Where(csproj => File.ReadAllText(csproj)
                .Contains($"PackageReference Include=\"{NombreDeLaLibreria}\"", StringComparison.Ordinal))
            .Select(Path.GetFileName)
            .ToList();

        Assert.Equal([ProyectoAutorizado], csprojsQueReferencianElPackage);
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
