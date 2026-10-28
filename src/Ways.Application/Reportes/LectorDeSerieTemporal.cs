using System.Data;
using System.Data.Common;
using Microsoft.EntityFrameworkCore;
using Ways.Application.Abstracciones;
using Ways.Domain.Reportes;

namespace Ways.Application.Reportes;

/// <summary>
/// La ÚNICA superficie de SQL crudo de stage-10-agregacion-dashboard (design decisión 2: "one
/// file is one review target and one grep target for the invariant checklist"). Dos cuerpos SQL
/// constantes (ventas, pagos) y un ejecutor genérico compartido — mismo patrón que
/// <c>ServicioDeCategorias</c>: la conexión se abre con <c>Db.Database.OpenConnectionAsync()</c>,
/// NUNCA <c>GetDbConnection().OpenAsync()</c> crudo, porque solo el primero corre el pipeline de
/// interceptores de EF que setea los GUCs de RLS (<c>InterceptorDeContextoDeTenant</c>) — saltarlo
/// falla en silencio a 0 filas, no con una excepción.
///
/// La granularidad se inlinea como literal validado (design decisión 3): sale de un
/// <c>switch</c> sobre <see cref="Granularidad"/>, nunca de texto de la request. La zona SÍ es
/// dato de tenant y viaja como parámetro — <c>timezone(text, timestamptz)</c> en vez de la forma
/// infix <c>AT TIME ZONE</c> porque la forma función no es ambigua con un parámetro posicional.
/// </summary>
public class LectorDeSerieTemporal(IWaysDbContext db)
{
    private const string SqlVentas =
        """
        SELECT date_trunc('{0}', timezone($1, cv.fecha))::date AS bucket,
               SUM(cv.total) AS neto,
               COUNT(*) FILTER (WHERE tc.signo > 0) AS cantidad_tx,
               COALESCE(SUM(cv.total) FILTER (WHERE tc.signo > 0), 0) AS neto_tx,
               COUNT(*) FILTER (WHERE tc.signo < 0) AS cantidad_ncx,
               COALESCE(SUM(cv.total) FILTER (WHERE tc.signo < 0), 0) AS neto_ncx
        FROM comprobantes_venta cv
        JOIN tipos_comprobante tc ON tc.id_tipo_comprobante = cv.id_tipo_comprobante
        WHERE cv.deleted_at IS NULL
          AND cv.estado <> 'anulado'::estado_comprobante
          AND cv.id_venta_origen IS NULL
          AND tc.clase = 'venta'::clase_comprobante
          AND cv.id_tenant = $2
          AND cv.id_punto_venta = ANY($3)
          AND cv.fecha >= $4
          AND cv.fecha < $5
        GROUP BY 1
        ORDER BY 1
        """;

    /// <summary>Sin columna <c>estado</c> — <c>pagos</c> no tiene máquina de estados. Reusada tal
    /// cual por <c>pagos/resumen</c> (stage-10 slice 5, design: Raw-SQL Invariant Checklist).</summary>
    private const string SqlPagos =
        """
        SELECT date_trunc('{0}', timezone($1, g.fecha))::date AS bucket,
               SUM(g.importe) AS importe
        FROM pagos g
        WHERE g.deleted_at IS NULL
          AND g.id_tenant = $2
          AND g.id_punto_venta = ANY($3)
          AND g.fecha >= $4
          AND g.fecha < $5
        GROUP BY 1
        ORDER BY 1
        """;

    public Task<IReadOnlyList<FilaSerieDeVentas>> EjecutarVentasAsync(
        Granularidad granularidad, string zona, int idTenant, IReadOnlyCollection<int> idsPuntoVenta,
        DateTimeOffset desdeUtc, DateTimeOffset hastaUtcExclusivo, CancellationToken ct = default) =>
        EjecutarAsync(
            SqlVentas, granularidad, zona, idTenant, idsPuntoVenta, desdeUtc, hastaUtcExclusivo,
            ProyectarFilaDeVentas, ct);

