using Ways.Domain.Common;
using Ways.Domain.Compras;

namespace Ways.Domain.Tests.Compras;

/// <summary>
/// stage-8-compras-transferencias-inventario, Slice 2 (task 2.2, design: Compra Arithmetic /
/// Testing Strategy — "the bulk of the stage's test mass") — pura, sin base de datos.
/// </summary>
public class CalculadorDeCompraTests
{
    private static readonly IReadOnlyDictionary<int, (decimal? MargenGrupo, decimal? MargenProveedor)> SinMargenes =
        new Dictionary<int, (decimal?, decimal?)>();

    private static LineaDeCompra Linea(
        int orden = 1, int? idArticulo = 1, decimal unidades = 1m, decimal? bultos = null,
        decimal? unidadesPorBulto = null, decimal costoUnitario = 100m, decimal descuento = 0m,
        int idAlicuotaIva = 1, decimal porcentajeIva = 21m, bool actualizaCosto = true) =>
        new(orden, idArticulo, "item de prueba", unidades, bultos, unidadesPorBulto, costoUnitario, descuento,
            idAlicuotaIva, porcentajeIva, actualizaCosto);

    // ---- cantidad: unidades + bultos * unidadesPorBulto ---------------------------------------

    [Fact]
    public void LaCantidadEsSoloUnidadesCuandoNoHayBultos()
    {
        var resultado = CalculadorDeCompra.Calcular([Linea(unidades: 5m)], discriminaIva: false, SinMargenes);

        Assert.Equal(5m, resultado.Items[0].Cantidad);
    }

    [Fact]
    public void LaCantidadSumaBultosPorUnidadesPorBultoALasUnidades()
    {
        // 2 unidades sueltas + 3 bultos de 10 = 32.
        var resultado = CalculadorDeCompra.Calcular(
            [Linea(unidades: 2m, bultos: 3m, unidadesPorBulto: 10m)], discriminaIva: false, SinMargenes);

        Assert.Equal(32m, resultado.Items[0].Cantidad);
    }

    [Fact]
    public void LaCantidadRedondeaAwayFromZeroATresDecimales()
    {
        var resultado = CalculadorDeCompra.Calcular(
            [Linea(unidades: 1.0005m, costoUnitario: 1m)], discriminaIva: false, SinMargenes);

        Assert.Equal(1.001m, resultado.Items[0].Cantidad);
    }

    [Fact]
    public void UnaCantidadCeroOMenorEsRechazada()
    {
        var error = Assert.Throws<ErrorDominio>(() =>
            CalculadorDeCompra.Calcular([Linea(unidades: 0m)], discriminaIva: false, SinMargenes));

        Assert.Equal("cantidad_de_item_invalida", error.Codigo);
        Assert.Equal(400, error.EstadoHttp);
    }

    // ---- descuento > bruto ---------------------------------------------------------------------

    [Fact]
    public void UnDescuentoMayorAlBrutoEsRechazado()
    {
        // Bruto = 1 * 100 = 100; descuento 150 > 100.
        var error = Assert.Throws<ErrorDominio>(() =>
            CalculadorDeCompra.Calcular([Linea(costoUnitario: 100m, descuento: 150m)], discriminaIva: false, SinMargenes));

        Assert.Equal("descuento_de_item_invalido", error.Codigo);
        Assert.Equal(400, error.EstadoHttp);
    }

    [Fact]
    public void UnDescuentoIgualAlBrutoEsAceptadoYDaTotalCero()
    {
        var resultado = CalculadorDeCompra.Calcular(
            [Linea(costoUnitario: 100m, descuento: 100m)], discriminaIva: false, SinMargenes);

        Assert.Equal(0m, resultado.Items[0].Total);
        Assert.Equal(0m, resultado.Total);
    }

