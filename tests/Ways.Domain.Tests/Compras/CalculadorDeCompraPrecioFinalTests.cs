using Ways.Domain.Common;
using Ways.Domain.Compras;

namespace Ways.Domain.Tests.Compras;

/// <summary>
/// Modo "precio final" (<c>precios_incluyen_iva</c>) de <see cref="CalculadorDeCompra"/> y el
/// efecto de las percepciones en el total — pura, sin base de datos.
/// </summary>
public class CalculadorDeCompraPrecioFinalTests
{
    private static readonly IReadOnlyDictionary<int, (decimal? MargenGrupo, decimal? MargenProveedor)> SinMargenes =
        new Dictionary<int, (decimal?, decimal?)>();

    private static LineaDeCompra Linea(
        int orden, decimal costo, int idAlicuota, decimal porcentaje, decimal unidades = 1m, decimal descuento = 0m) =>
        new(orden, 1, "item de prueba", unidades, null, null, costo, descuento, idAlicuota, porcentaje, true);

    private static CompraCalculada CalcularFinal(
        IReadOnlyList<LineaDeCompra> lineas, IReadOnlyDictionary<int, decimal>? ivaImpreso = null,
        IReadOnlyList<PercepcionDeCompra>? percepciones = null) =>
        CalculadorDeCompra.Calcular(
            lineas, discriminaIva: true, SinMargenes, ivaImpreso, preciosIncluyenIva: true, percepciones);

    [Fact]
    public void ElIvaSeExtraeDelPrecioFinalYElNetoEsElResto()
    {
        var resultado = CalcularFinal([Linea(1, 121m, idAlicuota: 1, porcentaje: 21m)]);

        Assert.Equal(new AlicuotaCalculada(1, 21m, 100m, 21m), resultado.Alicuotas.Single());
        Assert.Equal(21m, resultado.IvaTotal);
        Assert.Equal(121m, resultado.Total);
    }

    [Fact]
    public void AlicuotasMezcladasSumanExactamenteLoTipeadoSinImportarElRedondeo()
    {
        var resultado = CalcularFinal(
        [
            Linea(1, 121m, idAlicuota: 1, porcentaje: 21m),
            Linea(2, 60.5m, idAlicuota: 1, porcentaje: 21m),
            Linea(3, 110.5m, idAlicuota: 2, porcentaje: 10.5m),
            Linea(4, 50m, idAlicuota: 5, porcentaje: 0m)
        ]);

        Assert.Equal(new AlicuotaCalculada(1, 21m, 150m, 31.5m), resultado.Alicuotas[0]);
        Assert.Equal(new AlicuotaCalculada(2, 10.5m, 100m, 10.5m), resultado.Alicuotas[1]);
        Assert.Equal(new AlicuotaCalculada(5, 0m, 50m, 0m), resultado.Alicuotas[2]);
        Assert.Equal(42m, resultado.IvaTotal);
        Assert.Equal(342m, resultado.Total);
        Assert.Equal(342m, resultado.Alicuotas.Sum(a => a.Neto + a.Iva));
    }

    [Fact]
    public void ElRedondeoSeHaceUnaVezPorAlicuotaYNetoMasIvaIgualaElFinal()
    {
        // Tres líneas finales de 0.50 al 21%: el final de la alícuota es 1.50 y su IVA
        // round(1.50 x 21 / 121, 2) = 0.26; el neto es el resto (1.24), no un redondeo propio.
        var resultado = CalcularFinal(
        [
            Linea(1, 0.5m, idAlicuota: 1, porcentaje: 21m),
            Linea(2, 0.5m, idAlicuota: 1, porcentaje: 21m),
            Linea(3, 0.5m, idAlicuota: 1, porcentaje: 21m)
        ]);

        var alicuota = Assert.Single(resultado.Alicuotas);
        Assert.Equal(0.26m, alicuota.Iva);
        Assert.Equal(1.24m, alicuota.Neto);
        Assert.Equal(1.5m, alicuota.Neto + alicuota.Iva);
        Assert.Equal(1.5m, resultado.Total);
    }

    [Fact]
    public void ElDescuentoSeRestaDelPrecioFinalDeLaLinea()
    {
        var resultado = CalcularFinal([Linea(1, 100m, idAlicuota: 1, porcentaje: 21m, unidades: 2m, descuento: 20m)]);

        var item = Assert.Single(resultado.Items);
        Assert.Equal(180m, item.Total);
        Assert.Equal(90m, item.CostoEfectivo);
        Assert.Equal(180m, resultado.Total);
        Assert.Equal(200m, resultado.Subtotal);
        Assert.Equal(20m, resultado.DescuentoTotal);
    }

    [Fact]
    public void ElCostoEfectivoDeUnArticuloEsElFinalSobreLaCantidadSinSumarleIvaOtraVez()
    {
        var final = CalcularFinal([Linea(1, 30.25m, idAlicuota: 1, porcentaje: 21m, unidades: 4m)]);
        var neto = CalculadorDeCompra.Calcular(
            [Linea(1, 30.25m, idAlicuota: 1, porcentaje: 21m, unidades: 4m)], discriminaIva: true, SinMargenes);

        Assert.Equal(30.25m, final.Items.Single().CostoEfectivo);
        Assert.Equal(36.6m, neto.Items.Single().CostoEfectivo);
    }

