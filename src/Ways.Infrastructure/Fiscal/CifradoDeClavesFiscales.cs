using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Ways.Application.Abstracciones;
using Ways.Application.Fiscal;
using Ways.Domain.Common;
using Ways.Domain.Fiscal;

namespace Ways.Infrastructure.Fiscal;

/// <summary>
/// Implementación de <see cref="IAlmacenDeClavesFiscales"/> — AES-256-GCM vía
/// <see cref="AesGcm"/> (BCL, cero dependencia de terceros, proposal.md decisión 1). Dos mitades:
///
/// (1) Crypto PURA y ESTÁTICA (<see cref="Cifrar"/>/<see cref="Descifrar"/>/<see cref="ConstruirAad"/>)
/// — testeable sin base de datos ni configuración (targets 57-59): AAD = <c>"v1|" + idTenant + "|"
/// + idEmpresa + "|" + ambiente + "|" + huellaSha256</c> (design D5), que ata el ciphertext a SU
/// FILA — mover el blob a otra fila (otro tenant, otra empresa, otro ambiente, otro certificado)
/// falla la autenticación de <see cref="AesGcm"/>. <c>id_clave_maestra</c> queda EXCLUIDO a
/// propósito: es la versión de clave que lo abre, no identidad de fila — incluirla haría que una
/// rotación legítima cambiara el AAD (target 58).
///
/// (2) Los dos métodos de instancia del puerto (D6: la clave maestra viene de
/// configuración/entorno, JAMÁS de la base ni del repo, JAMÁS un fallback a texto plano ni una
/// generada en boot): <see cref="CifrarAsync"/> resuelve la clave maestra ACTUAL
/// (<c>Ways:Fiscal:ClaveMaestraActual</c> + <c>Ways:Fiscal:ClavesMaestras:&lt;id&gt;</c>, 32 bytes
/// base64) para cifrar material NUEVO; <see cref="UsarCertificadoAsync{T}"/> resuelve la clave por
/// la versión guardada en la fila (<c>id_clave_maestra</c>) para descifrar una existente. Ausente/
/// corta ⇒ el error nombrado de D6, nunca una excepción de crypto pelada ni texto plano.
/// </summary>
public sealed class CifradoDeClavesFiscales(IWaysDbContext db, IConfiguration configuration)
    : IAlmacenDeClavesFiscales
{
    public const int TamanioNonce = 12;
    public const int TamanioTag = 16;
    private const int TamanioClaveEnBytes = 32;

    public async Task<(byte[] Ciphertext, byte[] Nonce, byte[] Tag, string IdClaveMaestra)> CifrarAsync(
        byte[] clavePrivada,
        int idTenant,
        int idEmpresa,
        AmbienteFiscal ambiente,
        string huellaSha256,
        CancellationToken ct)
    {
        if (!TryResolverActual(out var idClaveMaestra, out var claveMaestra))
        {
            throw new ErrorDominio(
                "clave_maestra_ausente",
                "No hay clave maestra de cifrado fiscal configurada — el alta o la rotación de " +
                "certificados está inhabilitada.",
                503);
        }

        try
        {
            var (ciphertext, nonce, tag) = Cifrar(claveMaestra, clavePrivada, idTenant, idEmpresa, ambiente, huellaSha256);
            return await Task.FromResult((ciphertext, nonce, tag, idClaveMaestra));
        }
        finally
        {
            CryptographicOperations.ZeroMemory(claveMaestra);
        }
    }

    public async Task<T> UsarCertificadoAsync<T>(
        int idEmpresa,
        AmbienteFiscal ambiente,
        Func<X509Certificate2, Task<T>> uso,
        CancellationToken ct)
    {
        var certificado = await db.CertificadosFiscales
            .FirstOrDefaultAsync(c => c.IdEmpresa == idEmpresa && c.Ambiente == ambiente && c.Activo, ct)
            ?? throw CertificadoAusente();

        if (!TryResolverPorId(certificado.IdClaveMaestra, out _, out var claveMaestra))
        {
            throw CertificadoAusente();
        }

        byte[]? clavePrivadaPlana = null;
        try
        {
            clavePrivadaPlana = Descifrar(
                claveMaestra,
                certificado.ClavePrivadaCifrada,
                certificado.Nonce,
                certificado.TagAutenticacion,
                certificado.IdTenant,
                certificado.IdEmpresa,
                certificado.Ambiente,
                certificado.HuellaSha256);

            using var publico = X509Certificate2.CreateFromPem(certificado.CertificadoPem);
            using var rsa = RSA.Create();
            rsa.ImportPkcs8PrivateKey(clavePrivadaPlana, out _);
            using var conClavePrivada = publico.CopyWithPrivateKey(rsa);

            return await uso(conClavePrivada);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(claveMaestra);
            if (clavePrivadaPlana is not null)
            {
                CryptographicOperations.ZeroMemory(clavePrivadaPlana);
            }
        }
    }

    private static ErrorDominio CertificadoAusente() =>
        new(
            "certificado_fiscal_ausente",
            "No hay certificado fiscal activo (o su clave maestra no está disponible) para esta " +
            "empresa y ambiente.",
            409);

    // --- Crypto puro (targets 57-59): sin IConfiguration ni IWaysDbContext, testeable directo. ---

    public static (byte[] Ciphertext, byte[] Nonce, byte[] Tag) Cifrar(
        byte[] claveMaestra,
        byte[] plaintext,
        int idTenant,
        int idEmpresa,
        AmbienteFiscal ambiente,
        string huellaSha256)
    {
        var nonce = RandomNumberGenerator.GetBytes(TamanioNonce);
        var tag = new byte[TamanioTag];
        var ciphertext = new byte[plaintext.Length];
        var aad = ConstruirAad(idTenant, idEmpresa, ambiente, huellaSha256);

        using var aesGcm = new AesGcm(claveMaestra, TamanioTag);
        aesGcm.Encrypt(nonce, plaintext, ciphertext, tag, aad);

        return (ciphertext, nonce, tag);
    }

    /// <summary>Lanza <see cref="CryptographicException"/> si <paramref name="tag"/> no valida —
    /// tanto por una clave equivocada como por un AAD que no coincide con la fila (target 57: el
    /// tamper de cualquiera de sus cuatro componentes tiene que fallar acá).</summary>
    public static byte[] Descifrar(
        byte[] claveMaestra,
        byte[] ciphertext,
        byte[] nonce,
        byte[] tag,
        int idTenant,
        int idEmpresa,
        AmbienteFiscal ambiente,
        string huellaSha256)
    {
        var aad = ConstruirAad(idTenant, idEmpresa, ambiente, huellaSha256);
        var plaintext = new byte[ciphertext.Length];

        using var aesGcm = new AesGcm(claveMaestra, TamanioTag);
        aesGcm.Decrypt(nonce, ciphertext, tag, plaintext, aad);

        return plaintext;
    }

    /// <summary><c>"v1|" + idTenant + "|" + idEmpresa + "|" + ambiente + "|" + huellaSha256</c>
    /// (design D5) — <c>ambiente</c> se escribe en minúscula (<c>"homologacion"</c>/<c>"produccion"</c>),
    /// la misma forma que su etiqueta de <c>CREATE TYPE</c> en la base, no el nombre del miembro de
    /// C# (<c>Homologacion</c>/<c>Produccion</c>): ata el AAD a la representación que la fila
    /// realmente persiste, no a un detalle de nombrado del enum de este lenguaje.</summary>
    public static byte[] ConstruirAad(int idTenant, int idEmpresa, AmbienteFiscal ambiente, string huellaSha256) =>
        Encoding.UTF8.GetBytes(
            $"v1|{idTenant}|{idEmpresa}|{ambiente.ToString().ToLowerInvariant()}|{huellaSha256}");

    private bool TryResolverActual(out string idClaveMaestra, out byte[] clave) =>
        TryResolverPorId(configuration["Ways:Fiscal:ClaveMaestraActual"], out idClaveMaestra, out clave);

    private bool TryResolverPorId(string? id, out string idClaveMaestra, out byte[] clave)
    {
        idClaveMaestra = id ?? string.Empty;
        clave = [];

        if (string.IsNullOrWhiteSpace(id))
        {
            return false;
        }

        var valor = configuration[$"Ways:Fiscal:ClavesMaestras:{id}"];
        if (string.IsNullOrWhiteSpace(valor))
        {
            return false;
        }

        byte[] bytes;
        try
        {
            bytes = Convert.FromBase64String(valor);
        }
        catch (FormatException)
        {
            return false;
        }

        if (bytes.Length != TamanioClaveEnBytes)
        {
            return false;
        }

        clave = bytes;
        return true;
    }
}
