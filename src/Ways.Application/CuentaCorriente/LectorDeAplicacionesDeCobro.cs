using Microsoft.EntityFrameworkCore;
using Ways.Application.Abstracciones;
using Ways.Domain.Catalogos;
using Ways.Domain.Ventas;

namespace Ways.Application.CuentaCorriente;

/// <summary>Lo cobrado, lo aplicado y lo disponible de un recibo <c>RC</c>.</summary>
public sealed record DisponibleDeCobro(int IdRecibo, decimal NetoCobrado, decimal Aplicado, decimal Disponible);

/// <summary>La parte en cuenta corriente, lo cobrado por aplicaciones y el saldo pendiente de una venta.</summary>
public sealed record SaldoDeVenta(int IdVenta, decimal ParteEnCuentaCorriente, decimal Cobrado, decimal SaldoPendiente);

/// <summary>
/// Lectores de las aplicaciones de cobros a ventas (E7-S2, doc 10 §8). Lo aplicado de un recibo y lo cobrado de una venta
/// son la suma de sus aplicaciones vivas (una liberada tiene <c>deleted_at</c> y el filtro global de baja lógica la
/// excluye). La aritmética vive en <see cref="ReglaDeAplicacionDeCobro"/>; acá solo se junta lo que hace falta, con una
/// consulta por concepto para todos los ids pedidos. Un id que no existe (o es un espejo fiscal) no figura en el resultado.
/// </summary>
public static class LectorDeAplicacionesDeCobro
{
    public static async Task<IReadOnlyDictionary<int, DisponibleDeCobro>> LeerDisponiblePorReciboAsync(
        IWaysDbContext db, IReadOnlyCollection<int> idsRecibos, CancellationToken ct)
    {
        if (idsRecibos.Count == 0)
        {
            return new Dictionary<int, DisponibleDeCobro>();
        }

        var estados = await LeerEstadosAsync(db, idsRecibos, ct);

        var cobros = await db.Cobros
            .Where(c => idsRecibos.Contains(c.IdComprobanteVenta)
                && db.ComprobantesVenta.SinEspejosFiscales().Any(v => v.Id == c.IdComprobanteVenta))
            .Select(c => new { c.IdComprobanteVenta, c.Importe, c.Vuelto })
            .ToListAsync(ct);

        var aplicado = await db.AplicacionesCobro
            .Where(a => idsRecibos.Contains(a.IdComprobanteCobro))
            .GroupBy(a => a.IdComprobanteCobro)
            .Select(g => new { Id = g.Key, Total = g.Sum(a => a.Importe) })
            .ToDictionaryAsync(g => g.Id, g => g.Total, ct);

        return estados.ToDictionary(e => e.Key, e =>
        {
            var neto = ReglaDeAplicacionDeCobro.NetoCobrado(
                cobros.Where(c => c.IdComprobanteVenta == e.Key).Select(c => (c.Importe, c.Vuelto)));
            var deEsteRecibo = aplicado.GetValueOrDefault(e.Key, 0m);
            return new DisponibleDeCobro(
                e.Key, neto, deEsteRecibo, ReglaDeAplicacionDeCobro.Disponible(e.Value, neto, deEsteRecibo));
        });
    }

    public static async Task<IReadOnlyDictionary<int, SaldoDeVenta>> LeerSaldoPorVentaAsync(
        IWaysDbContext db, IReadOnlyCollection<int> idsVentas, CancellationToken ct)
    {
        if (idsVentas.Count == 0)
        {
            return new Dictionary<int, SaldoDeVenta>();
        }

        var estados = await LeerEstadosAsync(db, idsVentas, ct);

        var cobros = await db.Cobros
            .Where(c => idsVentas.Contains(c.IdComprobanteVenta)
                && db.ComprobantesVenta.SinEspejosFiscales().Any(v => v.Id == c.IdComprobanteVenta))
            .Select(c => new { c.IdComprobanteVenta, c.IdMedioPago, c.Importe })
            .ToListAsync(ct);

        // dangling-fk-read-models: un medio dado de baja sigue siendo el medio con que se cobró; sin ignorar la baja lógica,
        // la parte en cuenta corriente de la venta desaparecería con él.
        var idsMedios = cobros.Select(c => c.IdMedioPago).Distinct().ToList();
        var mediosEnCuentaCorriente = (await db.MediosPago
                .IgnoreQueryFilters(["BajaLogica"])
                .Where(m => idsMedios.Contains(m.Id) && m.Comportamiento == ComportamientoMedioPago.CuentaCorriente)
                .Select(m => m.Id)
                .ToListAsync(ct))
            .ToHashSet();

        var cobrado = await LeerCobradoPorVentaAsync(db, idsVentas, ct);

        return estados.ToDictionary(e => e.Key, e =>
        {
            var parte = ReglaDeAplicacionDeCobro.ParteEnCuentaCorriente(
                cobros.Where(c => c.IdComprobanteVenta == e.Key)
                    .Select(c => (c.Importe, mediosEnCuentaCorriente.Contains(c.IdMedioPago))));
            var deEstaVenta = cobrado.GetValueOrDefault(e.Key, 0m);
            return new SaldoDeVenta(
                e.Key, parte, deEstaVenta, ReglaDeAplicacionDeCobro.SaldoPendiente(e.Value, parte, deEstaVenta));
        });
    }

    /// <summary>Lo cobrado de cada venta pedida por aplicaciones de recibos; una venta sin aplicaciones figura con 0.</summary>
    public static async Task<IReadOnlyDictionary<int, decimal>> LeerCobradoPorVentaAsync(
        IWaysDbContext db, IReadOnlyCollection<int> idsVentas, CancellationToken ct)
    {
        if (idsVentas.Count == 0)
        {
            return new Dictionary<int, decimal>();
        }

        var cobrado = await db.AplicacionesCobro
            .Where(a => idsVentas.Contains(a.IdComprobanteVenta))
            .GroupBy(a => a.IdComprobanteVenta)
            .Select(g => new { Id = g.Key, Total = g.Sum(a => a.Importe) })
            .ToDictionaryAsync(g => g.Id, g => g.Total, ct);

        return idsVentas.Distinct().ToDictionary(id => id, id => cobrado.GetValueOrDefault(id, 0m));
    }

    private static async Task<Dictionary<int, EstadoComprobante>> LeerEstadosAsync(
        IWaysDbContext db, IReadOnlyCollection<int> ids, CancellationToken ct) =>
        await db.ComprobantesVenta.SinEspejosFiscales()
            .Where(c => ids.Contains(c.Id))
            .Select(c => new { c.Id, c.Estado })
            .ToDictionaryAsync(c => c.Id, c => c.Estado, ct);
}
