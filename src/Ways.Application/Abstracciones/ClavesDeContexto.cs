namespace Ways.Application.Abstracciones;

/// <summary>
/// Claves de <c>[FromKeyedServices]</c> compartidas entre Application e Infrastructure
/// para los <c>WaysDbContext</c>/<c>IWaysDbContext</c> atados a un <see cref="ITenantActual"/>
/// fijo, en vez del de la sesión HTTP en curso (ADR-2).
///
/// Vive acá (no en <c>Ways.Infrastructure.DependencyInjection</c>) porque Application no
/// puede referenciar Infrastructure, y hay puntos de uso legítimos en los dos lados: la
/// semilla de arranque (Infrastructure) y una verificación de negocio como la suspensión
/// de tenant en el login (Application, <see cref="Usuarios.ServicioDeAutenticacion"/>).
/// </summary>
public static class ClavesDeContexto
{
    /// <summary>Contexto fijo en modo plataforma: ve y puede escribir cualquier tenant.
    /// Nunca se resuelve desde una request HTTP.</summary>
    public const string Plataforma = "plataforma";
}
