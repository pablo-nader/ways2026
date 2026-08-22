using System.Security.Cryptography.Pkcs;
using Ways.Infrastructure.Fiscal;

namespace Ways.Application.Tests.Fiscal;

/// <summary>
/// stage-19a-slice2 (task 2.13, design D3/D7/D4-crypto note, target 27): estructura del CMS —
/// dígest SHA-256 y exactamente un certificado (<see cref="X509IncludeOption.EndCertOnly"/>), más
/// la verificación de que la firma es válida contra el certificado de prueba efímero de
/// <see cref="CertificadoDePrueba"/> (D7).
/// </summary>
public class FirmanteCmsTests
{
    private const string OidSha256 = "2.16.840.1.101.3.4.2.1";

    [Fact]
    public void ElCmsUsaSha256YExactamenteUnCertificado()
    {
        var certificado = CertificadoDePrueba.Generar();
        var base64 = FirmanteCms.FirmarBase64("<loginTicketRequest/>", certificado);

        var decodificado = new SignedCms();
        decodificado.Decode(Convert.FromBase64String(base64));

        Assert.Single(decodificado.SignerInfos);
        Assert.Equal(OidSha256, decodificado.SignerInfos[0].DigestAlgorithm.Value);
        Assert.Single(decodificado.Certificates);
    }

    /// <summary>target 36 (D7): firma end-to-end con el certificado que pasó por el round trip
    /// PKCS#12 de <see cref="CertificadoDePrueba.Generar"/>. El kill de la mutación "firmar con el
    /// resultado crudo de <c>CreateSelfSigned</c>" solo se observa en Windows (la clave efímera de
    /// <c>CreateSelfSigned</c> no siempre es utilizable por <c>CmsSigner</c> ahí) — recorded as a
    /// platform-conditional kill, honestamente, en vez de simular un kill que esta suite en Linux
    /// no puede reproducir.</summary>
    [Fact]
    public void LaFirmaVerificaContraElCertificadoDePrueba()
    {
        var certificado = CertificadoDePrueba.Generar();
        Assert.True(certificado.HasPrivateKey);

        var base64 = FirmanteCms.FirmarBase64("<loginTicketRequest/>", certificado);

        var decodificado = new SignedCms();
        decodificado.Decode(Convert.FromBase64String(base64));

        decodificado.CheckSignature(verifySignatureOnly: true);
    }
}
