using Ways.Application.Tests.Infraestructura;

namespace Ways.Application.Tests.Fiscal;

/// <summary>
/// stage-19a-slice2 (task 2.15, design D2, verify criterion 10, target 29): <c>SobreSoap.cs</c>
/// es el único archivo de <c>src/</c> que puede nombrar el protocolo SOAP. El marcador NO es la
/// substring "soap" a secas (case-insensitive) porque el propio nombre del tipo aislado,
/// <c>SobreSoap</c>, contiene esa substring y OTROS archivos de <c>src/</c> lo referencian a
/// propósito (<c>ClienteWsaa</c>, y <c>ClienteWsfe</c> en la slice 3) — eso es justamente la
/// abstracción que design D2 pide: llamar a <c>SobreSoap</c> como caja negra sin "saber SOAP".
/// Por eso el scan primero borra toda ocurrencia del identificador <c>SobreSoap</c> del contenido
/// y recién ahí busca los marcadores reales del protocolo: el namespace URI, el prefijo
/// <c>soapenv</c> y el stack rechazado por la decisión 7 del proposal
/// (<c>System.ServiceModel</c>).
/// </summary>
public class SobreSoapAislamientoTests
{
    private const string ArchivoAutorizado = "SobreSoap.cs";
    private static readonly string[] Marcadores = ["soapenv", "schemas.xmlsoap.org", "ServiceModel"];

    /// <summary>Carpetas de build que el scan no tiene que atravesar — `obj/` en particular puede
    /// contener `.cs` generados (p. ej. `GlobalUsings.g.cs`) que no son fuente propia del
    /// proyecto.</summary>
    private static readonly string[] CarpetasExcluidas = ["bin", "obj"];

    [Fact]
    public void SoloSobreSoapNombraElProtocoloSoapEnSrc()
    {
        var raiz = RaizDelRepositorio.Resolver();
        var srcDir = Path.Combine(raiz, "src");

        var archivosCs = Directory.EnumerateFiles(srcDir, "*.cs", SearchOption.AllDirectories)
            .Where(archivo => !EstaBajoCarpetaExcluida(srcDir, archivo))
            .ToList();

        foreach (var marcador in Marcadores)
        {
            var archivosQueLoNombran = archivosCs
                .Where(archivo =>
                {
                    var contenido = File.ReadAllText(archivo);
                    var sinReferenciasAlTipo = contenido.Replace("SobreSoap", string.Empty, StringComparison.Ordinal);
                    return sinReferenciasAlTipo.Contains(marcador, StringComparison.Ordinal);
                })
                .Select(Path.GetFileName)
                .ToList();

            Assert.True(
                archivosQueLoNombran.Count == 0 || archivosQueLoNombran is [ArchivoAutorizado],
                $"El marcador '{marcador}' apareció fuera de {ArchivoAutorizado}: {string.Join(", ", archivosQueLoNombran)}");
        }
    }

    private static bool EstaBajoCarpetaExcluida(string raizDeBusqueda, string archivo)
    {
        var relativo = Path.GetRelativePath(raizDeBusqueda, archivo);
        var segmentos = relativo.Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        return segmentos.Any(segmento => CarpetasExcluidas.Contains(segmento, StringComparer.OrdinalIgnoreCase));
    }
}