    [Fact]
    public void UnCostoUnitarioNegativoEsRechazado()
    {
        var error = Assert.Throws<ErrorDominio>(() =>
            CalculadorDeCompra.Calcular([Linea(costoUnitario: -1m)], discriminaIva: false, SinMargenes));

        Assert.Equal("costo_de_item_invalido", error.Codigo);
    }

    [Fact]
    public void UnDescuentoNegativoEsRechazado()
    {
        var error = Assert.Throws<ErrorDominio>(() =>
            CalculadorDeCompra.Calcular([Linea(descuento: -1m)], discriminaIva: false, SinMargenes));

        Assert.Equal("importes_de_item_invalidos", error.Codigo);
    }

    // ---- discrimina_iva = true (C-FA): costoUnitario neto de IVA -------------------------------

    [Fact]
    public void ConIvaDiscriminadoElTotalSumaElIvaYElCostoEfectivoLoIncluye()
    {
        // 1 unidad a 100 neto, IVA 21% -> iva 21, total 121, costoEfectivo = 121 / 1 = 121.
        var resultado = CalculadorDeCompra.Calcular(
            [Linea(costoUnitario: 100m, porcentajeIva: 21m)], discriminaIva: true, SinMargenes);

        Assert.Equal(100m, resultado.Subtotal);
        Assert.Equal(21m, resultado.IvaTotal);
        Assert.Equal(121m, resultado.Total);
        Assert.Equal(121m, resultado.Items[0].CostoEfectivo);
    }

    [Fact]
    public void ConIvaDiscriminadoYVariasCantidadesElCostoEfectivoEsPorUnidad()
    {
        // 2 unidades a 100 neto, IVA 21% -> total de línea (200 - 0) = 200, con IVA = 242,
        // costoEfectivo = 242 / 2 = 121 (mismo costo unitario efectivo que la prueba anterior).
        var resultado = CalculadorDeCompra.Calcular(
            [Linea(unidades: 2m, costoUnitario: 100m, porcentajeIva: 21m)], discriminaIva: true, SinMargenes);

        Assert.Equal(121m, resultado.Items[0].CostoEfectivo);
    }

    // ---- discrimina_iva = false (C-FB/C-FC): costoUnitario ya incluye IVA ----------------------

    [Fact]
    public void SinIvaDiscriminadoElIvaTotalEsNuloYElCostoEfectivoNoLoIncluyeDeNuevo()
    {
        var resultado = CalculadorDeCompra.Calcular(
            [Linea(costoUnitario: 121m, porcentajeIva: 21m)], discriminaIva: false, SinMargenes);

        Assert.Null(resultado.IvaTotal);
        Assert.Equal(121m, resultado.Total);
        Assert.Equal(121m, resultado.Items[0].CostoEfectivo);
    }

    // ---- narrowing numeric(14,4) -> numeric(14,2) AwayFromZero ----------------------------------

    [Fact]
    public void ElCostoEfectivoRedondeaAwayFromZeroEnElMedio()
    {
        // 3 unidades a 33.335 (numeric 14,4) -> bruto 100.005 -> redondeado AwayFromZero a
        // 100.01, no al par más cercano (100.00) que daría el banker's rounding default.
        var resultado = CalculadorDeCompra.Calcular(
            [Linea(unidades: 3m, costoUnitario: 33.335m)], discriminaIva: false, SinMargenes);

        Assert.Equal(100.01m, resultado.Subtotal);
        // costoEfectivo = total / cantidad = 100.01 / 3 = 33.3366... -> 33.34 AwayFromZero.
        Assert.Equal(33.34m, resultado.Items[0].CostoEfectivo);
    }

    // ---- bonificación (costo cero) no debe tocar costo, pero SÍ es un total/costoEfectivo válido

    [Fact]
    public void UnaLineaDeBonificacionConCostoCeroEsAceptadaYDaCostoEfectivoCero()
    {
        var resultado = CalculadorDeCompra.Calcular(
            [Linea(costoUnitario: 0m)], discriminaIva: false, SinMargenes);

        Assert.Equal(0m, resultado.Items[0].Total);
        Assert.Equal(0m, resultado.Items[0].CostoEfectivo);
    }

