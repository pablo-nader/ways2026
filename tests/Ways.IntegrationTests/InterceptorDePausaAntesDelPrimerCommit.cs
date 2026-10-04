using System.Data.Common;
using Microsoft.EntityFrameworkCore.Diagnostics;

namespace Ways.IntegrationTests;

/// <summary>
/// Rendezvous forzado de los tests de concurrencia: pausa la PRIMERA transacción que llega a comitear, justo
/// ANTES del <c>COMMIT</c> y hasta que el test libere <paramref name="puedeComitear"/>. En esa ventana la
/// transacción ya hizo todas sus escrituras y sigue sosteniendo todos sus locks —el estado de "una escritura en
/// curso, a punto de terminar"—, que una carrera libre por HTTP no puede garantizar. Los commits que lleguen
/// después pasan sin esperar.
///
/// <para>Complementa a <see cref="InterceptorDePausaTrasIniciarLaTransaccion"/>, que pausa al otro extremo (después de
/// abrir la transacción y antes de su primer statement). Se arma con
/// <see cref="WaysApiFixture.ConInterceptorEnElHost"/> y el test tiene que garantizar que la primera transacción
/// que comitea mientras está armado es la que quiere pausar. Los dos <see cref="TaskCompletionSource"/> se
/// construyen con <see cref="TaskCreationOptions.RunContinuationsAsynchronously"/>.</para>
/// </summary>
internal sealed class InterceptorDePausaAntesDelPrimerCommit(
    TaskCompletionSource alPuntoDeComitear, TaskCompletionSource puedeComitear) : DbTransactionInterceptor
{
    private int comitsVistos;

    public override async ValueTask<InterceptionResult> TransactionCommittingAsync(
        DbTransaction transaction, TransactionEventData eventData, InterceptionResult result,
        CancellationToken cancellationToken = default)
    {
        if (Interlocked.Increment(ref comitsVistos) == 1)
        {
            alPuntoDeComitear.TrySetResult();
            await puedeComitear.Task;
        }

        return await base.TransactionCommittingAsync(transaction, eventData, result, cancellationToken);
    }
}
