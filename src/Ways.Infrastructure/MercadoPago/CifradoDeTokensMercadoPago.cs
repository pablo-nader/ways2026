using System.Security.Cryptography;
using System.Text;
using Microsoft.Extensions.Configuration;
using Ways.Application.MercadoPago;
using Ways.Domain.Common;
using Ways.Domain.MercadoPago;

namespace Ways.Infrastructure.MercadoPago;

/// <summary>
/// AES-256-GCM de los tokens de Mercado Pago. Paralelo a <c>CifradoDeClavesFiscales</c> y con claves maestras propias
/// (<c>Ways:MercadoPago:ClaveMaestraActual</c> y <c>Ways:MercadoPago:ClavesMaestras:&lt;id&gt;</c>, 32 bytes en base64):
/// comprometer o rotar una familia de claves no toca a la otra.
///
/// El AAD de cada blob es <c>v1|id_tenant|id_empresa|id_conexion|access</c> o <c>...|refresh</c>: mover un blob a otra conexión, a
/// otro tenant, a otra empresa o de un token al otro falla la autenticación. <c>id_clave_maestra</c> queda fuera del AAD
/// porque es la versión de la clave que lo abre, no identidad de la fila.
/// </summary>
public sealed class CifradoDeTokensMercadoPago(IConfiguration configuration) : IAlmacenDeTokensMercadoPago
{
    public const int TamanioNonce = 12;
    public const int TamanioTag = 16;
    private const int TamanioClaveEnBytes = 32;

    public const string TokenDeAcceso = "access";
    public const string TokenDeRenovacion = "refresh";

    public bool PuedeCifrar
    {
        get
        {
            if (!TryResolverActual(out _, out var clave))
            {
                return false;
            }

            CryptographicOperations.ZeroMemory(clave);
            return true;
        }
    }

    public TokensCifrados Cifrar(int idTenant, int idEmpresa, int idConexion, string accessToken, string refreshToken)
    {
        if (!TryResolverActual(out var idClaveMaestra, out var claveMaestra))
        {
            throw ClaveMaestraAusente();
        }

        try
        {
            var acceso = Cifrar(claveMaestra, Encoding.UTF8.GetBytes(accessToken), idTenant, idEmpresa, idConexion, TokenDeAcceso);
            var renovacion = Cifrar(
                claveMaestra, Encoding.UTF8.GetBytes(refreshToken), idTenant, idEmpresa, idConexion, TokenDeRenovacion);

            return new TokensCifrados(
                acceso.Ciphertext, acceso.Nonce, acceso.Tag,
                renovacion.Ciphertext, renovacion.Nonce, renovacion.Tag,
                idClaveMaestra);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(claveMaestra);
        }
    }

    public (string AccessToken, string RefreshToken) Descifrar(ConexionMercadoPago conexion)
    {
        if (conexion.AccessTokenCifrado is null || conexion.AccessTokenNonce is null || conexion.AccessTokenTag is null
            || conexion.RefreshTokenCifrado is null || conexion.RefreshTokenNonce is null || conexion.RefreshTokenTag is null)
        {
            throw TokensIlegibles();
        }

        if (!TryResolverPorId(conexion.IdClaveMaestra, out _, out var claveMaestra))
        {
            throw TokensIlegibles();
        }

        try
        {
            var acceso = Descifrar(
                claveMaestra, conexion.AccessTokenCifrado, conexion.AccessTokenNonce, conexion.AccessTokenTag,
                conexion.IdTenant, conexion.IdEmpresa, conexion.Id, TokenDeAcceso);
            var renovacion = Descifrar(
                claveMaestra, conexion.RefreshTokenCifrado, conexion.RefreshTokenNonce, conexion.RefreshTokenTag,
                conexion.IdTenant, conexion.IdEmpresa, conexion.Id, TokenDeRenovacion);

            return (Encoding.UTF8.GetString(acceso), Encoding.UTF8.GetString(renovacion));
        }
        catch (CryptographicException)
        {
            // Clave equivocada y fila manipulada producen la misma excepción de la BCL; el mensaje no distingue cuál fue.
            throw TokensIlegibles();
        }
        finally
        {
            CryptographicOperations.ZeroMemory(claveMaestra);
        }
    }

    public static (byte[] Ciphertext, byte[] Nonce, byte[] Tag) Cifrar(
        byte[] claveMaestra, byte[] plaintext, int idTenant, int idEmpresa, int idConexion, string token)
    {
        var nonce = RandomNumberGenerator.GetBytes(TamanioNonce);
        var tag = new byte[TamanioTag];
        var ciphertext = new byte[plaintext.Length];

        using var aesGcm = new AesGcm(claveMaestra, TamanioTag);
        aesGcm.Encrypt(nonce, plaintext, ciphertext, tag, ConstruirAad(idTenant, idEmpresa, idConexion, token));

        return (ciphertext, nonce, tag);
    }

    /// <summary>Lanza <see cref="CryptographicException"/> si el tag no valida: clave equivocada o cualquier componente del AAD distinto.</summary>
    public static byte[] Descifrar(
        byte[] claveMaestra, byte[] ciphertext, byte[] nonce, byte[] tag, int idTenant, int idEmpresa, int idConexion, string token)
    {
        var plaintext = new byte[ciphertext.Length];

        using var aesGcm = new AesGcm(claveMaestra, TamanioTag);
        aesGcm.Decrypt(nonce, ciphertext, tag, plaintext, ConstruirAad(idTenant, idEmpresa, idConexion, token));

        return plaintext;
    }

    public static byte[] ConstruirAad(int idTenant, int idEmpresa, int idConexion, string token) =>
        Encoding.UTF8.GetBytes($"v1|{idTenant}|{idEmpresa}|{idConexion}|{token}");

    private static ErrorDominio ClaveMaestraAusente() =>
        new(
            "mercadopago_no_configurado",
            "No hay clave maestra de cifrado de Mercado Pago configurada: la conexión está inhabilitada.",
            503);

    private static ErrorDominio TokensIlegibles() =>
        new(
            "mercadopago_tokens_ilegibles",
            "Los tokens de Mercado Pago de esta empresa no pudieron leerse: hay que reconectar la cuenta.",
            409);

    private bool TryResolverActual(out string idClaveMaestra, out byte[] clave) =>
        TryResolverPorId(configuration["Ways:MercadoPago:ClaveMaestraActual"], out idClaveMaestra, out clave);

    private bool TryResolverPorId(string? id, out string idClaveMaestra, out byte[] clave)
    {
        idClaveMaestra = id ?? string.Empty;
        clave = [];

        if (string.IsNullOrWhiteSpace(id))
        {
            return false;
        }

        var valor = configuration[$"Ways:MercadoPago:ClavesMaestras:{id}"];
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
