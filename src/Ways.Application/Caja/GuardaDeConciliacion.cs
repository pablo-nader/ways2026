using System.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using Ways.Application.Abstracciones;
using Ways.Domain.Caja;

namespace Ways.Application.Caja;

/// <summary>
/// Guarda del congelamiento de la tesorería: lo que alimenta el libro de tesorería con fecha menor o igual a la marca de conciliación
/// de la empresa no se modifica. La marca vigente es el mayor <c>hasta</c> de <c>conciliaciones_tesoreria</c>.
///
/// Protocolo de locks. El escritor de la marca toma un lock advisory EXCLUSIVO de transacción sobre la clave de
/// <c>(tenant, empresa)</c> y no toma ningún otro lock; todo escritor protegido toma el mismo lock COMPARTIDO, lee la marca y recién
/// ahí decide. Así, una escritura que ya leyó la marca "libre" termina antes de que la marca se pueda crear, y una marca ya creada se
/// ve siempre.
///
/// Orden: el lock compartido es el PRIMER lock de la transacción del escritor protegido, antes de cualquier lock de turno, compra,
/// pago, proveedor o advisory de tesorería (<see cref="EscriturasDeTesoreria.TomarLockDeEmpresaAsync"/>). Es obligatorio porque
/// PostgreSQL encola un pedido compartido detrás de un exclusivo que ya espera: si un escritor retuviera otro lock mientras pide
/// este, podría esperar al exclusivo, que espera a otro escritor compartido, que espera ese otro lock. Con este orden nadie espera
/// la marca reteniendo algo, y el escritor de la marca no espera nada salvo a los compartidos, que no esperan al escritor de la
/// marca. Volver a pedir el compartido dentro de la misma transacción es seguro: PostgreSQL concede de inmediato un lock que la
/// propia transacción ya tiene.
///
/// La clave es un <c>bigint</c> con el bit alto encendido y <c>(idTenant, idEmpresa)</c> en los 63 restantes, así que no puede
/// coincidir con el advisory de tesorería (forma de dos enteros, otro espacio de claves) ni con el de familias (prefijo positivo).
/// </summary>
public static class GuardaDeConciliacion
{
    public static long ClaveDe(int idTenant, int idEmpresa) =>
        long.MinValue | ((long)idTenant << 32) | unchecked((uint)idEmpresa);

    /// <summary><c>pg_advisory_xact_lock_shared(bigint)</c>: compatible con otros compartidos. Se libera al commit o rollback.</summary>
    public static Task TomarCompartidoAsync(IWaysDbContext db, int idTenant, int idEmpresa, CancellationToken ct) =>
        TomarAsync(db, "SELECT pg_advisory_xact_lock_shared($1)", idTenant, idEmpresa, ct);

    /// <summary><c>pg_advisory_xact_lock(bigint)</c>: exclusivo del escritor de la marca. Se libera al commit o rollback.</summary>
    public static Task TomarExclusivoAsync(IWaysDbContext db, int idTenant, int idEmpresa, CancellationToken ct) =>
        TomarAsync(db, "SELECT pg_advisory_xact_lock($1)", idTenant, idEmpresa, ct);

    /// <summary>La marca vigente de la empresa, o <c>null</c> si nunca se concilió. Para decidir algo, el llamador tiene que haber
    /// tomado antes el lock compartido en la misma transacción.</summary>
    public static Task<DateTimeOffset?> LeerMarcaAsync(IWaysDbContext db, int idEmpresa, CancellationToken ct) =>
        db.ConciliacionesTesoreria
            .Where(c => c.IdEmpresa == idEmpresa)
            .MaxAsync(c => (DateTimeOffset?)c.Hasta, ct);

    /// <summary>Toma el lock compartido (si la transacción todavía no lo tiene), lee la marca y falla con 409 <c>periodo_conciliado</c>
    /// si <paramref name="fecha"/> es menor o igual a ella. Tiene que correr dentro de la transacción del escritor.</summary>
    public static async Task ExigirNoConciliadoAsync(
        IWaysDbContext db, int idTenant, int idEmpresa, DateTimeOffset fecha, CancellationToken ct)
    {
        await TomarCompartidoAsync(db, idTenant, idEmpresa, ct);

        var marca = await LeerMarcaAsync(db, idEmpresa, ct);
        if (ReglasDeConciliacion.EstaCongelado(fecha, marca))
        {
            throw ReglasDeConciliacion.PeriodoConciliado(marca!.Value);
        }
    }

    private static async Task TomarAsync(IWaysDbContext db, string sql, int idTenant, int idEmpresa, CancellationToken ct)
    {
        var transaccion = db.Database.CurrentTransaction?.GetDbTransaction()
            ?? throw new InvalidOperationException(
                "El lock de conciliación es de alcance de transacción: tomarlo fuera de una no protege nada.");

        var conexion = db.Database.GetDbConnection();
        if (conexion.State != ConnectionState.Open)
        {
            await db.Database.OpenConnectionAsync(ct);
        }

        await using var comando = conexion.CreateCommand();
        comando.Transaction = transaccion;
        comando.CommandText = sql;
        ParametrosDeComando.Agregar(comando, ClaveDe(idTenant, idEmpresa));
        await comando.ExecuteNonQueryAsync(ct);
    }
}
