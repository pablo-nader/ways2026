using System.Security.Cryptography;
using System.Text;

namespace Ways.Domain.Dispositivos;

/// <summary>
/// Generación y hashing del secreto de un dispositivo — dominio puro, testeable sin base
/// (CLAUDE.md, política de testing). El secreto en sí nunca se persiste: viaja una única vez
/// en la cookie <c>ways.dispositivo</c>; lo único que se guarda es <see cref="Hashear"/>.
/// </summary>
public static class TokenDeDispositivo
{
    /// <summary>32 bytes de entropía (256 bits) — igual de fuerte que el material que ya usa
    /// el sistema para claves simétricas (etapa 19a).</summary>
    private const int LargoDelSecretoEnBytes = 32;

    /// <summary>Genera un secreto nuevo y devuelve, junto con él, su hash — la única forma en
    /// que un llamador debería obtener un par (secreto, hash): evita que alguien hashee un
    /// secreto ya usado por otro dispositivo sin querer.</summary>
    public static (string Secreto, string Hash) GenerarNuevo()
    {
        var secreto = CodificarBase64Url(RandomNumberGenerator.GetBytes(LargoDelSecretoEnBytes));
        return (secreto, Hashear(secreto));
    }

    /// <summary>SHA-256 en hex minúscula (64 caracteres) — determinístico: el mismo secreto
    /// siempre hashea igual, necesario para poder buscar por <c>token_hash</c> en el login.</summary>
    public static string Hashear(string secreto)
    {
        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes(secreto));
        return Convert.ToHexStringLower(bytes);
    }

    /// <summary>Base64 URL-safe sin padding: el secreto viaja como valor de cookie, y '+'/'/'/'='
    /// obligarían a un escapado que no hace falta si de entrada se evitan esos caracteres.</summary>
    private static string CodificarBase64Url(byte[] bytes) =>
        Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');
}
