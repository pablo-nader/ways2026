using Ways.Domain.Common;
using Ways.Domain.Compras;

namespace Ways.Domain.Tests.Compras;

/// <summary>
/// Impuestos internos por línea en <see cref="CalculadorDeCompra"/>: se suman al total fuera de la base del IVA y
/// entran en el costo efectivo. Pura, sin base de datos.
/// </summary>
public class CalculadorDeCompraImpuestosInternosTests
{
    private static readonly IReadOnlyDictionary<int, (decimal? MargenGrupo, decimal? MargenProveedor)> SinMargenes =
        new Dictionary<int, (decimal?, decimal?)>();

    private static LineaDeCompra Linea(
        int orden, decimal costo, decimal impuestoInterno, decimal unidades = 1m, int? idArticulo = 1, decimal porcentajeIva = 21m) =>
        new(orden, idArticulo, "item de prueba", unidades, null, null, costo, 0m, 1, porcentajeIva, idArticulo is not null,
            impuestoInterno);

    private static CompraCalculada Calcular(
        IReadOnlyList<LineaDeCompra> lineas, bool discriminaIva = true, bool preciosIncluyenIva = false) =>
        CalculadorDeCompra.Calcular(lineas, discriminaIva, SinMargenes, null, preciosIncluyenIva);

    [Fact]
    public void ElTotalSumaElImpuestoInternoFueraDeLaBaseDelIva()
    {
        var resultado = Calcular([Linea(1, costo: 100m, impuestoInterno: 10m)]);

        var alicuota = Assert.Single(resultado.Alicuotas);
        Assert.Equal(100m, alicuota.Neto);
        Assert.Equal(21m, alicuota.Iva);
        Assert.Equal(10m, resultado.ImpuestosInternos);
        Assert.Equal(131m, resultado.Total);
    }

    [Fact]
    public void ElImpuestoInternoNoCambiaElNetoNiElIvaNiElTotalDeLaLinea()
    {
        var sin = Calcular([Linea(1, costo: 100m, impuestoInterno: 0m)]);
        var con = Calcular([Linea(1, costo: 100m, impuestoInterno: 10m)]);

        Assert.Equal(sin.Alicuotas, con.Alicuotas);
        Assert.Equal(sin.IvaTotal, con.IvaTotal);
        Assert.Equal(sin.Items[0].Total, con.Items[0].Total);
        Assert.Equal(sin.Subtotal, con.Subtotal);
    }

    [Fact]
    public void ElCostoEfectivoConIvaIncluyeElImpuestoInterno()
    {
        var resultado = Calcular([Linea(1, costo: 100m, impuestoInterno: 20m, unidades: 2m)]);

        // (200 neto x 1,21 + 20) / 2 unidades
        Assert.Equal(131m, Assert.Single(resultado.Items).CostoEfectivo);
    }

    [Fact]
    public void ConPreciosFinalesElImpuestoInternoSeSumaAlFinalYAlCostoEfectivo()
    {
        var resultado = Calcular([Linea(1, costo: 121m, impuestoInterno: 10m)], preciosIncluyenIva: true);

        var alicuota = Assert.Single(resultado.Alicuotas);
        Assert.Equal(100m, alicuota.Neto);
        Assert.Equal(21m, alicuota.Iva);
        Assert.Equal(131m, resultado.Total);
        Assert.Equal(131m, Assert.Single(resultado.Items).CostoEfectivo);
    }

    [Fact]
    public void ElTotalDelComprobanteSumaTodasLasLineasYLasPercepciones()
    {
        var resultado = CalculadorDeCompra.Calcular(
            [Linea(1, 100m, 10m), Linea(2, 50m, 5m, idArticulo: 2)],
            discriminaIva: true,
            SinMargenes,
            null,
            false,
            [new PercepcionDeCompra("IIBB", 150m, 3m, 4.5m)]);

        Assert.Equal(15m, resultado.ImpuestosInternos);
        Assert.Equal(150m + 31.5m + 15m + 4.5m, resultado.Total);
    }

    [Fact]
    public void SinImpuestoInternoNadaCambia()
    {
        var resultado = Calcular([Linea(1, costo: 100m, impuestoInterno: 0m)]);

        Assert.Equal(0m, resultado.ImpuestosInternos);
        Assert.Equal(121m, resultado.Total);
        Assert.Equal(121m, Assert.Single(resultado.Items).CostoEfectivo);
    }

    [Fact]
    public void ElCostoEfectivoDesdeElItemGuardadoTambienLoIncluye()
    {
        Assert.Equal(131m, CalculadorDeCompra.CalcularCostoEfectivoDesdeItem(100m, 1m, 21m, true, false, 10m));
        Assert.Equal(131m, CalculadorDeCompra.CalcularCostoEfectivoDesdeItem(121m, 1m, 21m, true, true, 10m));
    }

    [Fact]
    public void UnImpuestoInternoEnUnComprobanteQueNoDiscriminaIvaSeRechaza()
    {
        var error = Assert.Throws<ErrorDominio>(
            () => Calcular([Linea(1, costo: 100m, impuestoInterno: 10m)], discriminaIva: false));

        Assert.Equal("impuesto_interno_sin_discriminar", error.Codigo);
        Assert.Equal(400, error.EstadoHttp);
    }

    [Fact]
    public void UnImpuestoInternoNegativoSeRechaza()
    {
        var error = Assert.Throws<ErrorDominio>(() => Calcular([Linea(1, costo: 100m, impuestoInterno: -1m)]));

        Assert.Equal("impuesto_interno_invalido", error.Codigo);
        Assert.Equal(400, error.EstadoHttp);
    }

    [Fact]
    public void UnImpuestoInternoConMasDeDosDecimalesSeRechaza()
    {
        var error = Assert.Throws<ErrorDominio>(() => Calcular([Linea(1, costo: 100m, impuestoInterno: 0.005m)]));

        Assert.Equal("impuesto_interno_invalido", error.Codigo);
        Assert.Equal(400, error.EstadoHttp);
    }

    [Fact]
    public void UnImpuestoInternoConDosDecimalesSeAcepta()
    {
        Assert.Equal(0.01m, Calcular([Linea(1, costo: 100m, impuestoInterno: 0.01m)]).ImpuestosInternos);
    }

    [Fact]
    public void UnConceptoNoAdmiteImpuestoInterno()
    {
        var error = Assert.Throws<ErrorDominio>(
            () => Calcular([Linea(1, costo: 100m, impuestoInterno: 10m, idArticulo: null)]));

        Assert.Equal("concepto_con_impuesto_interno", error.Codigo);
    }
}
