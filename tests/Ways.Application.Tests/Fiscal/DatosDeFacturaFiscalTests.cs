using Ways.Application.Fiscal;
using Ways.Domain.Clientes;
using Ways.Domain.Organizacion;

namespace Ways.Application.Tests.Fiscal;

/// <summary>
/// Facturación por medio de pago, T7b: los armadores puros de los datos legales de la factura impresa
/// (<see cref="EmisorDeFacturaFiscal"/>, <see cref="ReceptorDeFacturaFiscal"/>, <see cref="TotalesDeFacturaFiscal"/>).
/// </summary>
public class DatosDeFacturaFiscalTests
{
    private static Cliente Cliente(string nombre, string? apellido = null, string? razonSocial = null, string? domicilio = null) =>
        new() { Nombre = nombre, Apellido = apellido, RazonSocial = razonSocial, Domicilio = domicilio };

    [Fact]
    public void ElNombreAImprimirEsLaRazonSocialSiLaHayYSiNoNombreYApellido()
    {
        Assert.Equal("Gómez Hnos. SRL", ReceptorDeFacturaFiscal.NombreAImprimir(Cliente("Juana", "Gómez", " Gómez Hnos. SRL ")));
        Assert.Equal("Juana Gómez", ReceptorDeFacturaFiscal.NombreAImprimir(Cliente("Juana", "Gómez")));
        Assert.Equal("Juana Gómez", ReceptorDeFacturaFiscal.NombreAImprimir(Cliente(" Juana ", " Gómez ", "   ")));
    }

    [Fact]
    public void ElNombreAImprimirSinApellidoNoDejaEspaciosSobrantes()
    {
        Assert.Equal("Juana", ReceptorDeFacturaFiscal.NombreAImprimir(Cliente("Juana")));
        Assert.Equal("Juana", ReceptorDeFacturaFiscal.NombreAImprimir(Cliente("Juana", "  ")));
    }

    [Theory]
    [InlineData(80, "CUIT")]
    [InlineData(86, "CUIL")]
    [InlineData(96, "DNI")]
    [InlineData(94, "Pasaporte")]
    [InlineData(99, "Sin identificar")]
    public void LaEtiquetaDelDocumentoSigueLosCodigosDeArca(short codigo, string etiqueta) =>
        Assert.Equal(etiqueta, ReceptorDeFacturaFiscal.EtiquetaDeTipoDocumento(codigo));

    [Fact]
    public void ElReceptorLlevaElDocumentoEnviadoAArcaYElDomicilioDelCliente()
    {
        var receptor = ReceptorDeFacturaFiscal.De(Cliente("Juana", "Gómez", domicilio: "Calle Falsa 123"), 96, 30111222, "Consumidor Final");

        Assert.Equal(
            new ReceptorDeFacturaFiscal("Juana Gómez", "DNI", 96, 30111222, "Consumidor Final", "Calle Falsa 123"), receptor);
    }

    [Fact]
    public void ElEmisorTomaElDomicilioDelPuntoDeVentaYLosDatosLegalesDeLaEmpresa()
    {
        var empresa = new Empresa
        {
            RazonSocial = "Emisor SA", NombreFantasia = "Mercado", Cuit = "20111111112", IngresosBrutos = "Exento",
            InicioActividades = new DateOnly(2012, 4, 2)
        };
        var puntoVenta = new PuntoVenta { Nombre = "Local", Domicilio = "Av. Siempre Viva 742" };

        var emisor = EmisorDeFacturaFiscal.De(empresa, puntoVenta, "Responsable Inscripto");

        Assert.Equal(
            new EmisorDeFacturaFiscal(
                "Emisor SA", "Mercado", "20111111112", "Responsable Inscripto", "Exento", new DateOnly(2012, 4, 2),
                "Av. Siempre Viva 742"),
            emisor);
    }

    [Fact]
    public void LosTotalesTraenCadaAlicuotaConSuPorcentajeEnElOrdenDeLaComposicion()
    {
        LineaFiscal[] lineas =
        [
            new(2, "10.5%", 4, 10.5m, 110.50m),
            new(1, "21%", 5, 21m, 121.00m),
            new(1, "21%", 5, 21m, 60.50m)
        ];

        var totales = TotalesDeFacturaFiscal.De(lineas);

        Assert.Equal(
            [(10.5m, 100.00m, 10.50m), (21m, 150.00m, 31.50m)],
            totales.Alicuotas.Select(a => (a.Porcentaje, a.Neto, a.Iva)));
        Assert.Equal((250.00m, 42.00m, 0m, 0m, 0m, 292.00m), (totales.ImpNeto, totales.ImpIva, totales.ImpOpEx, totales.ImpTotConc, totales.ImpTrib, totales.ImpTotal));
    }

    [Fact]
    public void ExentoYNoGravadoVanASusImportesYNoALasAlicuotas()
    {
        LineaFiscal[] lineas =
        [
            new(1, "21%", 5, 21m, 121.00m),
            new(3, "Exento", null, 0m, 50.00m),
            new(4, "No gravado", null, 0m, 30.00m)
        ];

        var totales = TotalesDeFacturaFiscal.De(lineas);

        var alicuota = Assert.Single(totales.Alicuotas);
        Assert.Equal((21m, 100m, 21m), (alicuota.Porcentaje, alicuota.Neto, alicuota.Iva));
        Assert.Equal((50m, 30m, 201m), (totales.ImpOpEx, totales.ImpTotConc, totales.ImpTotal));
    }

    [Fact]
    public void LosTotalesDeUnaFacturaSoloExentaNoTienenAlicuotas()
    {
        var totales = TotalesDeFacturaFiscal.De([new(3, "Exento", null, 0m, 50.00m)]);

        Assert.Empty(totales.Alicuotas);
        Assert.Equal((0m, 0m, 50m, 50m), (totales.ImpNeto, totales.ImpIva, totales.ImpOpEx, totales.ImpTotal));
    }
}
