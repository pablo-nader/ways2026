using System.Text.Json;
using Ways.Application.MercadoPago;
using Ways.Infrastructure.MercadoPago;

namespace Ways.Application.Tests.MercadoPago;

/// <summary>El pago de <c>/v1/payments</c> se lee con las opciones del cliente de la API y la comisión informada sale solo de las
/// comisiones a cargo del vendedor de un pago aprobado por el importe del cobro.</summary>
public class ComisionInformadaDeMercadoPagoTests
{
    private static PagoDeMercadoPago Leer(string json) =>
        JsonSerializer.Deserialize<PagoDeMercadoPago>(json, ClienteApiMercadoPago.OpcionesJson)!;

    private static decimal? Comision(string json, decimal importePagado = 100m) =>
        ComisionInformadaDeMercadoPago.Calcular(Leer(json), importePagado);

    [Fact]
    public void ElPagoSeLeeIgnorandoLoQueWaysNoUsaYConNumerosComoCadena()
    {
        var pago = Leer("""
            {
              "id": 1234567890, "status": "approved", "transaction_amount": "100.00", "otro": {"x": 1},
              "fee_details": [{"type": "mercadopago_fee", "amount": 9.04, "fee_payer": "collector"}],
              "transaction_details": {"net_received_amount": 90.96, "total_paid_amount": 100}
            }
            """);

        Assert.Equal(1234567890L, pago.Id);
        Assert.Equal(100m, pago.TransactionAmount);
        var comision = Assert.Single(pago.FeeDetails!);
        Assert.Equal(new ComisionDePagoMercadoPago("mercadopago_fee", 9.04m, "collector"), comision);
        Assert.Equal(90.96m, pago.TransactionDetails!.NetReceivedAmount);
    }

    [Fact]
    public void LaComisionEsLaSumaDeLasComisionesACargoDelVendedor()
    {
        var comision = Comision("""
            {"status": "approved", "transaction_amount": 100, "fee_details": [
              {"type": "mercadopago_fee", "amount": 3.50, "fee_payer": "collector"},
              {"type": "financing_fee", "amount": 1.25, "fee_payer": "collector"}]}
            """);

        Assert.Equal(4.75m, comision);
    }

    // Clausula: solo las comisiones con fee_payer = collector cuentan; la que paga el comprador no se descuenta de la cuenta.
    [Fact]
    public void UnaComisionQuePagaElCompradorNoCuenta()
    {
        var comision = Comision("""
            {"status": "approved", "transaction_amount": 100, "fee_details": [
              {"type": "mercadopago_fee", "amount": 3.50, "fee_payer": "collector"},
              {"type": "financing_fee", "amount": 12.00, "fee_payer": "payer"}]}
            """);

        Assert.Equal(3.50m, comision);
    }

    [Fact]
    public void SinComisionesYConElNetoIgualAlImporteLaComisionEsCero()
    {
        Assert.Equal(0m, Comision("""
            {"status": "approved", "transaction_amount": 100, "fee_details": [],
             "transaction_details": {"net_received_amount": 100}}
            """));
    }

    [Theory]
    [InlineData("""{"status": "approved", "transaction_amount": 100, "fee_details": [], "transaction_details": {"net_received_amount": 96}}""")]
    [InlineData("""{"status": "approved", "transaction_amount": 100}""")]
    [InlineData("""{"status": "approved", "transaction_amount": 100, "fee_details": [{"amount": 5, "fee_payer": "payer"}]}""")]
    public void SinComisionesDelVendedorYSinUnNetoQueLoConfirmeNoHayComision(string json)
    {
        Assert.Null(Comision(json));
    }

    [Theory]
    [InlineData("""{"status": "in_process", "transaction_amount": 100, "fee_details": [{"amount": 3, "fee_payer": "collector"}]}""")]
    [InlineData("""{"transaction_amount": 100, "fee_details": [{"amount": 3, "fee_payer": "collector"}]}""")]
    [InlineData("""{"status": "approved", "transaction_amount": 99.99, "fee_details": [{"amount": 3, "fee_payer": "collector"}]}""")]
    [InlineData("""{"status": "approved", "fee_details": [{"amount": 3, "fee_payer": "collector"}]}""")]
    [InlineData("""{"status": "approved", "transaction_amount": 100, "fee_details": [{"amount": -3, "fee_payer": "collector"}]}""")]
    [InlineData("""{"status": "approved", "transaction_amount": 100, "fee_details": [{"amount": 3.005, "fee_payer": "collector"}]}""")]
    [InlineData("""{"status": "approved", "transaction_amount": 100, "fee_details": [{"fee_payer": "collector"}]}""")]
    [InlineData("""{"status": "approved", "transaction_amount": 100, "fee_details": [{"amount": 3}]}""")]
    [InlineData("""{"status": "approved", "transaction_amount": 100, "fee_details": [null]}""")]
    [InlineData("""{"status": "approved", "transaction_amount": 100, "fee_details": [{"amount": 60, "fee_payer": "collector"}, {"amount": 40.01, "fee_payer": "collector"}]}""")]
    public void UnPagoNoAprobadoDeOtroImporteOConComisionesIlegiblesNoInformaComision(string json)
    {
        Assert.Null(Comision(json));
    }

    [Fact]
    public void SinImportePagadoDelCobroNoHayComision()
    {
        Assert.Null(ComisionInformadaDeMercadoPago.Calcular(
            Leer("""{"status": "approved", "transaction_amount": 100, "fee_details": [{"amount": 3, "fee_payer": "collector"}]}"""), null));
    }
}
