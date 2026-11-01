using System.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using Ways.Application.Abstracciones;
using Ways.Domain.Caja;

namespace Ways.Application.Caja;

/// <summary>
/// Recalcula el arqueo persistido de un turno cerrado cuando cambia algo que el arqueo cuenta (un pago editado o dado de baja,
/// los medios de una venta), con la misma derivación que el cierre (<see cref="LectorDeMovimientosDelTurno"/> +
/// <see cref="CalculadorDeArqueo"/>) y el ancla pineada al cierre. El modo de cierre (clásico o por retiro) no queda
/// persistido, así que el declarado de las filas existentes no se toca y las filas nuevas se declaran en 0: nadie declaró nada
/// para ese medio. La marca del turno (<c>fecha_recalculo</c>, <c>id_empleado_recalculo</c>) se escribe solo si alguna fila
/// cambió.
///
/// Corre dentro de la transacción del llamador, que ya tiene el turno <c>FOR UPDATE</c>: ese lock cubre las filas de
/// <c>arqueos_turno</c> y la marca, y serializa entre sí los recálculos del mismo turno.
/// </summary>
public static class RecalculoDeArqueoDeTurnoCerrado
{
    /// <summary>Devuelve si alguna fila del arqueo cambió.</summary>
    public static async Task<bool> RecalcularAsync(
        IWaysDbContext db, LectorDeMovimientosDelTurno lector, int idTenant, int idTurnoCaja, int? idMedioPagoEfectivo,
        int idEmpleado, DateTimeOffset momento, CancellationToken ct)
    {
        var insumos = await lector.LeerAsync(idTurnoCaja, ct);
        var idAncla = idMedioPagoEfectivo ?? ResolvedorDeMedioDeCajaFisica.Resolver(insumos.Actividad);
        var lineas = CalculadorDeArqueo.Calcular(insumos, idAncla);

        var filas = await db.ArqueosTurno.Where(a => a.IdTurnoCaja == idTurnoCaja).ToListAsync(ct);
        var cambios = RecalculadorDeArqueo.Planificar(
            lineas, insumos.Actividad,
            filas.Select(f => new ArqueoPersistido(f.IdMedioPago, f.ImporteEsperado, f.ImporteEsperadoOriginal)).ToList());

        if (cambios.Count == 0)
        {
            return false;
        }

        foreach (var cambio in cambios)
        {
            if (cambio.EsNueva)
            {
                db.ArqueosTurno.Add(new ArqueoTurno
                {
                    IdTenant = idTenant,
                    IdTurnoCaja = idTurnoCaja,
                    IdMedioPago = cambio.IdMedioPago,
                    ImporteEsperado = cambio.ImporteEsperado,
                    ImporteDeclarado = 0m,
                    ImporteEsperadoOriginal = cambio.ImporteEsperadoOriginal
                });
                continue;
            }

            var fila = filas.Single(f => f.IdMedioPago == cambio.IdMedioPago);
            fila.ImporteEsperado = cambio.ImporteEsperado;
            fila.ImporteEsperadoOriginal = cambio.ImporteEsperadoOriginal;
        }

        await db.SaveChangesAsync(ct);

        var conexion = db.Database.GetDbConnection();
        if (conexion.State != ConnectionState.Open)
        {
            await db.Database.OpenConnectionAsync(ct);
        }

        await using var comando = conexion.CreateCommand();
        comando.Transaction = db.Database.CurrentTransaction?.GetDbTransaction();
        comando.CommandText =
            "UPDATE turnos_caja SET fecha_recalculo = $1, id_empleado_recalculo = $2, updated_at = $1 " +
            "WHERE id_turno_caja = $3 AND id_tenant = $4";
        ParametrosDeComando.Agregar(comando, momento);
        ParametrosDeComando.Agregar(comando, idEmpleado);
        ParametrosDeComando.Agregar(comando, idTurnoCaja);
        ParametrosDeComando.Agregar(comando, idTenant);
        await comando.ExecuteNonQueryAsync(ct);

        return true;
    }
}
