using Microsoft.EntityFrameworkCore;
using Ways.Application.Abstracciones;
using Ways.Domain.Common;
using Ways.Domain.Fiscal;
using Ways.Domain.Ventas;

namespace Ways.Application.Ventas;

/// <summary>
/// Lectura del ticket de origen de una devolución para el POS: qué vendió, cuánto ya se devolvió y cuánto queda, artículo por artículo, y
/// si el ticket admite una devolución. Solo lee (no toma locks): la verdad la fija <c>ServicioDeVentas.EmitirAsync</c> al emitir la nota
/// de crédito, así que lo que muestra esta lectura puede quedar viejo y la emisión lo re-juzga.
/// </summary>
public static class ConsultaDeDevolvibles
{
    /// <summary>Busca el ticket por el número que imprime (<c>numero</c> dentro del punto de venta). 404 si no hay un ticket de venta
    /// con ese número en el punto de venta, también si es de otro tenant (el filtro de EF y RLS lo dejan invisible).</summary>
    public static async Task<TicketDevolvible> BuscarPorNumeroAsync(
        IWaysDbContext db, int idPuntoVenta, long numero, CancellationToken ct = default)
    {
        var id = await (
                from c in db.ComprobantesVenta.SinEspejosFiscales()
                join t in db.TiposComprobante on c.IdTipoComprobante equals t.Id
                where c.IdPuntoVenta == idPuntoVenta && c.Numero == numero && t.Codigo == ReglaDeComprobantes.CodigoDeVenta
                select (int?)c.Id)
            .FirstOrDefaultAsync(ct);

        return id is { } encontrado
            ? await ObtenerAsync(db, encontrado, ct)
            : throw ErrorDominio.NoEncontrado($"No existe un ticket de venta con el número {numero} en el punto de venta {idPuntoVenta}.");
    }

    /// <summary>El ticket con sus líneas devolvibles. 404 si el id no es un comprobante visible o es un espejo fiscal (la factura de un
    /// ticket no es una venta que se devuelva: se devuelve el ticket).</summary>
    public static async Task<TicketDevolvible> ObtenerAsync(IWaysDbContext db, int id, CancellationToken ct = default)
    {
        var cabecera = await (
                from c in db.ComprobantesVenta.SinEspejosFiscales()
                join t in db.TiposComprobante on c.IdTipoComprobante equals t.Id
                where c.Id == id
                select new
                {
                    c.Id, c.Numero, c.Fecha, c.IdPuntoVenta, c.IdCliente, c.Total, c.Estado, c.ResultadoFiscal, c.FacturacionDecididaEn,
                    CodigoTipo = t.Codigo
                })
            .FirstOrDefaultAsync(ct)
            ?? throw ErrorDominio.NoEncontrado($"No existe el comprobante {id}.");

        // Un cliente dado de baja no tiene nombre que mostrar: queda sin dato, igual que si faltara.
        var nombreDelCliente = await db.Clientes
            .Where(cl => cl.Id == cabecera.IdCliente)
            .Select(cl => cl.Nombre)
            .FirstOrDefaultAsync(ct);

        // El espejo de la factura del ticket (el único que tiene, sin contar el de una nota de crédito): se lee sin el predicado de
        // espejos justamente porque hay que verlo.
        var resultadoDeLaFactura = await db.ComprobantesVenta
            .Where(c => c.IdVentaOrigen == id && c.IdComprobanteAsociado == null)
            .Select(c => c.ResultadoFiscal)
            .FirstOrDefaultAsync(ct);

        // La decisión de facturar el ticket sigue debida o pendiente: la factura que nazca después se arma con todos sus items.
        var decisionPendiente = cabecera.FacturacionDecididaEn is null
            || await db.FacturacionesDeVenta.AnyAsync(
                f => f.IdComprobanteVenta == id && f.IdFacturacionOrigen == null && f.Estado == EstadoFacturacionDeVenta.Pendiente, ct);

        var items = await db.ItemsComprobanteVenta
            .Where(i => i.IdComprobanteVenta == id && i.Cantidad > 0m && i.IdArticulo != null)
            .OrderBy(i => i.Orden)
            .Select(i => new { i.IdArticulo, i.Descripcion, i.CodigoBarra, i.PorImporte, i.PrecioUnitario })
            .ToListAsync(ct);

        var vendidos = await LectorDeDevolucionesDelTicket.LeerVendidosAsync(db, id, ct);
        var yaDevueltas = await LectorDeDevolucionesDelTicket.LeerYaDevueltasAsync(db, id, ct);
        var saldos = DevolucionDeTicket.Saldos(vendidos, yaDevueltas);

        var lineas = saldos
            .Select(s =>
            {
                var primero = items.First(i => i.IdArticulo == s.IdArticulo && i.PorImporte == s.PorImporte
                    && (!s.PorImporte || i.PrecioUnitario == s.PrecioUnitario));

                return new LineaDevolvible(
                    s.IdArticulo, primero.Descripcion, primero.CodigoBarra, s.PorImporte ? s.PrecioUnitario : null, s.Vendida,
                    s.Devuelta, s.Devolvible, s.ImporteUnitario, s.ImporteDeLoRestante);
            })
            .ToList();

        var motivo = ReglaDeDevoluciones.Evaluar(cabecera.CodigoTipo, cabecera.Estado, [cabecera.ResultadoFiscal, resultadoDeLaFactura], decisionPendiente)
            ?? (lineas.All(l => l.CantidadDevolvible <= 0m) ? MotivoDeNoDevolvible.SinUnidadesPorDevolver : null);

        return new TicketDevolvible(
            cabecera.Id, cabecera.Numero, NumeroDeComprobante.Formatear(cabecera.IdPuntoVenta, cabecera.Numero), cabecera.Fecha,
            cabecera.IdPuntoVenta, cabecera.IdCliente, nombreDelCliente, cabecera.Total, motivo is null,
            motivo is { } m ? CodigoDelMotivo(m) : null, lineas);
    }

    private static string CodigoDelMotivo(MotivoDeNoDevolvible motivo) => motivo switch
    {
        MotivoDeNoDevolvible.Anulado => "anulado",
        MotivoDeNoDevolvible.ConFacturaFiscal => "con_factura_fiscal",
        MotivoDeNoDevolvible.SinUnidadesPorDevolver => "sin_unidades_por_devolver",
        MotivoDeNoDevolvible.NoEsUnTicketDeVenta => "no_es_un_ticket_de_venta",
        _ => throw new ArgumentOutOfRangeException(nameof(motivo), motivo, null)
    };
}
