using System.Security.Cryptography;
using System.Text;

namespace Ways.Domain.MercadoPago;

/// <summary>PKCE con método S256 (RFC 7636): el verificador queda en el <c>state</c> protegido y solo viaja su hash.</summary>
public static class Pkce
{
    public static string GenerarVerificador() => Base64Url(RandomNumberGenerator.GetBytes(32));

    public static string DesafioS256(string verificador) =>
        Base64Url(SHA256.HashData(Encoding.ASCII.GetBytes(verificador)));

    private static string Base64Url(byte[] bytes) =>
        Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');
}
