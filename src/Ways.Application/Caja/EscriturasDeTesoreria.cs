using System.Data.Common;
using Microsoft.EntityFrameworkCore;
using Ways.Application.Abstracciones;
using Ways.Domain.Caja;

namespace Ways.Application.Caja;

/// <summary>
/// stage-gastos-origen-fondos-pos (PR2): la ÚNICA autoridad de append a la cadena de tesorería de
/// una empresa — extraída de <c>ServicioDeTurnos.InsertarArqueosYTesoreriaAsync</c> (el escritor
/// original, PR1) para que <c>ServicioDeGastos</c> (un gasto de origen
/// <see cref="Ways.Domain.Gastos.OrigenFondosGasto.Tesoreria"/>) reuse EXACTAMENTE el mismo
/// protocolo de lock+lectura+insert en vez de duplicar el SQL — mismo criterio de extracción que
/// <see cref="Ways.Application.CuentaCorriente.EscriturasDeCuentaCorrienteProveedor"/>.
///
/// <see cref="TomarLockDeEmpresaAsync"/> y <see cref="ApendearAsync"/> son DOS pasos separados a
/// propósito (nunca un único método): el llamador decide DÓNDE, en su propia secuencia de locks,
/// cae el advisory lock de la empresa — el cierre lo toma último (después del lock exclusivo del
/// turno); el gasto también lo toma último (después del turno <c>FOR SHARE</c>, la compra ligada
/// <c>FOR SHARE</c> si aplica, y el pago a proveedor si aplica) — mismo orden relativo "locks de
/// fila primero, advisory al final" en los dos escritores, así que nunca pueden formar un ciclo
/// entre sí (single-read-under-lock regla 4).
/// </summary>
public static class EscriturasDeTesoreria
{
    /// <summary><c>pg_advisory_xact_lock(id_tenant, id_empresa)</c> — serializa cualquier append a
    /// la cadena de la MISMA empresa (varios puntos de venta comparten un solo fondo de
    /// tesorería). Alcance de transacción (<c>_xact_</c>): se libera solo al commit/rollback de la
    /// transacción del LLAMADOR, nunca necesita un release explícito.</summary>
    public static async Task TomarLockDeEmpresaAsync(
        DbConnection conexion, DbTransaction? transaccion, int idTenant, int idEmpresa, CancellationToken ct)
    {
        await using var comando = conexion.CreateCommand();
        comando.Transaction = transaccion;
        comando.CommandText = "SELECT pg_advisory_xact_lock($1, $2)";
        ParametrosDeComando.Agregar(comando, idTenant);
        ParametrosDeComando.Agregar(comando, idEmpresa);
        await comando.ExecuteScalarAsync(ct);
    }

    /// <summary>Lee el <c>Final</c> de la última fila de la cadena de la empresa (single-read-under-
    /// lock regla 1: nace DESPUÉS de <see cref="TomarLockDeEmpresaAsync"/>, nunca antes — el
    /// llamador es responsable de haber tomado ese lock primero, en ESTA misma transacción) y
    /// agrega la fila siguiente encadenada (<c>Inicio</c> = ese <c>Final</c>,
    /// <c>Final</c> = <c>Inicio + Ingreso − Egreso</c>, backstop de esquema
    /// <c>ck_movimientos_tesoreria_cadena</c>).</summary>
    public static async Task<MovimientoTesoreria> ApendearAsync(
        IWaysDbContext db, int idTenant, int idEmpresa, int? idPuntoVenta, DateTimeOffset fecha,
        TipoMovimientoTesoreria tipo, int? idTurnoCaja, int? idGasto, string concepto, decimal ingreso,
        decimal egreso, int idEmpleado, CancellationToken ct)
    {
        var inicio = await db.MovimientosTesoreria
            .Where(m => m.IdEmpresa == idEmpresa && m.IdTenant == idTenant)
            .OrderByDescending(m => m.Id)
            .Select(m => m.Final)
            .FirstOrDefaultAsync(ct);

        var movimiento = new MovimientoTesoreria
        {
            IdTenant = idTenant,
            IdEmpresa = idEmpresa,
            IdPuntoVenta = idPuntoVenta,
            Fecha = fecha,
            Tipo = tipo,
            IdTurnoCaja = idTurnoCaja,
            IdGasto = idGasto,
            Concepto = concepto,
            Inicio = inicio,
            Ingreso = ingreso,
            Egreso = egreso,
            Final = inicio + ingreso - egreso,
            IdEmpleado = idEmpleado
        };

        db.MovimientosTesoreria.Add(movimiento);
        await db.SaveChangesAsync(ct);

        return movimiento;
    }
}
