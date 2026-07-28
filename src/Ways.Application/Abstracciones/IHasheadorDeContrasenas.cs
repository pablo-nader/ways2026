namespace Ways.Application.Abstracciones;

public enum ResultadoVerificacion
{
    Invalida,
    Valida,
    ValidaPeroHayQueRehashear
}

/// <summary>
/// Hasheo de contraseñas. Está detrás de una interfaz a propósito: el día que se cambie
/// el algoritmo, se implementa uno nuevo y el <see cref="ResultadoVerificacion.ValidaPeroHayQueRehashear"/>
/// migra las cuentas existentes de forma transparente al iniciar sesión.
/// </summary>
public interface IHasheadorDeContrasenas
{
    /// <summary>Identificador del algoritmo, se guarda junto al hash.</summary>
    string Algoritmo { get; }

    string Hashear(string password);

    ResultadoVerificacion Verificar(string hashGuardado, string passwordIngresada);
}
