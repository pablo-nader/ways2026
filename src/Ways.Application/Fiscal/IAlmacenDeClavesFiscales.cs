using System.Security.Cryptography.X509Certificates;
using Ways.Domain.Fiscal;

namespace Ways.Application.Fiscal;

/// <summary>
/// Puerto del cifrado/descifrado de material de clave fiscal (design.md: Ports and the CAE
/// machine; decisión 1/D5/D6). La implementación concreta (<c>CifradoDeClavesFiscales</c>, AES-256-GCM
/// vía la BCL) vive en Infrastructure.
///
/// <see cref="CifrarAsync"/> es la ampliación de esta slice sobre el snippet de <c>design.md</c>
/// (que solo nombra <see cref="UsarCertificadoAsync{T}"/> literalmente): sin ella,
/// <c>ServicioDeCertificados</c> (Application) tendría que llamar <c>AesGcm</c> directo, cruzando
/// el límite hexagonal que el resto del proyecto respeta (Clean/Hexagonal Architecture) — acá el
/// ABM cifra material NUEVO antes de guardarlo, la contraparte simétrica de
/// <see cref="UsarCertificadoAsync{T}"/> que descifra una fila existente para firmar.
/// DEVIACIÓN REGISTRADA, no silenciosa.
/// </summary>
public interface IAlmacenDeClavesFiscales
{
    /// <summary>Cifra <paramref name="clavePrivada"/> con la clave maestra ACTUAL
    /// (<c>Ways:Fiscal:ClaveMaestraActual</c>, D6) y devuelve el ciphertext/nonce/tag junto con el
    /// <c>id_clave_maestra</c> que la cifró (versionado, para poder rotar la clave maestra sin
    /// invalidar filas ya cifradas con una anterior). Clave maestra ausente/corta ⇒
    /// <c>503 clave_maestra_ausente</c> — el alta/rotación de certificados queda inhabilitada,
    /// JAMÁS un fallback a texto plano (D6).</summary>
    Task<(byte[] Ciphertext, byte[] Nonce, byte[] Tag, string IdClaveMaestra)> CifrarAsync(
        byte[] clavePrivada,
        int idTenant,
        int idEmpresa,
        AmbienteFiscal ambiente,
        string huellaSha256,
        CancellationToken ct);

    /// <summary>Resuelve el certificado ACTIVO de <paramref name="idEmpresa"/>+<paramref name="ambiente"/>,
    /// lo descifra (por la versión de clave maestra de SU fila, <c>id_clave_maestra</c> — no
    /// necesariamente la "actual"), reconstruye un <see cref="X509Certificate2"/> con la clave
    /// privada adjunta y lo entrega a <paramref name="uso"/>. El material descifrado vive SOLO
    /// dentro de este callback; el buffer se limpia con
    /// <see cref="System.Security.Cryptography.CryptographicOperations.ZeroMemory"/> en un
    /// <c>finally</c>, pase lo que pase. Sin certificado activo, o sin la clave maestra que lo
    /// descifra ⇒ <c>409 certificado_fiscal_ausente</c> — el camino fiscal queda INERTE (I4),
    /// JAMÁS una excepción de crypto pelada.</summary>
    Task<T> UsarCertificadoAsync<T>(
        int idEmpresa,
        AmbienteFiscal ambiente,
        Func<X509Certificate2, Task<T>> uso,
        CancellationToken ct);
}
