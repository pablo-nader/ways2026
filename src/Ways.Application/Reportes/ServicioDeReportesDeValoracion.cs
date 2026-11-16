using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Ways.Application.Abstracciones;
using Ways.Application.Caja;
using Ways.Application.Parametros;
using Ways.Application.Precios;
using Ways.Domain.Catalogos;
using Ways.Domain.Common;
using Ways.Domain.Compras;
using Ways.Domain.Reportes;
using Ways.Domain.Stock;

namespace Ways.Application.Reportes;

/// <summary>
/// Cuánto vale lo que tiene una empresa: mercadería a costo y a precio de venta más dinero (efectivo de la caja general y cuentas de
/// tesorería), hoy y como evolución. Cada punto corta en un instante (<see cref="CortesDeValoracion"/>) y todo se calcula con lo
/// ocurrido hasta ese instante inclusive.
///
/// Stock: suma de <c>movimientos_stock.cantidad</c> hasta el corte por par artículo-sucursal de las sucursales de la empresa,
/// agregada en SQL; el acotado a cero por par y la suma por artículo son de <see cref="CalculoDeValoracion"/>. Los artículos dados de
/// baja no se valoran. Precio: la lista predeterminada de la empresa (si no, la compartida) resuelta al corte con
/// <see cref="ServicioDePrecios.PreciosVigentesEnLoteAsync"/>. Dinero: el mismo acumulador que el libro de tesorería.
///
/// Costo: no hay historial. El punto actual usa <c>articulos.costo_nominal</c>. Los pasados reconstruyen el costo con la última
/// línea de compra confirmada anterior al corte que habría escrito el costo nominal (no nota de crédito, <c>actualiza_costo</c>,
/// costo efectivo positivo), convertida a la misma base que el costo nominal con
/// <see cref="CalculadorDeCompra.CalcularCostoEfectivoDesdeItem"/>; sin esa línea se usa el costo actual y el artículo cuenta como
/// estimado. Las compras son del tenant (el costo nominal es del artículo, no de la empresa).
///
/// Cantidad de consultas: una de stock y una de precios por punto, las de saldos de tesorería por punto, y una sola de artículos,
/// familias y líneas de compra para todo el rango.
/// </summary>
public class ServicioDeReportesDeValoracion(
    IWaysDbContext db, ServicioDeParametros parametros, ServicioDePrecios precios, AcumuladorDeSaldosDeTesoreria acumulador,
    IRelojDelSistema reloj)
{
    public async Task<ValoracionDeInventario> ObtenerAsync(
        int idEmpresa, DateOnly desde, DateOnly hasta, Granularidad granularidad, CancellationToken ct = default)
    {
        await ExigirEmpresaAsync(idEmpresa, ct);

        var resuelto = await parametros.ResolverAsync(ParametroConocido.ZonaHoraria.Clave, idEmpresa, null, ct);
        var zonaId = JsonSerializer.Deserialize<string>(resuelto.Valor)!;
        var rango = RangoDeReporte.Crear(desde, hasta, granularidad, TimeZoneInfo.FindSystemTimeZoneById(zonaId));
        var cortes = CortesDeValoracion.Calcular(rango, reloj.Ahora);

        var stockPorPunto = new List<IReadOnlyDictionary<int, decimal>>();
        foreach (var corte in cortes)
        {
            stockPorPunto.Add(await StockPositivoAsync(idEmpresa, corte.Instante, ct));
        }

        var idsConStock = stockPorPunto.SelectMany(s => s.Keys).Distinct().ToList();
        var articulos = await LeerArticulosAsync(idsConStock, ct);
        var familiaPorArticulo = articulos.Values.Where(a => a.IdFamilia is not null).ToDictionary(a => a.IdArticulo, a => a.IdFamilia!.Value);
        var lineas = cortes.Any(c => !c.EsAhora)
            ? await LeerLineasDeCostoAsync(idsConStock, familiaPorArticulo, cortes[^1].Instante, ct)
            : [];
        var familiaDeLineas = lineas.Count == 0 ? familiaPorArticulo : await FamiliasDeLasLineasAsync(lineas, familiaPorArticulo, ct);

        var lista = await ListaPredeterminadaAsync(idEmpresa, ct);
        var areas = await db.Areas.Select(a => new { a.Id, a.Nombre }).ToDictionaryAsync(a => a.Id, a => a.Nombre, ct);
        var areasVisibles = areas.Keys.ToHashSet();
        var contexto = await acumulador.CargarAsync(idEmpresa, ct);
        var aValorar = articulos.ToDictionary(a => a.Key, a => new ArticuloAValorar(a.Key, a.Value.IdArea, a.Value.CostoNominal));

        var puntos = new List<PuntoDeValoracion>();
        MercaderiaValorada? ultimaMercaderia = null;
        AcumuladorDeSaldosDeTesoreria.Acumulado? ultimoDinero = null;

        for (var i = 0; i < cortes.Count; i++)
        {
            var corte = cortes[i];
            var stock = stockPorPunto[i];

            IReadOnlyDictionary<int, decimal>? costoDeCompra = corte.EsAhora
                ? null
                : CostoHistoricoDeCompras.Resolver(lineas, corte.Instante, stock.Keys, familiaDeLineas);

            var preciosDelPunto = await PreciosAsync(lista?.Id, stock.Keys.ToList(), corte.Instante, ct);
            var mercaderia = CalculoDeValoracion.Valorar(stock, aValorar, costoDeCompra, preciosDelPunto, areasVisibles);

            var dineroDelPunto = await acumulador.AcumularAsync(contexto, corte.Instante, inclusivo: true, ct);
            var cuentas = SaldosDeCuentas(contexto, dineroDelPunto).Sum(c => c.Saldo);
            var dinero = CalculoDeValoracion.Dinero(dineroDelPunto.Efectivo, cuentas);

            puntos.Add(new PuntoDeValoracion(
                corte.Instante, mercaderia.Costo, mercaderia.Venta, dineroDelPunto.Efectivo, cuentas, dinero,
                CalculoDeValoracion.TotalACosto(mercaderia, dinero), CalculoDeValoracion.TotalAVenta(mercaderia, dinero),
                mercaderia.SinCosto, mercaderia.SinPrecio, mercaderia.ConCostoEstimado, mercaderia.ValorCostoEstimado));

            ultimaMercaderia = mercaderia;
            ultimoDinero = dineroDelPunto;
        }

        var porArea = ultimaMercaderia!.PorArea
            .Select(a => new AreaDeValoracion(
                a.IdArea, a.IdArea is { } id ? areas[id] : "Sin área", a.Costo, a.Venta))
            .ToList();
        var detalle = new DetalleActualDeValoracion(
            SaldosDeCuentas(contexto, ultimoDinero!), porArea, lista?.Id, lista?.Nombre);

        return new ValoracionDeInventario(idEmpresa, desde, hasta, granularidad, zonaId, puntos, detalle);
    }

    private async Task ExigirEmpresaAsync(int idEmpresa, CancellationToken ct)
    {
        // Mismo 404 para "no existe" y "es de otro tenant".
        if (!await db.Empresas.AnyAsync(e => e.Id == idEmpresa, ct))
        {
            throw ErrorDominio.NoEncontrado($"No existe la empresa {idEmpresa}.");
        }
    }

    /// <summary>Las cuentas vivas, más las dadas de baja que tengan movimientos, igual que el libro de tesorería.</summary>
    private static List<CuentaDeValoracion> SaldosDeCuentas(
        AcumuladorDeSaldosDeTesoreria.ContextoDeSaldos contexto, AcumuladorDeSaldosDeTesoreria.Acumulado acumulado) =>
        contexto.Cuentas
            .Where(c => !c.Eliminada || acumulado.PorCuenta.ContainsKey(c.Id))
            .OrderBy(c => c.Nombre).ThenBy(c => c.Id)
            .Select(c => new CuentaDeValoracion(c.Id, c.Nombre, c.SaldoInicial + acumulado.PorCuenta.GetValueOrDefault(c.Id)))
            .ToList();

    private async Task<IReadOnlyDictionary<int, decimal>> StockPositivoAsync(
        int idEmpresa, DateTimeOffset corte, CancellationToken ct)
    {
        var pares = await (
            from m in db.MovimientosStock
            join s in db.Sucursales.IgnoreQueryFilters(["BajaLogica"]) on m.IdSucursal equals s.Id
            join a in db.Articulos on m.IdArticulo equals a.Id
            where s.IdEmpresa == idEmpresa && m.CreadoEl <= corte
            group m by new { m.IdArticulo, m.IdSucursal } into g
            select new StockDeSucursal(g.Key.IdArticulo, g.Key.IdSucursal, g.Sum(x => x.Cantidad)))
            .ToListAsync(ct);

        return CalculoDeValoracion.StockPositivoPorArticulo(pares);
    }

    private sealed record FichaDeArticulo(int IdArticulo, int? IdArea, decimal? CostoNominal, int? IdFamilia);

    private async Task<Dictionary<int, FichaDeArticulo>> LeerArticulosAsync(IReadOnlyList<int> ids, CancellationToken ct)
    {
        if (ids.Count == 0)
        {
            return [];
        }

        return await db.Articulos
            .Where(a => ids.Contains(a.Id))
            .Select(a => new FichaDeArticulo(a.Id, a.IdArea, a.CostoNominal, a.IdFamilia))
            .ToDictionaryAsync(a => a.IdArticulo, ct);
    }

    /// <summary>Todas las líneas de compra que escribieron costo nominal para los artículos con stock y los miembros de sus
    /// familias, hasta el último corte. El costo efectivo se arma acá con la misma fórmula que la confirmación de la compra.</summary>
    private async Task<IReadOnlyList<LineaQueEscribioCosto>> LeerLineasDeCostoAsync(
        IReadOnlyList<int> idsConStock, IReadOnlyDictionary<int, int> familiaPorArticulo, DateTimeOffset ultimoCorte,
        CancellationToken ct)
    {
        if (idsConStock.Count == 0)
        {
            return [];
        }

        var familias = familiaPorArticulo.Values.Distinct().ToList();
        var miembros = familias.Count == 0
            ? []
            : await db.Articulos
                .Where(a => a.IdFamilia != null && familias.Contains(a.IdFamilia.Value))
                .Select(a => a.Id)
                .ToListAsync(ct);
        var ids = idsConStock.Concat(miembros).Distinct().ToList();

        var leidas = await (
            from i in db.ItemsComprobanteCompra
            join c in db.ComprobantesCompra on i.IdComprobanteCompra equals c.Id
            join t in db.TiposComprobante on c.IdTipoComprobante equals t.Id
            where i.IdArticulo != null && ids.Contains(i.IdArticulo.Value)
               && i.ActualizaCosto && i.CostoUnitario > 0m && i.Cantidad > 0m
               && c.Estado == EstadoCompra.Confirmada && c.FechaRecepcion != null && c.FechaRecepcion <= ultimoCorte
               && t.Signo >= 0
            select new
            {
                IdArticulo = i.IdArticulo!.Value, FechaRecepcion = c.FechaRecepcion!.Value, IdComprobante = c.Id, i.Orden,
                i.CostoUnitario, i.Total, i.Cantidad, i.PorcentajeIva, i.ImpuestoInterno, c.DiscriminaIva, c.PreciosIncluyenIva
            })
            .ToListAsync(ct);

        return leidas
            .Select(l => (Linea: l, CostoEfectivo: CalculadorDeCompra.CalcularCostoEfectivoDesdeItem(
                l.Total, l.Cantidad, l.PorcentajeIva, l.DiscriminaIva, l.PreciosIncluyenIva, l.ImpuestoInterno)))
            .Where(x => CalculadorDeCompra.ActualizaElCosto(
                (x.Linea.Orden, x.Linea.IdArticulo, true, x.Linea.CostoUnitario, x.CostoEfectivo)))
            .Select(x => new LineaQueEscribioCosto(
                x.Linea.IdArticulo, x.Linea.FechaRecepcion, x.Linea.IdComprobante, x.Linea.Orden, x.CostoEfectivo))
            .ToList();
    }

    /// <summary>La familia de cada artículo que aparece en las líneas, incluidos los miembros sin stock.</summary>
    private async Task<IReadOnlyDictionary<int, int>> FamiliasDeLasLineasAsync(
        IReadOnlyList<LineaQueEscribioCosto> lineas, IReadOnlyDictionary<int, int> conocidas, CancellationToken ct)
    {
        var faltantes = lineas.Select(l => l.IdArticulo).Distinct().Where(id => !conocidas.ContainsKey(id)).ToList();
        if (faltantes.Count == 0)
        {
            return conocidas;
        }

        var resultado = new Dictionary<int, int>(conocidas);
        var deLosFaltantes = await db.Articulos
            .Where(a => faltantes.Contains(a.Id) && a.IdFamilia != null)
            .Select(a => new { a.Id, IdFamilia = a.IdFamilia!.Value })
            .ToListAsync(ct);
        foreach (var f in deLosFaltantes)
        {
            resultado[f.Id] = f.IdFamilia;
        }

        return resultado;
    }

    private async Task<ListaPrecio?> ListaPredeterminadaAsync(int idEmpresa, CancellationToken ct)
    {
        var deLaEmpresa = await db.ListasPrecio
            .Where(l => l.EsDefault && l.IdEmpresa == idEmpresa)
            .OrderBy(l => l.Id)
            .FirstOrDefaultAsync(ct);
        return deLaEmpresa ?? await db.ListasPrecio
            .Where(l => l.EsDefault && l.IdEmpresa == null)
            .OrderBy(l => l.Id)
            .FirstOrDefaultAsync(ct);
    }

    private async Task<IReadOnlyDictionary<int, decimal?>> PreciosAsync(
        int? idLista, IReadOnlyList<int> idsArticulo, DateTimeOffset corte, CancellationToken ct)
    {
        if (idLista is not { } id || idsArticulo.Count == 0)
        {
            return new Dictionary<int, decimal?>();
        }

        var enLote = await precios.PreciosVigentesEnLoteAsync(idsArticulo, [id], corte, ct);
        return enLote.ToDictionary(p => p.Key.IdArticulo, p => p.Value);
    }
}
