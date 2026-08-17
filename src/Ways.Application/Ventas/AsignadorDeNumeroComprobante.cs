using System.Data;
using System.Data.Common;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using Ways.Application.Abstracciones;

namespace Ways.Application.Ventas;

/// <summary>
/// Asigna <c>comprobantes_venta.numero</c> de forma atómica por punto de venta + tipo de
/// comprobante (design decisiones 8 y 9, clon de
/// <see cref="Clientes.AsignadorDeNumeroCliente"/>): <c>INSERT ... ON CONFLICT DO NOTHING</c>
/// (creación perezosa de la fila, sin backfill) seguido de <c>UPDATE numeraciones_comprobante
/// SET proximo_numero = proximo_numero + 1 ... RETURNING</c>, vía ADO.NET crudo sobre la
/// conexión/transacción activa de <paramref name="db"/> — nunca <c>Database.SqlQuery&lt;T&gt;()</c>/
/// <c>FromSqlRaw&lt;T&gt;()</c> (mismo hallazgo de stage-1-slice-2 que documentan
/// <see cref="Clientes.AsignadorDeNumeroCliente"/>/<see cref="Articulos.AsignadorDeCodigoInternoArticulo"/>).
///
/// A diferencia de <see cref="Clientes.AsignadorDeNumeroCliente"/> (PK = <c>id_tenant</c> solo),
/// acá la fila la identifica <c>(id_punto_venta, tipo_comprobante)</c> — <c>id_tenant</c> viaja
/// aparte, solo para el INSERT inicial (RLS <c>WITH CHECK</c>) y la columna de diagnóstico; no
/// participa del <c>WHERE</c> del UPDATE porque la PK ya es global (design decisión 8).
///
/// <c>proximo_numero</c> es <c>bigint</c> (doc 10): el contador se expone como <see cref="long"/>,
/// nunca <see cref="int"/> — a diferencia de <c>clientes.numero</c>/<c>articulos.codigo_interno</c>.
///
/// Estática a propósito: sin estado propio, cada método recibe el <see cref="IWaysDbContext"/>
/// del llamador de turno (mismo criterio que los dos asignadores hermanos). Llamado desde
/// <see cref="AsignarComprometidoAsync"/>, en su PROPIA transacción chica, comprometida ANTES de
/// la transacción que escribe el resto de la venta (design: Failure Semantics — corrección de
/// esta slice para que "el número se consume aunque falle el resto" sea literal, ver el
/// doc-comment de <c>ServicioDeVentas.EmitirAsync</c>), no dentro de ella.
/// </summary>
public static class AsignadorDeNumeroComprobante
{
    /// <summary>stage-7-cuenta-corriente (Slice 2, task 2.4, design decisión 7 — "numeración
    /// untouched"): promovido de método privado de <c>ServicioDeVentas</c> a acá — pure move, sin
    /// cambio de mecanismo. Abre y comitea su PROPIA transacción chica (ver el doc-comment de la
    /// clase); el llamador la envuelve en su propio <c>CreateExecutionStrategy().ExecuteAsync</c>
    /// (EnableRetryOnFailure exige que <c>BeginTransactionAsync</c> viva dentro de esa lambda).
    /// Reusado tal cual por <c>ServicioDeVentas.EmitirAsync</c> (TX/NCX) y
    /// <c>ServicioDeCuentaCorriente.RegistrarPagoAsync</c> (RC) — <c>numeraciones_comprobante</c>
    /// ya está keyed por <c>(id_punto_venta, tipo_comprobante)</c>, así que RC obtiene su propia
    /// serie sin ningún mecanismo nuevo.</summary>
    public static async Task<long> AsignarComprometidoAsync(
        IWaysDbContext db, int idTenant, int idPuntoVenta, string codigoTipoComprobante, CancellationToken ct = default)
    {
        await using var transaccion = await db.Database.BeginTransactionAsync(ct);

        await AsegurarContadorAsync(db, idTenant, idPuntoVenta, codigoTipoComprobante, ct);
        var numero = await AsignarSiguienteAsync(db, idPuntoVenta, codigoTipoComprobante, ct);

        await transaccion.CommitAsync(ct);
        return numero;
    }

    public static async Task AsegurarContadorAsync(
        IWaysDbContext db, int idTenant, int idPuntoVenta, string tipoComprobante, CancellationToken ct = default)
    {
        var conexion = await ObtenerConexionAbiertaAsync(db, ct);

        await using var comando = conexion.CreateCommand();
        comando.Transaction = db.Database.CurrentTransaction?.GetDbTransaction();
        comando.CommandText =
            "INSERT INTO numeraciones_comprobante (id_tenant, id_punto_venta, tipo_comprobante, proximo_numero) " +
            "VALUES ($1, $2, $3, 1) " +
            "ON CONFLICT (id_punto_venta, tipo_comprobante) DO NOTHING";

        ParametrosDeComando.Agregar(comando, idTenant);
        ParametrosDeComando.Agregar(comando, idPuntoVenta);
        ParametrosDeComando.Agregar(comando, tipoComprobante);

        await comando.ExecuteNonQueryAsync(ct);
    }

    public static async Task<long> AsignarSiguienteAsync(
        IWaysDbContext db, int idPuntoVenta, string tipoComprobante, CancellationToken ct = default)
    {
        var conexion = await ObtenerConexionAbiertaAsync(db, ct);

        await using var comando = conexion.CreateCommand();
        comando.Transaction = db.Database.CurrentTransaction?.GetDbTransaction();
        comando.CommandText =
            "UPDATE numeraciones_comprobante SET proximo_numero = proximo_numero + 1 " +
            "WHERE id_punto_venta = $1 AND tipo_comprobante = $2 RETURNING proximo_numero - 1";

        ParametrosDeComando.Agregar(comando, idPuntoVenta);
        ParametrosDeComando.Agregar(comando, tipoComprobante);

        var resultado = await comando.ExecuteScalarAsync(ct)
            ?? throw new InvalidOperationException(
                $"No existe contador de numeraciones para el punto de venta {idPuntoVenta}, " +
                $"tipo {tipoComprobante}: llamá a {nameof(AsegurarContadorAsync)} antes de asignar.");

        return Convert.ToInt64(resultado);
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
