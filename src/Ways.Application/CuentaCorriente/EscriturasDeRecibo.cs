using System.Data.Common;
using Ways.Application.Abstracciones;
using Ways.Domain.Catalogos;
using Ways.Domain.CuentaCorriente;
using Ways.Domain.MercadoPago;
using Ways.Domain.Ventas;

namespace Ways.Application.CuentaCorriente;

/// <summary>Los datos de cabecera de un recibo (RC) ya resueltos: número reservado, turno, cliente e importe aplicado.</summary>
internal sealed record ReciboAEscribir(
    int IdTenant, int IdEmpleado, DateTimeOffset Momento, int IdTipoComprobante, long Numero, int IdPuntoVenta,
    int IdTurnoCaja, int IdCliente, decimal ImporteAplicado, string? Observaciones);

/// <summary>
/// Escritura de un pago a cuenta (RC), compartida por <see cref="ServicioDeCuentaCorriente"/> y por la venta que deja su
/// excedente a cuenta. Corre dentro de una transacción ya abierta, con el turno ya re-chequeado bajo su lock; no abre ni
/// comitea nada. Son dos pasos porque las aplicaciones del recibo van entre ambos: el orden de locks pone los comprobantes
/// antes que el cliente.
/// </summary>
internal static class EscriturasDeRecibo
{
    /// <summary>Comprobante sin items (afecta_stock = false de RC) y sus cobros. Subtotal y total son el importe aplicado,
    /// sin descuento. Un recibo no se apoya en un cobro de Mercado Pago, así que un medio con proveedor se rechaza.</summary>
    public static async Task<(ComprobanteVenta Comprobante, IReadOnlyList<Cobro> Pagos)> InsertarAsync(
        IWaysDbContext db, ReciboAEscribir recibo, IReadOnlyList<PagoDeCuenta> pagos,
        IReadOnlyDictionary<int, MedioPago> medioPorId, CancellationToken ct)
    {
        foreach (var pago in pagos)
        {
            ReglaDeCobroEnVenta.ExigirFormaDelPago(
                medioPorId[pago.IdMedioPago].Proveedor, idCobro: null, pago.Referencia, traeNumeroPreasignado: false);
        }

        var comprobante = new ComprobanteVenta
        {
            IdTipoComprobante = recibo.IdTipoComprobante,
            Numero = recibo.Numero,
            Fecha = recibo.Momento,
            IdPuntoVenta = recibo.IdPuntoVenta,
            IdTurnoCaja = recibo.IdTurnoCaja,
            IdEmpleado = recibo.IdEmpleado,
            IdCliente = recibo.IdCliente,
            Subtotal = recibo.ImporteAplicado,
            DescuentoTotal = 0m,
            Total = recibo.ImporteAplicado,
            Observaciones = recibo.Observaciones,
            Estado = EstadoComprobante.Emitido,
            FacturacionDecididaEn = recibo.Momento,
            CreatedAt = recibo.Momento,
            UpdatedAt = recibo.Momento
        };
        db.ComprobantesVenta.Add(comprobante);
        await db.SaveChangesAsync(ct);

        var cobros = pagos
            .Select(p => new Cobro
            {
                IdComprobanteVenta = comprobante.Id,
                IdMedioPago = p.IdMedioPago,
                Importe = p.Importe,
                Referencia = p.Referencia,
                Vuelto = p.Vuelto,
                CreatedAt = recibo.Momento,
                UpdatedAt = recibo.Momento
            })
            .ToList();
        db.Cobros.AddRange(cobros);
        await db.SaveChangesAsync(ct);

        return (comprobante, cobros);
    }

    /// <summary>Un único movimiento Pago, negativo (reduce la deuda), con el lock de la fila del cliente que toma el UPDATE
    /// del saldo. <c>id_cobro</c> va NULL: lo genera el recibo entero, que puede traer varios medios.</summary>
    public static async Task AcreditarAsync(
        DbConnection conexion, DbTransaction? transaccion, ReciboAEscribir recibo, int idComprobante, CancellationToken ct)
    {
        var nuevoSaldo = await EscriturasDeCuentaCorriente.ActualizarSaldoClienteAsync(
            conexion, transaccion, recibo.IdTenant, recibo.IdCliente, -recibo.ImporteAplicado, ct);

        await EscriturasDeCuentaCorriente.InsertarMovimientoCcAsync(
            conexion, transaccion, recibo.IdTenant, recibo.IdCliente, recibo.Momento, recibo.IdPuntoVenta, recibo.IdEmpleado,
            TipoMovimientoCc.Pago, idComprobante, null, -recibo.ImporteAplicado, nuevoSaldo, detalle: null, ct);
    }
}