    // ---- precio sugerido: delegado a SugeridorDePrecio, nunca reimplementado -------------------

    [Fact]
    public void ElPrecioSugeridoSeDelegaEnSugeridorDePrecioConElCostoEfectivoComoBase()
    {
        var margenes = new Dictionary<int, (decimal?, decimal?)> { [1] = (50m, null) };

        var resultado = CalculadorDeCompra.Calcular(
            [Linea(idArticulo: 1, costoUnitario: 100m)], discriminaIva: false, margenes);

        // costoEfectivo = 100 (sin IVA discriminado); margen grupo 50% -> Sugerir(100, null,
        // null, 50, null) = 150.
        var esperado = Ways.Domain.Precios.SugeridorDePrecio.Sugerir(100m, null, null, 50m, null);
        Assert.Equal(esperado, resultado.Items[0].PrecioSugerido);
        Assert.Equal(150m, resultado.Items[0].PrecioSugerido);
    }

    [Fact]
    public void SinMargenNiCostoNominalElPrecioSugeridoEsNulo()
    {
        var resultado = CalculadorDeCompra.Calcular([Linea(idArticulo: 1)], discriminaIva: false, SinMargenes);

        Assert.Null(resultado.Items[0].PrecioSugerido);
    }

    // ---- dos líneas del mismo artículo: cada una calcula su propio costo, nada se funde --------

    [Fact]
    public void DosLineasDelMismoArticuloCalculanSuPropioCostoEfectivoIndependiente()
    {
        var resultado = CalculadorDeCompra.Calcular(
            [
                Linea(orden: 1, idArticulo: 7, costoUnitario: 100m),
                Linea(orden: 2, idArticulo: 7, costoUnitario: 200m)
            ],
            discriminaIva: false, SinMargenes);

        Assert.Equal(2, resultado.Items.Count);
        Assert.Equal(100m, resultado.Items[0].CostoEfectivo);
        Assert.Equal(200m, resultado.Items[1].CostoEfectivo);
    }

    // ---- ResolverActualizacionesDeCosto: dedupe con el mayor orden ganando ---------------------

    [Fact]
    public void ResolverActualizacionesDeCostoDedupeaConElMayorOrdenGanando()
    {
        var items = new List<(int Orden, int? IdArticulo, bool ActualizaCosto, decimal CostoUnitario, decimal CostoEfectivo)>
        {
            (1, 7, true, 100m, 100m),
            (2, 7, true, 200m, 200m)
        };

        var resultado = CalculadorDeCompra.ResolverActualizacionesDeCosto(items, new Dictionary<int, int>());

        Assert.Equal([new ActualizacionDeCosto(IdFamilia: null, IdArticulo: 7, Costo: 200m)], resultado);
    }

    [Fact]
    public void ResolverActualizacionesDeCostoExcluyeActualizaCostoFalso()
    {
        var items = new List<(int Orden, int? IdArticulo, bool ActualizaCosto, decimal CostoUnitario, decimal CostoEfectivo)>
        {
            (1, 7, false, 100m, 100m)
        };

        var resultado = CalculadorDeCompra.ResolverActualizacionesDeCosto(items, new Dictionary<int, int>());

        Assert.Empty(resultado);
    }

    [Fact]
    public void ResolverActualizacionesDeCostoExcluyeCostoUnitarioCeroOMenor()
    {
        // Guard anti-bonificación (design decisión 4): una línea con costo cero no debe pisar
        // articulos.costo_nominal aunque actualizaCosto sea true.
        var items = new List<(int Orden, int? IdArticulo, bool ActualizaCosto, decimal CostoUnitario, decimal CostoEfectivo)>
        {
            (1, 7, true, 0m, 0m)
        };

        var resultado = CalculadorDeCompra.ResolverActualizacionesDeCosto(items, new Dictionary<int, int>());

        Assert.Empty(resultado);
    }

