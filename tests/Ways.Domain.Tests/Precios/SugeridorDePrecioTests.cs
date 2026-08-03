using Ways.Domain.Precios;

namespace Ways.Domain.Tests.Precios;

/// <summary>
/// stage-3-articulos-y-precios, Slice 2 (task 2.6, spec: precios / Margin-Based Price
/// Suggestion) — función pura, sin base de datos.
/// </summary>
public class SugeridorDePrecioTests
{
    /// <summary>Spec: Grupo margin wins over proveedor margin.</summary>
    [Fact]
    public void ElMargenDeGrupoGanaAlDeProveedor()
    {
        var sugerido = SugeridorDePrecio.Sugerir(
            costoNominal: 100m, costoLista: null, descuentoProveedor: null,
            margenGrupo: 30m, margenProveedor: 20m);

        Assert.Equal(130m, sugerido);
    }

    /// <summary>Spec: Falls back to proveedor margin without a grupo margin.</summary>
    [Fact]
    public void SinMargenDeGrupoUsaElDeProveedor()
    {
        var sugerido = SugeridorDePrecio.Sugerir(
            costoNominal: 100m, costoLista: null, descuentoProveedor: null,
            margenGrupo: null, margenProveedor: 15m);

        Assert.Equal(115m, sugerido);
    }

    /// <summary>Spec: Base cost is costo_nominal when present, else costo_lista * (1 -
    /// descuento_proveedor) — costo_nominal precede aunque costo_lista también esté
    /// presente.</summary>
    [Fact]
    public void CostoNominalPrecedeSobreCostoListaConDescuento()
    {
        var sugerido = SugeridorDePrecio.Sugerir(
            costoNominal: 100m, costoLista: 500m, descuentoProveedor: 50m,
            margenGrupo: 10m, margenProveedor: null);

        // Si hubiera usado costo_lista * (1 - descuento) = 500 * 0.5 = 250, el resultado sería
        // 275 (margen 10%) en vez de 110 — confirma que costo_nominal ganó.
        Assert.Equal(110m, sugerido);
    }

    [Fact]
    public void SinCostoNominalUsaCostoListaMenosElDescuento()
    {
        var sugerido = SugeridorDePrecio.Sugerir(
            costoNominal: null, costoLista: 200m, descuentoProveedor: 25m,
            margenGrupo: 20m, margenProveedor: null);

        // costo base = 200 * (1 - 0.25) = 150; sugerido = 150 * 1.20 = 180.
        Assert.Equal(180m, sugerido);
    }

    [Fact]
    public void SinDescuentoProveedorElCostoListaSeUsaSinDescontar()
    {
        var sugerido = SugeridorDePrecio.Sugerir(
            costoNominal: null, costoLista: 100m, descuentoProveedor: null,
            margenGrupo: 10m, margenProveedor: null);

        Assert.Equal(110m, sugerido);
    }

    [Fact]
    public void SinCostoBaseNoHaySugerencia()
    {
        var sugerido = SugeridorDePrecio.Sugerir(
            costoNominal: null, costoLista: null, descuentoProveedor: null,
            margenGrupo: 30m, margenProveedor: 20m);

        Assert.Null(sugerido);
    }

    [Fact]
    public void SinMargenNoHaySugerencia()
    {
        var sugerido = SugeridorDePrecio.Sugerir(
            costoNominal: 100m, costoLista: null, descuentoProveedor: null,
            margenGrupo: null, margenProveedor: null);

        Assert.Null(sugerido);
    }

    [Fact]
    public void RedondeaADosDecimalesAlejandoDeCero()
    {
        var sugerido = SugeridorDePrecio.Sugerir(
            costoNominal: 100m, costoLista: null, descuentoProveedor: null,
            margenGrupo: 33.335m, margenProveedor: null);

        // 100 * 1.33335 = 133.335 -> AwayFromZero redondea a 133.34, no 133.33 (banker's).
        Assert.Equal(133.34m, sugerido);
    }
}
