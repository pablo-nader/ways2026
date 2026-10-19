using Microsoft.EntityFrameworkCore;
using Ways.Application.Abstracciones;
using Ways.Application.Precios;
using Ways.Domain.Catalogos;
using Ways.Domain.Common;
using Ways.Domain.Compras;

namespace Ways.Application.Reportes;

/// <param name="VerVentas">Si el usuario puede leer reportes de ventas: sin eso la ficha no consulta ventas.</param>
/// <param name="VerCostos">Si el usuario puede leer costos: sin eso la ficha no consulta costo ni compras.</param>
public sealed record SolicitudDeFichaDeArticulo(string Articulo, string? Empresa, int Compras, bool VerVentas, bool VerCostos);

public sealed record PrecioDeListaDeFicha(string Lista, bool EsDerivada, decimal? Precio);

public sealed record StockDeFicha(string PuntoVenta, decimal Cantidad, decimal? Minimo);

public sealed record VentasDeFicha(DateOnly Desde, DateOnly Hasta, IReadOnlyList<string> Zonas, decimal Cantidad, decimal Importe);

/// <param name="Comprobante">Número del comprobante del proveedor.</param>
/// <param name="Descuento">Importe de la línea (no un porcentaje).</param>
/// <param name="CostoEfectivo">Costo por unidad que la compra escribe en el costo nominal, o nulo si la cantidad es cero.</param>
public sealed record CompraDeFicha(
    DateOnly Fecha, string Proveedor, string Comprobante, decimal Cantidad, decimal CostoUnitario, decimal Descuento,
    decimal? CostoEfectivo);

public sealed record CostosDeFicha(decimal? CostoNominal, int TotalDeCompras, IReadOnlyList<CompraDeFicha> Compras);

public sealed record FichaDeArticulo(
    int Id, string Codigo, string Nombre, bool Activo, bool EsProducto, string UnidadDeVenta, string Area, string Grupo,
    string Categoria, string Marca, string Proveedor, string Alcance, IReadOnlyList<PrecioDeListaDeFicha> Precios,
    IReadOnlyList<StockDeFicha> Stock, VentasDeFicha? Ventas, CostosDeFicha? Costos);

