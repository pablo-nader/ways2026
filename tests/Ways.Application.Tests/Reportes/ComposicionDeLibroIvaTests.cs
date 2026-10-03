using Ways.Application.Fiscal;
using Ways.Application.Reportes;
using Ways.Domain.Common;

namespace Ways.Application.Tests.Reportes;

/// <summary>Armado del libro IVA sin base de datos: columnas de compras (incluido B/C sin crédito
/// fiscal, exento vs no gravado y percepciones), recomposición de ventas con la función de la
/// emisión fiscal, signo de la nota de crédito y filas cuyos componentes no cierran.</summary>
public class ComposicionDeLibroIvaTests
{
    private static readonly DateOnly Desde = new(2026, 5, 1);
    private static readonly DateOnly Hasta = new(2026, 5, 31);

    private static AlicuotaDeCompraParaLibro Gravada(decimal porcentaje, decimal neto, decimal iva) =>
        new(5, $"{porcentaje}%", porcentaje, neto, iva);

    private static AlicuotaDeCompraParaLibro Exento(decimal neto) => new(null, "Exento", 0m, neto, 0m);

    private static AlicuotaDeCompraParaLibro NoGravado(decimal neto) => new(null, "No gravado", 0m, neto, 0m);

    private static CompraParaLibro Compra(
        string numero = "0001-00000001", string tipo = "C-FA", bool discrimina = true, decimal total = 0m,
        IReadOnlyList<AlicuotaDeCompraParaLibro>? alicuotas = null, decimal percepcionIva = 0m,
        decimal percepcionIibb = 0m, DateOnly? fecha = null) =>
        new(fecha ?? new DateOnly(2026, 5, 10), tipo, numero, "Proveedor SA", "30-11111111-8", discrimina, total,
            alicuotas ?? [], percepcionIva, percepcionIibb);

    private static VentaParaLibro Venta(
        IReadOnlyList<LineaFiscal> lineas, decimal total, short signo = 1, string tipo = "FA",
        string numero = "0003-00000001", DateOnly? fecha = null) =>
        new(fecha ?? new DateOnly(2026, 5, 10), tipo, numero, "Cliente SA", "CUIT 30222222228", signo, total, lineas);

    private static LineaFiscal LineaGravada(decimal porcentaje, short codigo, decimal total) =>
        new(codigo, $"{porcentaje}%", codigo, porcentaje, total);

    [Fact]
    public void UnaFacturaAConAlicuotasMixtasExentoNoGravadoYPercepcionesCierraContraSuTotal()
    {
        var compra = Compra(
            total: 1556m,
            alicuotas: [Gravada(21m, 1000m, 210m), Gravada(10.5m, 200m, 21m), Exento(50m), NoGravado(30m)],
            percepcionIva: 15m,
            percepcionIibb: 30m);

        var libro = ComposicionDeLibroIva.DeCompras(Desde, Hasta, null, [compra]);

        var fila = Assert.Single(libro.Filas);
        Assert.Equal(
            [new AlicuotaDeLibroIva(21m, 1000m, 210m), new AlicuotaDeLibroIva(10.5m, 200m, 21m)], fila.Alicuotas);
        Assert.Equal(50m, fila.Exento);
        Assert.Equal(30m, fila.NoGravado);
        Assert.Equal(15m, fila.PercepcionIva);
        Assert.Equal(30m, fila.PercepcionIibb);
        Assert.Equal(1556m, fila.Total);
        Assert.Equal(1200m, fila.NetoGravado);
        Assert.Equal(231m, fila.IvaTotal);
        Assert.Equal(0m, fila.Diferencia);
        Assert.Equal("Proveedor SA", fila.Contraparte);
        Assert.Equal("30-11111111-8", fila.Documento);
    }

    [Fact]
    public void UnaFacturaBOCSinDiscriminarVaEnteraANoGravadoSinCreditoFiscal()
    {
        var compra = Compra(tipo: "C-FB", discrimina: false, total: 500m, percepcionIibb: 20m);

        var fila = Assert.Single(ComposicionDeLibroIva.DeCompras(Desde, Hasta, null, [compra]).Filas);

        Assert.Empty(fila.Alicuotas);
        Assert.Equal(0m, fila.IvaTotal);
        Assert.Equal(480m, fila.NoGravado);
        Assert.Equal(0m, fila.Exento);
        Assert.Equal(20m, fila.PercepcionIibb);
        Assert.Equal(500m, fila.Total);
        Assert.Equal(0m, fila.Diferencia);
    }

    [Fact]
    public void ExentoYNoGravadoSeDistinguenPorNombreYUnCeroPorCientoRealQuedaComoAlicuota()
    {
        var ceroReal = new AlicuotaDeCompraParaLibro(3, "0%", 0m, 80m, 0m);
        var compra = Compra(total: 80m + 50m + 30m, alicuotas: [ceroReal, Exento(50m), NoGravado(30m)]);

        var fila = Assert.Single(ComposicionDeLibroIva.DeCompras(Desde, Hasta, null, [compra]).Filas);

        Assert.Equal([new AlicuotaDeLibroIva(0m, 80m, 0m)], fila.Alicuotas);
        Assert.Equal(50m, fila.Exento);
        Assert.Equal(30m, fila.NoGravado);
        Assert.Equal(0m, fila.Diferencia);
    }

