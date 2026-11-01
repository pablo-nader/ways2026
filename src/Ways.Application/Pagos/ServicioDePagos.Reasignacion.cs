using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using Ways.Application.Abstracciones;
using Ways.Application.Auditoria;
using Ways.Application.Plantel;
using Ways.Domain.Auditoria;
using Ways.Domain.Common;

namespace Ways.Application.Pagos;

public partial class ServicioDePagos
{
    /// <summary>
    /// Mueve un pago de sueldo al libro de otro empleado, sin borrarlo ni volver a cargarlo: cambia el empleado del asiento
    /// del pago, corrige por desplazamiento los saldos resultantes de los dos libros y los dos saldos cacheados, y actualiza
    /// el nombre en el concepto del pago y de su egreso de tesorería. Es la única operación que cambia un saldo resultante ya
    /// asentado; el rastro queda en la auditoría (<c>pago.reasignacion</c>).
    /// update-404-first: el pago inexistente es 404 antes de validar el cuerpo. Forma (b) de ef-retry-safe-writes: mueve
    /// saldos sin clave de idempotencia, y un reintento tras un commit ambiguo los desplazaría dos veces.
    ///
    /// Orden de locks: filas de personal (origen y destino) <c>FOR UPDATE</c> en id ascendente → asiento del personal →
    /// pago. No toma el advisory de tesorería: el egreso no cambia de importe, solo de texto. Sin ciclo: los escritores
    /// del libro del personal (pago, ajuste, carga de horas) toman solo filas de personal, y los del pago rechazan un pago de
    /// personal antes de sus locks o lockean el pago sin tocar personal.
    /// </summary>
    public async Task<ResultadoDeReasignacionDePago> ReasignarPersonalAsync(
        int idPago, SolicitudDeReasignacionDePago? solicitud, CancellationToken ct = default)
    {
        var idTenant = ExigirTenantDeLaSesion();
        var momento = reloj.Ahora;

        var inmutables = await LeerDatosInmutablesAsync(idPago, ct);

        if (solicitud?.IdPersonal is not > 0)
        {
            throw new ErrorDominio("personal_requerido", "Indicá el empleado al que se reasigna el pago.", 400);
        }

        var idMovimiento = inmutables.IdMovimientoPersonal
            ?? throw ErrorDominio.Conflicto(
                "pago_sin_movimiento_de_personal", "El pago no es un pago de sueldo de un empleado.");
        var idDestino = solicitud.IdPersonal.Value;

        var estrategia = FabricaDeEstrategiaSinReintento.CrearEstrategiaSinReintento(db);
        return await estrategia.ExecuteAsync(async () =>
            await EjecutarReasignacionAsync(idTenant, idPago, idMovimiento, idDestino, momento, ct));
    }

    private async Task<ResultadoDeReasignacionDePago> EjecutarReasignacionAsync(
        int idTenant, int idPago, int idMovimiento, int idDestino, DateTimeOffset momento, CancellationToken ct)
    {
        await using var transaccion = await db.Database.BeginTransactionAsync(ct);

        var conexion = await ObtenerConexionAbiertaAsync(ct);
        var transaccionCruda = db.Database.CurrentTransaction?.GetDbTransaction();

        // El origen se lee sin lock solo para saber qué filas bloquear; se confirma bajo el lock del asiento.
        var idOrigen = await EscriturasDePersonal.LeerPersonalDelMovimientoAsync(
            conexion, transaccionCruda, idTenant, idMovimiento, ct)
            ?? throw PagoCambioConcurrente();

        var origenVivo = false;
        var destinoVivo = false;
        foreach (var idPersonal in new[] { idOrigen, idDestino }.Distinct().Order())
        {
            var vivo = await EscriturasDePersonal.BloquearPersonalAsync(
                conexion, transaccionCruda, idTenant, idPersonal, ct) is not null;
            origenVivo |= vivo && idPersonal == idOrigen;
            destinoVivo |= vivo && idPersonal == idDestino;
        }

        if (!destinoVivo)
        {
            throw ErrorDominio.NoEncontrado($"No existe el personal {idDestino}.");
        }

        if (idDestino == idOrigen)
        {
            throw ErrorDominio.Conflicto(
                "reasignacion_al_mismo_personal", "El pago ya pertenece a ese empleado.");
        }

        var movimiento = await EscriturasDePersonal.BloquearMovimientoAsync(
            conexion, transaccionCruda, idTenant, idMovimiento, ct);
        if (!origenVivo || movimiento is null || movimiento.IdPersonal != idOrigen)
        {
            throw PagoCambioConcurrente();
        }

        if (await db.MovimientosCuentaCorriente.AnyAsync(m => m.IdMovimientoPersonal == idMovimiento, ct))
        {
            throw ErrorDominio.Conflicto(
                "pago_con_descuento_de_cuenta_corriente",
                "El pago descontó de la cuenta corriente del cliente vinculado y no se puede reasignar.");
        }

        var nombreDelDestino = await db.Personal
            .Where(p => p.Id == idDestino)
            .Select(p => p.Nombre)
            .FirstAsync(ct);

        await TomarLockDePagoAsync(idPago, idTenant, ct);
        var pago = await db.Pagos.FirstOrDefaultAsync(p => p.Id == idPago, ct)
            ?? throw ErrorDominio.NoEncontrado($"No existe el gasto {idPago}.");

        var conceptoAnterior = pago.Concepto;
        var concepto = $"Pago de personal: {nombreDelDestino}";

        await EscriturasDePersonal.ReasignarMovimientoAsync(
            conexion, transaccionCruda, idTenant, idMovimiento, idOrigen, idDestino, movimiento.Importe, ct);
        await EscriturasDePersonal.ActualizarSaldoAsync(
            conexion, transaccionCruda, idTenant, idOrigen, -movimiento.Importe, ct);
        await EscriturasDePersonal.ActualizarSaldoAsync(
            conexion, transaccionCruda, idTenant, idDestino, movimiento.Importe, ct);

        await using (var comando = conexion.CreateCommand())
        {
            comando.Transaction = transaccionCruda;
            comando.CommandText =
                "UPDATE movimientos_tesoreria SET concepto = $1 WHERE id_pago = $2 AND id_tenant = $3";
            ParametrosDeComando.Agregar(comando, concepto);
            ParametrosDeComando.Agregar(comando, idPago);
            ParametrosDeComando.Agregar(comando, idTenant);
            await comando.ExecuteNonQueryAsync(ct);
        }

        pago.Concepto = concepto;
        pago.UpdatedAt = momento;

        auditoria.Registrar(new RegistroDeAuditoria(
            idTenant, pago.IdPuntoVenta, AccionAuditada.PagoReasignacion, pago.Id,
            new Dictionary<string, object?>
            {
                ["id_personal"] = idOrigen, ["id_movimiento_personal"] = idMovimiento, ["concepto"] = conceptoAnterior
            },
            new Dictionary<string, object?>
            {
                ["id_personal"] = idDestino, ["id_movimiento_personal"] = idMovimiento, ["concepto"] = concepto
            }));
        await db.SaveChangesAsync(ct);

        await transaccion.CommitAsync(ct);

        return new ResultadoDeReasignacionDePago(idPago, idOrigen, idDestino, concepto);
    }
}
