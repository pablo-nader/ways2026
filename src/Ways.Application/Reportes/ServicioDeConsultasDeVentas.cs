using System.Globalization;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Ways.Application.Abstracciones;
using Ways.Application.Parametros;
using Ways.Domain.Catalogos;
using Ways.Domain.Common;
using Ways.Domain.Reportes;
using Ways.Domain.Ventas;

namespace Ways.Application.Reportes;

public enum AgrupacionDeVentas
{
    PuntoVenta,
    MedioPago,
    Vendedor,
    Dia,
    Area,
    Grupo,
    Categoria,
    Marca
}

public enum OrdenDeArticulos
{
    Importe,
    Cantidad
}

/// <summary>Lo que pide el conector MCP: un período (nombre o fechas) y, opcionalmente, empresa y
/// punto de venta por id o por nombre. Todo llega como texto y se interpreta acá.</summary>
public sealed record SolicitudDeConsultaDeVentas(
    string? Periodo, string? Desde, string? Hasta, string? Empresa, string? PuntoVenta);

public sealed record EmpresaConsultada(
    int IdEmpresa, string Nombre, string ZonaHoraria, DateOnly Desde, DateOnly Hasta, decimal Neto, int CantidadTx);

/// <summary>Una fila de una tabla de la consulta: <see cref="Cantidad"/> cuenta comprobantes, salvo
/// en el desglose por medio de pago, donde cuenta pagos, y en área, categoría y marca, donde suma las
/// unidades de las líneas.</summary>
public sealed record FilaDeVentas(string Grupo, decimal Importe, decimal Cantidad);

public sealed record ResumenDeVentasConsultado(
    string? Periodo, DateOnly Desde, DateOnly Hasta, IReadOnlyList<string> Zonas, string Alcance,
    bool FiltraPuntoVenta, decimal NetoVendido, int CantidadTx, decimal? TicketPromedio, int CantidadNcx, decimal NetoNcx,
    IReadOnlyList<EmpresaConsultada> Empresas, IReadOnlyList<FilaDeVentas> PorMedioPago,
    IReadOnlyList<FilaDeVentas> PorPuntoVenta, IReadOnlyList<FilaDeVentas>? PorDia);

public sealed record SolicitudDeVentasPorArticulo(
    SolicitudDeConsultaDeVentas Consulta, string? Texto, string? Area, string? Grupo, string? Categoria, string? Marca,
    string? Proveedor, string? Articulo, OrdenDeArticulos Orden, int Limite);

public sealed record FilaDeArticulo(
    int IdArticulo, string? Codigo, string Articulo, string? Area, string? Grupo, string? Categoria, string? Marca,
    decimal Cantidad, decimal Importe);

/// <param name="ImporteDeTodosLosArticulos">Total de todas las líneas con artículo del período y alcance, sin los
/// filtros de artículo: es el denominador del porcentaje.</param>
/// <param name="ImporteFiltrado">Total de los artículos que cumplen los filtros (todos, no solo los devueltos).</param>
/// <param name="CantidadDeArticulos">Cuántos artículos distintos cumplen los filtros.</param>
public sealed record VentasPorArticuloConsultadas(
    string? Periodo, DateOnly Desde, DateOnly Hasta, IReadOnlyList<string> Zonas, string Alcance,
    IReadOnlyList<string> Filtros, OrdenDeArticulos Orden, decimal ImporteDeTodosLosArticulos, decimal ImporteFiltrado,
    int CantidadDeArticulos, IReadOnlyList<FilaDeArticulo> Filas);

public sealed record EmpresaResuelta(int Id, string Nombre);

public sealed record ArticuloResuelto(int Id, string Codigo, string Nombre);

/// <param name="Desde">Primer día de la ventana (el más antiguo entre las zonas de las empresas).</param>
/// <param name="Hasta">Último día de la ventana: el "hoy" local de la empresa (el más reciente entre las zonas).</param>
public sealed record VentasDeArticuloEnVentana(
    DateOnly Desde, DateOnly Hasta, IReadOnlyList<string> Zonas, decimal Cantidad, decimal Importe);

/// <param name="Total">Suma de <see cref="Filas"/>: para punto de venta, vendedor y día coincide con
/// lo vendido; para medio de pago es lo cobrado neto de vuelto.</param>
public sealed record AgrupacionConsultada(
    string? Periodo, DateOnly Desde, DateOnly Hasta, IReadOnlyList<string> Zonas, string Alcance,
    AgrupacionDeVentas Por, decimal Total, IReadOnlyList<FilaDeVentas> Filas);

