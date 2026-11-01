using System.Text.RegularExpressions;
using Ways.Application.Tests.Infraestructura;

namespace Ways.Application.Tests.MercadoPago;

/// <summary>
/// E3-T2: todo servicio que crea pagos de una venta (una fila de <c>Cobro</c>) tiene que juzgar su forma con
/// <c>ReglaDeCobroEnVenta.ExigirFormaDelPago</c>: así un medio con proveedor nunca entra sin un cobro aprobado de Mercado Pago, y
/// un pago con cobro nunca entra por un camino que no lo valida.
///
/// Qué es: un barrido de TEXTO sobre <c>src/Ways.Application</c>. Cada archivo que construye un <c>Cobro</c> con
/// <c>new Cobro</c> tiene que contener la llamada, o figurar en <see cref="Exentos"/> con su motivo. Una entrada que ya no
/// construye un <c>Cobro</c> también falla, para que la lista no acumule fantasmas. Las pruebas de autoverificación alimentan la
/// clasificación con muestras en memoria.
///
/// Qué NO ve: que la llamada esté antes de la escritura, ni que se le pase el proveedor del medio correcto; eso lo cubren las
/// pruebas de integración de <c>MercadoPagoVentaTests</c> sobre la venta, el cambio de medios y los recibos. Tampoco ve un
/// escritor que inserte en <c>cobros</c> con SQL crudo.
/// </summary>
public class EscritoresDePagosDeVentaEstructuralTests
{
    private static readonly Regex ConstruyeUnCobro = new(@"\bnew\s+Cobro\s*[({]", RegexOptions.Compiled);

    private const string Llamada = "ReglaDeCobroEnVenta.ExigirFormaDelPago(";

    /// <summary>Archivos que construyen un <c>Cobro</c> sin juzgar su forma, con el motivo. Hoy ninguno.</summary>
    private static readonly IReadOnlyDictionary<string, string> Exentos = new Dictionary<string, string>();

    internal static IReadOnlyList<string> ArchivosSinLaLlamada(IReadOnlyDictionary<string, string> fuentes) =>
        [.. fuentes
            .Where(f => ConstruyeUnCobro.IsMatch(f.Value) && !f.Value.Contains(Llamada, StringComparison.Ordinal))
            .Select(f => f.Key)
            .Where(archivo => !Exentos.ContainsKey(archivo))
            .Order(StringComparer.Ordinal)];

    private static Dictionary<string, string> FuentesDeLaAplicacion()
    {
        var raiz = RaizDelRepositorio.Resolver();
        var carpeta = Path.Combine(raiz, "src", "Ways.Application");
        return Directory.EnumerateFiles(carpeta, "*.cs", SearchOption.AllDirectories)
            .Where(f => !f.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}", StringComparison.Ordinal)
                && !f.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}", StringComparison.Ordinal))
            .ToDictionary(f => Path.GetRelativePath(raiz, f).Replace(Path.DirectorySeparatorChar, '/'), File.ReadAllText);
    }

    [Fact]
    public void TodoEscritorDePagosDeVentaJuzgaLaFormaDelPago()
    {
        var sinLlamada = ArchivosSinLaLlamada(FuentesDeLaAplicacion());

        Assert.True(
            sinLlamada.Count == 0,
            "Construyen un Cobro sin ReglaDeCobroEnVenta.ExigirFormaDelPago: " + string.Join(", ", sinLlamada));
    }

    [Fact]
    public void LosCuatroEscritoresConocidosConstruyenUnCobro()
    {
        var conCobro = FuentesDeLaAplicacion()
            .Where(f => ConstruyeUnCobro.IsMatch(f.Value))
            .Select(f => Path.GetFileName(f.Key))
            .Order(StringComparer.Ordinal)
            .ToList();

        Assert.Equal(
            ["ServicioDeCambioDeMedios.cs", "ServicioDeCuentaCorriente.cs", "ServicioDeFacturacionDeRemitos.cs", "ServicioDeVentas.cs"],
            conCobro);
    }

    [Fact]
    public void ExentosNoAcumulaFantasmas() =>
        Assert.All(Exentos.Keys, archivo => Assert.True(
            File.Exists(Path.Combine(RaizDelRepositorio.Resolver(), archivo)) && ConstruyeUnCobro.IsMatch(
                File.ReadAllText(Path.Combine(RaizDelRepositorio.Resolver(), archivo))),
            $"{archivo} ya no construye un Cobro: sacalo de Exentos."));

    [Fact]
    public void LaClasificacionDetectaUnEscritorSinLaLlamadaYAceptaUnoConLlamada()
    {
        var fuentes = new Dictionary<string, string>
        {
            ["a/Sin.cs"] = "var p = new Cobro { Importe = 1 };",
            ["a/Con.cs"] = "ReglaDeCobroEnVenta.ExigirFormaDelPago(x); var p = new Cobro { Importe = 1 };",
            ["a/Ninguno.cs"] = "var p = new CobroDeVenta(1);"
        };

        Assert.Equal(["a/Sin.cs"], ArchivosSinLaLlamada(fuentes));
    }
}
