using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Ways.Application.Abstracciones;
using Ways.Application.Fiscal;
using Ways.Application.Parametros;
using Ways.Domain.Catalogos;
using Ways.Domain.Clientes;
using Ways.Domain.Common;
using Ways.Domain.Compras;
using Ways.Domain.Fiscal;
using Ways.Domain.Reportes;
using Ways.Domain.Ventas;

namespace Ways.Application.Reportes;

/// <summary>
/// Libro IVA compras y ventas. Lee, filtra por tenant y empresa y delega el armado en
/// <see cref="ComposicionDeLibroIva"/>. Compras: comprobantes confirmados de tipos que registran
/// libro IVA, por <c>fecha_comprobante</c>. Ventas: comprobantes emitidos de tipos fiscales con CAE
/// aprobado (<c>aprobado</c> o <c>aprobado_con_observaciones</c>), por el día local de la empresa;
/// pendientes, rechazados y anulados quedan afuera.
///
/// Los catálogos globales (tipos de comprobante, alícuotas) se leen sin el filtro de baja lógica:
/// dar de baja un tipo o una alícuota no puede sacar del libro un comprobante ya declarado.
/// Clientes y proveedores sí respetan la baja lógica; si el nombre ya no es visible, la fila
/// sigue en el libro con la leyenda <see cref="ContraparteNoDisponible"/>.
/// </summary>
public class ServicioDeLibroIva(IWaysDbContext db, ServicioDeParametros parametros)
{
    public const string ContraparteNoDisponible = "(no disponible)";

    private const string FiltroDeBaja = "BajaLogica";

    public async Task<LibroIva> ObtenerComprasAsync(
        int? idEmpresa, DateOnly desde, DateOnly hasta, int topeDeFilas, CancellationToken ct = default)
    {
        // Compras filtra por una fecha sin zona: el rango solo se usa para validar el período.
        RangoDeReporte.Crear(desde, hasta, Granularidad.Dia, TimeZoneInfo.Utc);

        var idsPuntoVenta = await ResolverPuntosDeVentaAsync(idEmpresa, ct);

        var tipos = await db.TiposComprobante.IgnoreQueryFilters([FiltroDeBaja])
            .Where(t => t.Clase == ClaseComprobante.Compra && t.RegistraLibroIva)
            .ToDictionaryAsync(t => t.Id, t => t.Codigo, ct);
        var idsTipo = tipos.Keys.ToList();

        var consulta = db.ComprobantesCompra
            .Where(c => c.Estado == EstadoCompra.Confirmada)
            .Where(c => c.FechaComprobante >= desde && c.FechaComprobante <= hasta)
            .Where(c => idsTipo.Contains(c.IdTipoComprobante));

        if (idsPuntoVenta is not null)
        {
            consulta = consulta.Where(c => idsPuntoVenta.Contains(c.IdPuntoVenta));
        }

        ExigirTope(await consulta.CountAsync(ct), topeDeFilas);

        var comprobantes = await consulta
            .Select(c => new
            {
                c.Id, c.FechaComprobante, c.IdTipoComprobante, c.NumeroExterno, c.IdProveedor, c.DiscriminaIva, c.Total
            })
            .ToListAsync(ct);

        var ids = comprobantes.Select(c => c.Id).ToList();

        var alicuotasGuardadas = await db.AlicuotasComprobanteCompra
            .Where(a => ids.Contains(a.IdComprobanteCompra))
            .ToListAsync(ct);
        var catalogo = await LeerAlicuotasAsync(alicuotasGuardadas.Select(a => a.IdAlicuotaIva), ct);
        var alicuotasPorCompra = alicuotasGuardadas
            .ToLookup(a => a.IdComprobanteCompra);

        var percepciones = await db.PercepcionesComprobanteCompra
            .Where(p => ids.Contains(p.IdComprobanteCompra))
            .ToListAsync(ct);
        var percepcionesPorCompra = percepciones.ToLookup(p => p.IdComprobanteCompra);

        var idsProveedor = comprobantes.Select(c => c.IdProveedor).Distinct().ToList();
        var proveedores = await db.Proveedores
            .Where(p => idsProveedor.Contains(p.Id))
            .ToDictionaryAsync(p => p.Id, p => new { p.RazonSocial, p.Cuit }, ct);

        var filas = comprobantes
            .Select(c =>
            {
                proveedores.TryGetValue(c.IdProveedor, out var proveedor);
                var percibidas = percepcionesPorCompra[c.Id].ToList();

                return new CompraParaLibro(
                    c.FechaComprobante!.Value,
                    tipos[c.IdTipoComprobante],
                    c.NumeroExterno!,
                    proveedor?.RazonSocial ?? ContraparteNoDisponible,
                    proveedor?.Cuit,
                    c.DiscriminaIva,
                    c.Total,
                    alicuotasPorCompra[c.Id]
                        .Select(a => new AlicuotaDeCompraParaLibro(
                            catalogo[a.IdAlicuotaIva].CodigoAfip, catalogo[a.IdAlicuotaIva].Nombre, a.Porcentaje, a.Neto,
                            a.Iva))
                        .ToList(),
                    percibidas.Where(p => p.Tipo == TiposDePercepcion.Iva).Sum(p => p.Importe),
                    percibidas.Where(p => p.Tipo == TiposDePercepcion.Iibb).Sum(p => p.Importe));
            })
            .ToList();

        return ComposicionDeLibroIva.DeCompras(desde, hasta, idEmpresa, filas);
    }

