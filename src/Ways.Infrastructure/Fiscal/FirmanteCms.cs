using System.Security.Cryptography;
using System.Security.Cryptography.Pkcs;
using System.Security.Cryptography.X509Certificates;
using System.Text;

namespace Ways.Infrastructure.Fiscal;

/// <summary>
/// Firma la TRA en un CMS (PKCS#7) — <see cref="System.Security.Cryptography.Pkcs.SignedCms"/> de
/// la BCL exclusivamente, cero dependencia de terceros (proposal decisión 7: la parte sensible ya
/// es BCL, así que ningún tercero toca material de clave). <see cref="X509IncludeOption.EndCertOnly"/>
/// porque el CMS solo necesita el certificado del firmante, nunca la cadena completa (target 27).
/// </summary>
public static class FirmanteCms
{
    private static readonly Oid Sha256 = new("2.16.840.1.101.3.4.2.1");

    public static string FirmarBase64(string tra, X509Certificate2 certificado)
    {
        var contentInfo = new ContentInfo(Encoding.UTF8.GetBytes(tra));
        var signedCms = new SignedCms(contentInfo);
        var signer = new CmsSigner(certificado)
        {
            IncludeOption = X509IncludeOption.EndCertOnly,
            DigestAlgorithm = Sha256
        };

        signedCms.ComputeSignature(signer);
        return Convert.ToBase64String(signedCms.Encode());
    }
}
