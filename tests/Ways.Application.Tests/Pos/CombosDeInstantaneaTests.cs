using System.Text.Json;
using Ways.Application.Pos;
using Ways.Domain.Articulos;
using Ways.Domain.Combos;

namespace Ways.Application.Tests.Pos;

/// <summary>
/// Los combos de la instantánea: qué combos viajan (<see cref="ArmadorDeInstantanea.ArmarCombos"/>), en qué orden y
/// cómo se serializan. Un combo viaja ENTERO o no viaja; las claves vacías se omiten y la etiqueta de contenido
/// cambia cuando cambia un combo o un componente.
/// </summary>
public class CombosDeInstantaneaTests
{
    private static readonly JsonSerializerOptions Opciones = new(JsonSerializerDefaults.Web);

    private static ArticuloDeInstantanea Articulo(int id, UnidadVenta unidad = UnidadVenta.Unidad, int? idFamilia = null) =>
        new(id, $"art-{id}", $"Artículo {id}", [], 3, 21m, true, unidad, [], idFamilia);

    private static ComponenteDeCombo PorArticulo(int id, decimal cantidad = 1m) => new(id, null, cantidad);

    private static ComponenteDeCombo PorFamilia(int id, decimal cantidad = 1m) => new(null, id, cantidad);

    private static ComboCandidato Combo(int id, params ComponenteDeCombo[] componentes) =>
        new(id, $"Combo {id}", 5000m, componentes, Codigo: id * 10);

    [Fact]
    public void UnComboCuyosComponentesSeVendenViajaConSuContenidoCompleto()
    {
        var articulos = new[] { Articulo(1), Articulo(2, idFamilia: 40) };

        var combos = ArmadorDeInstantanea.ArmarCombos([Combo(7, PorArticulo(1), PorFamilia(40, 2m))], articulos);

        var combo = Assert.Single(combos!);
        Assert.Equal(7, combo.IdCombo);
        Assert.Equal(70, combo.Codigo);
        Assert.Equal("Combo 7", combo.Nombre);
        Assert.Equal(5000m, combo.Precio);
        Assert.Equal(
            [new ComponenteDeComboDeInstantanea(1, null, 1m), new ComponenteDeComboDeInstantanea(null, 40, 2m)],
            combo.Componentes);
    }

    /// <summary>Cláusula: un componente artículo ausente de la instantánea (inactivo, sin precio o dado de baja)
    /// descarta el combo ENTERO, aunque los demás componentes sí se vendan.</summary>
    [Fact]
    public void UnComboConUnArticuloQueNoEstaEnLaInstantaneaNoViajaNiParcial()
    {
        var combos = ArmadorDeInstantanea.ArmarCombos(
            [Combo(7, PorArticulo(1), PorArticulo(99))], [Articulo(1)]);

        Assert.Null(combos);
    }

    /// <summary>Cláusula: una familia sin ningún artículo en la instantánea no se puede completar.</summary>
    [Fact]
    public void UnComboConUnaFamiliaSinArticulosVendiblesNoViaja()
    {
        var combos = ArmadorDeInstantanea.ArmarCombos(
            [Combo(7, PorArticulo(1), PorFamilia(40))], [Articulo(1), Articulo(2, idFamilia: 41)]);

        Assert.Null(combos);
    }

    /// <summary>Cláusula: solo cuentan los artículos que se venden por unidad. Un componente artículo pesable no puede
    /// completarse (la venta online lo deja afuera de la detección).</summary>
    [Fact]
    public void UnComboConUnArticuloPesableNoViaja()
    {
        var combos = ArmadorDeInstantanea.ArmarCombos(
            [Combo(7, PorArticulo(1), PorArticulo(2))], [Articulo(1), Articulo(2, UnidadVenta.Peso)]);

        Assert.Null(combos);
    }

    /// <summary>Cláusula: una familia cuyos únicos artículos son pesables tampoco se puede completar.</summary>
    [Fact]
    public void UnaFamiliaSoloConArticulosPesablesNoCuenta()
    {
        var combos = ArmadorDeInstantanea.ArmarCombos(
            [Combo(7, PorArticulo(1), PorFamilia(40))], [Articulo(1), Articulo(2, UnidadVenta.Peso, idFamilia: 40)]);

        Assert.Null(combos);
    }

    /// <summary>Cláusula: una cantidad que el motor no puede usar (fraccionaria) descarta el combo.</summary>
    [Fact]
    public void UnComboConCantidadFraccionariaNoViaja()
    {
        var combos = ArmadorDeInstantanea.ArmarCombos(
            [Combo(7, PorArticulo(1, 1.5m), PorArticulo(2))], [Articulo(1), Articulo(2)]);

        Assert.Null(combos);
    }

    [Fact]
    public void ViajanLosCombosCompletosYSeDescartanSoloLosIncompletos()
    {
        var articulos = new[] { Articulo(1), Articulo(2) };

        var combos = ArmadorDeInstantanea.ArmarCombos(
            [Combo(9, PorArticulo(1), PorArticulo(2)), Combo(3, PorArticulo(1), PorArticulo(99)), Combo(5, PorArticulo(2), PorArticulo(1))],
            articulos);

        Assert.Equal([5, 9], combos!.Select(c => c.IdCombo));
    }

