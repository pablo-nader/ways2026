using Microsoft.EntityFrameworkCore;
using Ways.Application.Abstracciones;
using Ways.Domain.Catalogos;
using Ways.Domain.Common;
using Ways.Domain.Ventas;

namespace Ways.Application.CuentaCorriente;

/// <summary>
/// Listados de cobros de un cliente (E7-S3): sus ventas con saldo pendiente en cuenta corriente y sus recibos con lo
/// disponible. Los importes salen de <see cref="LectorDeAplicacionesDeCobro"/> (una única fuente de la aritmética); acá solo
/// se arma qué ids se miran y cómo se muestran. Un cliente inexistente o de otro tenant es 404.
/// </summary>
public static class LectorDeCobrosDeCliente
{
    /// <summary>Las ventas emitidas del cliente con parte en cuenta corriente, ordenadas de la más nueva a la más vieja,
    /// que todavía tienen saldo pendiente. Los medios dados de baja siguen contando: son con los que se cobró.</summary>
    public static async Task<IReadOnlyList<VentaPendienteDeCobro>> LeerVentasPendientesAsync(
        IWaysDbContext db, int idCliente, CancellationToken ct)
    {
        await ExigirClienteAsync(db, idCliente, ct);

        var mediosEnCuentaCorriente = db.MediosPago
            .IgnoreQueryFilters(["BajaLogica"])
            .Where(m => m.Comportamiento == ComportamientoMedioPago.CuentaCorriente)
            .Select(m => m.Id);

        // dangling-fk-read-models: un tipo dado de baja sigue siendo el tipo del comprobante ya emitido.
        var candidatas = await (
                from c in db.ComprobantesVenta.SinEspejosFiscales()
                join t in db.TiposComprobante.IgnoreQueryFilters(["BajaLogica"]) on c.IdTipoComprobante equals t.Id
                where c.IdCliente == idCliente
                    && c.Estado == EstadoComprobante.Emitido
                    && t.Signo > 0
                    && t.Codigo != ReglaDeAplicacionDeCobro.CodigoDeRecibo
                    && db.Cobros.Any(p => p.IdComprobanteVenta == c.Id && mediosEnCuentaCorriente.Contains(p.IdMedioPago))
                select new { c.Id, t.Codigo, t.Letra, c.Numero, c.IdPuntoVenta, c.Fecha })
            .ToListAsync(ct);

        var saldos = await LectorDeAplicacionesDeCobro.LeerSaldoPorVentaAsync(db, candidatas.Select(c => c.Id).ToList(), ct);

        return candidatas
            .Where(c => saldos[c.Id].SaldoPendiente > 0m)
            .OrderByDescending(c => c.Fecha).ThenByDescending(c => c.Id)
            .Select(c => new VentaPendienteDeCobro(
                c.Id, c.Codigo, c.Letra?.ToString(), c.Numero, NumeroDeComprobante.Formatear(c.IdPuntoVenta, c.Numero),
                c.Fecha, saldos[c.Id].ParteEnCuentaCorriente, saldos[c.Id].Cobrado, saldos[c.Id].SaldoPendiente))
            .ToList();
    }

    /// <summary>Los recibos no anulados del cliente, del más nuevo al más viejo, con lo cobrado, lo aplicado, lo disponible y
    /// sus aplicaciones vivas. Con <paramref name="soloConDisponible"/>, solo los que todavía tienen algo para aplicar.</summary>
    public static async Task<IReadOnlyList<ReciboDeCliente>> LeerRecibosAsync(
        IWaysDbContext db, int idCliente, bool soloConDisponible, CancellationToken ct)
    {
        await ExigirClienteAsync(db, idCliente, ct);

        var recibos = await (
                from c in db.ComprobantesVenta.SinEspejosFiscales()
                join t in db.TiposComprobante.IgnoreQueryFilters(["BajaLogica"]) on c.IdTipoComprobante equals t.Id
                where c.IdCliente == idCliente
                    && c.Estado == EstadoComprobante.Emitido
                    && t.Codigo == ReglaDeAplicacionDeCobro.CodigoDeRecibo
                select new { c.Id, c.Numero, c.IdPuntoVenta, c.Fecha })
            .ToListAsync(ct);

        var ids = recibos.Select(r => r.Id).ToList();
        var disponibles = await LectorDeAplicacionesDeCobro.LeerDisponiblePorReciboAsync(db, ids, ct);

        var aplicaciones = await db.AplicacionesCobro
            .Where(a => ids.Contains(a.IdComprobanteCobro))
            .OrderBy(a => a.Id)
            .Select(a => new { a.Id, a.IdComprobanteCobro, a.IdComprobanteVenta, a.Importe, a.Fecha, a.Origen })
            .ToListAsync(ct);

        var idsVentas = aplicaciones.Select(a => a.IdComprobanteVenta).Distinct().ToList();
        var numerosDeVentas = (await db.ComprobantesVenta.SinEspejosFiscales()
                .Where(c => idsVentas.Contains(c.Id))
                .Select(c => new { c.Id, c.IdPuntoVenta, c.Numero })
                .ToListAsync(ct))
            .ToDictionary(c => c.Id, c => NumeroDeComprobante.Formatear(c.IdPuntoVenta, c.Numero));

        return recibos
            .Select(r => (Recibo: r, Estado: disponibles[r.Id]))
            .Where(x => !soloConDisponible || x.Estado.Disponible > 0m)
            .OrderByDescending(x => x.Recibo.Fecha).ThenByDescending(x => x.Recibo.Id)
            .Select(x => new ReciboDeCliente(
                x.Recibo.Id, x.Recibo.Numero, NumeroDeComprobante.Formatear(x.Recibo.IdPuntoVenta, x.Recibo.Numero),
                x.Recibo.Fecha, x.Estado.NetoCobrado, x.Estado.Aplicado, x.Estado.Disponible,
                aplicaciones
                    .Where(a => a.IdComprobanteCobro == x.Recibo.Id)
                    .Select(a => new AplicacionDeReciboListada(
                        a.Id, a.IdComprobanteVenta, numerosDeVentas[a.IdComprobanteVenta], a.Importe, a.Fecha, a.Origen))
                    .ToList()))
            .ToList();
    }

    private static async Task ExigirClienteAsync(IWaysDbContext db, int idCliente, CancellationToken ct)
    {
        if (!await db.Clientes.AnyAsync(c => c.Id == idCliente, ct))
        {
            // ADR-8: mismo 404 para "no existe" y "es de otro tenant".
            throw ErrorDominio.NoEncontrado($"No existe el cliente {idCliente}.");
        }
    }
}