    /// <summary>Sin consumidor todavía en esta slice — stage-10 slice 5
    /// (<c>ServicioDeReportesDeEgresos.ObtenerPagosResumenAsync</c>) es el primero. Declarado acá
    /// porque design decisión 2 asigna TODO el raw-SQL de la etapa a este único archivo.</summary>
    public Task<IReadOnlyList<FilaSerieDePagos>> EjecutarPagosAsync(
        Granularidad granularidad, string zona, int idTenant, IReadOnlyCollection<int> idsPuntoVenta,
        DateTimeOffset desdeUtc, DateTimeOffset hastaUtcExclusivo, CancellationToken ct = default) =>
        EjecutarAsync(
            SqlPagos, granularidad, zona, idTenant, idsPuntoVenta, desdeUtc, hastaUtcExclusivo,
            ProyectarFilaDePagos, ct);

    private async Task<IReadOnlyList<T>> EjecutarAsync<T>(
        string sqlTemplate, Granularidad granularidad, string zona, int idTenant,
        IReadOnlyCollection<int> idsPuntoVenta, DateTimeOffset desdeUtc, DateTimeOffset hastaUtcExclusivo,
        Func<DbDataReader, T> proyectar, CancellationToken ct)
    {
        var laAbrimosAca = await AbrirSiHaceFaltaAsync(ct);

        try
        {
            var conexion = db.Database.GetDbConnection();
            await using var comando = conexion.CreateCommand();
            comando.CommandText = string.Format(sqlTemplate, LiteralDeGranularidad(granularidad));

            ParametrosDeComando.Agregar(comando, zona);
            ParametrosDeComando.Agregar(comando, idTenant);
            ParametrosDeComando.Agregar(comando, idsPuntoVenta.ToArray());
            ParametrosDeComando.Agregar(comando, desdeUtc);
            ParametrosDeComando.Agregar(comando, hastaUtcExclusivo);

            var filas = new List<T>();
            await using var lector = await comando.ExecuteReaderAsync(ct);
            while (await lector.ReadAsync(ct))
            {
                filas.Add(proyectar(lector));
            }

            return filas;
        }
        finally
        {
            if (laAbrimosAca)
            {
                await db.Database.CloseConnectionAsync();
            }
        }
    }

    private static FilaSerieDeVentas ProyectarFilaDeVentas(DbDataReader lector) => new(
        lector.GetFieldValue<DateOnly>(0), lector.GetDecimal(1), Convert.ToInt32(lector.GetInt64(2)),
        lector.GetDecimal(3), Convert.ToInt32(lector.GetInt64(4)), lector.GetDecimal(5));

    private static FilaSerieDePagos ProyectarFilaDePagos(DbDataReader lector) => new(
        lector.GetFieldValue<DateOnly>(0), lector.GetDecimal(1));

    private static string LiteralDeGranularidad(Granularidad granularidad) => granularidad switch
    {
        Granularidad.Dia => "day",
        Granularidad.Semana => "week",
        Granularidad.Mes => "month",
        _ => throw new ArgumentOutOfRangeException(nameof(granularidad))
    };

    /// <summary><c>Database.OpenConnectionAsync()</c>, no <c>GetDbConnection().OpenAsync()</c>
    /// crudo — mismo motivo que <c>ServicioDeCategorias.AbrirSiHaceFaltaAsync</c>: solo el primero
    /// corre el interceptor que setea los GUCs de RLS. Devuelve si esta llamada abrió la conexión,
    /// para no interferir con una ya abierta por otra operación de EF en curso en el mismo
    /// request.</summary>
    private async Task<bool> AbrirSiHaceFaltaAsync(CancellationToken ct)
    {
        if (db.Database.GetDbConnection().State == ConnectionState.Open)
        {
            return false;
        }

        await db.Database.OpenConnectionAsync(ct);
        return true;
    }
}

/// <summary>Una fila de la serie de ventas antes del gap-fill (<c>RangoDeReporte.Buckets()</c>
/// hace el resto en C#, design decisión 4). <see cref="Neto"/> ya es neto de NCX por construcción
/// (design decisión 9, <c>tipos_comprobante.signo</c> como discriminador — el total de un NCX ya
/// llega negativo); <see cref="CantidadTx"/>/<see cref="NetoTx"/> cuentan solo <c>signo &gt;
/// 0</c>.</summary>
public sealed record FilaSerieDeVentas(
    DateOnly Bucket, decimal Neto, int CantidadTx, decimal NetoTx, int CantidadNcx, decimal NetoNcx);

/// <summary>Una fila de la serie de pagos — sin discriminador de signo, <c>pagos</c> no tiene
/// noción de nota de crédito.</summary>
public sealed record FilaSerieDePagos(DateOnly Bucket, decimal Importe);
