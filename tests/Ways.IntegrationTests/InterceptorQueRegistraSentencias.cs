using System.Collections.Concurrent;
using System.Data.Common;
using Microsoft.EntityFrameworkCore.Diagnostics;

namespace Ways.IntegrationTests;

/// <summary>
/// Registra el texto de cada sentencia que EF Core manda a la base, en orden. Es la red de una cláusula cuyo
/// efecto puede no decidirlo el resultado sino el plan de Postgres: un <c>ORDER BY</c> sobre una columna que el
/// plan ya recorre por un índice devuelve las filas en ese orden aunque la cláusula falte, y ningún dato sembrado
/// distingue las dos formas. Afirmar sobre el texto que la cláusula se pidió es lo que la ve. No ve las sentencias de
/// ADO.NET crudo: <c>conexion.CreateCommand()</c> no pasa por el pipeline de comandos de EF.
/// </summary>
internal sealed class InterceptorQueRegistraSentencias : DbCommandInterceptor
{
    private readonly ConcurrentQueue<string> sentencias = new();

    public IReadOnlyList<string> Sentencias => [.. sentencias];

    public override ValueTask<InterceptionResult<DbDataReader>> ReaderExecutingAsync(
        DbCommand command, CommandEventData eventData, InterceptionResult<DbDataReader> result,
        CancellationToken cancellationToken = default)
    {
        sentencias.Enqueue(command.CommandText);

        return base.ReaderExecutingAsync(command, eventData, result, cancellationToken);
    }

    public override ValueTask<InterceptionResult<int>> NonQueryExecutingAsync(
        DbCommand command, CommandEventData eventData, InterceptionResult<int> result,
        CancellationToken cancellationToken = default)
    {
        sentencias.Enqueue(command.CommandText);

        return base.NonQueryExecutingAsync(command, eventData, result, cancellationToken);
    }

    public override ValueTask<InterceptionResult<object>> ScalarExecutingAsync(
        DbCommand command, CommandEventData eventData, InterceptionResult<object> result,
        CancellationToken cancellationToken = default)
    {
        sentencias.Enqueue(command.CommandText);

        return base.ScalarExecutingAsync(command, eventData, result, cancellationToken);
    }
}