/// <summary>
/// Consultas de ventas del conector MCP. No calcula "vendido" por su cuenta: delega en
/// <see cref="ServicioDeReportesDeVentas"/> (mismo criterio que el reporte de la web) y solo suma lo
/// que el reporte entrega por empresa. La excepción es el desglose por medio de pago, que es una
/// consulta propia porque descuenta el vuelto (ver <see cref="ConsultarMediosDePagoAsync"/>), y las
/// consultas por artículo (área, grupo, categoría, marca y artículo), que salen de
/// <see cref="ServicioDeReportesDeArticulos.ObtenerVentasPorArticuloAsync"/>: la clasificación es la ACTUAL
/// del artículo y las líneas sin artículo no cuentan.
/// Cada empresa resuelve su propia zona horaria y su propio "hoy".
/// </summary>
public class ServicioDeConsultasDeVentas(
    IWaysDbContext db, IRelojDelSistema reloj, ServicioDeParametros parametros, ServicioDeReportesDeVentas reportes,
    ServicioDeReportesDeArticulos articulos)
{
    private static readonly IReadOnlyDictionary<string, AgrupacionDeVentas> Agrupaciones = new Dictionary<string, AgrupacionDeVentas>
    {
        ["punto_venta"] = AgrupacionDeVentas.PuntoVenta,
        ["medio_pago"] = AgrupacionDeVentas.MedioPago,
        ["vendedor"] = AgrupacionDeVentas.Vendedor,
        ["dia"] = AgrupacionDeVentas.Dia,
        ["area"] = AgrupacionDeVentas.Area,
        ["grupo"] = AgrupacionDeVentas.Grupo,
        ["categoria"] = AgrupacionDeVentas.Categoria,
        ["marca"] = AgrupacionDeVentas.Marca
    };

    private const int DiasMaximosDeLaTablaPorDia = 31;

    public static IReadOnlyCollection<string> NombresDeAgrupacion => Agrupaciones.Keys.ToList();

    public static AgrupacionDeVentas ParsearAgrupacion(string? texto) =>
        texto is not null && Agrupaciones.TryGetValue(texto.Trim().ToLowerInvariant(), out var agrupacion)
            ? agrupacion
            : throw new ErrorDominio(
                "agrupacion_desconocida",
                $"'agrupar_por' tiene que ser uno de: {string.Join(", ", Agrupaciones.Keys)}.",
                400);

    public async Task<ResumenDeVentasConsultado> ResumirAsync(SolicitudDeConsultaDeVentas solicitud, CancellationToken ct = default)
    {
        var alcance = await ResolverAlcanceAsync(solicitud, ct);

        decimal neto = 0, netoNcx = 0, netoTx = 0;
        int cantidadTx = 0, cantidadNcx = 0;
        var empresas = new List<EmpresaConsultada>();
        var medios = new Dictionary<int, Acumulado>();
        var puntos = new Dictionary<int, Acumulado>();
        var dias = new Dictionary<int, Acumulado>();

        foreach (var contexto in alcance.Contextos)
        {
            var empresa = contexto.Empresa;
            var resumen = await reportes.ObtenerResumenAsync(
                empresa.Id, empresa.IdPuntoVenta, contexto.Desde, contexto.Hasta, Granularidad.Dia, ct);

            neto += resumen.NetoVendido;
            cantidadTx += resumen.CantidadTx;
            cantidadNcx += resumen.CantidadNcx;
            netoNcx += resumen.NetoNcx;
            netoTx += resumen.NetoVendido - resumen.NetoNcx;
            empresas.Add(new EmpresaConsultada(
                empresa.Id, empresa.Nombre, contexto.ZonaId, contexto.Desde, contexto.Hasta, resumen.NetoVendido, resumen.CantidadTx));

            foreach (var bucket in resumen.Serie)
            {
                Sumar(dias, bucket.Inicio.DayNumber, bucket.Neto, bucket.CantidadTx);
            }

            foreach (var fila in await ConsultarMediosDePagoAsync(contexto, ct))
            {
                Sumar(medios, fila.Key, fila.Value.Importe, fila.Value.Cantidad);
            }

            if (empresa.IdPuntoVenta is null)
            {
                var porPuntoVenta = await reportes.ObtenerPorPuntoVentaAsync(empresa.Id, contexto.Desde, contexto.Hasta, ct);
                foreach (var fila in porPuntoVenta.Filas)
                {
                    Sumar(puntos, fila.IdPuntoVenta, fila.Neto, fila.CantidadTx);
                }
            }
        }

        var primero = alcance.Contextos[0];
        var porDia = dias.Count <= DiasMaximosDeLaTablaPorDia
            ? dias.OrderBy(d => d.Key)
                .Select(d => new FilaDeVentas(
                    DateOnly.FromDayNumber(d.Key).ToString("yyyy-MM-dd", CultureInfo.InvariantCulture), d.Value.Importe, d.Value.Cantidad))
                .ToList()
            : null;

        return new ResumenDeVentasConsultado(
            NombreSiEsConNombre(solicitud.Periodo),
            alcance.Contextos.Min(c => c.Desde),
            alcance.Contextos.Max(c => c.Hasta),
            alcance.Contextos.Select(c => c.ZonaId).Distinct().ToList(),
            alcance.Descripcion,
            primero.Empresa.IdPuntoVenta is not null,
            neto, cantidadTx, cantidadTx > 0 ? netoTx / cantidadTx : null, cantidadNcx, netoNcx,
            empresas,
            await NombrarAsync(AgrupacionDeVentas.MedioPago, medios, alcance, ct),
            await NombrarAsync(AgrupacionDeVentas.PuntoVenta, puntos, alcance, ct),
            porDia);
    }

    public async Task<AgrupacionConsultada> AgruparAsync(
        SolicitudDeConsultaDeVentas solicitud, AgrupacionDeVentas por, CancellationToken ct = default)
    {
        var alcance = await ResolverAlcanceAsync(solicitud, ct);

        if (por is AgrupacionDeVentas.Area or AgrupacionDeVentas.Grupo or AgrupacionDeVentas.Categoria or AgrupacionDeVentas.Marca)
        {
            return await AgruparPorClasificacionAsync(solicitud, por, alcance, ct);
        }

        var acumulados = new Dictionary<int, Acumulado>();

        foreach (var contexto in alcance.Contextos)
        {
            var empresa = contexto.Empresa;
            switch (por)
            {
                case AgrupacionDeVentas.Dia:
                    var serie = await reportes.ObtenerResumenAsync(
                        empresa.Id, empresa.IdPuntoVenta, contexto.Desde, contexto.Hasta, Granularidad.Dia, ct);
                    foreach (var bucket in serie.Serie)
                    {
                        Sumar(acumulados, bucket.Inicio.DayNumber, bucket.Neto, bucket.CantidadTx);
                    }

                    break;
                case AgrupacionDeVentas.PuntoVenta when empresa.IdPuntoVenta is { } idPuntoVenta:
                    // Con punto de venta elegido se usa el resumen de ese punto: así el corte de día
                    // sale de la misma zona con la que se calculó el período.
                    var delPunto = await reportes.ObtenerResumenAsync(
                        empresa.Id, idPuntoVenta, contexto.Desde, contexto.Hasta, Granularidad.Dia, ct);
                    Sumar(acumulados, idPuntoVenta, delPunto.NetoVendido, delPunto.CantidadTx);
                    break;
                case AgrupacionDeVentas.PuntoVenta:
                    var porPuntoVenta = await reportes.ObtenerPorPuntoVentaAsync(empresa.Id, contexto.Desde, contexto.Hasta, ct);
                    foreach (var fila in porPuntoVenta.Filas)
                    {
                        Sumar(acumulados, fila.IdPuntoVenta, fila.Neto, fila.CantidadTx);
                    }

                    break;
                case AgrupacionDeVentas.Vendedor:
                    var porVendedor = await reportes.ObtenerPorVendedorAsync(
                        empresa.Id, empresa.IdPuntoVenta, contexto.Desde, contexto.Hasta, ct);
                    foreach (var fila in porVendedor.Filas)
                    {
                        Sumar(acumulados, fila.IdEmpleado, fila.Neto, fila.CantidadTx);
                    }

                    break;
                case AgrupacionDeVentas.MedioPago:
                    foreach (var fila in await ConsultarMediosDePagoAsync(contexto, ct))
                    {
                        Sumar(acumulados, fila.Key, fila.Value.Importe, fila.Value.Cantidad);
                    }

                    break;
                default:
                    throw new ArgumentOutOfRangeException(nameof(por));
            }
        }

        var filas = await NombrarAsync(por, acumulados, alcance, ct);

        return new AgrupacionConsultada(
            NombreSiEsConNombre(solicitud.Periodo),
            alcance.Contextos.Min(c => c.Desde),
            alcance.Contextos.Max(c => c.Hasta),
            alcance.Contextos.Select(c => c.ZonaId).Distinct().ToList(),
            alcance.Descripcion,
            por,
            filas.Sum(f => f.Importe),
            filas);
    }

    /// <summary>Artículos vendidos, tal como los entrega el reporte de ventas por artículo de la web (por
    /// empresa, sin líneas sin artículo; área, grupo y categoría con sus descendientes son los ACTUALES del
    /// artículo). El área, el grupo, la categoría y el artículo se pasan al reporte; el texto, la marca y el
    /// proveedor, que el reporte no conoce, recortan sus filas acá por los ids de los artículos que coinciden.
    /// El denominador del porcentaje (<see cref="VentasPorArticuloConsultadas.ImporteDeTodosLosArticulos"/>) es
    /// la suma de las filas de una llamada al reporte SIN ningún filtro, de modo que "qué parte de lo vendido es X"
    /// no depende de lo que se filtró. Cada filtro por nombre se resuelve contra los registros visibles.</summary>
    public async Task<VentasPorArticuloConsultadas> PorArticuloAsync(
        SolicitudDeVentasPorArticulo solicitud, CancellationToken ct = default)
    {
        var alcance = await ResolverAlcanceAsync(solicitud.Consulta, ct);
        var filtros = new List<string>();

        var area = await ResolverFiltroAsync(solicitud.Area, "el área", db.Areas.AsNoTracking().OrderBy(a => a.Id).Select(a => new CandidatoSimple(a.Id, a.Nombre)), filtros, "área", ct);
        var grupo = await ResolverFiltroAsync(solicitud.Grupo, "el grupo", db.Grupos.AsNoTracking().OrderBy(g => g.Id).Select(g => new CandidatoSimple(g.Id, g.Nombre)), filtros, "grupo", ct);
        var categoria = await ResolverFiltroAsync(
            solicitud.Categoria, "la categoría", db.Categorias.AsNoTracking().OrderBy(c => c.Id).Select(c => new CandidatoSimple(c.Id, c.Nombre)), filtros, "categoría", ct);
        var marca = await ResolverFiltroAsync(solicitud.Marca, "la marca", db.Marcas.AsNoTracking().OrderBy(m => m.Id).Select(m => new CandidatoSimple(m.Id, m.Nombre)), filtros, "marca", ct);
        var proveedor = await ResolverFiltroAsync(
            solicitud.Proveedor, "el proveedor",
            db.Proveedores.AsNoTracking().OrderBy(p => p.Id).Select(p => new CandidatoSimple(p.Id, p.NombreFantasia ?? p.RazonSocial)), filtros, "proveedor", ct);
        var articulo = await ResolverArticuloAsync(solicitud.Articulo, filtros, ct);

        var texto = string.IsNullOrWhiteSpace(solicitud.Texto) ? null : solicitud.Texto.Trim();
        if (texto is not null)
        {
            filtros.Insert(0, $"texto '{texto}'");
        }

        var filasFiltradas = await LeerArticulosVendidosAsync(alcance, area?.Id, grupo?.Id, categoria?.Id, articulo?.Id, ct);
        var hayFiltroDelReporte = area is not null || grupo is not null || categoria is not null || articulo is not null;
        var filasDeTodos = hayFiltroDelReporte
            ? await LeerArticulosVendidosAsync(alcance, null, null, null, null, ct)
            : filasFiltradas;
        var deTodos = filasDeTodos.Sum(f => f.Importe);

        if (texto is not null || marca is not null || proveedor is not null)
        {
            var coinciden = await IdsDeArticulosQueCoincidenAsync(
                filasFiltradas.Select(f => f.IdArticulo).ToList(), texto, marca?.Id, proveedor?.Id, ct);
            filasFiltradas = [.. filasFiltradas.Where(f => coinciden.Contains(f.IdArticulo))];
        }

        var importeFiltrado = filasFiltradas.Sum(f => f.Importe);
        var ordenados = (solicitud.Orden == OrdenDeArticulos.Cantidad
                ? filasFiltradas.OrderByDescending(f => f.Cantidad).ThenByDescending(f => f.Importe)
                : filasFiltradas.OrderByDescending(f => f.Importe).ThenByDescending(f => f.Cantidad))
            .ThenBy(f => f.IdArticulo)
            .ToList();

        return new VentasPorArticuloConsultadas(
            NombreSiEsConNombre(solicitud.Consulta.Periodo),
            alcance.Contextos.Min(c => c.Desde),
            alcance.Contextos.Max(c => c.Hasta),
            alcance.Contextos.Select(c => c.ZonaId).Distinct().ToList(),
            alcance.Descripcion,
            filtros,
            solicitud.Orden,
            deTodos,
            importeFiltrado,
            ordenados.Count,
            [.. ordenados.Take(solicitud.Limite)]);
    }

    /// <summary>Ventas por área, grupo, categoría o marca del artículo ACTUAL, a partir de las filas del
    /// reporte de ventas por artículo sin filtros. Se agrupa por el ID de la clasificación, no por su nombre: dos
    /// categorías con el mismo nombre siguen siendo dos filas, y solo en ese caso la etiqueta lleva "(id N)". Un
    /// artículo sin esa clasificación, con la clasificación dada de baja o que ya no es visible va a "(sin …)". La
    /// clasificación se lee por id de artículo, con los nombres de un catálogo que solo trae filas visibles.</summary>
    private async Task<AgrupacionConsultada> AgruparPorClasificacionAsync(
        SolicitudDeConsultaDeVentas solicitud, AgrupacionDeVentas por, AlcanceResuelto alcance, CancellationToken ct)
    {
        var filas = await LeerArticulosVendidosAsync(alcance, null, null, null, null, ct);
        var idsDeArticulo = filas.Select(f => f.IdArticulo).ToList();

        var clasificacion = await db.Articulos.AsNoTracking()
            .Where(a => idsDeArticulo.Contains(a.Id))
            .Select(a => new
            {
                a.Id,
                Clave = por == AgrupacionDeVentas.Area ? (int?)a.IdArea
                    : por == AgrupacionDeVentas.Grupo ? a.IdGrupo
                    : por == AgrupacionDeVentas.Categoria ? a.IdCategoria
                    : a.IdMarca
            })
            .ToDictionaryAsync(a => a.Id, a => a.Clave, ct);

        var idsDeClasificacion = clasificacion.Values.Where(v => v is not null).Select(v => v!.Value).Distinct().ToList();
        var (sin, nombres) = por switch
        {
            AgrupacionDeVentas.Area => ("(sin área)", await NombresVisiblesAsync(db.Areas, idsDeClasificacion, ct)),
            AgrupacionDeVentas.Grupo => ("(sin grupo)", await NombresVisiblesAsync(db.Grupos, idsDeClasificacion, ct)),
            AgrupacionDeVentas.Categoria => ("(sin categoría)", await NombresVisiblesAsync(db.Categorias, idsDeClasificacion, ct)),
            _ => ("(sin marca)", await NombresVisiblesAsync(db.Marcas, idsDeClasificacion, ct))
        };

        var agrupadas = filas
            .GroupBy(f => clasificacion.GetValueOrDefault(f.IdArticulo) is { } id && nombres.ContainsKey(id) ? id : (int?)null)
            .Select(g => new { Clave = g.Key, Importe = g.Sum(f => f.Importe), Cantidad = g.Sum(f => f.Cantidad) })
            .ToList();

        var homonimos = agrupadas
            .Where(g => g.Clave is not null)
            .GroupBy(g => nombres[g.Clave!.Value], StringComparer.OrdinalIgnoreCase)
            .Where(g => g.Count() > 1)
            .Select(g => g.Key)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        var grupos = agrupadas
            .Select(g =>
            {
                if (g.Clave is not { } clave)
                {
                    return new FilaDeVentas(sin, g.Importe, g.Cantidad);
                }

                var nombre = nombres[clave];
                return new FilaDeVentas(homonimos.Contains(nombre) ? $"{nombre} (id {clave})" : nombre, g.Importe, g.Cantidad);
            })
            .OrderByDescending(f => f.Importe)
            .ThenBy(f => f.Grupo, StringComparer.OrdinalIgnoreCase)
            .ToList();

        return new AgrupacionConsultada(
            NombreSiEsConNombre(solicitud.Periodo),
            alcance.Contextos.Min(c => c.Desde),
            alcance.Contextos.Max(c => c.Hasta),
            alcance.Contextos.Select(c => c.ZonaId).Distinct().ToList(),
            alcance.Descripcion,
            por,
            grupos.Sum(f => f.Importe),
            grupos);
    }

    private static async Task<Dictionary<int, string>> NombresVisiblesAsync<T>(
        IQueryable<T> catalogo, IReadOnlyCollection<int> ids, CancellationToken ct) where T : CatalogoSimple =>
        await catalogo.Where(c => ids.Contains(c.Id)).Select(c => new { c.Id, c.Nombre }).ToDictionaryAsync(c => c.Id, c => c.Nombre, ct);

    /// <summary>Una llamada al reporte por empresa del alcance, sumando por artículo entre empresas.</summary>
    private async Task<List<FilaDeArticulo>> LeerArticulosVendidosAsync(
        AlcanceResuelto alcance, int? idArea, int? idGrupo, int? idCategoria, int? idArticulo, CancellationToken ct)
    {
        var porArticulo = new Dictionary<int, FilaDeArticulo>();
        foreach (var contexto in alcance.Contextos)
        {
            var reporte = await articulos.ObtenerVentasPorArticuloAsync(
                contexto.Empresa.Id, contexto.Empresa.IdPuntoVenta, contexto.Desde, contexto.Hasta,
                idArea, idGrupo, idCategoria, idArticulo, ct);
            foreach (var fila in reporte.Articulos)
            {
                porArticulo[fila.IdArticulo] = porArticulo.TryGetValue(fila.IdArticulo, out var previa)
                    ? previa with { Cantidad = previa.Cantidad + fila.Cantidad, Importe = previa.Importe + fila.Total }
                    : new FilaDeArticulo(fila.IdArticulo, fila.Codigo, fila.Descripcion, fila.Area, fila.Grupo, fila.Categoria, null, fila.Cantidad, fila.Total);
            }
        }

        return [.. porArticulo.Values];
    }

    private async Task<HashSet<int>> IdsDeArticulosQueCoincidenAsync(
        IReadOnlyCollection<int> idsVendidos, string? texto, int? idMarca, int? idProveedor, CancellationToken ct)
    {
        var consulta = db.Articulos.Where(a => idsVendidos.Contains(a.Id));

        if (texto is not null)
        {
            var patron = $"%{PatronLike(texto)}%";
            consulta = consulta.Where(a => EF.Functions.Like(a.Nombre.ToLower(), patron) || EF.Functions.Like(a.CodigoInterno.ToLower(), patron));
        }

        if (idMarca is { } marca)
        {
            consulta = consulta.Where(a => a.IdMarca == marca);
        }

        if (idProveedor is { } proveedor)
        {
            consulta = consulta.Where(a => a.IdProveedorHabitual == proveedor);
        }

        return [.. await consulta.Select(a => a.Id).ToListAsync(ct)];
    }

    private static string PatronLike(string texto) =>
        texto.ToLowerInvariant().Replace("\\", "\\\\").Replace("%", "\\%").Replace("_", "\\_");

    private async Task<CandidatoSimple?> ResolverArticuloAsync(string? texto, List<string> descripciones, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(texto))
        {
            return null;
        }

        var elegido = await BuscarArticuloAsync(texto, ct);
        descripciones.Add($"artículo {elegido.Nombre}");
        return new CandidatoSimple(elegido.Id, elegido.Nombre);
    }

    private const int MaximoDeCandidatos = 10;

    /// <summary>Un artículo visible por texto, con coincidencia exacta primero y cada paso resuelto en SQL antes de
    /// cualquier tope de filas: id, código interno (sin distinguir mayúsculas), código de barras activo (sin distinguir
    /// mayúsculas), nombre exacto (sin distinguir mayúsculas) y, recién entonces, el que contiene el texto en el nombre o en el código. Un
    /// paso con una sola coincidencia termina la búsqueda; con más de una es ambiguo (hasta 10 candidatos listados) y
    /// no sigue al paso siguiente; si ningún paso encuentra nada, no existe.</summary>
    public async Task<ArticuloResuelto> BuscarArticuloAsync(string texto, CancellationToken ct = default)
    {
        var buscado = texto.Trim();
        var exacto = PatronLike(buscado);
        var patron = $"%{PatronLike(buscado)}%";
        var visibles = db.Articulos.AsNoTracking();

        var pasos = new List<IQueryable<Domain.Articulos.Articulo>>();
        if (int.TryParse(buscado, NumberStyles.None, CultureInfo.InvariantCulture, out var id))
        {
            pasos.Add(visibles.Where(a => a.Id == id));
        }

        pasos.Add(visibles.Where(a => EF.Functions.Like(a.CodigoInterno.ToLower(), exacto)));
        pasos.Add(visibles.Where(a => db.CodigosBarra.Any(c => c.IdArticulo == a.Id && c.Activo && EF.Functions.Like(c.Codigo.ToLower(), exacto))));
        pasos.Add(visibles.Where(a => EF.Functions.Like(a.Nombre.ToLower(), exacto)));
        pasos.Add(visibles.Where(a => EF.Functions.Like(a.Nombre.ToLower(), patron) || EF.Functions.Like(a.CodigoInterno.ToLower(), patron)));

        foreach (var paso in pasos)
        {
            var coinciden = await paso
                .OrderBy(a => a.Id)
                .Take(MaximoDeCandidatos + 1)
                .Select(a => new ArticuloResuelto(a.Id, a.CodigoInterno, a.Nombre))
                .ToListAsync(ct);
            if (coinciden.Count == 1)
            {
                return coinciden[0];
            }

            if (coinciden.Count > 1)
            {
                var lista = string.Join(Environment.NewLine, coinciden.Take(MaximoDeCandidatos).Select(c => $"{c.Id} | {c.Codigo} | {c.Nombre}"));
                var mas = coinciden.Count > MaximoDeCandidatos ? $"{Environment.NewLine}… y más" : string.Empty;
                throw new ErrorDominio(
                    "alcance_ambiguo",
                    $"'{texto}' es ambiguo para el artículo; coincide con (id | código | nombre):{Environment.NewLine}{lista}{mas}" +
                    $"{Environment.NewLine}Indicá el id o el código exacto.",
                    400);
            }
        }

        throw new ErrorDominio("alcance_no_encontrado", $"No encontré el artículo '{texto}'.", 404);
    }

    /// <summary>Empresa del tenant por id o por nombre, o <c>null</c> si no se indicó ninguna.</summary>
    public async Task<EmpresaResuelta?> ResolverEmpresaAsync(string? texto, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(texto))
        {
            return null;
        }

        var empresas = await db.Empresas.AsNoTracking().OrderBy(e => e.Id)
            .Select(e => new CandidatoEmpresa(e.Id, e.NombreFantasia ?? e.RazonSocial, e.RazonSocial))
            .ToListAsync(ct);
        var elegida = Buscar(texto, "la empresa", empresas, e => e.Id, e => [e.Nombre, e.RazonSocial], e => e.Nombre);
        return new EmpresaResuelta(elegida.Id, elegida.Nombre);
    }

    /// <summary>Cantidad e importe vendidos de un artículo en los últimos <paramref name="dias"/> días, contando el
    /// de hoy: desde <c>hoy - (dias - 1)</c> hasta <c>hoy</c>, con el "hoy" y la zona de cada empresa del alcance
    /// (todas las del tenant si no se indica una). Es la misma fila del reporte de ventas por artículo.</summary>
    public async Task<VentasDeArticuloEnVentana> VentasDeUnArticuloAsync(
        int idArticulo, string? empresa, int dias, CancellationToken ct = default)
    {
        var alcance = await ResolverAlcanceAsync(new SolicitudDeConsultaDeVentas(null, null, null, empresa, null), ct, dias);
        var filas = await LeerArticulosVendidosAsync(alcance, null, null, null, idArticulo, ct);

        return new VentasDeArticuloEnVentana(
            alcance.Contextos.Min(c => c.Desde),
            alcance.Contextos.Max(c => c.Hasta),
            alcance.Contextos.Select(c => c.ZonaId).Distinct().ToList(),
            filas.Sum(f => f.Cantidad),
            filas.Sum(f => f.Importe));
    }

    public static OrdenDeArticulos ParsearOrden(string? texto) =>
        string.IsNullOrWhiteSpace(texto) || texto.Trim().Equals("importe", StringComparison.OrdinalIgnoreCase)
            ? OrdenDeArticulos.Importe
            : texto.Trim().Equals("cantidad", StringComparison.OrdinalIgnoreCase)
                ? OrdenDeArticulos.Cantidad
                : throw new ErrorDominio("orden_invalido", "'orden' tiene que ser importe o cantidad.", 400);

    private static async Task<CandidatoSimple?> ResolverFiltroAsync(
        string? texto, string que, IQueryable<CandidatoSimple> visibles, List<string> descripciones, string nombreDelFiltro,
        CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(texto))
        {
            return null;
        }

        var candidatos = await visibles.ToListAsync(ct);
        var elegido = Buscar(texto, que, candidatos, c => c.Id, c => [c.Nombre], c => c.Nombre);
        descripciones.Add($"{nombreDelFiltro} {elegido.Nombre}");
        return elegido;
    }

    /// <summary>Lo cobrado por medio de pago, NETO del vuelto: <c>pagos_comprobante.importe</c> guarda
    /// lo entregado por el cliente y el vuelto va aparte, así que el efectivo realmente cobrado es
    /// <c>importe - vuelto</c>. Los mismos filtros de comprobante que el reporte de ventas (clase venta,
    /// no anulado, sin espejos fiscales, puntos de venta del alcance, fecha del rango) y el signo del
    /// tipo de comprobante, de modo que una nota de crédito resta. Es un método aparte a propósito:
    /// el reporte de la web suma el importe sin descontar el vuelto y puede cambiar sin tocar esta
    /// regla.</summary>
    private async Task<Dictionary<int, Acumulado>> ConsultarMediosDePagoAsync(ContextoDeEmpresa contexto, CancellationToken ct)
    {
        var idsPuntoVenta = await IdsDePuntosDeVentaAsync(contexto, ct);
        var resultado = new Dictionary<int, Acumulado>();
        if (idsPuntoVenta.Count == 0)
        {
            return resultado;
        }

        var agregados = await db.ComprobantesVenta
            .SinEspejosFiscales()
            .Where(cv => idsPuntoVenta.Contains(cv.IdPuntoVenta))
            .Where(cv => cv.Estado != EstadoComprobante.Anulado)
            .Where(cv => cv.Fecha >= contexto.Rango.DesdeUtc && cv.Fecha < contexto.Rango.HastaUtcExclusivo)
            .Join(
                db.TiposComprobante.Where(t => t.Clase == ClaseComprobante.Venta),
                cv => cv.IdTipoComprobante, t => t.Id, (cv, t) => new { cv.Id, t.Signo })
            .Join(
                db.PagosComprobante, cv => cv.Id, p => p.IdComprobanteVenta,
                (cv, p) => new { cv.Signo, p.IdMedioPago, p.Importe, p.Vuelto })
            .GroupBy(x => x.IdMedioPago)
            .Select(g => new { Clave = g.Key, Neto = g.Sum(x => (x.Importe - x.Vuelto) * x.Signo), CantidadPagos = g.Count() })
            .ToListAsync(ct);

        foreach (var agregado in agregados)
        {
            resultado[agregado.Clave] = new Acumulado(agregado.Neto, agregado.CantidadPagos);
        }

        return resultado;
    }

    private async Task<List<int>> IdsDePuntosDeVentaAsync(ContextoDeEmpresa contexto, CancellationToken ct)
    {
        var empresa = contexto.Empresa;
        return empresa.IdPuntoVenta is { } idPuntoVenta
            ? [idPuntoVenta]
            : await db.PuntosVenta.Where(pv => pv.IdEmpresa == empresa.Id).Select(pv => pv.Id).ToListAsync(ct);
    }

    private async Task<AlcanceResuelto> ResolverAlcanceAsync(
        SolicitudDeConsultaDeVentas solicitud, CancellationToken ct, int? ultimosDias = null)
    {
        var empresasDelTenant = await db.Empresas
            .AsNoTracking()
            .OrderBy(e => e.Id)
            .Select(e => new CandidatoEmpresa(e.Id, e.NombreFantasia ?? e.RazonSocial, e.RazonSocial))
            .ToListAsync(ct);
        if (empresasDelTenant.Count == 0)
        {
            throw ErrorDominio.NoEncontrado("El tenant no tiene empresas cargadas.");
        }

        CandidatoEmpresa? empresaElegida = string.IsNullOrWhiteSpace(solicitud.Empresa)
            ? null
            : Buscar(solicitud.Empresa, "la empresa", empresasDelTenant, e => e.Id, e => [e.Nombre, e.RazonSocial], e => e.Nombre);

        var enAlcance = new List<EmpresaEnAlcance>();
        string descripcion;

        if (!string.IsNullOrWhiteSpace(solicitud.PuntoVenta))
        {
            var puntosDelTenant = await db.PuntosVenta
                .AsNoTracking()
                .OrderBy(pv => pv.Id)
                .Select(pv => new CandidatoPuntoVenta(pv.Id, pv.IdEmpresa, pv.Nombre))
                .ToListAsync(ct);
            var texto = solicitud.PuntoVenta.Trim();

            // El id se busca entre todos los puntos del tenant para poder decir que no pertenece a
            // la empresa pedida; el nombre, en cambio, solo entre los de esa empresa.
            var porId = int.TryParse(texto, NumberStyles.None, CultureInfo.InvariantCulture, out var id)
                ? puntosDelTenant.FirstOrDefault(pv => pv.Id == id)
                : null;
            var punto = porId ?? Buscar(
                texto, "el punto de venta",
                empresaElegida is null ? puntosDelTenant : puntosDelTenant.Where(pv => pv.IdEmpresa == empresaElegida.Id).ToList(),
                pv => pv.Id, pv => [pv.Nombre], pv => pv.Nombre);

            if (empresaElegida is not null && punto.IdEmpresa != empresaElegida.Id)
            {
                throw new ErrorDominio(
                    "punto_venta_no_pertenece_a_la_empresa",
                    $"El punto de venta '{punto.Nombre}' no pertenece a la empresa '{empresaElegida.Nombre}'.",
                    400);
            }

            var empresaDelPunto = empresasDelTenant.FirstOrDefault(e => e.Id == punto.IdEmpresa)
                ?? throw ErrorDominio.NoEncontrado($"No existe la empresa del punto de venta '{punto.Nombre}'.");
            enAlcance.Add(new EmpresaEnAlcance(empresaDelPunto.Id, empresaDelPunto.Nombre, punto.Id));
            descripcion = $"empresa {empresaDelPunto.Nombre}, punto de venta {punto.Nombre}";
        }
        else if (empresaElegida is not null)
        {
            enAlcance.Add(new EmpresaEnAlcance(empresaElegida.Id, empresaElegida.Nombre, null));
            descripcion = $"empresa {empresaElegida.Nombre}";
        }
        else
        {
            enAlcance.AddRange(empresasDelTenant.Select(e => new EmpresaEnAlcance(e.Id, e.Nombre, null)));
            descripcion = enAlcance.Count == 1
                ? $"empresa {enAlcance[0].Nombre}"
                : $"todas las empresas ({string.Join(", ", enAlcance.Select(e => e.Nombre))})";
        }

        var contextos = new List<ContextoDeEmpresa>();
        foreach (var empresa in enAlcance)
        {
            var resuelto = await parametros.ResolverAsync(
                ParametroConocido.ZonaHoraria.Clave, empresa.Id, empresa.IdPuntoVenta, ct);
            var zonaId = JsonSerializer.Deserialize<string>(resuelto.Valor)!;
            var zona = TimeZoneInfo.FindSystemTimeZoneById(zonaId);

            var hoy = PeriodoDeConsulta.HoyLocal(reloj.Ahora, zona);
            var (desde, hasta) = ultimosDias is { } n
                ? (hoy.AddDays(-(n - 1)), hoy)
                : PeriodoDeConsulta.Resolver(solicitud.Periodo, solicitud.Desde, solicitud.Hasta, hoy);
            var rango = RangoDeReporte.Crear(desde, hasta, Granularidad.Dia, zona);
            contextos.Add(new ContextoDeEmpresa(empresa, zonaId, desde, hasta, rango));
        }

        return new AlcanceResuelto(contextos, descripcion);
    }

    /// <summary>Id numérico primero; si no hay, nombre exacto sin distinguir mayúsculas; si no hay,
    /// el único cuyo nombre lo contiene. Más de uno o ninguno es un error que lista los candidatos.</summary>
    private static T Buscar<T>(
        string texto, string que, IReadOnlyList<T> candidatos, Func<T, int> id, Func<T, string[]> nombres, Func<T, string> etiqueta)
        where T : class
    {
        var buscado = texto.Trim();
        if (int.TryParse(buscado, NumberStyles.None, CultureInfo.InvariantCulture, out var numero) &&
            candidatos.FirstOrDefault(c => id(c) == numero) is { } porId)
        {
            return porId;
        }

        var exactos = candidatos.Where(c => nombres(c).Any(n => string.Equals(n, buscado, StringComparison.OrdinalIgnoreCase))).ToList();
        if (exactos.Count == 1)
        {
            return exactos[0];
        }

        var coincidencias = exactos.Count > 1
            ? exactos
            : candidatos.Where(c => nombres(c).Any(n => n.Contains(buscado, StringComparison.OrdinalIgnoreCase))).ToList();
        if (coincidencias.Count == 1)
        {
            return coincidencias[0];
        }

        var lista = string.Join(", ", (coincidencias.Count > 1 ? coincidencias : candidatos).Take(10).Select(c => $"{etiqueta(c)} (id {id(c)})"));
        throw coincidencias.Count > 1
            ? new ErrorDominio("alcance_ambiguo", $"'{texto}' es ambiguo para {que}; coincide con: {lista}. Indicá el id.", 400)
            : new ErrorDominio("alcance_no_encontrado", $"No encontré {que} '{texto}'. Disponibles: {lista}.", 404);
    }

    private static void Sumar(Dictionary<int, Acumulado> destino, int clave, decimal importe, decimal cantidad) =>
        destino[clave] = destino.TryGetValue(clave, out var actual)
            ? new Acumulado(actual.Importe + importe, actual.Cantidad + cantidad)
            : new Acumulado(importe, cantidad);

    private async Task<List<FilaDeVentas>> NombrarAsync(
        AgrupacionDeVentas por, Dictionary<int, Acumulado> acumulados, AlcanceResuelto alcance, CancellationToken ct)
    {
        var ids = acumulados.Keys.ToList();
        Func<int, string> nombre;

        switch (por)
        {
            case AgrupacionDeVentas.Dia:
                nombre = clave => DateOnly.FromDayNumber(clave).ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
                break;
            case AgrupacionDeVentas.MedioPago:
                var medios = await db.MediosPago.AsNoTracking()
                    .Where(m => ids.Contains(m.Id)).Select(m => new { m.Id, m.Nombre }).ToDictionaryAsync(m => m.Id, m => m.Nombre, ct);
                nombre = clave => medios.GetValueOrDefault(clave) ?? $"(medio de pago id {clave})";
                break;
            case AgrupacionDeVentas.PuntoVenta:
                var puntos = await db.PuntosVenta.AsNoTracking()
                    .Where(pv => ids.Contains(pv.Id)).Select(pv => new { pv.Id, pv.Nombre, pv.IdEmpresa }).ToDictionaryAsync(pv => pv.Id, ct);
                var variasEmpresas = alcance.Contextos.Count > 1;
                nombre = clave => puntos.TryGetValue(clave, out var punto)
                    ? variasEmpresas
                        ? $"{punto.Nombre} ({alcance.Contextos.FirstOrDefault(c => c.Empresa.Id == punto.IdEmpresa)?.Empresa.Nombre})"
                        : punto.Nombre
                    : $"(punto de venta id {clave})";
                break;
            case AgrupacionDeVentas.Vendedor:
                var vendedores = await db.Usuarios.AsNoTracking()
                    .Where(u => ids.Contains(u.Id)).Select(u => new { u.Id, u.NombreUsuario }).ToDictionaryAsync(u => u.Id, u => u.NombreUsuario, ct);
                nombre = clave => vendedores.GetValueOrDefault(clave) ?? $"(vendedor id {clave})";
                break;
            default:
                throw new ArgumentOutOfRangeException(nameof(por));
        }

        var filas = acumulados.Select(a => new { a.Key, Fila = new FilaDeVentas(nombre(a.Key), a.Value.Importe, a.Value.Cantidad) });
        return (por == AgrupacionDeVentas.Dia
                ? filas.OrderByDescending(f => f.Key)
                : filas.OrderByDescending(f => f.Fila.Importe).ThenBy(f => f.Fila.Grupo, StringComparer.OrdinalIgnoreCase))
            .Select(f => f.Fila)
            .ToList();
    }

    private static string? NombreSiEsConNombre(string? periodo) =>
        string.IsNullOrWhiteSpace(periodo) ? null : periodo.Trim().ToLowerInvariant();

    private readonly record struct Acumulado(decimal Importe, decimal Cantidad);

    private sealed record CandidatoSimple(int Id, string Nombre);

    private sealed record CandidatoArticulo(int Id, string Nombre, string Codigo);

    private sealed record CandidatoEmpresa(int Id, string Nombre, string RazonSocial);

    private sealed record CandidatoPuntoVenta(int Id, int IdEmpresa, string Nombre);

    private sealed record EmpresaEnAlcance(int Id, string Nombre, int? IdPuntoVenta);

    private sealed record ContextoDeEmpresa(
        EmpresaEnAlcance Empresa, string ZonaId, DateOnly Desde, DateOnly Hasta, RangoDeReporte Rango);

    private sealed record AlcanceResuelto(IReadOnlyList<ContextoDeEmpresa> Contextos, string Descripcion);
}
