using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Http;
using Ways.Api.Spa;

namespace Ways.IntegrationTests;

/// <summary>
/// Las reglas puras del reparto del front (<see cref="RutasDeLaSpa"/>): qué ruta es heredada y qué
/// entrada sirve cada prefijo. No toca el host de la API; el comportamiento servido lo prueba
/// <see cref="RutasDeLaSpaEnElServidorTests"/>.
/// </summary>
public class RutasDeLaSpaTests
{
    [Fact]
    public void LaListaDeRutasHeredadasEstaOrdenadaYSinRepetidos()
    {
        var lista = RutasDeLaSpa.RutasHeredadasDelBackOffice;

        Assert.Equal(lista.OrderBy(r => r, StringComparer.Ordinal), lista);
        Assert.Equal(lista.Count, lista.Distinct(StringComparer.Ordinal).Count());
        Assert.All(lista, r => Assert.Equal(r.ToLowerInvariant(), r));
    }

    /// <summary>App.tsx es la fuente de verdad de las rutas: si alguien agrega o saca una pantalla de nivel
    /// superior sin tocar la lista, los favoritos de esa pantalla dejan de redirigir (o redirigen una ruta que
    /// ya no existe). Compara el primer segmento de cada <c>path="/..."</c> contra la lista.</summary>
    [Fact]
    public void LaListaDeRutasHeredadasCoincideConLasRutasDeAppTsx()
    {
        var app = File.ReadAllText(Path.Combine(RaizDelRepositorio(), "src", "Ways.Web", "src", "App.tsx"));

        var deAppTsx = Regex.Matches(app, "path=\"/([^/\"]+)")
            .Select(m => m.Groups[1].Value)
            .Distinct(StringComparer.Ordinal)
            .Order(StringComparer.Ordinal)
            .ToList();

        Assert.NotEmpty(deAppTsx);
        Assert.Equal(deAppTsx, RutasDeLaSpa.RutasHeredadasDelBackOffice);
    }

    [Theory]
    [InlineData("/pos")]
    [InlineData("/pos/")]
    [InlineData("/pos/lo-que-sea")]
    [InlineData("/Login")]
    [InlineData("/descargar")]
    [InlineData("/caja/cierre")]
    [InlineData("/organizacion/tenants")]
    [InlineData("/catalogos-fiscales")]
    public void UnaRutaConPrimerSegmentoHeredadoEsHeredada(string ruta) =>
        Assert.True(RutasDeLaSpa.EsRutaHeredada(new PathString(ruta)));

    [Theory]
    [InlineData("")]
    [InlineData("/")]
    [InlineData("/pos.html")]
    [InlineData("/posx")]
    [InlineData("/admin")]
    [InlineData("/admin/pos")]
    [InlineData("/online/login")]
    [InlineData("/api/pos")]
    [InlineData("/api/login")]
    [InlineData("/assets/pos/x.js")]
    [InlineData("/connect/token")]
    [InlineData("/mcp")]
    [InlineData("/catalogo")]
    public void UnaRutaSinPrimerSegmentoHeredadoNoLoEs(string ruta) =>
        Assert.False(RutasDeLaSpa.EsRutaHeredada(new PathString(ruta)));

    [Theory]
    [InlineData("/admin", "admin.html")]
    [InlineData("/admin/", "admin.html")]
    [InlineData("/Admin/articulos/edit/3", "admin.html")]
    [InlineData("/online", "online.html")]
    [InlineData("/online/producto/3", "online.html")]
    [InlineData("/", "index.html")]
    [InlineData("/administrador", "index.html")]
    [InlineData("/onlinex", "index.html")]
    [InlineData("/cualquier/cosa", "index.html")]
    public void CadaPrefijoSirveSuEntrada(string ruta, string entrada) =>
        Assert.Equal(entrada, RutasDeLaSpa.EntradaDe(new PathString(ruta)));

    private static string RaizDelRepositorio()
    {
        for (var directorio = new DirectoryInfo(AppContext.BaseDirectory); directorio is not null; directorio = directorio.Parent)
        {
            if (File.Exists(Path.Combine(directorio.FullName, "Ways.slnx")))
            {
                return directorio.FullName;
            }
        }

        throw new InvalidOperationException("No se encontró Ways.slnx subiendo desde el directorio de las pruebas.");
    }
}
