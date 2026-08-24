using System.Text;
using Ways.Domain.Fiscal;

namespace Ways.Application.Tests.Fiscal;

/// <summary>
/// stage-19a-slice5 (task 5.18, design.md: "The RG 4291 QR Payload Uses A Synthetic codAut In
/// 19a", target 72): <see cref="PayloadQrFiscal"/> contra un vector armado a mano —
/// independientemente de la implementación (base64 recalculado acá, no copiado del código de
/// producción, mutation-proof-tests: "un test que solo refleja la implementación no mata nada").
/// </summary>
public class PayloadQrFiscalTests
{
    [Fact]
    public void ElPayloadCodificaLosTreceCamposConTipoCodAutEYUrlDeAfip()
    {
        var url = PayloadQrFiscal.Construir(
            fecha: new DateOnly(2026, 8, 21),
            cuitEmisor: 20111111112,
            ptoVta: 3,
            tipoCmp: 1,
            nroCmp: 42,
            importe: 1250.50m,
            tipoDocRec: 80,
            nroDocRec: 20222222223,
            codAut: 12345678901234);

        // target 72: el JSON exacto, armado a mano — orden/campos/formato de la RG 4291.
        const string jsonEsperado =
            "{\"ver\":1,\"fecha\":\"2026-08-21\",\"cuit\":20111111112,\"ptoVta\":3,\"tipoCmp\":1," +
            "\"nroCmp\":42,\"importe\":1250.50,\"moneda\":\"PES\",\"ctz\":1.00,\"tipoDocRec\":80," +
            "\"nroDocRec\":20222222223,\"tipoCodAut\":\"E\",\"codAut\":12345678901234}";
        var base64Esperado = Convert.ToBase64String(Encoding.UTF8.GetBytes(jsonEsperado));
        var urlEsperada = "https://www.afip.gob.ar/fe/qr/?p=" + base64Esperado;

        Assert.Equal(urlEsperada, url);
    }

    [Fact]
    public void ElPrefijoDeUrlEsSiempreElOficialDeAfip()
    {
        var url = PayloadQrFiscal.Construir(
            new DateOnly(2026, 1, 1), 20000000001, 1, 6, 1, 100.00m, 96, 30111111119, 1);

        Assert.StartsWith("https://www.afip.gob.ar/fe/qr/?p=", url);
    }

    [Fact]
    public void CambiarUnSoloCampoCambiaElBase64Completo()
    {
        var url1 = PayloadQrFiscal.Construir(
            new DateOnly(2026, 1, 1), 20000000001, 1, 6, 1, 100.00m, 96, 30111111119, 1);
        var url2 = PayloadQrFiscal.Construir(
            new DateOnly(2026, 1, 1), 20000000001, 1, 6, 1, 100.00m, 96, 30111111119, 2); // codAut distinto

        Assert.NotEqual(url1, url2);
    }
}
