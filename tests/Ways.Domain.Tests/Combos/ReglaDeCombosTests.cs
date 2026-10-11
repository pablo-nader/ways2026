using Ways.Domain.Combos;
using Ways.Domain.Common;

namespace Ways.Domain.Tests.Combos;

public class ReglaDeCombosTests
{
    private static ComponenteDeCombo Art(int id, decimal cantidad = 1m) => new(id, null, cantidad);

    private static ComponenteDeCombo Fam(int id, decimal cantidad = 1m) => new(null, id, cantidad);

    private static void AssertCodigo(string codigo, decimal precio, params ComponenteDeCombo[] componentes)
    {
        var error = Assert.Throws<ErrorDominio>(() => ReglaDeCombos.Validar(precio, componentes));
        Assert.Equal(codigo, error.Codigo);
        Assert.Equal(400, error.EstadoHttp);
    }

    [Fact]
    public void UnComboConDosComponentesDistintosYPrecioPositivoEsValido()
    {
        ReglaDeCombos.Validar(5000m, [Art(1), Fam(2)]);
    }

    [Fact]
    public void UnSoloComponenteConCantidadDosAlcanzaPorqueElMinimoEsSobreLaCantidadTotal()
    {
        ReglaDeCombos.Validar(5000m, [Art(1, 2m)]);
    }

    [Fact]
    public void LosTopesDeCantidadYPrecioSeAceptanYUnoMasSeRechaza()
    {
        ReglaDeCombos.Validar(ReglaDeCombos.PrecioMaximo, [Art(1, ReglaDeCombos.CantidadMaxima), Art(2)]);

        AssertCodigo("combo_cantidad_invalida", 5000m, Art(1, ReglaDeCombos.CantidadMaxima + 1m), Art(2));
        AssertCodigo("combo_precio_invalido", ReglaDeCombos.PrecioMaximo + 0.01m, Art(1), Art(2));
    }

    [Fact]
    public void UnaSolaUnidadEnTotalSeRechaza() =>
        AssertCodigo("combo_unidades_insuficientes", 5000m, Art(1));

    [Fact]
    public void SinComponentesSeRechazaPorUnidadesInsuficientes() =>
        AssertCodigo("combo_unidades_insuficientes", 5000m);

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public void ElPrecioTieneQueSerMayorACero(double precio) =>
        AssertCodigo("combo_precio_invalido", (decimal)precio, Art(1), Art(2));

    [Fact]
    public void UnComponenteSinArticuloNiFamiliaSeRechaza() =>
        AssertCodigo("combo_componente_invalido", 5000m, Art(1), new ComponenteDeCombo(null, null, 1m));

    [Fact]
    public void UnComponenteConArticuloYFamiliaALaVezSeRechaza() =>
        AssertCodigo("combo_componente_invalido", 5000m, Art(1), new ComponenteDeCombo(2, 3, 1m));

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    [InlineData(1.5)]
    [InlineData(0.5)]
    public void LaCantidadDeUnComponenteTieneQueSerUnEnteroPositivo(double cantidad) =>
        AssertCodigo("combo_cantidad_invalida", 5000m, Art(1, (decimal)cantidad), Art(2));

    [Fact]
    public void ElMismoArticuloDosVecesSeRechaza() =>
        AssertCodigo("combo_componente_duplicado", 5000m, Art(1), Art(2), Art(1));

    [Fact]
    public void LaMismaFamiliaDosVecesSeRechaza() =>
        AssertCodigo("combo_componente_duplicado", 5000m, Fam(7), Art(2), Fam(7));

    [Fact]
    public void UnArticuloYUnaFamiliaConElMismoNumeroNoSonDuplicados()
    {
        ReglaDeCombos.Validar(5000m, [Art(7), Fam(7)]);
    }
}
