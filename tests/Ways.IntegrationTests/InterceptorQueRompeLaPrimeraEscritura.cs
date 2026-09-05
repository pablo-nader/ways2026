using System.Data.Common;
using System.Text.RegularExpressions;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Npgsql;

namespace Ways.IntegrationTests;

/// <summary>Qué sentencia vigila <see cref="InterceptorQueRompeLaPrimeraEscritura"/>. Una baja
/// lógica no inserta nada: escribe <c>deleted_at</c> con un UPDATE, así que sin este eje el
/// interceptor no dispara nunca y la prueba de <c>ServicioDeOfertas.EliminarAsync</c> pasaría por
/// el motivo equivocado (judgment-day fix/retry-double-add, item C4). <see cref="Select"/> extiende
/// el mismo eje a las LECTURAS: el nombre de la clase quedó de su primer uso, pero lo que hace es
/// romper la primera SENTENCIA de la clase pedida.</summary>
internal enum ClaseDeSentencia
{
    Insert,
    Update,

    /// <summary>Una LECTURA. La usa el caso del método seguro: <c>ManejadorDeErrores</c> parte la
    /// copia del fallo transitorio por método HTTP, y probar el lado <c>GET</c> exige romper un
    /// <c>SELECT</c> — ni el interceptor de INSERT ni el de UPDATE disparan nunca en esa
    /// request.</summary>
    Select
}

/// <summary>
/// Rompe el PRIMER lote que contenga una escritura sobre <c>tabla</c> con un error de Postgres del
/// SQLSTATE que se le pida, y deja pasar todo lo demás — incluido el segundo intento, si lo
/// hubiera. Es lo único que convierte "¿esta unidad es reintentable?" en una pregunta observable:
/// sin él, el reintento de <c>EnableRetryOnFailure</c> no se dispara nunca en una prueba y el
/// mutante sobrevive por construcción.
///
/// Generalización del <c>InterceptorQueRompeElRastro</c> de <c>BajasDeOrganizacionTests</c>
/// (etapa 20, judgment-day ronda 2), que estaba clavado en <c>auditoria</c>. Vive en su propio
/// archivo porque lo consumen todas las escrituras de
/// <see cref="EscriturasSinReintentoTests"/>, cada una sobre su propia tabla.
///
/// <para>La marca está ANCLADA al FINAL del nombre de la tabla y NO es un prefijo suelto
/// (judgment-day fix/retry-double-add, item C6): <c>"INSERT INTO articulos"</c> por
/// <c>Contains</c> también matchea <c>INSERT INTO articulos_empresas</c>, así que el interceptor
/// rompía la tabla equivocada y contaba intentos que no eran suyos. Con el ancla,
/// <see cref="Intentos"/> cuenta SOLO los de <c>tabla</c>.</para>
///
/// <see cref="Intentos"/> es el valor DISCRIMINANTE (<c>mutation-proof-tests</c> regla 4): bajo la
/// estrategia sin reintento la escritura se ve UNA sola vez; bajo la reintentable se ve dos, y el
/// segundo intento comitea el doble de filas. Afirmar solamente que el error llegó al llamador NO
/// distingue las dos estrategias — la reintentable también termina propagando si agota los cinco
/// intentos.
/// </summary>
internal sealed class InterceptorQueRompeLaPrimeraEscritura : DbCommandInterceptor
{
    private readonly Regex marca;
    private readonly string descripcion;
    private readonly string sqlState;
    private int intentos;

    public InterceptorQueRompeLaPrimeraEscritura(
        string tabla, string sqlState, ClaseDeSentencia clase = ClaseDeSentencia.Insert)
    {
        this.sqlState = sqlState;

        var verbo = clase switch
        {
            ClaseDeSentencia.Update => "UPDATE",
            ClaseDeSentencia.Select => "FROM",
            _ => "INSERT INTO"
        };
        descripcion = clase == ClaseDeSentencia.Select ? $"SELECT ... FROM {tabla}" : $"{verbo} {tabla}";

        // (?:"tabla"|tabla) admite la forma entrecomillada por si el identificador alguna vez la
        // necesita; el (?![\w$]) es el ancla: exige que el nombre TERMINE ahí. Sin él,
        // "articulos" matchea dentro de "articulos_empresas" — `_` es \w, así que el lookahead lo
        // rechaza.
        marca = new Regex(
            $@"{verbo} (?:""{Regex.Escape(tabla)}""|{Regex.Escape(tabla)})(?![\w$])",
            RegexOptions.None,
            TimeSpan.FromSeconds(5));
    }

    /// <summary>Cuántas veces se intentó ejecutar el lote que contiene la escritura vigilada.</summary>
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
        if (!marca.IsMatch(comando.CommandText))
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
            $"falla inyectada por la prueba sobre el {descripcion}", "ERROR", "ERROR", sqlState);
    }
}
