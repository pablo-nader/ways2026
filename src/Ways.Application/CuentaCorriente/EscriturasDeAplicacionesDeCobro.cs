using System.Data.Common;
using Ways.Application.Abstracciones;

namespace Ways.Application.CuentaCorriente;

/// <summary>Una aplicación liberada, con el punto de venta del recibo para su fila de auditoría.</summary>
public sealed record AplicacionDeCobroLiberada(
    int IdAplicacion, int IdComprobanteCobro, int IdComprobanteVenta, decimal Importe, int IdPuntoVentaDelRecibo);

/// <summary>
/// Escrituras crudas de <c>aplicaciones_cobro</c> que corren en la transacción de otro escritor (E7-S2). Hoy la única es la
/// liberación por anulación: <c>ServicioDeVentas.EjecutarAnulacionAsync</c> la llama con la fila del comprobante ya
/// bloqueada por su UPDATE de estado.
/// </summary>
public static class EscriturasDeAplicacionesDeCobro
{
    /// <summary>
    /// Da de baja (lógica) toda aplicación viva en la que el comprobante es el recibo o la venta, y devuelve las liberadas.
    /// Sin esto el trigger diferido <c>tg_comprobantes_venta_admiten_aplicaciones_cobro</c> rechaza la anulación al
    /// comitear. Dos anulaciones concurrentes que comparten una aplicación (la del recibo y la de su venta) se serializan
    /// en el lock de esa fila: la segunda, al retomarlo, re-evalúa <c>deleted_at IS NULL</c> y no la vuelve a liberar.
    /// El recibo nunca es un espejo fiscal: el predicado del join solo lo hace explícito.
    /// </summary>
    public static async Task<IReadOnlyList<AplicacionDeCobroLiberada>> LiberarDelComprobanteAsync(
        DbConnection conexion, DbTransaction? transaccion, int idTenant, int idComprobante, DateTimeOffset momento,
        CancellationToken ct)
    {
        await using var comando = conexion.CreateCommand();
        comando.Transaction = transaccion;
        comando.CommandText =
            "UPDATE aplicaciones_cobro a SET deleted_at = $1, updated_at = $1 " +
            "FROM comprobantes_venta r " +
            "WHERE r.id_comprobante_venta = a.id_comprobante_cobro AND r.id_tenant = a.id_tenant " +
            "AND r.id_venta_origen IS NULL " +
            "AND (a.id_comprobante_cobro = $2 OR a.id_comprobante_venta = $2) AND a.id_tenant = $3 " +
            "AND a.deleted_at IS NULL " +
            "RETURNING a.id_aplicacion_cobro, a.id_comprobante_cobro, a.id_comprobante_venta, a.importe, r.id_punto_venta";
        ParametrosDeComando.Agregar(comando, momento);
        ParametrosDeComando.Agregar(comando, idComprobante);
        ParametrosDeComando.Agregar(comando, idTenant);

        var liberadas = new List<AplicacionDeCobroLiberada>();
        await using var lector = await comando.ExecuteReaderAsync(ct);
        while (await lector.ReadAsync(ct))
        {
            liberadas.Add(new AplicacionDeCobroLiberada(
                lector.GetInt32(0), lector.GetInt32(1), lector.GetInt32(2), lector.GetDecimal(3), lector.GetInt32(4)));
        }

        return liberadas.OrderBy(l => l.IdAplicacion).ToList();
    }
}
