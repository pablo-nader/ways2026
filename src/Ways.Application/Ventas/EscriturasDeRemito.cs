using System.Data.Common;
using Ways.Application.Abstracciones;

namespace Ways.Application.Ventas;

/// <summary>
/// stage-17-presupuestos-y-remitos, Slice 6 (design.md:131-149/162-175, tasks 6.1/6.4/6.8/6.11-6.12,
/// mutation targets 48-50/57). La ÚNICA clase que liga y desliga remitos de un comprobante — misma
/// forma estructural que <see cref="EscriturasDePresupuesto"/>: <c>static</c>, misma postura de
/// conexión/transacción del llamador, nunca abre/flushea/comitea nada.
///
/// <see cref="BloquearAscendenteAsync"/> corre en la POSICIÓN 1 de <see cref="ServicioDeFacturacionDeRemitos"/>
/// (design decisión 12, mutation target 49) — ANTES del INSERT del comprobante y ANTES de
/// <c>clientes</c>: un <c>INSERT</c> de fila nueva NO es una posición del orden de locks (T10), así
/// que este es el único lock EXISTENTE que la consolidación toma. Su rol es doble: (a) tomar el lock
/// en orden ASCENDENTE por <c>id_remito</c> (mutation target 48 — sin esto, dos consolidaciones
/// concurrentes sobre sets superpuestos podrían deadlockear); (b) establecer el orden total contra
/// un <c>anular</c> de remito concurrente (la rendezvous de la tarea 6.15). NO es, por sí sola, la
/// autoridad de negocio de "¿este set sigue facturable?" — esa autoridad, la única que este archivo
/// deja atómica bajo el MISMO lock que la escritura final, es <see cref="LigarAsync"/> (mutation
/// target 50): re-validar acá con los valores leídos crearía un guard EQUIVALENTE al de
/// <see cref="LigarAsync"/> bajo el mismo lock (nada puede cambiar esas filas entre este SELECT y
/// ese UPDATE dentro de la MISMA transacción) — un mutante que borre el guard de
/// <see cref="LigarAsync"/> sobreviviría indetectado si este método también lo re-implementara
/// (mutation-proof-tests regla 3, la clase "pre-check que espeja un guard transaccional" — acá el
/// espejo sería DENTRO de la misma transacción, mismo lock, así que ninguna prueba de carrera podría
/// discriminar cuál de los dos guards mató al mutante). El único chequeo que sí corre acá es
/// defensivo (invariante de fila, nunca un caso de negocio alcanzable): si el conteo de filas
/// bloqueadas no matchea <c>idsRemito.Count</c>, algo desapareció bajo el lock — el llamador lo trata
/// como un invariante roto, no como un 409 de negocio (ese guard "mismo cliente/PV/tenant, todos
/// <c>emitido</c> y sin ligar, antes de cualquier escritura" vive en la fase de decisión de
/// <see cref="ServicioDeFacturacionDeRemitos"/>, task 6.16 — SIN lock, así que sí puede quedar
/// obsoleto ante una carrera real, que <see cref="LigarAsync"/> atrapa).
/// </summary>
public static class EscriturasDeRemito
{
    /// <summary>Lock ascendente explícito (design decisión 12, mutation target 48) — <c>ORDER BY
    /// id_remito</c> en el propio <c>SELECT ... FOR UPDATE</c>, nunca ordenado por el llamador: es
    /// Postgres quien toma los locks de fila en el orden que la cláusula <c>ORDER BY</c> evalúa,
    /// sin importar el orden de <paramref name="idsRemito"/> en el array de entrada. Devuelve los
    /// estados leídos bajo el lock (diagnóstico/futuro uso del llamador) — la autoridad de negocio
    /// vive en <see cref="LigarAsync"/>, ver el doc-comment de la clase.</summary>
    public static async Task<IReadOnlyList<(int IdRemito, string Estado, int? IdComprobante)>> BloquearAscendenteAsync(
        DbConnection conexion, DbTransaction? transaccion, int idTenant, IReadOnlyList<int> idsRemito,
        CancellationToken ct)
    {
        await using var comando = conexion.CreateCommand();
        comando.Transaction = transaccion;
        comando.CommandText =
            "SELECT id_remito, estado::text, id_comprobante_venta FROM remitos " +
            "WHERE id_remito = ANY($1) AND id_tenant = $2 AND deleted_at IS NULL " +
            "ORDER BY id_remito FOR UPDATE";

        ParametrosDeComando.Agregar(comando, idsRemito.ToArray());
        ParametrosDeComando.Agregar(comando, idTenant);

        var resultado = new List<(int, string, int?)>(idsRemito.Count);
        await using var lector = await comando.ExecuteReaderAsync(ct);
        while (await lector.ReadAsync(ct))
        {
            var idRemito = lector.GetInt32(0);
            var estado = lector.GetString(1);
            var idComprobante = lector.IsDBNull(2) ? (int?)null : lector.GetInt32(2);
            resultado.Add((idRemito, estado, idComprobante));
        }

        return resultado;
    }

