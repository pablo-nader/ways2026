using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;

namespace Ways.Application.Tests.Fiscal;

/// <summary>
/// Genera el certificado de prueba en RUNTIME (design D7, proposal decisión 12): CERO material de
/// clave se escribe a disco ni se commitea jamás — <see cref="Generar"/> arma un certificado
/// self-signed efímero (<see cref="CertificateRequest"/>, BCL) y lo recarga vía PKCS#12 antes de
/// devolverlo. En Windows la clave efímera que devuelve <c>CreateSelfSigned</c> directamente no
/// siempre es utilizable por <see cref="System.Security.Cryptography.Pkcs.CmsSigner"/> — la
/// máquina del dueño ES Windows 11, así que un suite verde en Linux que falla en esa máquina sería
/// el peor resultado posible para la slice cuyo propósito entero es andar sin ARCA. El round trip
/// cuesta tres líneas y saca la pregunta de la mesa (target 36 — el kill de esta mutación solo se
/// observa en Windows; recorded as a platform-conditional kill).
/// </summary>
public static class CertificadoDePrueba
{
    public static X509Certificate2 Generar(string sujeto = "CN=Ways Test, O=Ways, C=AR")
    {
        using var rsa = RSA.Create(2048);
        var solicitud = new CertificateRequest(
            sujeto, rsa, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);

        var ahora = DateTimeOffset.UtcNow;
        using var efimero = solicitud.CreateSelfSigned(ahora.AddDays(-1), ahora.AddYears(1));

        var contrasenaEfimera = Guid.NewGuid().ToString("N");
        var pfx = efimero.Export(X509ContentType.Pkcs12, contrasenaEfimera);
        return X509CertificateLoader.LoadPkcs12(
            pfx, contrasenaEfimera, X509KeyStorageFlags.Exportable);
    }
}
