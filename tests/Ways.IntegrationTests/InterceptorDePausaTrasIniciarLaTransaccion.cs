using System.Data.Common;
using Microsoft.EntityFrameworkCore.Diagnostics;

namespace Ways.IntegrationTests;

/// <summary>
/// Rendezvous forzado de los tests de concurrencia: pausa la transacción justo DESPUÉS de
/// <c>BeginTransactionAsync</c> y ANTES de su primer statement, hasta que el test libere
/// <paramref name="puedeContinuar"/>. En esa ventana la parte pausada ya tiene la transacción
/// ABIERTA pero todavía no tomó ningún lock de fila — justo el interleaving que una carrera
/// libre por HTTP no puede garantizar: el otro escritor corre y comitea ENTERO mientras esta
/// transacción espera.
///
/// El protocolo son dos <see cref="TaskCompletionSource"/>: el interceptor señala
/// <paramref name="transaccionIniciada"/> cuando la transacción abrió, y se queda esperando
/// <paramref name="puedeContinuar"/>. Los dos se construyen con
/// <see cref="TaskCreationOptions.RunContinuationsAsynchronously"/>: sin eso la continuación
/// corre inline sobre el hilo del interceptor y el test se clava.
///
/// Engancha el CICLO DE VIDA de la transacción y no un <c>DbCommand</c> puntual, así que sirve
/// igual para las transacciones manuales de un servicio y para las que el servicio bajo prueba
/// hereda del caller. Cuando lo que hay que pausar es un comando específico en vez de la
/// transacción, el patrón es un interceptor de <c>DbCommand</c> — ver
/// <c>ComprasAnulacionYConcurrenciaTests.InterceptorDeRendezVousConfirmar</c>.
/// </summary>
internal sealed class InterceptorDePausaTrasIniciarLaTransaccion(
    TaskCompletionSource transaccionIniciada, TaskCompletionSource puedeContinuar) : DbTransactionInterceptor
{
    public override async ValueTask<DbTransaction> TransactionStartedAsync(
        DbConnection connection, TransactionEndEventData eventData, DbTransaction transaction,
        CancellationToken cancellationToken = default)
    {
        transaccionIniciada.TrySetResult();
        await puedeContinuar.Task;
        return await base.TransactionStartedAsync(connection, eventData, transaction, cancellationToken);
    }
}
