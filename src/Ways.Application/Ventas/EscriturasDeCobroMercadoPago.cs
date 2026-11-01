using System.Data.Common;
using Ways.Application.Abstracciones;
using Ways.Domain.Common;
using Ways.Domain.MercadoPago;

namespace Ways.Application.Ventas;

/// <summary>
/// La ÚNICA clase que lee, bloquea y liga los cobros de Mercado Pago de una venta (E3-T2). Misma postura que
/// <see cref="EscriturasDeRemito"/>: <c>static</c>, con la conexión y la transacción del llamador, y nunca abre ni comitea nada.
///
/// <b>Locks.</b> <see cref="ReservarYValidarAsync"/> toma <c>FOR UPDATE</c> de las filas de <c>cobros_mercadopago</c> en orden
/// ascendente de id, después del turno y antes del comprobante, y las lee una sola vez bajo ese lock (<c>single-read-under-lock</c>:
/// ninguna entidad de EF las toca). <c>ServicioDeCobrosMercadoPago</c> no espera locks de ventas: su orden es advisory del punto de
/// venta → advisory del cobro → fila del cobro, y un cobro aprobado ya no lo modifica, así que la venta no puede cerrar un ciclo con él.
/// </summary>
internal static class EscriturasDeCobroMercadoPago
{
    /// <summary>Lo que la venta toma de un cobro validado: el id del pago de Mercado Pago, que queda como referencia del pago.</summary>
    internal readonly record struct CobroReservado(int IdCobro, string IdPagoMercadoPago);

    /// <summary>Bloquea los cobros pedidos, los lee y exige que cada uno pueda pagar el pago que lo nombra. Un cobro que no existe en el
    /// tenant (o no es visible) es 400 <c>cobro_mercadopago_inexistente</c>.</summary>
    internal static async Task<IReadOnlyDictionary<int, CobroReservado>> ReservarYValidarAsync(
        DbConnection conexion, DbTransaction? transaccion, int idTenant, int idPuntoVenta, long numeroPreasignado,
        IReadOnlyList<(int IdCobro, int IdMedioPago, decimal Importe)> pedidos, CancellationToken ct)
    {
        var reservados = new Dictionary<int, CobroReservado>();

        foreach (var pedido in pedidos.OrderBy(p => p.IdCobro))
        {
            var cobro = await LeerBloqueadoAsync(conexion, transaccion, idTenant, pedido.IdCobro, ct)
                ?? throw new ErrorDominio(
                    CodigosDePagoConMercadoPago.Inexistente, $"No existe el cobro de Mercado Pago {pedido.IdCobro}.", 400);

            ReglaDeCobroEnVenta.ExigirCobroUtilizable(cobro, idPuntoVenta, numeroPreasignado, pedido.IdMedioPago, pedido.Importe);

            reservados[pedido.IdCobro] = new CobroReservado(
                pedido.IdCobro,
                cobro.IdPagoMercadoPago!); // ExigirCobroUtilizable ya rechazó (409 sin_pago) un cobro aprobado sin id de pago
        }

        return reservados;
    }

    /// <summary>Liga cada cobro a la venta ya insertada. El <c>WHERE</c> repite que siga sin venta: bajo el lock ya lo comprobó
    /// <see cref="ReservarYValidarAsync"/>, así que una fila que no cambia es un invariante roto y no un caso de negocio.</summary>
    internal static async Task LigarAsync(
        DbConnection conexion, DbTransaction? transaccion, int idTenant, int idComprobanteVenta, IEnumerable<int> idsCobro,
        DateTimeOffset momento, CancellationToken ct)
    {
        foreach (var idCobro in idsCobro.OrderBy(id => id))
        {
            await using var comando = conexion.CreateCommand();
            comando.Transaction = transaccion;
            comando.CommandText =
                "UPDATE cobros_mercadopago SET id_comprobante_venta = $1, updated_at = $2 " +
                "WHERE id_cobro_mercadopago = $3 AND id_tenant = $4 AND id_comprobante_venta IS NULL";
            ParametrosDeComando.Agregar(comando, idComprobanteVenta);
            ParametrosDeComando.Agregar(comando, momento);
            ParametrosDeComando.Agregar(comando, idCobro);
            ParametrosDeComando.Agregar(comando, idTenant);

            if (await comando.ExecuteNonQueryAsync(ct) != 1)
            {
                throw new InvalidOperationException(
                    $"El cobro de Mercado Pago {idCobro} no se pudo ligar a la venta {idComprobanteVenta}: dejó de estar libre bajo su lock.");
            }
        }
    }

    private static async Task<CobroParaVender?> LeerBloqueadoAsync(
        DbConnection conexion, DbTransaction? transaccion, int idTenant, int idCobro, CancellationToken ct)
    {
        await using var comando = conexion.CreateCommand();
        comando.Transaction = transaccion;
        comando.CommandText =
            "SELECT id_punto_venta, numero_preasignado, id_medio_pago, estado, importe_pagado, id_pago_mercadopago, id_comprobante_venta " +
            "FROM cobros_mercadopago WHERE id_cobro_mercadopago = $1 AND id_tenant = $2 FOR UPDATE";
        ParametrosDeComando.Agregar(comando, idCobro);
        ParametrosDeComando.Agregar(comando, idTenant);

        await using var lector = await comando.ExecuteReaderAsync(ct);
        if (!await lector.ReadAsync(ct))
        {
            return null;
        }

        return new CobroParaVender(
            lector.GetInt32(0),
            lector.GetInt64(1),
            lector.GetInt32(2),
            lector.GetString(3),
            lector.IsDBNull(4) ? null : lector.GetDecimal(4),
            lector.IsDBNull(5) ? null : lector.GetString(5),
            lector.IsDBNull(6) ? null : lector.GetInt32(6));
    }
}
