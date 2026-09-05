using System.Data.Common;
using System.Text.RegularExpressions;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Npgsql;

namespace Ways.IntegrationTests;

/// <summary>
/// El COMMIT AMBIGUO de verdad: deja que el primer intento COMITEE y recién entonces le tira el
/// error transitorio encima, desde <c>TransactionCommittedAsync</c> — o sea DESPUÉS de que el
/// <c>COMMIT</c> ya llegó al servidor y ANTES de que el llamador se entere. Es exactamente la forma
/// del ACK perdido: las filas están en la base y el cliente cree que la operación falló.
///
/// <para><see cref="InterceptorQueRompeLaPrimeraEscritura"/> NO puede producir esa forma: rompe en
/// <c>*Executing</c>, así que el intento 1 nunca comitea nada. Contra ese interceptor, la guarda de
/// idempotencia de <c>ServicioDeVentas.EmitirAsync</c>
/// (<c>BuscarPorNumeroComprometidoAsync</c>) siempre encuentra <c>null</c> en el intento 2 y
/// BORRARLA no rompe ninguna prueba — el mutante sobrevive por construcción. Este interceptor es
/// la única forma de que la rama de RECUPERACIÓN de esa guarda llegue a ejecutarse.</para>
///
/// <para>Son DOS interceptores porque un <see cref="DbTransactionInterceptor"/> no ve comandos y un
/// <see cref="DbCommandInterceptor"/> no ve commits, y hace falta lo primero para romper el commit
/// y lo segundo para saber CUÁL commit romper. El vigía arma el quiebre al ver el <c>INSERT</c>
/// sobre la tabla vigilada, así que el commit de una transacción anterior —el paso de numeración de
/// la venta comitea en su propia transacción, ANTES— nunca se rompe por accidente.</para>
/// </summary>
internal sealed class InterceptorQueRompeElCommitAmbiguo
{
    private readonly VigiaDeLaEscritura vigia;
    private readonly QuiebreDelCommit quiebre;

    public InterceptorQueRompeElCommitAmbiguo(string tabla, string sqlState)
    {
        vigia = new VigiaDeLaEscritura(tabla);
        quiebre = new QuiebreDelCommit(vigia, tabla, sqlState);
    }

    /// <summary>Los dos, para pasárselos tal cual a
    /// <c>WaysApiFixture.CrearContextoDeAplicacionConReintentos</c>.</summary>
    public IInterceptor[] Interceptores => [vigia, quiebre];

    /// <summary>Cuántas veces se ENTRÓ al lambda reintentable, medido por la lectura de la guarda
    /// de idempotencia: <c>BuscarPorNumeroComprometidoAsync</c> es el único <c>SELECT ... FROM
    /// {tabla}</c> de todo <c>EmitirAsync</c> y corre como PRIMERA sentencia de cada intento, así
    /// que su conteo ES el conteo de intentos. Es el valor discriminante
    /// (<c>mutation-proof-tests</c> regla 4): <c>1</c> significaría que el reintento no ocurrió y
    /// que la prueba estaría verde por el motivo equivocado.</summary>
    public int Intentos => vigia.Lecturas;

    /// <summary>Cuántos commits se rompieron. Siempre <c>1</c>: solo el primero, para que el
    /// segundo intento pueda terminar.</summary>
    public int CommitsRotos => quiebre.Disparos;

    /// <summary>Cuántas veces se INSERTÓ de verdad sobre la tabla vigilada. El kill de la guarda:
    /// con ella, <c>1</c> (el intento 2 recupera lo ya comiteado); sin ella, el intento 2
    /// reinsertaría bajo el mismo número y chocaría contra su índice único.</summary>
    public int Inserciones => vigia.Inserciones;

    private sealed class VigiaDeLaEscritura : DbCommandInterceptor
    {
        private readonly Regex marcaDeInsert;
        private readonly Regex marcaDeLectura;
        private int inserciones;
        private int lecturas;

        public VigiaDeLaEscritura(string tabla)
        {
            // Mismo ancla (?![\w$]) que InterceptorQueRompeLaPrimeraEscritura: sin él,
            // "comprobantes_venta" matchearía dentro de un hipotético
            // "comprobantes_venta_algo" y el conteo dejaría de ser el de esta tabla.
            marcaDeInsert = Marca("INSERT INTO", tabla);
            marcaDeLectura = Marca("FROM", tabla);
        }

        public bool Armado => Volatile.Read(ref inserciones) > 0;

        public int Inserciones => Volatile.Read(ref inserciones);

        public int Lecturas => Volatile.Read(ref lecturas);

        public override ValueTask<InterceptionResult<DbDataReader>> ReaderExecutingAsync(
            DbCommand command, CommandEventData eventData, InterceptionResult<DbDataReader> result,
            CancellationToken cancellationToken = default)
        {
            Anotar(command);
            return base.ReaderExecutingAsync(command, eventData, result, cancellationToken);
        }

        public override ValueTask<InterceptionResult<int>> NonQueryExecutingAsync(
            DbCommand command, CommandEventData eventData, InterceptionResult<int> result,
            CancellationToken cancellationToken = default)
        {
            Anotar(command);
            return base.NonQueryExecutingAsync(command, eventData, result, cancellationToken);
        }

        public override ValueTask<InterceptionResult<object>> ScalarExecutingAsync(
            DbCommand command, CommandEventData eventData, InterceptionResult<object> result,
            CancellationToken cancellationToken = default)
        {
            Anotar(command);
            return base.ScalarExecutingAsync(command, eventData, result, cancellationToken);
        }

        private void Anotar(DbCommand comando)
        {
            var sql = comando.CommandText;

            if (marcaDeInsert.IsMatch(sql))
            {
                Interlocked.Increment(ref inserciones);
                return;
            }

            if (marcaDeLectura.IsMatch(sql))
            {
                Interlocked.Increment(ref lecturas);
            }
        }

        private static Regex Marca(string verbo, string tabla) =>
            new(
                $@"{verbo} (?:""{Regex.Escape(tabla)}""|{Regex.Escape(tabla)})(?![\w$])",
                RegexOptions.None,
                TimeSpan.FromSeconds(5));
    }

    private sealed class QuiebreDelCommit(VigiaDeLaEscritura vigia, string tabla, string sqlState)
        : DbTransactionInterceptor
    {
        private int disparos;

        public int Disparos => Volatile.Read(ref disparos);

        /// <summary><c>Committed</c> y no <c>Committing</c>: el <c>COMMIT</c> ya se ejecutó contra
        /// el servidor cuando EF llama a este gancho, así que las filas del intento 1 quedan
        /// PERSISTIDAS y el error viaja igual hacia arriba. Eso es el commit ambiguo; romper en
        /// <c>Committing</c> sería un commit que nunca ocurrió, que es el caso que ya cubre
        /// <see cref="InterceptorQueRompeLaPrimeraEscritura"/>.</summary>
        public override Task TransactionCommittedAsync(
            DbTransaction transaction, TransactionEndEventData eventData,
            CancellationToken cancellationToken = default)
        {
            // CompareExchange y no Increment: disparos tiene que contar los commits ROTOS, no los
            // vistos — un commit posterior sobre la tabla ya armada no puede inflar el conteo.
            if (vigia.Armado && Interlocked.CompareExchange(ref disparos, 1, 0) == 0)
            {
                throw new PostgresException(
                    $"ACK perdido inyectado por la prueba tras el COMMIT de {tabla}",
                    "ERROR", "ERROR", sqlState);
            }

            return base.TransactionCommittedAsync(transaction, eventData, cancellationToken);
        }
    }
}
