using Npgsql;

namespace Ways.Infrastructure.Persistencia;

/// <summary>
/// Verificación pura (sin base de datos) de los invariantes de conexión que ADR-3
/// (design.md, stage-1-organization-and-catalogs) exige para que los GUC de tenant no se
/// filtren entre requests que comparten una conexión física del pool.
/// </summary>
public static class InvariantesDeConexion
{
    /// <summary><c>Multiplexing</c> intercala comandos de distintos contextos sobre una
    /// misma conexión física, y <c>No Reset On Close</c> deshabilita el <c>DISCARD ALL</c>
    /// que Npgsql manda al devolver la conexión al pool — cualquiera de los dos rompe el
    /// aislamiento de <c>set_config(..., false)</c> a nivel de sesión.</summary>
    public static bool ViolaMultiplexingOResetOnClose(string cadenaDeConexion)
    {
        var builder = new NpgsqlConnectionStringBuilder(cadenaDeConexion);
        return builder.Multiplexing || builder.NoResetOnClose;
    }
}