    /// <summary>Cláusula: el orden es de contenido, no el de llegada: combos por id y, dentro de cada uno, artículos
    /// primero (por id) y familias después (por id). Sin esto la etiqueta de contenido cambiaría sin que cambie nada
    /// relevante.</summary>
    [Fact]
    public void ElOrdenEsDeterministaSinImportarElOrdenDeEntrada()
    {
        var articulos = new[] { Articulo(1), Articulo(2), Articulo(3, idFamilia: 40), Articulo(4, idFamilia: 41) };
        var a = Combo(8, PorFamilia(41), PorArticulo(2), PorFamilia(40), PorArticulo(1));
        var b = Combo(2, PorArticulo(1), PorArticulo(2));

        var combos = ArmadorDeInstantanea.ArmarCombos([a, b], articulos)!;
        var invertido = ArmadorDeInstantanea.ArmarCombos([b, a], articulos)!;

        Assert.Equal([2, 8], combos.Select(c => c.IdCombo));
        Assert.Equal(
            [
                new ComponenteDeComboDeInstantanea(1, null, 1m), new ComponenteDeComboDeInstantanea(2, null, 1m),
                new ComponenteDeComboDeInstantanea(null, 40, 1m), new ComponenteDeComboDeInstantanea(null, 41, 1m)
            ],
            combos[1].Componentes);
        Assert.Equal(JsonSerializer.Serialize(combos, Opciones), JsonSerializer.Serialize(invertido, Opciones));
    }

    /// <summary>Cláusula: un combo sin componentes no viaja (quedaría "completo" por vacío).</summary>
    [Fact]
    public void UnComboSinComponentesNoViaja()
    {
        Assert.Null(ArmadorDeInstantanea.ArmarCombos([Combo(7)], [Articulo(1)]));
    }

    [Fact]
    public void SinCombosElResultadoEsNuloYNoUnaListaVacia()
    {
        Assert.Null(ArmadorDeInstantanea.ArmarCombos([], [Articulo(1)]));
    }

    // ---- Forma serializada y etiqueta de contenido ----

    private static InstantaneaDePos Instantanea(IReadOnlyList<ComboDeInstantanea>? combos, DateTimeOffset? momento = null) =>
        new(momento ?? DateTimeOffset.UnixEpoch, 1, [], [], [], 0m, combos);

    private static ComboDeInstantanea ComboDeInstantanea(decimal precio = 5000m, int cantidad = 1) =>
        new(7, 70, "Combo 7", precio,
            [new ComponenteDeComboDeInstantanea(1, null, cantidad), new ComponenteDeComboDeInstantanea(null, 40, 1m)]);

    /// <summary>Cláusula: el <c>JsonIgnore(WhenWritingNull)</c> de <see cref="InstantaneaDePos.Combos"/> — sin combos la
    /// clave no viaja, así que la instantánea y su etiqueta son las de antes de la funcionalidad.</summary>
    [Fact]
    public void SinCombosLaClaveNoViaja()
    {
        var json = JsonSerializer.Serialize(Instantanea(null), Opciones);

        Assert.DoesNotContain("combos", json);
    }

    /// <summary>Cláusulas: el <c>JsonIgnore(WhenWritingNull)</c> de los dos ids del componente — viaja exactamente uno.</summary>
    [Fact]
    public void ElComponenteViajaSoloConElIdQueTiene()
    {
        var json = JsonSerializer.Serialize(Instantanea([ComboDeInstantanea()]), Opciones);

        Assert.Contains("\"componentes\":[{\"idArticulo\":1,\"cantidad\":1},{\"idFamilia\":40,\"cantidad\":1}]", json);
        Assert.Contains("\"idCombo\":7,\"codigo\":70,\"nombre\":\"Combo 7\",\"precio\":5000", json);
    }

    [Fact]
    public void LaEtiquetaNoCambiaSiNoCambiaElContenido()
    {
        var uno = EtiquetaDeInstantanea.Calcular(Instantanea([ComboDeInstantanea()], DateTimeOffset.UnixEpoch));
        var otro = EtiquetaDeInstantanea.Calcular(Instantanea([ComboDeInstantanea()], DateTimeOffset.UnixEpoch.AddHours(3)));

        Assert.Equal(uno, otro);
    }

    [Fact]
    public void LaEtiquetaCambiaConUnCombo()
    {
        var sin = EtiquetaDeInstantanea.Calcular(Instantanea(null));
        var con = EtiquetaDeInstantanea.Calcular(Instantanea([ComboDeInstantanea()]));

        Assert.NotEqual(sin, con);
    }

    [Fact]
    public void LaEtiquetaCambiaConElPrecioYConUnComponente()
    {
        var base_ = EtiquetaDeInstantanea.Calcular(Instantanea([ComboDeInstantanea()]));

        Assert.NotEqual(base_, EtiquetaDeInstantanea.Calcular(Instantanea([ComboDeInstantanea(precio: 4900m)])));
        Assert.NotEqual(base_, EtiquetaDeInstantanea.Calcular(Instantanea([ComboDeInstantanea(cantidad: 2)])));
    }
}
