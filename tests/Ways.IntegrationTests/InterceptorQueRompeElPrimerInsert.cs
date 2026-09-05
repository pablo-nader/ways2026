using System.Data.Common;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Npgsql;

namespace Ways.IntegrationTests;

/// <summary>
/// Rompe el PRIMER lote que contenga un <c>INSERT INTO &lt;tabla&gt;</c> con un error de Postgres
/// del SQLSTATE que se le pida, y deja pasar todo lo demás — incluido el segundo intento, si lo
/// hubiera. Es lo único que convierte "¿esta unidad es reintentable?" en una pregunta observable:
/// sin él, el reintento de <c>EnableRetryOnFailure</c> no se dispara nunca en una prueba y el
/// mutante sobrevive por construcción.
///
/// Generalización del <c>InterceptorQueRompeElRastro</c> de <c>BajasDeOrganizacionTests</c>
/// (etapa 20, judgment-day ronda 2), que estaba clavado en <c>auditoria</c>. Vive en su propio
/// archivo porque lo consumen todas las escrituras de
/// <see cref="EscriturasSinReintentoTests"/>, cada una sobre su propia tabla.
///
/// <see cref="Intentos"/> es el valor DISCRIMINANTE (<c>mutation-proof-tests</c> regla 4): bajo la
/// estrategia sin reintento el INSERT se ve UNA sola vez; bajo la reintentable se ve dos, y el
/// segundo intento comitea el doble de filas. Afirmar solamente que el error llegó al llamador NO
/// distingue las dos estrategias — la reintentable también termina propagando si agota los cinco
/// intentos.
/// </summary>
internal sealed class InterceptorQueRompeElPrimerInsert(string tabla, string sqlState) : DbCommandInterceptor
{
    private readonly string marca = $"INSERT INTO {tabla}";
    private int intentos;

    /// <summary>Cuántas veces se intentó ejecutar el lote que contiene el INSERT vigilado.</summary>
    public int Intentos => Volatile.Read(ref intentos);

    public override ValueTask<InterceptionResult<DbDataReader>> ReaderExecutingAsync(
        DbCommand command, CommandEventData eventData, InterceptionResult<DbDataReader> result,
        CancellationToken cancellationToken = default)
    {
        RomperSiCorresponde(command);
        return base.ReaderExecutingAsync(command, eventData, result, cancellationToken);
    }

    public override ValueTask<InterceptionResult<int>> NonQueryExecutingAsync(
        DbCommand command, CommandEventData eventData, InterceptionResult<int> result,
        CancellationToken cancellationToken = default)
    {
        RomperSiCorresponde(command);
        return base.NonQueryExecutingAsync(command, eventData, result, cancellationToken);
    }

    public override ValueTask<InterceptionResult<object>> ScalarExecutingAsync(
        DbCommand command, CommandEventData eventData, InterceptionResult<object> result,
        CancellationToken cancellationToken = default)
    {
        RomperSiCorresponde(command);
        return base.ScalarExecutingAsync(command, eventData, result, cancellationToken);
    }

    private void RomperSiCorresponde(DbCommand comando)
    {
        if (!comando.CommandText.Contains(marca, StringComparison.Ordinal))
        {
            return;
        }

        // Solo el PRIMER intento falla: si hubiera reintento, el segundo tiene que poder comitear
        // — es la única forma de que las filas duplicadas lleguen a la base y se puedan contar.
        if (Interlocked.Increment(ref intentos) > 1)
        {
            return;
        }

        throw new PostgresException(
            $"falla inyectada por la prueba sobre el {marca}", "ERROR", "ERROR", sqlState);
    }
}