/// <summary>
/// Ficha de un artículo para el conector MCP: precios vigentes por lista, stock por punto de venta, ventas de los
/// últimos 30 días y, solo si el pedido lo permite, costo y últimas compras. Los precios salen de
/// <see cref="ServicioDePrecios.PreciosVigentesEnLoteAsync"/> (la misma regla que el POS y la grilla, derivadas
/// incluidas) y las ventas de <see cref="ServicioDeConsultasDeVentas.VentasDeUnArticuloAsync"/>. Quién puede ver
/// ventas y costos lo decide el llamador con las políticas de la web.
/// </summary>
public class ServicioDeFichaDeArticulo(
    IWaysDbContext db, IRelojDelSistema reloj, ServicioDePrecios precios, ServicioDeConsultasDeVentas consultas)
{
    public const int ComprasPorDefecto = 5;
    public const int ComprasMaximas = 20;
    public const int DiasDeVentas = 30;

    public async Task<FichaDeArticulo> ObtenerAsync(SolicitudDeFichaDeArticulo solicitud, CancellationToken ct = default)
    {
        if (solicitud.Compras is < 1 or > ComprasMaximas)
        {
            throw new ErrorDominio(
                "compras_invalido", $"'compras' tiene que estar entre 1 y {ComprasMaximas}; llegó {solicitud.Compras}.", 400);
        }

        if (string.IsNullOrWhiteSpace(solicitud.Articulo))
        {
            throw new ErrorDominio("articulo_requerido", "Falta 'articulo': id, código interno o nombre.", 400);
        }

        var empresa = await consultas.ResolverEmpresaAsync(solicitud.Empresa, ct);
        var articulo = await consultas.BuscarArticuloAsync(solicitud.Articulo, ct);
        var idEmpresa = empresa?.Id;

        var cabecera = await LeerCabeceraAsync(articulo.Id, ct);
        var listas = await LeerPreciosAsync(articulo.Id, idEmpresa, ct);
        var stock = await LeerStockAsync(articulo.Id, idEmpresa, ct);

        VentasDeFicha? ventas = null;
        if (solicitud.VerVentas)
        {
            var enVentana = await consultas.VentasDeUnArticuloAsync(articulo.Id, idEmpresa?.ToString(), DiasDeVentas, ct);
            ventas = new VentasDeFicha(enVentana.Desde, enVentana.Hasta, enVentana.Zonas, enVentana.Cantidad, enVentana.Importe);
        }

        CostosDeFicha? costos = null;
        if (solicitud.VerCostos)
        {
            costos = new CostosDeFicha(
                cabecera.CostoNominal,
                await ContarComprasAsync(articulo.Id, idEmpresa, ct),
                await LeerComprasAsync(articulo.Id, idEmpresa, solicitud.Compras, ct));
        }

        return new FichaDeArticulo(
            cabecera.Id, cabecera.Codigo, cabecera.Nombre, cabecera.Activo, cabecera.EsProducto,
            cabecera.UnidadDeVenta, cabecera.Area, cabecera.Grupo, cabecera.Categoria, cabecera.Marca, cabecera.Proveedor,
            empresa is null ? "todas las empresas del tenant" : $"empresa {empresa.Nombre}",
            listas, stock, ventas, costos);
    }

    private sealed record Cabecera(
        int Id, string Codigo, string Nombre, bool Activo, bool EsProducto, string UnidadDeVenta, string Area, string Grupo,
        string Categoria, string Marca, string Proveedor, decimal? CostoNominal);

    /// <summary>LEFT JOIN a cada catálogo: una clasificación nula o dada de baja se muestra igual, como "(sin …)".</summary>
    private async Task<Cabecera> LeerCabeceraAsync(int idArticulo, CancellationToken ct)
    {
        var fila = await (
            from a in db.Articulos.AsNoTracking()
            where a.Id == idArticulo
            join ar in db.Areas on a.IdArea equals ar.Id into areas
            from ar in areas.DefaultIfEmpty()
            join g in db.Grupos on a.IdGrupo equals (int?)g.Id into grupos
            from g in grupos.DefaultIfEmpty()
            join c in db.Categorias on a.IdCategoria equals (int?)c.Id into categorias
            from c in categorias.DefaultIfEmpty()
            join m in db.Marcas on a.IdMarca equals (int?)m.Id into marcas
            from m in marcas.DefaultIfEmpty()
            join p in db.Proveedores on a.IdProveedorHabitual equals (int?)p.Id into proveedores
            from p in proveedores.DefaultIfEmpty()
            select new
            {
                a.Id,
                a.CodigoInterno,
                a.Nombre,
                a.Activo,
                a.EsProducto,
                a.UnidadVenta,
                a.CostoNominal,
                Area = ar != null ? ar.Nombre : null,
                Grupo = g != null ? g.Nombre : null,
                Categoria = c != null ? c.Nombre : null,
                Marca = m != null ? m.Nombre : null,
                ProveedorNombreFantasia = p != null ? p.NombreFantasia : null,
                ProveedorRazonSocial = p != null ? p.RazonSocial : null
            }).SingleAsync(ct);

        return new Cabecera(
            fila.Id, fila.CodigoInterno, fila.Nombre, fila.Activo, fila.EsProducto,
            fila.UnidadVenta.ToString().ToLowerInvariant(),
            fila.Area ?? "(sin área)", fila.Grupo ?? "(sin grupo)", fila.Categoria ?? "(sin categoría)",
            fila.Marca ?? "(sin marca)",
            EtiquetaDeProveedor(fila.ProveedorNombreFantasia, fila.ProveedorRazonSocial) ?? "(sin proveedor)",
            fila.CostoNominal);
    }

    private static string? EtiquetaDeProveedor(string? nombreFantasia, string? razonSocial) =>
        !string.IsNullOrWhiteSpace(nombreFantasia) ? nombreFantasia : razonSocial;

    /// <summary>Listas activas visibles; con empresa, las compartidas y las propias de esa empresa. El precio de cada
    /// una lo resuelve <see cref="ServicioDePrecios.PreciosVigentesEnLoteAsync"/> a "ahora".</summary>
    private async Task<IReadOnlyList<PrecioDeListaDeFicha>> LeerPreciosAsync(int idArticulo, int? idEmpresa, CancellationToken ct)
    {
        var listas = await db.ListasPrecio.AsNoTracking()
            .Where(l => l.Activo && (idEmpresa == null || l.IdEmpresa == null || l.IdEmpresa == idEmpresa))
            .OrderByDescending(l => l.EsDefault).ThenBy(l => l.Nombre).ThenBy(l => l.Id)
            .ToListAsync(ct);
        if (listas.Count == 0)
        {
            return [];
        }

        var vigentes = await precios.PreciosVigentesEnLoteAsync([idArticulo], [.. listas.Select(l => l.Id)], reloj.Ahora, ct);

        var idsEmpresaDeListas = listas.Where(l => l.IdEmpresa is not null).Select(l => l.IdEmpresa!.Value).Distinct().ToList();
        var nombresDeEmpresa = idsEmpresaDeListas.Count == 0
            ? []
            : await db.Empresas.AsNoTracking().Where(e => idsEmpresaDeListas.Contains(e.Id))
                .Select(e => new { e.Id, Nombre = e.NombreFantasia ?? e.RazonSocial })
                .ToDictionaryAsync(e => e.Id, e => e.Nombre, ct);

        return
        [
            .. listas.Select(l => new PrecioDeListaDeFicha(
                l.IdEmpresa is { } propia && idEmpresa is null
                    ? $"{l.Nombre} (empresa {nombresDeEmpresa.GetValueOrDefault(propia) ?? $"id {propia}"})"
                    : l.Nombre,
                l.Modo == ModoLista.Derivada,
                vigentes.GetValueOrDefault((idArticulo, l.Id))))
        ];
    }

    /// <summary>Una fila por punto de venta con saldo registrado. El tenant lo aísla el filtro de EF de <c>stock</c> y
    /// la FK compuesta <c>(id_articulo, id_tenant)</c>: las filas de stock de un artículo solo pueden ser de su tenant,
    /// y el artículo ya salió de una búsqueda con el filtro de tenant. Un punto de venta dado de baja que conserva
    /// stock se muestra como "(punto de venta id N)".</summary>
    private async Task<IReadOnlyList<StockDeFicha>> LeerStockAsync(int idArticulo, int? idEmpresa, CancellationToken ct)
    {
        var filas = await (
            from s in db.Stock.AsNoTracking()
            where s.IdArticulo == idArticulo
            join pv in db.PuntosVenta on s.IdPuntoVenta equals pv.Id into puntos
            from pv in puntos.DefaultIfEmpty()
            where idEmpresa == null || (pv != null && pv.IdEmpresa == idEmpresa)
            orderby s.IdPuntoVenta
            select new
            {
                s.IdPuntoVenta,
                Nombre = pv != null ? pv.Nombre : null,
                IdEmpresa = pv != null ? (int?)pv.IdEmpresa : null,
                s.Cantidad,
                s.Minimo
            }).ToListAsync(ct);

        var variasEmpresas = filas.Where(f => f.IdEmpresa is not null).Select(f => f.IdEmpresa).Distinct().Count() > 1;
        var nombresDeEmpresa = variasEmpresas
            ? await db.Empresas.AsNoTracking().Select(e => new { e.Id, Nombre = e.NombreFantasia ?? e.RazonSocial })
                .ToDictionaryAsync(e => e.Id, e => e.Nombre, ct)
            : [];

        return
        [
            .. filas.Select(f =>
            {
                var nombre = f.Nombre ?? $"(punto de venta id {f.IdPuntoVenta})";
                if (variasEmpresas && f.IdEmpresa is { } empresa)
                {
                    nombre = $"{nombre} ({nombresDeEmpresa.GetValueOrDefault(empresa) ?? $"empresa id {empresa}"})";
                }

                return new StockDeFicha(nombre, f.Cantidad, f.Minimo);
            })
        ];
    }

    /// <summary>Compras confirmadas con una línea de este artículo (borradores y anuladas no cuentan), en los puntos
    /// de venta de la empresa si se indicó una.</summary>
    private IQueryable<LineaDeCompra> ComprasConfirmadas(int idArticulo, int? idEmpresa) =>
        from i in db.ItemsComprobanteCompra
        where i.IdArticulo == idArticulo
        join c in db.ComprobantesCompra on i.IdComprobanteCompra equals c.Id
        where c.Estado == EstadoCompra.Confirmada
            && (idEmpresa == null || db.PuntosVenta.Any(pv => pv.Id == c.IdPuntoVenta && pv.IdEmpresa == idEmpresa))
        select new LineaDeCompra
        {
            IdItem = i.Id,
            IdComprobante = c.Id,
            IdProveedor = c.IdProveedor,
            FechaComprobante = c.FechaComprobante,
            FechaRecepcion = c.FechaRecepcion,
            NumeroExterno = c.NumeroExterno,
            DiscriminaIva = c.DiscriminaIva,
            PreciosIncluyenIva = c.PreciosIncluyenIva,
            Cantidad = i.Cantidad,
            CostoUnitario = i.CostoUnitario,
            Descuento = i.Descuento,
            Total = i.Total,
            PorcentajeIva = i.PorcentajeIva
        };

    // Clase con inicializador de miembros y no un record posicional: EF no traduce un OrderBy sobre una proyección por constructor.
    private sealed class LineaDeCompra
    {
        public int IdItem { get; init; }
        public int IdComprobante { get; init; }
        public int IdProveedor { get; init; }
        public DateOnly? FechaComprobante { get; init; }
        public DateTimeOffset? FechaRecepcion { get; init; }
        public string? NumeroExterno { get; init; }
        public bool DiscriminaIva { get; init; }
        public bool PreciosIncluyenIva { get; init; }
        public decimal Cantidad { get; init; }
        public decimal CostoUnitario { get; init; }
        public decimal Descuento { get; init; }
        public decimal Total { get; init; }
        public decimal PorcentajeIva { get; init; }
    }

    private Task<int> ContarComprasAsync(int idArticulo, int? idEmpresa, CancellationToken ct) =>
        ComprasConfirmadas(idArticulo, idEmpresa).CountAsync(ct);

    /// <summary>Las últimas compras por recepción descendente (el momento en que se confirmaron), y a igual recepción
    /// por comprobante y por línea descendentes. El costo efectivo es el mismo cálculo con que la compra escribe el
    /// costo nominal (<see cref="CalculadorDeCompra.CalcularCostoEfectivoDesdeItem"/>).</summary>
    private async Task<IReadOnlyList<CompraDeFicha>> LeerComprasAsync(int idArticulo, int? idEmpresa, int tope, CancellationToken ct)
    {
        var lineas = await ComprasConfirmadas(idArticulo, idEmpresa)
            .OrderByDescending(l => l.FechaRecepcion)
            .ThenByDescending(l => l.IdComprobante)
            .ThenByDescending(l => l.IdItem)
            .Take(tope)
            .ToListAsync(ct);

        var idsProveedor = lineas.Select(l => l.IdProveedor).Distinct().ToList();
        var proveedores = await db.Proveedores.AsNoTracking()
            .Where(p => idsProveedor.Contains(p.Id))
            .Select(p => new { p.Id, p.NombreFantasia, p.RazonSocial })
            .ToDictionaryAsync(p => p.Id, p => EtiquetaDeProveedor(p.NombreFantasia, p.RazonSocial), ct);

        return
        [
            .. lineas.Select(l => new CompraDeFicha(
                l.FechaComprobante ?? default,
                proveedores.GetValueOrDefault(l.IdProveedor) ?? $"(proveedor id {l.IdProveedor})",
                l.NumeroExterno ?? string.Empty,
                l.Cantidad,
                l.CostoUnitario,
                l.Descuento,
                l.Cantidad == 0m
                    ? null
                    : CalculadorDeCompra.CalcularCostoEfectivoDesdeItem(
                        l.Total, l.Cantidad, l.PorcentajeIva, l.DiscriminaIva, l.PreciosIncluyenIva)))
        ];
    }
}
