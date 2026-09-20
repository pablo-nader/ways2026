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
/// SET proximo_numero = proximo_numero + $1 ... RETURNING</c> (<see cref="AsignarBloqueAsync"/> —
/// <c>$1</c> es 1 para <see cref="AsignarSiguienteAsync"/>, la cantidad del bloque para
/// <see cref="ReservarBloqueAsync"/>, stage-pos-reserva-de-numeracion), vía ADO.NET crudo sobre la
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

    public static Task<long> AsignarSiguienteAsync(
        IWaysDbContext db, int idPuntoVenta, string tipoComprobante, CancellationToken ct = default) =>
        AsignarBloqueAsync(db, idPuntoVenta, tipoComprobante, cantidad: 1, ct);

    /// <summary>stage-pos-reserva-de-numeracion (DB CHANGE GATE aprobado): generalización de
    /// <see cref="AsignarSiguienteAsync"/> — <c>+ $1</c> en vez de <c>+ 1</c>, mismo statement,
    /// devuelve el PRIMER número del bloque (<c>[resultado, resultado + cantidad - 1]</c>). La
    /// fila la sigue teniendo que existir (<see cref="AsegurarContadorAsync"/> antes), y el mismo
    /// row lock del <c>UPDATE ... RETURNING</c> serializa esto contra cualquier otra asignación
    /// (de a uno o en bloque) sobre el mismo <c>(idPuntoVenta, tipoComprobante)</c>.</summary>
    public static async Task<long> AsignarBloqueAsync(
        IWaysDbContext db, int idPuntoVenta, string tipoComprobante, int cantidad, CancellationToken ct = default)
    {
        var conexion = await ObtenerConexionAbiertaAsync(db, ct);

        await using var comando = conexion.CreateCommand();
        comando.Transaction = db.Database.CurrentTransaction?.GetDbTransaction();
        comando.CommandText =
            "UPDATE numeraciones_comprobante SET proximo_numero = proximo_numero + $1 " +
            "WHERE id_punto_venta = $2 AND tipo_comprobante = $3 RETURNING proximo_numero - $1";

        ParametrosDeComando.Agregar(comando, cantidad);
        ParametrosDeComando.Agregar(comando, idPuntoVenta);
        ParametrosDeComando.Agregar(comando, tipoComprobante);

        var resultado = await comando.ExecuteScalarAsync(ct)
            ?? throw new InvalidOperationException(
                $"No existe contador de numeraciones para el punto de venta {idPuntoVenta}, " +
                $"tipo {tipoComprobante}: llamá a {nameof(AsegurarContadorAsync)} antes de asignar.");

        return Convert.ToInt64(resultado);
    }

    /// <summary>stage-pos-reserva-de-numeracion (DB CHANGE GATE aprobado): reserva un bloque de
    /// <paramref name="cantidad"/> números para que <paramref name="idDispositivo"/> los reparta
    /// offline (spec: el POS de escritorio no puede tocar el contador sin conexión, así que pide
    /// un bloque por adelantado y lo consume localmente). Propia transacción chica, comprometida
    /// ANTES de que el llamador haga nada más — mismo criterio que <see cref="AsignarComprometidoAsync"/>,
    /// cuyo doc-comment de clase explica por qué (Failure Semantics).
    ///
    /// El PRIMER paso de la transacción — no un chequeo aparte del llamador — es abandonar
    /// cualquier bloque vivo de ESTE dispositivo para esta serie, INCONDICIONALMENTE: el motivo
    /// típico (spec, punto 2) es que el dispositivo perdió su almacenamiento local y ya no sabe
    /// cuánto de ese bloque llegó a imprimir, pero el mecanismo no distingue esa causa de un
    /// simple reenvío de red del mismo pedido — abandona igual en cualquier caso, porque el
    /// resultado correcto (nunca reemitir un número) es el mismo. Este servicio conoce qué
    /// números LLEGARON (comprobantes_venta), pero no cuáles se imprimieron y se perdieron con la
    /// cola local — adivinar el punto de corte arriesga reemitir un número que ya está en el
    /// bolsillo de un cliente. Por eso el bloque anterior se abandona ENTERO, aunque le queden
    /// números sin usar: un hueco más es aceptable (ya lo son los que deja un rollback/retry de
    /// la numeración de a uno, ver <see cref="AsignarBloqueAsync"/>), un número duplicado no lo es
    /// (spec, comentario de <c>ServicioDeVentas.cs:342-349</c>).
    ///
    /// judgment-day (CRITICAL, ronda 1 — riesgo cerrado, ya no es un supuesto abierto):
    /// <c>abandonada_at</c> solo gobierna de qué bloque este dispositivo puede sacar números
    /// NUEVOS de acá en adelante — nunca gobierna qué números acepta el SERVIDOR después.
    /// <c>ServicioDeVentas.ExigirNumeroPreasignadoPropioAsync</c> ya no exige
    /// <c>AbandonadaAt IS NULL</c>: un número que en verdad cayó dentro de ALGUNA reserva de este
    /// dispositivo/punto de venta/tipo sigue siendo suyo aunque ese bloque ya no esté vigente, así
    /// que una venta encolada offline que sincroniza tarde (después de que el dispositivo pidió un
    /// bloque nuevo) ya no se rechaza con 409 solo por eso. El doble uso lo sigue previniendo
    /// <c>ux_comprobantes_venta_numero</c> más la guarda de idempotencia de
    /// <c>BuscarPorNumeroComprometidoAsync</c>, no este campo.
    ///
    /// Ese abandono ANTES del INSERT (misma transacción) es también lo que hace inofensivo un
    /// reintento sobre un commit ambiguo: si el intento anterior en verdad comiteó, el reintento
    /// abandona ESE bloque recién comiteado (nunca lo deja vivo dos veces) y reparte uno nuevo —
    /// el llamador siempre recibe el rango que quedó realmente vigente al final, nunca uno
    /// stale.</summary>
    public static async Task<(long Desde, long Hasta)> ReservarBloqueAsync(
        IWaysDbContext db, int idTenant, int idPuntoVenta, string tipoComprobante, int idDispositivo,
        int cantidad, DateTimeOffset momento, CancellationToken ct = default)
    {
        await using var transaccion = await db.Database.BeginTransactionAsync(ct);

        await AbandonarReservaVivaAsync(db, idTenant, idPuntoVenta, tipoComprobante, idDispositivo, momento, ct);

        await AsegurarContadorAsync(db, idTenant, idPuntoVenta, tipoComprobante, ct);
        var desde = await AsignarBloqueAsync(db, idPuntoVenta, tipoComprobante, cantidad, ct);
        var hasta = desde + cantidad - 1;

        await InsertarReservaAsync(db, idTenant, idPuntoVenta, tipoComprobante, idDispositivo, desde, hasta, momento, ct);

        await transaccion.CommitAsync(ct);
        return (desde, hasta);
    }

    private static async Task AbandonarReservaVivaAsync(
        IWaysDbContext db, int idTenant, int idPuntoVenta, string tipoComprobante, int idDispositivo,
        DateTimeOffset momento, CancellationToken ct)
    {
        var conexion = await ObtenerConexionAbiertaAsync(db, ct);

        await using var comando = conexion.CreateCommand();
        comando.Transaction = db.Database.CurrentTransaction?.GetDbTransaction();
        comando.CommandText =
            "UPDATE reservas_numeracion SET abandonada_at = $1, updated_at = $1 " +
            "WHERE id_tenant = $2 AND id_punto_venta = $3 AND tipo_comprobante = $4 AND id_dispositivo = $5 " +
            "AND abandonada_at IS NULL";

        ParametrosDeComando.Agregar(comando, momento);
        ParametrosDeComando.Agregar(comando, idTenant);
        ParametrosDeComando.Agregar(comando, idPuntoVenta);
        ParametrosDeComando.Agregar(comando, tipoComprobante);
        ParametrosDeComando.Agregar(comando, idDispositivo);

        await comando.ExecuteNonQueryAsync(ct);
    }

    private static async Task InsertarReservaAsync(
        IWaysDbContext db, int idTenant, int idPuntoVenta, string tipoComprobante, int idDispositivo,
        long desde, long hasta, DateTimeOffset momento, CancellationToken ct)
    {
        var conexion = await ObtenerConexionAbiertaAsync(db, ct);

        await using var comando = conexion.CreateCommand();
        comando.Transaction = db.Database.CurrentTransaction?.GetDbTransaction();
        comando.CommandText =
            "INSERT INTO reservas_numeracion " +
            "(id_tenant, id_punto_venta, tipo_comprobante, id_dispositivo, desde, hasta, created_at, updated_at) " +
            "VALUES ($1, $2, $3, $4, $5, $6, $7, $7)";

        ParametrosDeComando.Agregar(comando, idTenant);
        ParametrosDeComando.Agregar(comando, idPuntoVenta);
        ParametrosDeComando.Agregar(comando, tipoComprobante);
        ParametrosDeComando.Agregar(comando, idDispositivo);
        ParametrosDeComando.Agregar(comando, desde);
        ParametrosDeComando.Agregar(comando, hasta);
        ParametrosDeComando.Agregar(comando, momento);

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
