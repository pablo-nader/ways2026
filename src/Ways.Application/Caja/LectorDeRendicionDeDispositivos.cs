using System.Data;
using System.Data.Common;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using Ways.Application.Abstracciones;
using Ways.Domain.Ventas;

namespace Ways.Application.Caja;

/// <summary>
/// La IO de la guarda de cierre: por cada bloque de numeración VIVO del punto de venta, lee el
/// reporte del dispositivo y cuenta los comprobantes que de verdad existen en el rango que declaró
/// entregado. La DECISIÓN es de <see cref="ReglaDeRendicionDeCola"/> (pura, sin base) — acá no vive
/// ninguna regla.
///
/// SQL crudo sobre la conexión/transacción activa, misma convención que el resto del cierre
/// (<c>ServicioDeTurnos.MarcarCerradoAsync</c>/<c>ExigirTurnoAbiertoBajoLockAsync</c>): tiene que
/// leer DENTRO de la transacción del cierre, después del lock del turno, para ver el mismo
/// snapshot que el arqueo.
/// </summary>
public class LectorDeRendicionDeDispositivos(IWaysDbContext db)
{
    public async Task<IReadOnlyList<RendicionPendiente>> LeerPendientesAsync(
        int idTenant, int idPuntoVenta, DateTimeOffset momento, CancellationToken ct = default)
    {
        var conexion = await ObtenerConexionAbiertaAsync(ct);

        await using var comando = conexion.CreateCommand();
        comando.Transaction = db.Database.CurrentTransaction?.GetDbTransaction();

        // El join a dispositivos con `d.deleted_at IS NULL` ES LA VÁLVULA DE ESCAPE de la guarda:
        // revocar un dispositivo muerto (baja lógica, DELETE /api/dispositivos/{id}) saca su bloque
        // de esta consulta y el turno vuelve a poder cerrarse, sin ningún mecanismo nuevo. Un
        // dispositivo revocado tampoco puede sincronizar más (su sesión ya no autentica), así que
        // sus ventas encoladas no van a aparecer nunca — dejarlo bloqueando el cierre para siempre
        // sería un turno inmortal.
        //
        // La subconsulta NO filtra `cv.deleted_at IS NULL` ni por estado: un comprobante anulado (o,
        // en teoría, con baja lógica) igual OCUPÓ su número y igual llegó al servidor — es
        // exactamente lo que este conteo pregunta. Filtrarlo fabricaría un hueco inexistente.
        comando.CommandText =
            """
            SELECT r.id_dispositivo, d.nombre, r.tipo_comprobante, r.desde, r.entregado_hasta,
                   r.pendientes, r.reportado_at,
                   (SELECT COUNT(*) FROM comprobantes_venta cv
                      INNER JOIN tipos_comprobante tc ON tc.id_tipo_comprobante = cv.id_tipo_comprobante
                     WHERE cv.id_tenant = r.id_tenant
                       AND cv.id_punto_venta = r.id_punto_venta
                       AND tc.codigo = r.tipo_comprobante
                       AND cv.numero >= r.desde
                       AND cv.numero <= COALESCE(r.entregado_hasta, r.desde - 1)) AS emitidos
              FROM reservas_numeracion r
              INNER JOIN dispositivos d ON d.id_dispositivo = r.id_dispositivo AND d.id_tenant = r.id_tenant
             WHERE r.id_tenant = $1
               AND r.id_punto_venta = $2
               AND r.abandonada_at IS NULL
               AND d.deleted_at IS NULL
             ORDER BY r.id_dispositivo, r.tipo_comprobante
            """;

        ParametrosDeComando.Agregar(comando, idTenant);
        ParametrosDeComando.Agregar(comando, idPuntoVenta);

        var pendientes = new List<RendicionPendiente>();

        await using var lector = await comando.ExecuteReaderAsync(ct);
        while (await lector.ReadAsync(ct))
        {
            var idDispositivo = lector.GetInt32(0);
            var nombre = lector.GetString(1);
            var tipoComprobante = lector.GetString(2);
            var desde = lector.GetInt64(3);
            var entregadoHasta = await lector.IsDBNullAsync(4, ct) ? (long?)null : lector.GetInt64(4);
            var sinLlegar = await lector.IsDBNullAsync(5, ct) ? (int?)null : lector.GetInt32(5);
            var reportadoAt = await lector.IsDBNullAsync(6, ct)
                ? (DateTimeOffset?)null
                : lector.GetFieldValue<DateTimeOffset>(6);
            var emitidos = lector.GetInt64(7);

            var motivo = ReglaDeRendicionDeCola.Evaluar(
                reportadoAt, sinLlegar, entregadoHasta, desde, emitidos, momento);

            if (motivo is { } bloqueo)
            {
                pendientes.Add(new RendicionPendiente(
                    idDispositivo, nombre, tipoComprobante, bloqueo, sinLlegar, desde, entregadoHasta));
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
