using System.Data;
using System.Data.Common;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using Ways.Application.Abstracciones;

namespace Ways.Application.Fiscal;

/// <summary>
/// Asigna <c>numeraciones_fiscales.proximo_numero</c> por serie ARCA — <c>(id_punto_venta,
/// codigo_afip)</c> — con la disciplina OPUESTA de <see cref="Ventas.AsignadorDeNumeroComprobante"/>
/// (design D1, proposal decisión 13): ESE asignador abre y comitea su PROPIA transacción chica,
/// ANTES de la del llamador, a propósito, para que "el número se consume aunque falle el resto" —
/// un hueco en la serie interna es legítimo. Acá, para una serie de ARCA, ese mismo hueco DETIENE
/// la serie completa (error 10016): <see cref="AsignarSiguienteAsync"/> corre DENTRO de la
/// transacción del llamador (la emisión fiscal, slice 5), nunca abre ni comitea la suya — el row
/// lock del <c>UPDATE … RETURNING</c> se sostiene hasta el <c>COMMIT</c> de esa transacción,
/// incluido el round trip a WSFE (D1: <c>numeraciones_fiscales</c> es el ÚNICO lock existente que
/// esa transacción toma, en la posición 0 del orden total).
///
/// Mismo raw ADO sobre la conexión/transacción activa de <paramref name="db"/> que el asignador
/// hermano (<c>Database.SqlQuery&lt;T&gt;()</c>/<c>FromSqlRaw&lt;T&gt;()</c> prohibidos, hallazgo
/// stage-1-slice-2) — <c>WaysDbContext.RechazarEscriturasDeNumeracionFiscal</c> es el guard que
/// hace que <c>SaveChangesAsync</c> nunca pueda escribir esta tabla por accidente.
///
/// Implementa las guarded-UPDATEs U1 (<see cref="AsignarSiguienteAsync"/>) y U3
/// (<see cref="ReconciliarAsync"/>) enumeradas en tasks.md Slice 4 (mutation-proof-tests regla 3
/// v1.1) — U1 conjuncts (a) <c>id_punto_venta</c> (b) <c>codigo_afip</c>; U3 mismos dos conjuncts.
/// </summary>
public static class AsignadorDeNumeroFiscal
{
    public static async Task AsegurarContadorAsync(
        IWaysDbContext db, int idTenant, int idPuntoVenta, short codigoAfip, CancellationToken ct = default)
    {
        var conexion = await ObtenerConexionAbiertaAsync(db, ct);

        await using var comando = conexion.CreateCommand();
        comando.Transaction = db.Database.CurrentTransaction?.GetDbTransaction();
        comando.CommandText =
            "INSERT INTO numeraciones_fiscales (id_punto_venta, codigo_afip, id_tenant, proximo_numero) " +
            "VALUES ($1, $2, $3, 1) " +
            "ON CONFLICT (id_punto_venta, codigo_afip) DO NOTHING";

        ParametrosDeComando.Agregar(comando, idPuntoVenta);
        ParametrosDeComando.Agregar(comando, codigoAfip);
        ParametrosDeComando.Agregar(comando, idTenant);

        await comando.ExecuteNonQueryAsync(ct);
    }

    /// <summary>U1: <c>UPDATE numeraciones_fiscales SET proximo_numero = proximo_numero + 1 WHERE
    /// id_punto_venta = $1 AND codigo_afip = $2</c>. El row lock que este <c>UPDATE</c> toma es el
    /// que D1 fija en la posición 0 del orden total de locks — la emisión fiscal (slice 5) lo
    /// sostiene hasta su <c>COMMIT</c>, round trip a WSFE incluido.</summary>
    public static async Task<long> AsignarSiguienteAsync(
        IWaysDbContext db, int idPuntoVenta, short codigoAfip, CancellationToken ct = default)
    {
        var conexion = await ObtenerConexionAbiertaAsync(db, ct);

        await using var comando = conexion.CreateCommand();
        comando.Transaction = db.Database.CurrentTransaction?.GetDbTransaction();
        comando.CommandText =
            "UPDATE numeraciones_fiscales SET proximo_numero = proximo_numero + 1 " +
            "WHERE id_punto_venta = $1 AND codigo_afip = $2 RETURNING proximo_numero - 1";

        ParametrosDeComando.Agregar(comando, idPuntoVenta);
        ParametrosDeComando.Agregar(comando, codigoAfip);

        var resultado = await comando.ExecuteScalarAsync(ct)
            ?? throw new InvalidOperationException(
                $"No existe serie fiscal para el punto de venta {idPuntoVenta}, código AFIP " +
                $"{codigoAfip}: llamá a {nameof(AsegurarContadorAsync)} antes de asignar.");

        return Convert.ToInt64(resultado);
    }

    /// <summary>U3: <c>UPDATE numeraciones_fiscales SET ultimo_autorizado_arca = $, sincronizado_en
    /// = $ WHERE id_punto_venta = $ AND codigo_afip = $</c> — escribe SOLO estos dos campos,
    /// SIEMPRE juntos (CHECK 8 los exige juntos o ninguno), y NUNCA <c>proximo_numero</c> (D13):
    /// auto-sanar la serie sería un programa decidiendo, desatendido, si un documento legal existe
    /// o no. Si ARCA está adelante, un comprobante local está sin CAE y lo encuentra I2
    /// (<c>FECompConsultar</c>); si nosotros estamos adelante, un número quedó quemado y solo un
    /// operador lo libera (I1, 19c). El 409 <c>numeracion_fiscal_desincronizada</c> ante un 10016
    /// de WSFE ya se lanza en <c>ClienteWsfe</c> (slice 3, antes de llegar hasta acá) — este método
    /// es la escritura de reconciliación en sí, no el punto que decide si hay divergencia.</summary>
    public static async Task ReconciliarAsync(
        IWaysDbContext db,
        int idPuntoVenta,
        short codigoAfip,
        long ultimoAutorizadoArca,
        IRelojDelSistema reloj,
        CancellationToken ct = default)
    {
        var conexion = await ObtenerConexionAbiertaAsync(db, ct);

        await using var comando = conexion.CreateCommand();
        comando.Transaction = db.Database.CurrentTransaction?.GetDbTransaction();
        comando.CommandText =
            "UPDATE numeraciones_fiscales SET ultimo_autorizado_arca = $1, sincronizado_en = $2 " +
            "WHERE id_punto_venta = $3 AND codigo_afip = $4";

        ParametrosDeComando.Agregar(comando, ultimoAutorizadoArca);
        ParametrosDeComando.Agregar(comando, reloj.Ahora);
        ParametrosDeComando.Agregar(comando, idPuntoVenta);
        ParametrosDeComando.Agregar(comando, codigoAfip);

        await comando.ExecuteNonQueryAsync(ct);
    }

    private static async Task<DbConnection> ObtenerConexionAbiertaAsync(IWaysDbContext db, CancellationToken ct)
    {
        var conexion = db.Database.GetDbConnection();

        if (conexion.State != ConnectionState.Open)
        {
            await db.Database.OpenConnectionAsync(ct);
        }

        return conexion;
    }
}