    public async Task<LibroIva> ObtenerVentasAsync(
        int? idEmpresa, DateOnly desde, DateOnly hasta, int topeDeFilas, CancellationToken ct = default)
    {
        List<int> idsEmpresa;
        if (idEmpresa is { } id)
        {
            await ExigirEmpresaAsync(id, ct);
            idsEmpresa = [id];
        }
        else
        {
            idsEmpresa = await db.Empresas.OrderBy(e => e.Id).Select(e => e.Id).ToListAsync(ct);
        }

        var tipos = await db.TiposComprobante.IgnoreQueryFilters([FiltroDeBaja])
            .Where(t => t.Clase == ClaseComprobante.Venta && t.EsFiscal)
            .ToDictionaryAsync(t => t.Id, t => (t.Codigo, t.Signo), ct);
        var idsTipo = tipos.Keys.ToList();

        var alcances = new List<AlcanceDeEmpresa>();
        foreach (var idDeEmpresa in idsEmpresa)
        {
            var (zonaId, zona) = await ResolverZonaAsync(idDeEmpresa, ct);
            var rango = RangoDeReporte.Crear(desde, hasta, Granularidad.Dia, zona);
            var puntosVenta = await db.PuntosVenta
                .Where(pv => pv.IdEmpresa == idDeEmpresa)
                .ToDictionaryAsync(pv => pv.Id, pv => pv.NumeroFiscal, ct);
            var idsPuntoVenta = puntosVenta.Keys.ToList();

            var consulta = db.ComprobantesVenta
                .Where(c => idsPuntoVenta.Contains(c.IdPuntoVenta))
                .Where(c => c.Estado == EstadoComprobante.Emitido)
                .Where(c => c.ResultadoFiscal == ResultadoFiscal.Aprobado
                    || c.ResultadoFiscal == ResultadoFiscal.AprobadoConObservaciones)
                .Where(c => idsTipo.Contains(c.IdTipoComprobante))
                .Where(c => c.Fecha >= rango.DesdeUtc && c.Fecha < rango.HastaUtcExclusivo);

            alcances.Add(new AlcanceDeEmpresa(zonaId, zona, puntosVenta, consulta));
        }

        var cantidades = new List<int>();
        foreach (var alcance in alcances)
        {
            cantidades.Add(await alcance.Consulta.CountAsync(ct));
        }

        ExigirTope(cantidades.Sum(), topeDeFilas);

        var ventas = new List<VentaParaLibro>();
        foreach (var alcance in alcances)
        {
            ventas.AddRange(await LeerVentasAsync(alcance, tipos, ct));
        }

        var zonaDelLibro = idEmpresa is not null ? alcances[0].ZonaId : null;
        return ComposicionDeLibroIva.DeVentas(desde, hasta, idEmpresa, zonaDelLibro, ventas);
    }

    private sealed record AlcanceDeEmpresa(
        string ZonaId, TimeZoneInfo Zona, IReadOnlyDictionary<int, int?> NumeroFiscalPorPuntoVenta,
        IQueryable<ComprobanteVenta> Consulta);