    /// <summary>UPDATE guardado de N filas EN UN statement (design decisión 12, mutation target
    /// 50) — la autoridad final de "¿este set sigue facturable?", bajo el MISMO lock ascendente que
    /// <see cref="BloquearAscendenteAsync"/> ya tomó sobre estas filas EN ESTA transacción.
    /// <c>estado = 'emitido' AND id_comprobante_venta IS NULL</c> son los dos conjuntos guardados
    /// (además de tenant/ids/soft-delete) — filas devueltas != <c>idsRemito.Count</c> ⇒ el llamador
    /// lanza <c>409 remito_no_facturable</c> (otro consolidado ganó la carrera, o alguien anuló uno
    /// de los remitos — CONFLICT #4).</summary>
    public static async Task<int> LigarAsync(
        DbConnection conexion, DbTransaction? transaccion, int idTenant, IReadOnlyList<int> idsRemito,
        int idComprobanteVenta, DateTimeOffset momento, CancellationToken ct)
    {
        await using var comando = conexion.CreateCommand();
        comando.Transaction = transaccion;
        comando.CommandText =
            "UPDATE remitos SET estado = 'facturado'::estado_remito, id_comprobante_venta = $3, updated_at = $4 " +
            "WHERE id_remito = ANY($1) AND id_tenant = $2 " +
            "AND estado = 'emitido'::estado_remito AND id_comprobante_venta IS NULL AND deleted_at IS NULL";

        ParametrosDeComando.Agregar(comando, idsRemito.ToArray());
        ParametrosDeComando.Agregar(comando, idTenant);
        ParametrosDeComando.Agregar(comando, idComprobanteVenta);
        ParametrosDeComando.Agregar(comando, momento);

        return await comando.ExecuteNonQueryAsync(ct);
    }

    /// <summary>Desligue de la anulación de un <c>TXR</c> (design.md:170-174, task 6.12, mutation
    /// target 57): <c>estado</c> y <c>id_comprobante_venta</c> vuelven JUNTOS, en el MISMO statement
    /// — separarlos en dos <c>UPDATE</c>s dejaría una ventana donde <c>ck_remitos_facturacion</c>
    /// (estado y link tienen que viajar juntos, en las dos direcciones) podría ver una fila a medio
    /// actualizar bajo una lectura concurrente sin lock, o directamente tirar <c>23514</c> si el
    /// segundo statement nunca corre (fallo a mitad de camino). <c>WHERE id_comprobante_venta = $1
    /// AND ... AND estado = 'facturado'</c> es idempotente por construcción: una segunda invocación
    /// sobre el mismo comprobante ya desligado no matchea ninguna fila (0 filas, no un error) — el
    /// llamador (<c>EjecutarAnulacionAsync</c>) solo llega acá una vez, guardado por
    /// <c>MarcarAnuladoAsync</c>'s propio <c>WHERE estado = 'emitido'</c>.</summary>
    public static async Task<int> DesligarAsync(
        DbConnection conexion, DbTransaction? transaccion, int idTenant, int idComprobanteVenta,
        DateTimeOffset momento, CancellationToken ct)
    {
        await using var comando = conexion.CreateCommand();
        comando.Transaction = transaccion;
        comando.CommandText =
            "UPDATE remitos SET estado = 'emitido'::estado_remito, id_comprobante_venta = NULL, updated_at = $3 " +
            "WHERE id_comprobante_venta = $1 AND id_tenant = $2 AND estado = 'facturado'::estado_remito";

        ParametrosDeComando.Agregar(comando, idComprobanteVenta);
        ParametrosDeComando.Agregar(comando, idTenant);
        ParametrosDeComando.Agregar(comando, momento);

        return await comando.ExecuteNonQueryAsync(ct);
    }
}