    [Fact]
    public void UnaAlicuotaDeCompraSinMapeoSeMuestraConLasOtrasAlicuotasYSeAdvierteSinTirarElLibro()
    {
        var rara = new AlicuotaDeCompraParaLibro(null, "Rara", 7m, 100m, 7m);
        var buena = Compra("0001-00000002", total: 121m, alicuotas: [Gravada(21m, 100m, 21m)]);
        var conRara = Compra("0001-00000001", total: 107m, alicuotas: [rara]);

        var libro = ComposicionDeLibroIva.DeCompras(Desde, Hasta, null, [conRara, buena]);

        var fila = libro.Filas.Single(f => f.Numero == "0001-00000001");
        Assert.Equal([new AlicuotaDeLibroIva(7m, 100m, 7m)], fila.Alicuotas);
        Assert.Equal(0m, fila.Diferencia);
        Assert.Equal([AdvertenciasDeLibroIva.AlicuotaSinClasificar], fila.Advertencias);
        Assert.Empty(libro.Filas.Single(f => f.Numero == "0001-00000002").Advertencias);
        Assert.Equal(228m, libro.Totales.Total);
    }

    [Fact]
    public void UnaAlicuotaDeVentaSinMapeoSeRecomponeEnSuPorcentajeYSeAdvierteSinTirarElLibro()
    {
        var lineas = new List<LineaFiscal> { new(77, "Rara", null, 10m, 110m), LineaGravada(21m, 5, 121m) };

        var fila = Assert.Single(ComposicionDeLibroIva.DeVentas(Desde, Hasta, null, null, [Venta(lineas, 231m)]).Filas);

        Assert.Equal(
            [new AlicuotaDeLibroIva(21m, 100m, 21m), new AlicuotaDeLibroIva(10m, 100m, 10m)], fila.Alicuotas);
        Assert.Equal(0m, fila.Diferencia);
        Assert.Equal([AdvertenciasDeLibroIva.AlicuotaSinClasificar], fila.Advertencias);
    }

    [Fact]
    public void UnaVentaAnuladaLocalmenteConCaeSigueEnElLibroConSuImporteYLaAdvertencia()
    {
        var anulada = Venta([LineaGravada(21m, 5, 121m)], 121m) with { AnuladoLocalmente = true };

        var libro = ComposicionDeLibroIva.DeVentas(Desde, Hasta, null, null, [anulada]);

        var fila = Assert.Single(libro.Filas);
        Assert.Equal(121m, fila.Total);
        Assert.Equal([AdvertenciasDeLibroIva.AnuladoSinNotaDeCredito], fila.Advertencias);
        Assert.Equal(121m, libro.Totales.Total);
    }

    [Fact]
    public void UnaVentaSinNumeroFiscalDePuntoDeVentaSeAdvierte()
    {
        var venta = Venta([LineaGravada(21m, 5, 121m)], 121m) with { SinNumeroFiscal = true };

        var fila = Assert.Single(ComposicionDeLibroIva.DeVentas(Desde, Hasta, null, null, [venta]).Filas);

        Assert.Equal([AdvertenciasDeLibroIva.SinNumeroFiscal], fila.Advertencias);
    }

    [Fact]
    public void UnaFilaCuyosComponentesNoCierranSeInformaConSuDiferenciaYNoSeOculta()
    {
        var compra = Compra(total: 1000m, alicuotas: [Gravada(21m, 700m, 147m)]);

        var libro = ComposicionDeLibroIva.DeCompras(Desde, Hasta, null, [compra]);

        var fila = Assert.Single(libro.Filas);
        Assert.Equal(153m, fila.Diferencia);
        Assert.Equal(153m, libro.Totales.Diferencia);
        Assert.Equal(1000m, libro.Totales.Total);
    }

    [Fact]
    public void LosTotalesSumanPorAlicuotaYGeneralesYSeOrdenanDeMayorAMenor()
    {
        var compras = new[]
        {
            Compra("0001-00000002", total: 121m, alicuotas: [Gravada(21m, 100m, 21m)], fecha: new DateOnly(2026, 5, 12)),
            Compra(
                "0001-00000001", total: 363m + 55.25m + 10m,
                alicuotas: [Gravada(10.5m, 50m, 5.25m), Gravada(21m, 300m, 63m), NoGravado(10m)],
                fecha: new DateOnly(2026, 5, 11)),
            Compra("0001-00000009", total: 12m, discrimina: false, tipo: "C-FC", fecha: new DateOnly(2026, 5, 11))
        };

        var totales = ComposicionDeLibroIva.DeCompras(Desde, Hasta, null, compras).Totales;

        Assert.Equal(
            [new AlicuotaDeLibroIva(21m, 400m, 84m), new AlicuotaDeLibroIva(10.5m, 50m, 5.25m)], totales.PorAlicuota);
        Assert.Equal(22m, totales.NoGravado);
        Assert.Equal(121m + 428.25m + 12m, totales.Total);
        Assert.Equal(0m, totales.Diferencia);
        Assert.Equal(450m, totales.NetoGravado);
        Assert.Equal(89.25m, totales.IvaTotal);
    }