    private async Task<List<VentaParaLibro>> LeerVentasAsync(
        AlcanceDeEmpresa alcance, IReadOnlyDictionary<int, (string Codigo, short Signo)> tipos, CancellationToken ct)
    {
        var comprobantes = await alcance.Consulta
            .Select(c => new
            {
                c.Id, c.Fecha, c.IdTipoComprobante, c.Numero, c.IdPuntoVenta, c.IdCliente, c.Total
            })
            .ToListAsync(ct);

        var ids = comprobantes.Select(c => c.Id).ToList();
        var items = await db.ItemsComprobanteVenta
            .Where(i => ids.Contains(i.IdComprobanteVenta))
            .Select(i => new { i.IdComprobanteVenta, i.IdAlicuotaIva, i.PorcentajeIva, i.Total })
            .ToListAsync(ct);
        var catalogo = await LeerAlicuotasAsync(items.Select(i => i.IdAlicuotaIva), ct);
        var itemsPorComprobante = items.ToLookup(i => i.IdComprobanteVenta);

        var idsCliente = comprobantes.Select(c => c.IdCliente).Distinct().ToList();
        var clientes = await db.Clientes
            .Where(c => idsCliente.Contains(c.Id))
            .Select(c => new { c.Id, c.Nombre, c.RazonSocial, c.TipoDocumento, c.NumeroDocumento })
            .ToDictionaryAsync(c => c.Id, ct);

        return comprobantes
            .Select(c =>
            {
                clientes.TryGetValue(c.IdCliente, out var cliente);
                var tipo = tipos[c.IdTipoComprobante];
                var puntoDeVenta = alcance.NumeroFiscalPorPuntoVenta[c.IdPuntoVenta] ?? c.IdPuntoVenta;

                return new VentaParaLibro(
                    DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(c.Fecha, alcance.Zona).DateTime),
                    tipo.Codigo,
                    NumeroDeComprobante.Formatear(puntoDeVenta, c.Numero),
                    cliente is null
                        ? ContraparteNoDisponible
                        : string.IsNullOrWhiteSpace(cliente.RazonSocial) ? cliente.Nombre : cliente.RazonSocial,
                    DocumentoDe(cliente?.TipoDocumento, cliente?.NumeroDocumento),
                    tipo.Signo,
                    c.Total,
                    itemsPorComprobante[c.Id]
                        .Select(i => new LineaFiscal(
                            i.IdAlicuotaIva, catalogo[i.IdAlicuotaIva].Nombre, catalogo[i.IdAlicuotaIva].CodigoAfip,
                            i.PorcentajeIva, i.Total))
                        .ToList());
            })
            .ToList();
    }

    private static string? DocumentoDe(TipoDocumento? tipo, string? numero) =>
        tipo is null || string.IsNullOrWhiteSpace(numero) ? null : $"{tipo.Value.ToString().ToUpperInvariant()} {numero}";

    private async Task<IReadOnlyDictionary<int, AlicuotaIva>> LeerAlicuotasAsync(
        IEnumerable<int> idsAlicuota, CancellationToken ct)
    {
        var ids = idsAlicuota.Distinct().ToList();
        return await db.AlicuotasIva.IgnoreQueryFilters([FiltroDeBaja])
            .Where(a => ids.Contains(a.Id))
            .ToDictionaryAsync(a => a.Id, ct);
    }

    private static void ExigirTope(int cantidad, int tope)
    {
        if (cantidad > tope)
        {
            throw new ErrorDominio(
                "libro_iva_demasiado_grande",
                $"El libro tiene {cantidad} comprobantes; el tope es {tope}. Acotá el período o la empresa.",
                400);
        }
    }

    private async Task ExigirEmpresaAsync(int idEmpresa, CancellationToken ct)
    {
        // ADR-8: mismo 404 para "no existe" y "es de otro tenant".
        if (!await db.Empresas.AnyAsync(e => e.Id == idEmpresa, ct))
        {
            throw ErrorDominio.NoEncontrado($"No existe la empresa {idEmpresa}.");
        }
    }

    private async Task<List<int>?> ResolverPuntosDeVentaAsync(int? idEmpresa, CancellationToken ct)
    {
        if (idEmpresa is not { } id)
        {
            return null;
        }

        await ExigirEmpresaAsync(id, ct);
        return await db.PuntosVenta.Where(pv => pv.IdEmpresa == id).Select(pv => pv.Id).ToListAsync(ct);
    }

    private async Task<(string ZonaId, TimeZoneInfo Zona)> ResolverZonaAsync(int idEmpresa, CancellationToken ct)
    {
        var resuelto = await parametros.ResolverAsync(ParametroConocido.ZonaHoraria.Clave, idEmpresa, null, ct);
        var zonaId = JsonSerializer.Deserialize<string>(resuelto.Valor)!;
        return (zonaId, TimeZoneInfo.FindSystemTimeZoneById(zonaId));
    }
}