    /// <summary>Una bonificación total: costo unitario positivo, pero un descuento que iguala al importe bruto
    /// deja el costo efectivo en cero. Escribirlo pondría en cero el <c>costo_nominal</c> del artículo.</summary>
    [Fact]
    public void ResolverActualizacionesDeCostoExcluyeUnCostoEfectivoCeroAunConCostoUnitarioPositivo()
    {
        var items = new List<(int Orden, int? IdArticulo, bool ActualizaCosto, decimal CostoUnitario, decimal CostoEfectivo)>
        {
            (1, 7, true, 100m, 0m)
        };

        var resultado = CalculadorDeCompra.ResolverActualizacionesDeCosto(items, new Dictionary<int, int>());

        Assert.Empty(resultado);
    }

    /// <summary>Cada condición de <see cref="CalculadorDeCompra.ActualizaElCosto"/> tiene su caso, con las demás
    /// cumplidas: un test que rompe dos a la vez sobrevive a que se borre cualquiera de las dos.</summary>
    public static TheoryData<int?, bool, decimal, decimal, bool> CasosDeActualizaElCosto => new()
    {
        { 7, true, 100m, 100m, true },
        { null, true, 100m, 100m, false },
        { 7, false, 100m, 100m, false },
        { 7, true, 0m, 50m, false },
        { 7, true, -1m, 50m, false },
        { 7, true, 100m, 0m, false },
        { 7, true, 100m, -5m, false }
    };

    [Theory]
    [MemberData(nameof(CasosDeActualizaElCosto))]
    public void ActualizaElCostoExigeArticuloActualizaCostoYCostoUnitarioYEfectivoPositivos(
        int? idArticulo, bool actualizaCosto, decimal costoUnitario, decimal costoEfectivo, bool esperado)
    {
        Assert.Equal(
            esperado, CalculadorDeCompra.ActualizaElCosto((1, idArticulo, actualizaCosto, costoUnitario, costoEfectivo)));
    }

    [Fact]
    public void CalcularCostoEfectivoDesdeItemReplicaLaMismaFormulaQueCalcular()
    {
        var conIva = CalculadorDeCompra.CalcularCostoEfectivoDesdeItem(total: 100m, cantidad: 1m, porcentajeIva: 21m, discriminaIva: true);
        var sinIva = CalculadorDeCompra.CalcularCostoEfectivoDesdeItem(total: 121m, cantidad: 1m, porcentajeIva: 21m, discriminaIva: false);

        Assert.Equal(121m, conIva);
        Assert.Equal(121m, sinIva);
    }

    // ---- líneas por concepto (sin artículo) ----------------------------------------------------

    private static LineaDeCompra Concepto(
        string descripcion = "Flete", decimal unidades = 1m, decimal costoUnitario = 1000m, decimal descuento = 0m,
        decimal porcentajeIva = 21m, decimal? bultos = null, decimal? unidadesPorBulto = null, bool actualizaCosto = false) =>
        new(1, null, descripcion, unidades, bultos, unidadesPorBulto, costoUnitario, descuento, 1, porcentajeIva, actualizaCosto);

    [Fact]
    public void UnConceptoSumaSuImporteAlTotalPeroNoTieneArticuloNiPrecioSugerido()
    {
        var margenes = new Dictionary<int, (decimal? MargenGrupo, decimal? MargenProveedor)> { [1] = (30m, null) };

        var resultado = CalculadorDeCompra.Calcular(
            [Linea(orden: 1, idArticulo: 1, costoUnitario: 100m), Concepto() with { Orden = 2 }],
            discriminaIva: false, margenes);

        Assert.NotNull(resultado.Items[0].PrecioSugerido);
        Assert.Null(resultado.Items[1].IdArticulo);
        Assert.Null(resultado.Items[1].PrecioSugerido);
        Assert.Equal(1000m, resultado.Items[1].Total);
        Assert.Equal(1100m, resultado.Total);
    }

