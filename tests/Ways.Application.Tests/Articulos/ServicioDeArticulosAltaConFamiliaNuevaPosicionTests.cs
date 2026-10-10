using System.Text.RegularExpressions;
using Ways.Application.Tests.Infraestructura;

namespace Ways.Application.Tests.Articulos;

/// <summary>
/// El orden de las llamadas de <c>ServicioDeArticulos.CrearAsync</c> cuando el pedido trae <c>NombreFamiliaNueva</c>, como
/// aserción sobre el TEXTO FUENTE (mismo criterio que <c>ServicioDeAgrupacionDeFamiliasPosicionDeLocksTests</c>: los locks
/// son SQL crudo y un interceptor de EF no los ve). La red real, que mira <c>pg_locks</c>, está en
/// <c>ArticulosAltaConFamiliaNuevaTests</c>; esta fija lo que desde afuera no se ve: que el nombre se valide y se
/// pre-chequee antes de abrir la transacción, que el lock de membresía exclusivo cubra también a la familia nueva y que la
/// familia se inserte antes que el artículo, que la referencia por su id.
/// </summary>
public class ServicioDeArticulosAltaConFamiliaNuevaPosicionTests
{
    private static string Cuerpo()
    {
        var fuente = Regex.Replace(
            File.ReadAllText(Path.Combine(
                RaizDelRepositorio.Resolver(), "src", "Ways.Application", "Articulos", "ServicioDeArticulos.cs")),
            @"//[^\r\n]*", string.Empty);
        var inicio = fuente.IndexOf("public async Task<ArticuloListado> CrearAsync(", StringComparison.Ordinal);
        Assert.True(inicio >= 0);
        var fin = fuente.IndexOf("public async Task<ArticuloListado> ActualizarAsync(", inicio, StringComparison.Ordinal);
        Assert.True(fin > inicio);

        return fuente[inicio..fin];
    }

    private static int Posicion(string cuerpo, string fragmento)
    {
        var indice = cuerpo.IndexOf(fragmento, StringComparison.Ordinal);
        Assert.True(indice >= 0, $"No se encontró '{fragmento}'.");
        Assert.Equal(indice, cuerpo.LastIndexOf(fragmento, StringComparison.Ordinal));

        return indice;
    }

    [Fact]
    public void ElNombreSeNormalizaYSePreChequeaAntesDeAbrirLaTransaccion()
    {
        var cuerpo = Cuerpo();
        var transaccion = Posicion(cuerpo, "BeginTransactionAsync(");

        Assert.True(Posicion(cuerpo, "NormalizarNombreDeFamiliaNueva(datos)") < transaccion);
        Assert.True(Posicion(cuerpo, "NombreDeFamilia.ExigirDisponibleAsync(") < transaccion);
    }

    [Fact]
    public void ElLockExclusivoCubreLaFamiliaNuevaYEsLaPrimeraSentenciaDeLaTransaccion()
    {
        var cuerpo = Cuerpo();
        var guarda = Regex.Match(
            cuerpo,
            @"if \(datos\.IdFamilia is not null \|\| nombreFamiliaNueva is not null\)\s*\{\s*await LockDeMembresiaDeFamilias\.TomarExclusivoAsync\(",
            RegexOptions.None, TimeSpan.FromSeconds(5));

        Assert.True(guarda.Success);
        Assert.True(guarda.Index < Posicion(cuerpo, "await ExigirAreaValidaAsync("));
        Assert.True(guarda.Index > Posicion(cuerpo, "BeginTransactionAsync("));
        Assert.DoesNotContain("TomarCompartidoAsync", cuerpo);
    }

    [Fact]
    public void LaFamiliaSeInsertaAntesQueElArticuloYElArticuloLaReferenciaPorSuId()
    {
        var cuerpo = Cuerpo();
        var familia = Posicion(cuerpo, "db.Familias.Add(familia);");
        var articulo = Posicion(cuerpo, "db.Articulos.Add(articulo);");

        Assert.True(familia < articulo);
        Assert.True(Posicion(cuerpo, "idFamiliaDelArticulo = familia.Id;") < Posicion(cuerpo, "IdFamilia = idFamiliaDelArticulo,"));
        Assert.True(Posicion(cuerpo, "IdFamilia = idFamiliaDelArticulo,") < articulo);
    }
}
