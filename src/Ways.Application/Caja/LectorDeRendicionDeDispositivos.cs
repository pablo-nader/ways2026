using System.Data;
using System.Data.Common;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using Ways.Application.Abstracciones;
using Ways.Domain.Ventas;

namespace Ways.Application.Caja;

/// <summary>
/// La IO de la guarda de cierre: por cada bloque de numeración del punto de venta que todavía no
/// fue saldado, lee el reporte del dispositivo, cuenta los comprobantes del bloque que de verdad
/// llegaron y calcula el TECHO VERIFICADO del rango. La DECISIÓN es de
/// <see cref="ReglaDeRendicionDeCola"/> (pura, sin base) — acá no vive ninguna regla.
///
/// SQL crudo sobre la conexión/transacción activa, misma convención que el resto del cierre
/// (<c>ServicioDeTurnos.MarcarCerradoAsync</c>/<c>ExigirTurnoAbiertoBajoLockAsync</c>). Lee DENTRO
/// de la transacción del cierre y ANTES de que esa transacción escriba cualquier otra cosa, y eso
/// es exactamente para lo que ese lugar sirve: un rechazo aborta el cierre completo, incluido el
/// UPDATE guardado que ya había marcado el turno como cerrado.
///
/// Lo que ese lugar NO da, para que nadie lo suponga: la transacción del cierre no fija nivel de
/// aislamiento (READ COMMITTED, un snapshot nuevo por statement) y el único lock que tomó es sobre
/// la fila de <c>turnos_caja</c> — no cubre <c>reservas_numeracion</c>, <c>dispositivos</c> ni
/// <c>comprobantes_venta</c>. Esta lectura y la derivación del arqueo pueden ver estados distintos
/// de esas tres tablas; la carrera se acepta a propósito y el porqué está en
/// <c>ServicioDeTurnos.ResolverRendicionDeDispositivosAsync</c>.
/// </summary>
public class LectorDeRendicionDeDispositivos(IWaysDbContext db)
{
    public async Task<IReadOnlyList<RendicionPendiente>> LeerPendientesAsync(
        int idTenant, int idPuntoVenta, DateTimeOffset momento, CancellationToken ct = default)
    {
        var conexion = await ObtenerConexionAbiertaAsync(ct);

        await using var comando = conexion.CreateCommand();
        comando.Transaction = db.Database.CurrentTransaction?.GetDbTransaction();

        // `r.rendicion_saldada_at IS NULL` y NO `r.abandonada_at IS NULL` (judgment-day, SEVERE):
        // abandonar es rutina —`ReservarBloqueAsync` abandona el bloque vivo en CADA reposición—,
        // así que filtrar por abandono dejaba que la rotación del bloque borrara el hueco sola. Un
        // bloque sale de esta consulta solo cuando alguien se hizo cargo de sus números sin rendir:
        // un supervisor que forzó el cierre mientras ESTE bloque lo bloqueaba.
        //
        // `abandonada_at` sí se SELECCIONA —como `vivo`— porque la REGLA lo necesita: la frescura del
        // reporte solo aplica al bloque vivo (ver el parámetro `bloqueVivo` de
        // `ReglaDeRendicionDeCola.Evaluar`). Es un dato de la decisión, nunca un filtro de alcance.
        //
        // El join a dispositivos con `d.deleted_at IS NULL` ES LA OTRA VÁLVULA DE ESCAPE: revocar un
        // dispositivo muerto (baja lógica, DELETE /api/dispositivos/{id}) saca sus bloques de esta
        // consulta y el turno vuelve a poder cerrarse, sin ningún mecanismo nuevo. Un dispositivo
        // revocado tampoco puede sincronizar más (su sesión ya no autentica), así que sus ventas
        // encoladas no van a aparecer nunca — dejarlo bloqueando el cierre para siempre sería un
        // turno inmortal.
        //
        // `techo` es el techo VERIFICADO del rango: el mayor entre lo que el dispositivo declaró
        // entregado y el número más alto del bloque que YA llegó. Sin ese GREATEST, declarar
        // `desde - 1` vaciaba el rango esperado y el bloque pasaba sin importar qué tuviera en la
        // cola. `emitidos` cuenta sobre el BLOQUE entero y no sobre `[desde, techo]`: los dos dan el
        // mismo número —el techo nunca queda por debajo del máximo llegado dentro del bloque, así
        // que no hay comprobante del bloque por encima de él— y de esta forma el conteo y el máximo
        // salen de un solo escaneo.
        //
        // El lateral NO filtra `cv.deleted_at IS NULL` ni por estado: un comprobante anulado (o, en
        // teoría, con baja lógica) igual OCUPÓ su número y igual llegó al servidor — es exactamente
        // lo que este conteo pregunta. Filtrarlo fabricaría un hueco inexistente.
        comando.CommandText =
            """
            SELECT r.id_reserva_numeracion, r.id_dispositivo, d.nombre, r.tipo_comprobante, r.desde,
                   r.entregado_hasta, r.pendientes, r.reportado_at,
                   GREATEST(
                       COALESCE(r.entregado_hasta, r.desde - 1),
                       COALESCE(llegado.maximo, r.desde - 1)) AS techo,
                   llegado.emitidos, r.abandonada_at IS NULL AS vivo
              FROM reservas_numeracion r
              INNER JOIN dispositivos d ON d.id_dispositivo = r.id_dispositivo AND d.id_tenant = r.id_tenant
              CROSS JOIN LATERAL (
                  SELECT COUNT(*) AS emitidos, MAX(cv.numero) AS maximo
                    FROM comprobantes_venta cv
                    INNER JOIN tipos_comprobante tc ON tc.id_tipo_comprobante = cv.id_tipo_comprobante
                   WHERE cv.id_tenant = r.id_tenant
                     AND cv.id_punto_venta = r.id_punto_venta
                     AND tc.codigo = r.tipo_comprobante
                     AND cv.numero >= r.desde
                     AND cv.numero <= r.hasta) AS llegado
             WHERE r.id_tenant = $1
               AND r.id_punto_venta = $2
               AND r.rendicion_saldada_at IS NULL
               AND d.deleted_at IS NULL
             ORDER BY r.id_dispositivo, r.tipo_comprobante, r.id_reserva_numeracion
            """;

        ParametrosDeComando.Agregar(comando, idTenant);
        ParametrosDeComando.Agregar(comando, idPuntoVenta);

        var pendientes = new List<RendicionPendiente>();

        await using var lector = await comando.ExecuteReaderAsync(ct);
        while (await lector.ReadAsync(ct))
        {
            var idReserva = lector.GetInt64(0);
            var idDispositivo = lector.GetInt32(1);
            var nombre = lector.GetString(2);
            var tipoComprobante = lector.GetString(3);
            var desde = lector.GetInt64(4);
            var entregadoHasta = await lector.IsDBNullAsync(5, ct) ? (long?)null : lector.GetInt64(5);
            var sinLlegar = await lector.IsDBNullAsync(6, ct) ? (int?)null : lector.GetInt32(6);
            var reportadoAt = await lector.IsDBNullAsync(7, ct)
                ? (DateTimeOffset?)null
                : lector.GetFieldValue<DateTimeOffset>(7);
            var techo = lector.GetInt64(8);
            var emitidos = lector.GetInt64(9);
            var vivo = lector.GetBoolean(10);

            var motivo = ReglaDeRendicionDeCola.Evaluar(
                vivo, reportadoAt, sinLlegar, entregadoHasta, desde, techo, emitidos, momento);

            if (motivo is { } bloqueo)
            {
                pendientes.Add(new RendicionPendiente(
                    idReserva, idDispositivo, nombre, tipoComprobante, bloqueo, sinLlegar, desde,
                    entregadoHasta, techo, vivo));
            }
        }

        return pendientes;
    }

    private async Task<DbConnection> ObtenerConexionAbiertaAsync(CancellationToken ct)
    {
        var conexion = db.Database.GetDbConnection();

        if (conexion.State != ConnectionState.Open)
        {
            await db.Database.OpenConnectionAsync(ct);
        }

        return conexion;
    }
}