    [Fact]
    public void UnConceptoDiscriminaIvaComoCualquierOtraLinea()
    {
        var resultado = CalculadorDeCompra.Calcular([Concepto(costoUnitario: 1000m)], discriminaIva: true, SinMargenes);

        Assert.Equal(210m, resultado.IvaTotal);
        Assert.Equal(1210m, resultado.Total);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void UnConceptoSinDescripcionEsRechazado(string descripcion)
    {
        var error = Assert.Throws<ErrorDominio>(() =>
            CalculadorDeCompra.Calcular([Concepto(descripcion: descripcion)], discriminaIva: false, SinMargenes));

        Assert.Equal("concepto_sin_descripcion", error.Codigo);
        Assert.Equal(400, error.EstadoHttp);
    }

    [Fact]
    public void UnConceptoConBultosEsRechazado()
    {
        var error = Assert.Throws<ErrorDominio>(() =>
            CalculadorDeCompra.Calcular([Concepto(bultos: 2m, unidadesPorBulto: 6m)], discriminaIva: false, SinMargenes));

        Assert.Equal("concepto_con_bultos", error.Codigo);
        Assert.Equal(400, error.EstadoHttp);
    }

    [Fact]
    public void UnConceptoQueActualizaCostoEsRechazado()
    {
        var error = Assert.Throws<ErrorDominio>(() =>
            CalculadorDeCompra.Calcular([Concepto(actualizaCosto: true)], discriminaIva: false, SinMargenes));

        Assert.Equal("concepto_actualiza_costo", error.Codigo);
        Assert.Equal(400, error.EstadoHttp);
    }

    [Fact]
    public void ResolverActualizacionesDeCostoNuncaIncluyeUnConcepto()
    {
        var items = new List<(int Orden, int? IdArticulo, bool ActualizaCosto, decimal CostoUnitario, decimal CostoEfectivo)>
        {
            (1, null, true, 100m, 100m),
            (2, 7, true, 50m, 50m)
        };

        var resultado = CalculadorDeCompra.ResolverActualizacionesDeCosto(items, new Dictionary<int, int>());

        Assert.Equal([new ActualizacionDeCosto(IdFamilia: null, IdArticulo: 7, Costo: 50m)], resultado);
    }

    // ---- header: varias líneas ------------------------------------------------------------------

    [Fact]
    public void VariasLineasSumanSubtotalDescuentoYTotalCorrectamente()
    {
        var resultado = CalculadorDeCompra.Calcular(
            [
                Linea(orden: 1, idArticulo: 1, unidades: 1m, costoUnitario: 100m, descuento: 0m),
                Linea(orden: 2, idArticulo: 2, unidades: 2m, costoUnitario: 50m, descuento: 10m)
            ],
            discriminaIva: false, SinMargenes);

        // Línea 1: bruto 100, total 100. Línea 2: bruto 100, descuento 10, total 90.
        Assert.Equal(200m, resultado.Subtotal);
        Assert.Equal(10m, resultado.DescuentoTotal);
        Assert.Equal(190m, resultado.Total);
        Assert.Equal(resultado.Total, resultado.Items.Sum(i => i.Total));
    }

    // ---- edge case: lista vacía -------------------------------------------------------------------

    [Fact]
    public void UnaListaVaciaDeLineasDaTotalesEnCeroYSinItems()
    {
        var resultado = CalculadorDeCompra.Calcular([], discriminaIva: true, SinMargenes);

        Assert.Empty(resultado.Items);
        Assert.Equal(0m, resultado.Subtotal);
        Assert.Equal(0m, resultado.DescuentoTotal);
        Assert.Equal(0m, resultado.IvaTotal);
        Assert.Equal(0m, resultado.Total);
    }
}
