using Ways.Domain.Precios;

namespace Ways.Domain.Tests.Precios;

/// <summary>
/// stage-3-articulos-y-precios, Slice 2 (task 2.6, spec: precios / Margin-Based Price
/// Suggestion) — función pura, sin base de datos. Los casos de margen y costo usan múltiplo 0 (sin
/// redondeo a múltiplos) para aislar su regla; el redondeo tiene sus propios casos más abajo.
/// </summary>
public class SugeridorDePrecioTests
{
    private const decimal SinMultiplo = 0m;

    /// <summary>Spec: Grupo margin wins over proveedor margin.</summary>
    [Fact]
    public void ElMargenDeGrupoGanaAlDeProveedor()
    {
        var sugerido = SugeridorDePrecio.Sugerir(
            costoBase: 100m, margenGrupo: 30m, margenProveedor: 20m, SinMultiplo);

        Assert.Equal(130m, sugerido);
    }

    /// <summary>Spec: Falls back to proveedor margin without a grupo margin.</summary>
    [Fact]
    public void SinMargenDeGrupoUsaElDeProveedor()
    {
        var sugerido = SugeridorDePrecio.Sugerir(
            costoBase: 100m, margenGrupo: null, margenProveedor: 15m, SinMultiplo);

        Assert.Equal(115m, sugerido);
    }

    [Fact]
    public void SinCostoBaseNoHaySugerencia()
    {
        var sugerido = SugeridorDePrecio.Sugerir(
            costoBase: null, margenGrupo: 30m, margenProveedor: 20m, multiploDeRedondeo: 100m);

        Assert.Null(sugerido);
    }

    [Fact]
    public void SinMargenNoHaySugerencia()
    {
        var sugerido = SugeridorDePrecio.Sugerir(
            costoBase: 100m, margenGrupo: null, margenProveedor: null, multiploDeRedondeo: 100m);

        Assert.Null(sugerido);
    }

    [Fact]
    public void SinMultiploRedondeaADosDecimalesAlejandoDeCero()
    {
        var sugerido = SugeridorDePrecio.Sugerir(
            costoBase: 100m, margenGrupo: 33.335m, margenProveedor: null, SinMultiplo);

        // 100 * 1.33335 = 133.335 -> AwayFromZero redondea a 133.34, no 133.33 (banker's).
        Assert.Equal(133.34m, sugerido);
    }

    [Fact]
    public void UnMultiploNegativoTambienRedondeaADosDecimales()
    {
        var sugerido = SugeridorDePrecio.Sugerir(
            costoBase: 100m, margenGrupo: 33.335m, margenProveedor: null, multiploDeRedondeo: -100m);

        Assert.Equal(133.34m, sugerido);
    }

    [Fact]
    public void RedondeaAlMultiploMasCercano()
    {
        // 2000 * 1.448 = 2896 -> el múltiplo de 100 más cercano es 2900.
        var sugerido = SugeridorDePrecio.Sugerir(
            costoBase: 2000m, margenGrupo: 44.8m, margenProveedor: null, multiploDeRedondeo: 100m);

        Assert.Equal(2900m, sugerido);
    }

    [Fact]
    public void ElEmpateDelMultiploSeRedondeaLejosDeCero()
    {
        var sugerido = SugeridorDePrecio.Sugerir(
            costoBase: 2950m, margenGrupo: 0m, margenProveedor: null, multiploDeRedondeo: 100m);

        // 2950 está a medio múltiplo de 2900 y de 3000: gana 3000 (con banker's sería 2900).
        Assert.Equal(3000m, sugerido);
    }

    [Fact]
    public void UnCentavoPorDebajoDelEmpateBajaAlMultiploInferior()
    {
        var sugerido = SugeridorDePrecio.Sugerir(
            costoBase: 2949.99m, margenGrupo: 0m, margenProveedor: null, multiploDeRedondeo: 100m);

        Assert.Equal(2900m, sugerido);
    }

    [Fact]
    public void ElRedondeoSeAplicaAlPrecioConElMargenYNoAlCosto()
    {
        // 1000 * 1.5 = 1500 -> con múltiplo 1000 el empate (1.5) sube a 2000; el costo solo ya daría 1000.
        var sugerido = SugeridorDePrecio.Sugerir(
            costoBase: 1000m, margenGrupo: 50m, margenProveedor: null, multiploDeRedondeo: 1000m);

        Assert.Equal(2000m, sugerido);
    }

    [Theory]
    [InlineData(2896, 50, 2900)]
    [InlineData(2874, 50, 2850)]
    [InlineData(2875, 50, 2900)]
    public void AdmiteUnMultiploDistintoDeCien(decimal costoBase, decimal multiplo, decimal esperado)
    {
        var sugerido = SugeridorDePrecio.Sugerir(
            costoBase: costoBase, margenGrupo: 0m, margenProveedor: null, multiploDeRedondeo: multiplo);

        Assert.Equal(esperado, sugerido);
    }

    [Theory]
    [InlineData(10.3, 10.5)]
    [InlineData(10.2, 10.0)]
    [InlineData(10.25, 10.5)]
    public void AdmiteUnMultiploFraccionario(decimal costoBase, decimal esperado)
    {
        var sugerido = SugeridorDePrecio.Sugerir(
            costoBase: costoBase, margenGrupo: 0m, margenProveedor: null, multiploDeRedondeo: 0.5m);

        Assert.Equal(esperado, sugerido);
    }

    [Fact]
    public void UnPrecioPorDebajoDeLaMitadDelMultiploNoSeSugiereEnCero()
    {
        // 40 está más cerca de 0 que de 100: se conserva el redondeo a 2 decimales.
        var sugerido = SugeridorDePrecio.Sugerir(
            costoBase: 33.333m, margenGrupo: 20m, margenProveedor: null, multiploDeRedondeo: 100m);

        Assert.Equal(40m, sugerido);
    }

    [Fact]
    public void ElLimiteInferiorDelMultiploYaSeRedondeaAlMultiplo()
    {
        var sugerido = SugeridorDePrecio.Sugerir(
            costoBase: 50m, margenGrupo: 0m, margenProveedor: null, multiploDeRedondeo: 100m);

        Assert.Equal(100m, sugerido);
    }
}
