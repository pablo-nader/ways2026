using Microsoft.AspNetCore.Identity;
using Ways.Application.Abstracciones;

namespace Ways.Infrastructure.Seguridad;

/// <summary>
/// Hasheo con PBKDF2-SHA512, 100.000 iteraciones y salt aleatorio de 128 bits,
/// vía el <see cref="PasswordHasher{TUser}"/> de ASP.NET Core.
///
/// Por qué no hay una columna de salt: el formato de salida es autodescriptivo y ya
/// contiene, en un solo string base64, la versión del formato, el algoritmo, la cantidad
/// de iteraciones y el salt. Guardar el salt aparte era necesario con MD5/SHA1 sueltos;
/// con un hasher moderno duplica el dato y obliga a versionar dos columnas en lugar de una.
///
/// El algoritmo se guarda igual en <c>password_algoritmo</c> para poder migrar a Argon2id
/// más adelante sin invalidar las contraseñas: al iniciar sesión, un hash viejo devuelve
/// <see cref="ResultadoVerificacion.ValidaPeroHayQueRehashear"/> y se reescribe solo.
/// </summary>
public sealed class HasheadorPbkdf2 : IHasheadorDeContrasenas
{
    private sealed class Portador;

    private readonly PasswordHasher<Portador> _hasher = new();
    private static readonly Portador Instancia = new();

    public string Algoritmo => "pbkdf2-sha512-v3";

    public string Hashear(string password) => _hasher.HashPassword(Instancia, password);

    public ResultadoVerificacion Verificar(string hashGuardado, string passwordIngresada) =>
        _hasher.VerifyHashedPassword(Instancia, hashGuardado, passwordIngresada) switch
        {
            PasswordVerificationResult.Success => ResultadoVerificacion.Valida,
            PasswordVerificationResult.SuccessRehashNeeded => ResultadoVerificacion.ValidaPeroHayQueRehashear,
            _ => ResultadoVerificacion.Invalida
        };
}
