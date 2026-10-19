using System.ComponentModel;
using System.Globalization;
using System.Reflection;
using System.Text.RegularExpressions;
using ModelContextProtocol.Server;
using Ways.Api.ConectorMcp;
using Ways.Application.Reportes;
using Ways.Domain.Reportes;

namespace Ways.IntegrationTests;

/// <summary>
/// Los números que las descripciones de las herramientas le prometen a Claude salen de las constantes que el código
/// aplica. Una <c>[Description]</c> solo admite una expresión constante y un <c>int</c> no se interpola en una
/// <c>const string</c>, así que el texto sigue escrito a mano y esta prueba lo compara con la constante: cambiar el
/// límite sin cambiar la prosa, o al revés, la rompe.
/// </summary>
public partial class ConectorMcpDescripcionesYConstantesTests
{
    private static readonly MethodInfo[] Herramientas = [.. typeof(RegistroDelConectorMcp).Assembly.GetTypes()
        .Where(t => t.GetCustomAttribute<McpServerToolTypeAttribute>() is not null)
        .SelectMany(t => t.GetMethods(BindingFlags.Public | BindingFlags.Static))
        .Where(m => m.GetCustomAttribute<McpServerToolAttribute>() is not null)];

    /// <summary>El tope del rango de fechas lo aplica <c>RangoDeReporte</c> con una constante privada del dominio; se
    /// lee por reflexión para no publicarla solo por esta prueba.</summary>
    private static readonly int SpanMaximoEnDias = (int)typeof(RangoDeReporte)
        .GetField("SpanMaximoEnDias", BindingFlags.NonPublic | BindingFlags.Static)!
        .GetRawConstantValue()!;

    private static MethodInfo Herramienta(string nombre) =>
        Herramientas.Single(m => m.GetCustomAttribute<McpServerToolAttribute>()!.Name == nombre);

    private static string Descripcion(string herramienta, string? parametro = null)
    {
        var metodo = Herramienta(herramienta);
        var conDescripcion = parametro is null
            ? (ICustomAttributeProvider)metodo
            : metodo.GetParameters().Single(p => p.Name == parametro);
        return conDescripcion.GetCustomAttributes(typeof(DescriptionAttribute), false)
            .Cast<DescriptionAttribute>().Single().Description;
    }

    private static string Instrucciones() =>
        (string)typeof(RegistroDelConectorMcp)
            .GetField("Instrucciones", BindingFlags.NonPublic | BindingFlags.Static)!
            .GetRawConstantValue()!;

    public static TheoryData<string, string?, string> Fragmentos()
    {
        var datos = new TheoryData<string, string?, string>
        {
            { "buscar_articulos", null, $"las primeras {HerramientasDeCompras.MaximoDePalabras} de al menos 2 letras" },
            { "buscar_articulos", null, $"hasta {HerramientasDeCompras.MaximoDeCandidatosPorLinea} candidatos" },
            { "buscar_articulos", null, $"Hasta {HerramientasDeCompras.MaximoDeLineas} líneas." },
            { "crear_borrador_de_compra", "lineas", $"hasta {ArmadoDelBorradorDeCompra.MaximoDeLineas}." },
            {
                "articulo_info", null,
                $"devuelve hasta {ServicioDeConsultasDeVentas.MaximoDeOpcionesListadasEnUnError} para que se elija"
            },
            { "articulo_info", null, $"de los últimos {ServicioDeFichaDeArticulo.DiasDeVentas} días contando hoy" },
            {
                "articulo_info", "compras",
                $"de 1 a {ServicioDeFichaDeArticulo.ComprasMaximas}. " +
                $"Por defecto {ServicioDeFichaDeArticulo.ComprasPorDefecto}."
            },
            {
                "ventas_resumen", null,
                $"abarca {ServicioDeConsultasDeVentas.DiasMaximosDeLaTablaPorDia} días distintos o menos"
            }
        };

        foreach (var ventas in new[] { "ventas_resumen", "ventas_agrupadas", "ventas_por_articulo" })
        {
            datos.Add(ventas, null, $"como máximo {SpanMaximoEnDias} días");
            datos.Add(ventas, "desde", $"como máximo {SpanMaximoEnDias} días después.");
        }

        foreach (var conLimite in new[] { "ventas_agrupadas", "ventas_por_articulo" })
        {
            datos.Add(
                conLimite, "limite",
                $"de 1 a {HerramientasDeVentas.LimiteMaximo}. Por defecto {HerramientasDeVentas.LimitePorDefecto}.");
        }

        return datos;
    }

    [Theory]
    [MemberData(nameof(Fragmentos))]
    public void LaDescripcionDiceElNumeroDeSuConstante(string herramienta, string? parametro, string fragmento) =>
        Assert.Contains(fragmento, Descripcion(herramienta, parametro), StringComparison.Ordinal);

    /// <summary>Cada "N días" de las instrucciones es el tope del rango de las consultas de ventas o la ventana de
    /// ventas de la ficha, y cada uno aparece.</summary>
    [Fact]
    public void LosDiasQueNombranLasInstruccionesSalenDeSusConstantes()
    {
        var dias = DiasNombrados().Matches(Instrucciones())
            .Select(m => int.Parse(m.Groups[1].Value, CultureInfo.InvariantCulture))
            .ToHashSet();

        Assert.Equal(
            new[] { ServicioDeFichaDeArticulo.DiasDeVentas, SpanMaximoEnDias }.Order(),
            dias.Order());
    }

    [GeneratedRegex(@"(\d+) días")]
    private static partial Regex DiasNombrados();
}