    [Fact]
    public void ElIvaImpresoMueveElNetoEnSentidoContrarioYElFinalNoCambia()
    {
        var resultado = CalcularFinal(
            [Linea(1, 121m, idAlicuota: 1, porcentaje: 21m)], new Dictionary<int, decimal> { [1] = 21.5m });

        Assert.Equal(new AlicuotaCalculada(1, 21m, 99.5m, 21.5m), resultado.Alicuotas.Single());
        Assert.Equal(21.5m, resultado.IvaTotal);
        Assert.Equal(121m, resultado.Total);
    }

    [Fact]
    public void ElIvaImpresoSeVerificaContraElCalculadoDelModoPrecioFinal()
    {
        var error = Assert.Throws<ErrorDominio>(() => CalcularFinal(
            [Linea(1, 121m, idAlicuota: 1, porcentaje: 21m)], new Dictionary<int, decimal> { [1] = 22.01m }));

        Assert.Equal("iva_impreso_fuera_de_tolerancia", error.Codigo);
        Assert.Equal(400, error.EstadoHttp);
    }

    [Fact]
    public void ElIvaImpresoQueDejariaUnNetoNegativoSeRechaza()
    {
        // Final 0.50 al 21%: IVA calculado 0.09; un impreso de 1.09 está dentro de la tolerancia
        // pero dejaría un neto de -0.59.
        var error = Assert.Throws<ErrorDominio>(() => CalcularFinal(
            [Linea(1, 0.5m, idAlicuota: 1, porcentaje: 21m)], new Dictionary<int, decimal> { [1] = 1.09m }));

        Assert.Equal("iva_impreso_fuera_de_tolerancia", error.Codigo);
    }

    [Fact]
    public void ElModoPrecioFinalEnUnComprobanteQueNoDiscriminaSeRechazaEnVezDeIgnorarse()
    {
        var error = Assert.Throws<ErrorDominio>(() => CalculadorDeCompra.Calcular(
            [Linea(1, 121m, idAlicuota: 1, porcentaje: 21m)], discriminaIva: false, SinMargenes,
            preciosIncluyenIva: true));

        Assert.Equal("precios_incluyen_iva_sin_discriminar", error.Codigo);
        Assert.Equal(400, error.EstadoHttp);
    }

    [Fact]
    public void ElCostoDesdeUnItemPersistidoRespetaElModoDelComprobante()
    {
        Assert.Equal(121m, CalculadorDeCompra.CalcularCostoEfectivoDesdeItem(
            121m, 1m, 21m, discriminaIva: true, preciosIncluyenIva: true));
        Assert.Equal(121m, CalculadorDeCompra.CalcularCostoEfectivoDesdeItem(
            100m, 1m, 21m, discriminaIva: true, preciosIncluyenIva: false));
        Assert.Equal(100m, CalculadorDeCompra.CalcularCostoEfectivoDesdeItem(
            100m, 1m, 21m, discriminaIva: false));
    }

    [Fact]
    public void LasPercepcionesSumanAlTotalConPreciosNetos()
    {
        var resultado = CalculadorDeCompra.Calcular(
            [Linea(1, 100m, idAlicuota: 1, porcentaje: 21m)], discriminaIva: true, SinMargenes,
            percepciones:
            [
                new PercepcionDeCompra(TiposDePercepcion.Iibb, 100m, 3m, 3m),
                new PercepcionDeCompra(TiposDePercepcion.Iva, 100m, 1.5m, 1.5m)
            ]);

        Assert.Equal(21m, resultado.IvaTotal);
        Assert.Equal(125.5m, resultado.Total);
        Assert.Equal(2, resultado.Percepciones.Count);
    }

    [Fact]
    public void LasPercepcionesSumanAlTotalConPreciosFinalesSinContarElIvaDosVeces()
    {
        var resultado = CalcularFinal(
            [Linea(1, 121m, idAlicuota: 1, porcentaje: 21m)],
            percepciones: [new PercepcionDeCompra(TiposDePercepcion.Iibb, 100m, 3.63m, 3.63m)]);

        Assert.Equal(124.63m, resultado.Total);
        Assert.Equal(21m, resultado.IvaTotal);
    }

    [Fact]
    public void ElImporteDeLaPercepcionEsElQueDiceLaFacturaNoElQueSaldriaDeBaseYAlicuota()
    {
        var resultado = CalculadorDeCompra.Calcular(
            [Linea(1, 100m, idAlicuota: 1, porcentaje: 21m)], discriminaIva: true, SinMargenes,
            percepciones: [new PercepcionDeCompra(TiposDePercepcion.Iibb, 100m, 3m, 3.1m)]);

        Assert.Equal(124.1m, resultado.Total);
        Assert.Equal(3.1m, Assert.Single(resultado.Percepciones).Importe);
    }

    [Fact]
    public void UnComprobanteSinPercepcionesNoCambiaSuTotal()
    {
        var resultado = CalculadorDeCompra.Calcular(
            [Linea(1, 100m, idAlicuota: 1, porcentaje: 21m)], discriminaIva: true, SinMargenes);

        Assert.Equal(121m, resultado.Total);
        Assert.Empty(resultado.Percepciones);
    }
}
