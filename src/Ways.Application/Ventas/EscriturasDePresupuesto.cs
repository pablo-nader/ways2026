using System.Data.Common;
using Ways.Application.Abstracciones;
using Ways.Domain.Common;

namespace Ways.Application.Ventas;

/// <summary>
/// stage-17-presupuestos-y-remitos, Slice 3 (design: Interfaces/Contracts — "Application — the
/// two containment classes"; decisiones 5/6). Copia estructural de
/// <see cref="Compras.EscriturasDeOrdenDeCompra"/>: <c>static</c>, misma postura de
/// conexión/transacción del llamador, nunca abre/flushea/comitea nada. La ÚNICA clase que escribe
/// <c>presupuestos.estado = 'convertido'</c> — llamada DESDE la transacción de
/// <see cref="ServicioDeVentas"/>, en la POSICIÓN 1.5 (entre el turno y el INSERT del
/// comprobante), jamás desde <see cref="ServicioDePresupuestos"/> (la contención ES el producto —
/// un DI seam ahí solo invitaría a la segunda implementación que esta clase existe para impedir).
///
/// <see cref="MarcarConvertidoAsync"/> es UN solo statement con CUATRO conjuntos (design decisión
/// 5): <c>estado='enviado'</c>, <c>vencimiento >= $hoy</c>, <c>id_punto_venta = $pv</c>, más
/// tenant/id — todos client-reachable (<c>idPresupuestoOrigen</c>/<c>idPuntoVenta</c> viajan
/// independientes en el mismo body), así que ninguno puede quedar afuera del statement atómico
/// como un pre-chequeo únicamente. 0 filas ⇒ el llamador reclasifica bajo <c>FOR UPDATE</c> con
/// <see cref="ExigirCausaDelRechazoAsync"/>.
/// </summary>
public static class EscriturasDePresupuesto
{
    /// <summary>UN solo statement, CUATRO conjuntos (design decisión 5, mutation targets 31-33).
    /// <c>hoyEnZonaDelPuntoVenta</c> llega YA resuelto en la zona del punto de venta (decisión
    /// 10) — esta clase no conoce relojes ni zonas. Devuelve <c>false</c> en 0 filas; el llamador
    /// decide si reclasifica bajo lock.</summary>
    public static async Task<bool> MarcarConvertidoAsync(
        DbConnection conexion, DbTransaction? transaccion, int idTenant, int idPresupuesto,
        int idPuntoVenta, DateOnly hoyEnZonaDelPuntoVenta, DateTimeOffset momento, CancellationToken ct)
    {
        await using var comando = conexion.CreateCommand();
        comando.Transaction = transaccion;
        comando.CommandText =
            "UPDATE presupuestos SET estado = 'convertido'::estado_presupuesto, updated_at = $5 " +
            "WHERE id_presupuesto = $1 AND id_tenant = $2 AND id_punto_venta = $3 " +
            "AND estado = 'enviado'::estado_presupuesto AND vencimiento >= $4 AND deleted_at IS NULL " +
            "RETURNING id_presupuesto";

        ParametrosDeComando.Agregar(comando, idPresupuesto);
        ParametrosDeComando.Agregar(comando, idTenant);
        ParametrosDeComando.Agregar(comando, idPuntoVenta);
        ParametrosDeComando.Agregar(comando, hoyEnZonaDelPuntoVenta);
        ParametrosDeComando.Agregar(comando, momento);

        var resultado = await comando.ExecuteScalarAsync(ct);
        return resultado is not null;
    }

    /// <summary>Reclasificación bajo <c>FOR UPDATE</c> (design decisión 5): traduce el 0-filas de
    /// <see cref="MarcarConvertidoAsync"/> en la causa PRECISA, en el mismo orden que el
    /// doc-comment de la clase enumera. <c>convertido</c> se distingue de
    /// <c>borrador</c>/<c>anulado</c> a propósito (409 <c>presupuesto_ya_convertido</c> es más
    /// informativo que el genérico <c>presupuesto_no_convertible</c>) — mismo criterio de
    /// prioridad que la rama del snapshot de <see cref="ServicioDeVentas"/> usa como
    /// pre-chequeo.</summary>
    public static async Task ExigirCausaDelRechazoAsync(
        DbConnection conexion, DbTransaction? transaccion, int idTenant, int idPresupuesto,
        int idPuntoVenta, DateOnly hoyEnZonaDelPuntoVenta, CancellationToken ct)
    {
        await using var comando = conexion.CreateCommand();
        comando.Transaction = transaccion;
        comando.CommandText =
            "SELECT estado::text, vencimiento, id_punto_venta " +
            "FROM presupuestos WHERE id_presupuesto = $1 AND id_tenant = $2 AND deleted_at IS NULL " +
            "FOR UPDATE";

        ParametrosDeComando.Agregar(comando, idPresupuesto);
        ParametrosDeComando.Agregar(comando, idTenant);

        await using var lector = await comando.ExecuteReaderAsync(ct);
        if (!await lector.ReadAsync(ct))
        {
            throw ErrorDominio.NoEncontrado($"No existe el presupuesto {idPresupuesto}.");
        }

        var estado = lector.GetString(0);
        var vencimiento = lector.IsDBNull(1) ? (DateOnly?)null : lector.GetFieldValue<DateOnly>(1);
        var idPuntoVentaReal = lector.GetInt32(2);

        if (estado == "convertido")
        {
            throw new ErrorDominio(
                "presupuesto_ya_convertido", "El presupuesto ya fue convertido en una venta.", 409);
        }

        if (estado != "enviado")
        {
            throw new ErrorDominio(
                "presupuesto_no_convertible", "El presupuesto no está en un estado convertible.", 409);
        }

        if (vencimiento is null || vencimiento < hoyEnZonaDelPuntoVenta)
        {
            throw new ErrorDominio("presupuesto_vencido", "El presupuesto está vencido.", 409);
        }

        if (idPuntoVentaReal != idPuntoVenta)
        {
            throw new ErrorDominio(
                "punto_venta_no_coincide", "El presupuesto pertenece a otro punto de venta.", 400);
        }

        // Defensa en profundidad: bajo el MISMO lock que el UPDATE guardado ya evaluó, cada
        // conjunto individual pasó pero la fila igual no matcheó — invariante roto, nunca un caso
        // de negocio alcanzable (el lock FOR UPDATE de arriba ya serializa cualquier escritor
        // concurrente).
        throw new InvalidOperationException(
            $"El presupuesto {idPresupuesto} pasó todos los chequeos individuales pero el UPDATE " +
            "guardado no afectó ninguna fila bajo el lock ya tomado.");
    }
}
