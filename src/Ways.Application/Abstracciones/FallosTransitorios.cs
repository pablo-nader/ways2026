using System.Data.Common;

namespace Ways.Application.Abstracciones;

/// <summary>
/// El ÚNICO predicado de "esto es un fallo transitorio" del repo. Nació duplicado: la traducción a
/// <c>503 resultado_incierto</c> de <c>Ways.Api.Seguridad.ManejadorDeErrores</c> tenía su propia
/// copia y <c>ServicioDeAprovisionamiento</c> necesitaba la misma clasificación para su residual
/// propio; dos listas de SQLSTATE que se pueden desincronizar son dos clasificaciones distintas
/// para el mismo fallo.
///
/// <para>Tipado sobre <see cref="DbException"/> y no sobre <c>NpgsqlException</c> a propósito:
/// <c>Ways.Application</c> no referencia a Npgsql (ver el comentario del <c>csproj</c>) y no hace
/// falta — <c>DbException.SqlState</c> y <c>DbException.IsTransient</c> son virtuales desde .NET 5
/// y <c>PostgresException</c>/<c>NpgsqlException</c> los sobreescriben, así que el mismo predicado
/// ve exactamente lo mismo que vería sobre el tipo concreto.</para>
/// </summary>
public static class FallosTransitorios
{
    /// <summary>Los SQLSTATE por los que <c>EnableRetryOnFailure</c> existe, más el juicio propio
    /// del proveedor (<see cref="DbException.IsTransient"/>, que también cubre la conexión cortada
    /// sin SQLSTATE ninguno):
    /// <list type="bullet">
    /// <item><c>40001</c> serialization_failure y <c>40P01</c> deadlock_detected — la transacción
    /// murió y el resultado del commit es indeterminado desde el lado del cliente;</item>
    /// <item><c>57P01</c> admin_shutdown — el servidor terminó la conexión;</item>
    /// <item>toda la clase <c>08</c> (connection_exception: <c>08000</c>, <c>08001</c>,
    /// <c>08003</c>, <c>08004</c>, <c>08006</c>, <c>08007</c>, <c>08P01</c>) — se cortó el canal,
    /// que es LA forma del commit ambiguo.</item>
    /// </list>
    /// Match por prefijo para la clase 08 y no por lista cerrada: la clase entera significa
    /// exactamente lo mismo y un código nuevo no puede querer decir otra cosa.</summary>
    public static bool EsTransitorio(DbException error) =>
        (error.SqlState is { } sqlState
            && (sqlState is "40001" or "40P01" or "57P01"
                || sqlState.StartsWith("08", StringComparison.Ordinal)))
        || error.IsTransient;

    /// <summary>La misma pregunta sobre una excepción cualquiera: recorre la cadena de
    /// <see cref="Exception.InnerException"/> buscando la primera <see cref="DbException"/>
    /// transitoria. Es lo que hace falta cuando el fallo llega envuelto —
    /// <c>DbUpdateException</c>, o el <c>RetryLimitExceededException</c> con el que EF cierra los
    /// cinco intentos agotados—, porque la envoltura no es una <see cref="DbException"/>. Nombre
    /// distinto y no una sobrecarga: <c>NpgsqlException</c> es a la vez <see cref="DbException"/> y
    /// <see cref="Exception"/>, y una sobrecarga elegida por el tipo ESTÁTICO del llamador es
    /// exactamente la clase de ambigüedad que no se quiere en un clasificador de errores.</summary>
    public static bool EsTransitorioEnLaCadena(Exception? error)
    {
        for (var actual = error; actual is not null; actual = actual.InnerException)
        {
            if (actual is DbException baseDeDatos && EsTransitorio(baseDeDatos))
            {
                return true;
            }
        }

        return false;
    }
}
