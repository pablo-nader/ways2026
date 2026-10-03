using System.Data.Common;
using Ways.Application.Abstracciones;

namespace Ways.Application.Familias;

/// <summary>
/// Lock de MEMBRESÍA de familias (doc 10 §3, "Familias de artículos"): un lock de lectores y
/// escritores, uno por tenant, que hace estable la pertenencia a las familias mientras dura una
/// transacción. Es un <c>pg_advisory_xact_lock</c> de la variante de UNA clave <c>bigint</c>, cuyo
/// espacio de claves no se superpone nunca con el de la variante de dos <c>int</c> que usan los demás
/// locks advisory del repo (par artículo-lista de <c>ServicioDePrecios</c>, ofertas, bajas de
/// organización y usuarios, tesorería).
///
/// <para><b>Protocolo.</b> Toda transacción que escribe campos compartidos de una familia o precios
/// toma, en este orden global: (1) este lock, (2) las filas de <c>articulos</c> en orden ascendente
/// de <c>id_articulo</c> (<c>SELECT … ORDER BY id_articulo FOR NO KEY UPDATE</c>, nunca
/// <c>FOR UPDATE</c> sobre varias filas: choca con el <c>FOR KEY SHARE</c> que toman las ventas por
/// sus FK y puede formar un deadlock), (3) los locks de pares artículo-lista en orden ascendente de
/// <c>id_articulo</c>. Como todos suben en el mismo orden, dos escritores nunca se esperan en
/// ciclo.</para>
///
/// <para><b>Compartido o exclusivo.</b> <see cref="TomarCompartidoAsync"/> es para quien escribe
/// sin cambiar la pertenencia: varios escritores conviven, y todos excluyen a quien la cambia.
/// <see cref="TomarExclusivoAsync"/> es para quien entra, sale o mueve artículos entre familias
/// ("solo este"). Cada transacción elige UNA vez, antes de leer nada: PostgreSQL no promueve un lock
/// compartido a exclusivo, y dos transacciones que lo intentaran a la vez se bloquearían entre sí.
/// La pertenencia que se lee DESPUÉS de tomar este lock es estable hasta el commit.</para>
///
/// <para>Tiene que ser la PRIMERA sentencia de la transacción. Recibe la conexión y la transacción
/// del llamador (misma forma que <c>EscriturasDeTesoreria.TomarLockDeEmpresaAsync</c>) para que los
/// servicios con SQL crudo puedan usarlo sin pasar por <c>IWaysDbContext</c>.</para>
/// </summary>
public static class LockDeMembresiaDeFamilias
{
    /// <summary>"FAMI" en ASCII: la mitad alta de la clave de 64 bits.</summary>
    private const int PrefijoDeClave = 0x46414D49;

    /// <summary>La clave <c>bigint</c> del lock de un tenant: <c>"FAMI"</c> en los 32 bits altos y el
    /// <paramref name="idTenant"/> en los bajos. Pública para que las pruebas puedan sostener o
    /// buscar este mismo lock desde otra conexión.</summary>
    public static long ClaveDe(int idTenant) => ((long)PrefijoDeClave << 32) | unchecked((uint)idTenant);

    /// <summary><c>pg_advisory_xact_lock_shared(bigint)</c>: compatible con otros compartidos,
    /// incompatible con <see cref="TomarExclusivoAsync"/>. Se libera solo al commit o rollback.</summary>
    public static Task TomarCompartidoAsync(
        DbConnection conexion, DbTransaction? transaccion, int idTenant, CancellationToken ct) =>
        TomarAsync("SELECT pg_advisory_xact_lock_shared($1)", conexion, transaccion, idTenant, ct);

    /// <summary><c>pg_advisory_xact_lock(bigint)</c>: incompatible con cualquier otro tomador, sea
    /// compartido o exclusivo. Se libera solo al commit o rollback.</summary>
    public static Task TomarExclusivoAsync(
        DbConnection conexion, DbTransaction? transaccion, int idTenant, CancellationToken ct) =>
        TomarAsync("SELECT pg_advisory_xact_lock($1)", conexion, transaccion, idTenant, ct);

    private static async Task TomarAsync(
        string sql, DbConnection conexion, DbTransaction? transaccion, int idTenant, CancellationToken ct)
    {
        // Un lock de alcance de transacción tomado en autocommit se libera al terminar la propia
        // sentencia: no protegería nada y nadie se enteraría. Fallar fuerte, como
        // ServicioDeAuditoria.RegistrarAsync.
        if (transaccion is null)
        {
            throw new InvalidOperationException(
                "El lock de membresía de familias es de alcance de transacción: tomarlo fuera de una no protege nada.");
        }

        await using var comando = conexion.CreateCommand();
        comando.Transaction = transaccion;
        comando.CommandText = sql;
        ParametrosDeComando.Agregar(comando, ClaveDe(idTenant));
        await comando.ExecuteNonQueryAsync(ct);
    }
}
