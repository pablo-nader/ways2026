namespace Ways.Application.Dominios;

/// <summary>
/// Dominio por el que llegó la request: el host de la plataforma o el dominio propio de un tenant.
/// Se resuelve una sola vez, en un middleware que corre antes de la autenticación, así que existe
/// aunque todavía no haya sesión (login, páginas públicas).
/// </summary>
public interface IDominioActual
{
    /// <summary><c>true</c> cuando el host es uno de la plataforma (<c>Plataforma:Hosts</c>).</summary>
    bool EsPlataforma { get; }

    /// <summary>Host de la request en minúscula y sin puerto.</summary>
    string Host { get; }

    /// <summary>El tenant dueño del dominio; <c>null</c> en un host de la plataforma.</summary>
    int? IdTenant { get; }

    /// <summary>Nombre del comercio (el del tenant); <c>null</c> en un host de la plataforma.</summary>
    string? NombreComercio { get; }
}
