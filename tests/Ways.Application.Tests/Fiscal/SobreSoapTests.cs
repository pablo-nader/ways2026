using System.Xml.Linq;
using Ways.Infrastructure.Fiscal;

namespace Ways.Application.Tests.Fiscal;

/// <summary>
/// stage-19a-slice2 (task 2.14, 2.16, design D2/D3, targets 28/30): <see cref="SobreSoap"/> es
/// pura — sin certificado, sin <c>HttpClient</c>, sin reloj — así que el golden del sobre
/// <c>loginCms</c> no depende de nada más que un <see cref="XElement"/> constante (el CMS
/// firmado es un blob opaco desde este archivo: probarlo con un valor de prueba prueba
/// exactamente lo mismo que probarlo con uno real, y evita que la firma criptográfica infle este
/// test).
/// </summary>
public class SobreSoapTests
{
    [Fact]
    public void ElSobreLoginCmsCoincideByteAByteConElGoldenDelManual()
    {
        var raiz = ResolverRaizDelRepositorio();
        var golden = File.ReadAllText(Path.Combine(
            raiz, "tests", "Ways.Application.Tests", "Fiscal", "Fixtures", "Wsaa", "LoginCmsEnvelopeGolden.xml"));

        var sobre = SobreSoap.Construir(SobreSoap.EspacioWsaa, "loginCms", new XElement("in0", "CMS_DE_PRUEBA"));

        Assert.Equal(golden, sobre);
    }

    [Fact]
    public void LaSoapActionDeWsaaEsSiempreVacia()
    {
        Assert.Equal(string.Empty, SobreSoap.AccionDe(SobreSoap.EspacioWsaa, "loginCms"));
    }

    [Fact]
    public void LaSoapActionDeWsfeConcatenaElEspacioDeNombresYLaOperacion()
    {
        Assert.Equal(
            SobreSoap.EspacioWsfe + "FECAESolicitar",
            SobreSoap.AccionDe(SobreSoap.EspacioWsfe, "FECAESolicitar"));
    }

    /// <summary>target 30: TODO golden lleva <c>XDeclaration</c> con <c>UTF-8</c> en mayúsculas y
    /// cero salto de línea (el whitespace ES el contrato — D3). Cubre el sobre WSAA de esta slice;
    /// la slice 3 repite este mismo assert para los goldens de WSFE.</summary>
    [Fact]
    public void TodoSobreLlevaLaDeclaracionXmlYCeroFormato()
    {
        var sobre = SobreSoap.Construir(SobreSoap.EspacioWsaa, "loginCms", new XElement("in0", "X"));

        Assert.StartsWith("<?xml version=\"1.0\" encoding=\"UTF-8\"?>", sobre, StringComparison.Ordinal);
        Assert.DoesNotContain('\n', sobre);
        Assert.DoesNotContain('\r', sobre);
        Assert.DoesNotContain("  ", sobre, StringComparison.Ordinal);
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