    [Fact]
    public void LasFilasSeOrdenanPorFechaTipoYNumero()
    {
        var compras = new[]
        {
            Compra("0001-00000003", total: 1m, discrimina: false, fecha: new DateOnly(2026, 5, 20)),
            Compra("0001-00000002", total: 1m, discrimina: false, fecha: new DateOnly(2026, 5, 10)),
            Compra("0001-00000001", total: 1m, discrimina: false, fecha: new DateOnly(2026, 5, 10))
        };

        var libro = ComposicionDeLibroIva.DeCompras(Desde, Hasta, null, compras);

        Assert.Equal(["0001-00000001", "0001-00000002", "0001-00000003"], libro.Filas.Select(f => f.Numero));
    }

    [Fact]
    public void UnLibroSinFilasTieneTotalesEnCero()
    {
        var libro = ComposicionDeLibroIva.DeCompras(Desde, Hasta, 7, []);

        Assert.Empty(libro.Filas);
        Assert.Empty(libro.Totales.PorAlicuota);
        Assert.Equal(0m, libro.Totales.Total);
        Assert.Equal(7, libro.IdEmpresa);
    }

    [Fact]
    public void LaVentaFiscalSeRecomponeConLaFuncionDeLaEmisionPorAlicuotaExentoYNoGravado()
    {
        var lineas = new List<LineaFiscal>
        {
            LineaGravada(21m, 5, 121m),
            LineaGravada(10.5m, 4, 110.5m),
            new(90, "Exento", null, 0m, 50m),
            new(91, "No gravado", null, 0m, 30m)
        };

        var libro = ComposicionDeLibroIva.DeVentas(Desde, Hasta, 1, "UTC", [Venta(lineas, 311.5m)]);

        var fila = Assert.Single(libro.Filas);
        Assert.Equal(
            [new AlicuotaDeLibroIva(21m, 100m, 21m), new AlicuotaDeLibroIva(10.5m, 100m, 10.5m)], fila.Alicuotas);
        Assert.Equal(50m, fila.Exento);
        Assert.Equal(30m, fila.NoGravado);
        Assert.Equal(0m, fila.PercepcionIva);
        Assert.Equal(0m, fila.PercepcionIibb);
        Assert.Equal(311.5m, fila.Total);
        Assert.Equal(0m, fila.Diferencia);
        Assert.Equal("UTC", libro.ZonaHoraria);

        var emision = ComposicionDeTotalesFiscales.Componer(lineas);
        Assert.Equal(emision.ImpNeto, fila.NetoGravado);
        Assert.Equal(emision.ImpIVA, fila.IvaTotal);
        Assert.Equal(emision.ImpOpEx, fila.Exento);
        Assert.Equal(emision.ImpTotConc, fila.NoGravado);
        Assert.Equal(emision.ImpTotal, fila.Total);
    }

    [Fact]
    public void UnaNotaDeCreditoRestaTodosLosImportes()
    {
        var factura = Venta([LineaGravada(21m, 5, 121m), new LineaFiscal(90, "Exento", null, 0m, 50m)], 171m);
        var notaDeCredito = Venta(
            [LineaGravada(21m, 5, 60.5m), new LineaFiscal(90, "Exento", null, 0m, 10m)], 70.5m, signo: -1, tipo: "NCA",
            numero: "0003-00000002");

        var libro = ComposicionDeLibroIva.DeVentas(Desde, Hasta, null, null, [factura, notaDeCredito]);

        var nc = libro.Filas.Single(f => f.TipoComprobante == "NCA");
        Assert.Equal([new AlicuotaDeLibroIva(21m, -50m, -10.5m)], nc.Alicuotas);
        Assert.Equal(-10m, nc.Exento);
        Assert.Equal(-70.5m, nc.Total);
        Assert.Equal(0m, nc.Diferencia);
        Assert.Equal([new AlicuotaDeLibroIva(21m, 50m, 10.5m)], libro.Totales.PorAlicuota);
        Assert.Equal(40m, libro.Totales.Exento);
        Assert.Equal(100.5m, libro.Totales.Total);
        Assert.Null(libro.ZonaHoraria);
    }

    [Fact]
    public void UnaVentaCuyoTotalNoCierraContraSusLineasSeInformaConSuDiferencia()
    {
        var venta = Venta([LineaGravada(21m, 5, 121m)], 130m);

        var fila = Assert.Single(ComposicionDeLibroIva.DeVentas(Desde, Hasta, null, null, [venta]).Filas);

        Assert.Equal(9m, fila.Diferencia);
    }
}
