using System.Text.Json;
using Ways.Application.MercadoPago;
using Ways.Infrastructure.MercadoPago;

namespace Ways.Application.Tests.MercadoPago;

/// <summary>La orden se lee con las mismas opciones que el cliente de la API: tolera campos que Ways no usa y las formas
/// de número y cadena que Mercado Pago mezcla.</summary>
public class OrdenDeMercadoPagoTests
{
    private static OrdenDeMercadoPago Leer(string json) =>
        JsonSerializer.Deserialize<OrdenDeMercadoPago>(json, ClienteApiMercadoPago.OpcionesJson)!;

    [Fact]
    public void LaOrdenAprobadaSeLeeIgnorandoLoQueWaysNoUsa()
    {
        var orden = Leer("""
            {
              "id": "ORD01", "status": "processed", "campo_nuevo": {"x": [1, 2]}, "total_amount": "123.45",
              "type_response": {"qr_data": "00020101", "otro": 1},
              "transactions": {"payments": [
                {"id": "PAY01", "status": "processed", "amount": "123.45", "paid_amount": "120.00", "reference_id": "1234567890",
                 "payment_method": {"id": "visa"}}
              ]}
            }
            """);

        Assert.Equal("ORD01", orden.Id);
        Assert.Equal("processed", orden.Status);
        Assert.Equal("00020101", orden.TypeResponse!.QrData);
        var pago = Assert.Single(orden.Transactions!.Payments!);
        Assert.Equal("PAY01", pago.Id);
        Assert.Equal(123.45m, pago.Amount);
        Assert.Equal(120.00m, pago.PaidAmount);
        Assert.Equal(1234567890L, pago.ReferenciaNumerica);
    }

    [Theory]
    [InlineData("1234567890", 1234567890L)]
    [InlineData("\"1234567890\"", 1234567890L)]
    [InlineData("\"abc\"", null)]
    [InlineData("null", null)]
    [InlineData("1.5", null)]
    public void ElReferenceIdSeLeeComoNumeroOComoCadenaYUnaFormaRaraNoImpideLeerLaOrden(string referencia, long? esperado)
    {
        var orden = Leer("{\"id\": \"ORD01\", \"transactions\": {\"payments\": [{\"id\": \"PAY01\", \"reference_id\": " + referencia + "}]}}");

        Assert.Equal(esperado, orden.Transactions!.Payments!.Single().ReferenciaNumerica);
    }

    [Fact]
    public void ElCuerpoDeLaCreacionSaleEnSnakeCaseConLosImportesComoCadena()
    {
        var cuerpo = new SolicitudDeOrdenQrMercadoPago(
            "qr", "W1P2N3I1", "10.50", "PT10M",
            new ConfiguracionDeOrdenMercadoPago(new ConfiguracionDeQrDeOrdenMercadoPago("WAYSPV2", "dynamic")),
            [new ItemDeOrdenMercadoPago("Venta 3", "10.50", 1, "unit")]);

        var json = JsonSerializer.Serialize(cuerpo, ClienteApiMercadoPago.OpcionesJson);

        Assert.Equal(
            """{"type":"qr","external_reference":"W1P2N3I1","total_amount":"10.50","expiration_time":"PT10M","config":{"qr":{"external_pos_id":"WAYSPV2","mode":"dynamic"}},"items":[{"title":"Venta 3","unit_price":"10.50","quantity":1,"unit_measure":"unit"}]}""",
            json);
    }
}
