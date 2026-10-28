using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Ways.Application.Abstracciones;
using Ways.Application.Exportacion;
using Ways.Application.Listados;
using Ways.Application.Parametros;
using Ways.Application.Usuarios;
using Ways.Domain.Articulos;
using Ways.Domain.Catalogos;
using Ways.Domain.Common;
using Ways.Domain.Ofertas;
using Ways.Domain.Reportes;
using Ways.Domain.Ventas;

namespace Ways.Application.Reportes;

/// <summary>
/// stage-10-agregacion-dashboard, Slice 5: <c>GET /api/reportes/articulos/top</c> (spec
/// reportes-de-gestion: Top Artículos Ranks By Net Quantity And Revenue; design decisión 10) —
/// LINQ puro sobre <c>ItemsComprobanteVenta</c> ⋈ <c>ComprobantesVenta</c> ⋈
/// <c>TiposComprobante</c>, sin costo ni margen: esos campos viven en <c>/rentabilidad</c>,
/// gateado aparte por <c>LecturaDeRentabilidad</c> (no en esta policy). Los filtros de
/// <c>Tenant</c>/<c>BajaLogica</c> de EF aplican gratis sobre ambas tablas (design decisión 1), salvo
/// el subconjunto de artículos de ventas por artículo, que apaga solo <c>BajaLogica</c> —
/// a diferencia de <c>LectorDeSerieTemporal</c>, esta consulta nunca corre SQL crudo.
/// </summary>
public class ServicioDeReportesDeArticulos(IWaysDbContext db, ServicioDeParametros parametros)
{
    public async Task<TopArticulos> ObtenerTopArticulosAsync(
        int idEmpresa, int? idPuntoVenta, DateOnly desde, DateOnly hasta, int? limite,
        CancellationToken ct = default)
    {
        await ExigirEmpresaAsync(idEmpresa, ct);
        var idsPuntoVenta = await ResolverPuntosDeVentaAsync(idEmpresa, idPuntoVenta, ct);
        var (zonaId, zona) = await ResolverZonaAsync(idEmpresa, idPuntoVenta, ct);

        // Granularidad.Dia es un valor interno sin efecto observable acá: este reporte no expone
        // serie ni parámetro de granularidad (design: Interfaces/Contracts — "granularidad solo en
        // las dos series"), pero el corte UTC del período igual depende de la zona resuelta
        // (design decisión 5), así que se reutiliza RangoDeReporte solo por DesdeUtc/HastaUtcExclusivo.
        var rango = RangoDeReporte.Crear(desde, hasta, Granularidad.Dia, zona);

        var lineas = idsPuntoVenta.Count == 0
            ? []
            : await LineasVendidas(idsPuntoVenta, rango).ToListAsync(ct);

        var articulos = lineas
            .GroupBy(l => l.IdArticulo!.Value)
            .Select(g =>
            {
                // design decisión 10: nunca re-unir contra articulos — la etiqueta sale del
                // snapshot de descripcion de la línea más reciente del período dentro del grupo,
                // nunca del nombre actual del catálogo.
                var etiqueta = g.OrderByDescending(l => l.Fecha).First().Descripcion;
                return new ArticuloTop(g.Key, etiqueta, g.Sum(l => l.Cantidad), g.Sum(l => l.Total));
            })
            .OrderByDescending(a => a.Total)
            .ToList();

        if (limite is { } n && n > 0)
        {
            articulos = articulos.Take(n).ToList();
        }

        return new TopArticulos(desde, hasta, zonaId, articulos);
    }

    /// <summary>Línea de venta neta de un artículo: base compartida de
    /// <see cref="ObtenerTopArticulosAsync"/> y <see cref="ObtenerVentasPorArticuloAsync"/>.</summary>
    private sealed record LineaVendida(int? IdArticulo, string Descripcion, decimal Cantidad, decimal Total, DateTimeOffset Fecha);

    private IQueryable<LineaVendida> LineasVendidas(
        IReadOnlyList<int> idsPuntoVenta, RangoDeReporte rango, int? idArticulo = null,
        IQueryable<int>? soloArticulos = null)
    {
        var consulta = db.ItemsComprobanteVenta
            .Where(i => i.IdArticulo != null)
            .Join(db.ComprobantesVenta.SinEspejosFiscales(), i => i.IdComprobanteVenta, c => c.Id, (i, c) => new { Item = i, Comprobante = c })
            .Join(db.TiposComprobante, x => x.Comprobante.IdTipoComprobante, t => t.Id, (x, t) => new { x.Item, x.Comprobante, Tipo = t })
            // spec: "clase venta, estado <> anulado" — mismo par de filtros que
            // ventas/por-punto-venta y ventas/por-vendedor (design: Raw-SQL Invariant
            // Checklist, fila articulos/top: "idem").
            .Where(x => x.Tipo.Clase == ClaseComprobante.Venta
                && x.Comprobante.Estado != EstadoComprobante.Anulado
                && idsPuntoVenta.Contains(x.Comprobante.IdPuntoVenta)
                && x.Comprobante.Fecha >= rango.DesdeUtc
                && x.Comprobante.Fecha < rango.HastaUtcExclusivo);

        if (idArticulo is { } id)
        {
            consulta = consulta.Where(x => x.Item.IdArticulo == id);
        }

        if (soloArticulos is not null)
        {
            consulta = consulta.Where(x => soloArticulos.Contains(x.Item.IdArticulo!.Value));
        }

        return consulta.Select(x => new LineaVendida(x.Item.IdArticulo, x.Item.Descripcion, x.Item.Cantidad, x.Item.Total, x.Comprobante.Fecha));
    }

    /// <summary>Ventas netas por artículo del período, filtrables por la clasificación ACTUAL del
    /// artículo (área, grupo, categoría con sus descendientes) o por un artículo puntual; los
    /// filtros se combinan con AND. Un id de filtro de baja lógica o ajeno no matchea ninguna fila
    /// (dangling-fk-read-models regla 2). Las etiquetas salen de un LEFT JOIN: un FK nulo o
    /// colgante es <c>null</c>, y un artículo dado de baja conserva la fila con el snapshot de la
    /// línea. Sin costo ni margen.</summary>
    public async Task<VentasPorArticulo> ObtenerVentasPorArticuloAsync(
        int idEmpresa, int? idPuntoVenta, DateOnly desde, DateOnly hasta,
        int? idArea, int? idGrupo, int? idCategoria, int? idArticulo,
        CancellationToken ct = default)
    {
        await ExigirEmpresaAsync(idEmpresa, ct);
        var idsPuntoVenta = await ResolverPuntosDeVentaAsync(idEmpresa, idPuntoVenta, ct);
        var (zonaId, zona) = await ResolverZonaAsync(idEmpresa, idPuntoVenta, ct);
        var rango = RangoDeReporte.Crear(desde, hasta, Granularidad.Dia, zona);

        var sinResultados = idsPuntoVenta.Count == 0;

        if (idArea is { } idAreaPedida && !await db.Areas.AnyAsync(x => x.Id == idAreaPedida, ct))
        {
            sinResultados = true;
        }

        if (idGrupo is { } idGrupoPedido && !await db.Grupos.AnyAsync(x => x.Id == idGrupoPedido, ct))
        {
            sinResultados = true;
        }

        HashSet<int>? categorias = null;
        if (idCategoria is { } idCategoriaPedida)
        {
            if (await db.Categorias.AnyAsync(x => x.Id == idCategoriaPedida, ct))
            {
                var padrePorCategoria = await db.Categorias
                    .Select(c => new { c.Id, c.IdCategoriaPadre })
                    .ToDictionaryAsync(c => c.Id, c => c.IdCategoriaPadre, ct);
                categorias = CadenaDeCategorias.ConstruirDescendientes(idCategoriaPedida, padrePorCategoria).ToHashSet();
            }
            else
            {
                sinResultados = true;
            }
        }

        if (sinResultados)
        {
            return new VentasPorArticulo(desde, hasta, zonaId, 0m, 0m, []);
        }

        IQueryable<int>? idsQueCoinciden = null;

        if (idArea is not null || idGrupo is not null || categorias is not null)
        {
            // Un artículo dado de baja que se vendió en el período sigue contando; solo se apaga
            // el filtro de baja, el de tenant se mantiene.
            var articulosQuery = db.Articulos.IgnoreQueryFilters(["BajaLogica"]);

            if (idArea is { } areaFiltro)
            {
                articulosQuery = articulosQuery.Where(a => a.IdArea == areaFiltro);
            }

            if (idGrupo is { } grupoFiltro)
            {
                articulosQuery = articulosQuery.Where(a => a.IdGrupo == grupoFiltro);
            }

            if (categorias is not null)
            {
                articulosQuery = articulosQuery.Where(a => a.IdCategoria != null && categorias.Contains(a.IdCategoria.Value));
            }

            idsQueCoinciden = articulosQuery.Select(a => a.Id);
        }

        var lineasQuery = LineasVendidas(idsPuntoVenta, rango, idArticulo, idsQueCoinciden);

        var lineas = await lineasQuery.ToListAsync(ct);
        var idsVendidos = lineas.Select(l => l.IdArticulo!.Value).Distinct().ToList();

        // LEFT JOIN: un artículo dado de baja, o un FK de clasificación nulo o colgante, deja la
        // fila con etiquetas null en vez de perderla.
        var etiquetas = await (
            from a in db.Articulos
            where idsVendidos.Contains(a.Id)
            join area in db.Areas on a.IdArea equals area.Id into areasUnidas
            from area in areasUnidas.DefaultIfEmpty()
            join grupo in db.Grupos on a.IdGrupo equals grupo.Id into gruposUnidos
            from grupo in gruposUnidos.DefaultIfEmpty()
            join categoria in db.Categorias on a.IdCategoria equals categoria.Id into categoriasUnidas
            from categoria in categoriasUnidas.DefaultIfEmpty()
            select new
            {
                a.Id,
                a.CodigoInterno,
                a.Nombre,
                Area = area != null ? area.Nombre : null,
                Grupo = grupo != null ? grupo.Nombre : null,
                Categoria = categoria != null ? categoria.Nombre : null
            }).ToDictionaryAsync(x => x.Id, ct);

        var filas = lineas
            .GroupBy(l => l.IdArticulo!.Value)
            .Select(g =>
            {
                var snapshot = g.OrderByDescending(l => l.Fecha).First().Descripcion;
                return etiquetas.TryGetValue(g.Key, out var e)
                    ? new ArticuloVendido(
                        g.Key, e.CodigoInterno, e.Nombre, e.Area, e.Grupo, e.Categoria,
                        g.Sum(l => l.Cantidad), g.Sum(l => l.Total))
                    : new ArticuloVendido(
                        g.Key, null, snapshot, null, null, null,
                        g.Sum(l => l.Cantidad), g.Sum(l => l.Total));
            })
            .OrderByDescending(f => f.Total)
            .ThenBy(f => f.Descripcion, StringComparer.Ordinal)
            .ThenBy(f => f.IdArticulo)
            .ToList();

        return new VentasPorArticulo(desde, hasta, zonaId, filas.Sum(f => f.Cantidad), filas.Sum(f => f.Total), filas);
    }

    /// <summary>Ventas netas por día de la semana y hora local (zona resuelta del alcance pedido),
    /// con el promedio por ocurrencia del día en el rango. El monto es el total del comprobante: la
    /// nota de crédito ya viene negativa y resta; solo los comprobantes de signo positivo cuentan
    /// como ticket, igual que <c>LectorDeSerieTemporal</c>. Sin costo ni margen.</summary>
    public async Task<VentasPorHora> ObtenerVentasPorHoraAsync(
        int idEmpresa, int? idPuntoVenta, DateOnly desde, DateOnly hasta, CancellationToken ct = default)
    {
        await ExigirEmpresaAsync(idEmpresa, ct);
        var idsPuntoVenta = await ResolverPuntosDeVentaAsync(idEmpresa, idPuntoVenta, ct);
        var (zonaId, zona) = await ResolverZonaAsync(idEmpresa, idPuntoVenta, ct);
        var rango = RangoDeReporte.Crear(desde, hasta, Granularidad.Dia, zona);

        var ventas = idsPuntoVenta.Count == 0
            ? []
            : await VentasConSigno(idsPuntoVenta, rango).ToListAsync(ct);

        // Application no referencia Npgsql (sin AtTimeZone): la conversión a hora local corre en C#
        // y solo se materializan tres columnas por comprobante.
        var sumas = ventas
            .Select(v => (Local: TimeZoneInfo.ConvertTime(v.Fecha, zona), v.Total, v.Signo))
            .GroupBy(v => (Dia: GrillaDeVentasPorHora.DiaDeLaSemanaIso(DateOnly.FromDateTime(v.Local.DateTime)), v.Local.Hour))
            .Select(g => new SumaDeHora(g.Key.Dia, g.Key.Hour, g.Sum(v => v.Total), g.Count(v => v.Signo > 0)));

        var grilla = GrillaDeVentasPorHora.Construir(desde, hasta, sumas);

        return new VentasPorHora(desde, hasta, zonaId, grilla.Total, grilla.Tickets, grilla.Dias, grilla.Celdas);
    }

    private sealed record VentaConSigno(DateTimeOffset Fecha, decimal Total, short Signo);

    private IQueryable<VentaConSigno> VentasConSigno(IReadOnlyList<int> idsPuntoVenta, RangoDeReporte rango) =>
        db.ComprobantesVenta.SinEspejosFiscales()
            .Join(db.TiposComprobante, c => c.IdTipoComprobante, t => t.Id, (c, t) => new { Comprobante = c, Tipo = t })
            .Where(x => x.Tipo.Clase == ClaseComprobante.Venta
                && x.Comprobante.Estado != EstadoComprobante.Anulado
                && idsPuntoVenta.Contains(x.Comprobante.IdPuntoVenta)
                && x.Comprobante.Fecha >= rango.DesdeUtc
                && x.Comprobante.Fecha < rango.HastaUtcExclusivo)
            .Select(x => new VentaConSigno(x.Comprobante.Fecha, x.Comprobante.Total, x.Tipo.Signo));

    private async Task ExigirEmpresaAsync(int idEmpresa, CancellationToken ct)
    {
        // ADR-8: mismo 404 para "no existe" y "es de otro tenant" — el filtro de EF/RLS ya deja
        // invisible una empresa ajena, mismo criterio que ServicioDeReportesDeVentas.
        var existe = await db.Empresas.AnyAsync(e => e.Id == idEmpresa, ct);
        if (!existe)
        {
            throw ErrorDominio.NoEncontrado($"No existe la empresa {idEmpresa}.");
        }
    }

    /// <summary>Sin <paramref name="idPuntoVenta"/>: todos los puntos de venta de la empresa
    /// (empresa-wide, design decisión 5). Con él: la misma regla de pertenencia que
    /// <c>ServicioDeReportesDeVentas.ResolverPuntosDeVentaAsync</c>.</summary>
    private async Task<IReadOnlyList<int>> ResolverPuntosDeVentaAsync(int idEmpresa, int? idPuntoVenta, CancellationToken ct)
    {
        var puntosDeLaEmpresa = db.PuntosVenta.Where(pv => pv.IdEmpresa == idEmpresa);

        if (idPuntoVenta is { } id)
        {
            var pertenece = await puntosDeLaEmpresa.AnyAsync(pv => pv.Id == id, ct);
            if (!pertenece)
            {
                throw new ErrorDominio(
                    "punto_venta_no_pertenece_a_la_empresa",
                    "El punto de venta indicado no pertenece a la empresa declarada.",
                    400);
            }

            return [id];
        }

        return await puntosDeLaEmpresa.Select(pv => pv.Id).ToListAsync(ct);
    }

    /// <summary>design decisión 5: la zona se resuelve UNA vez, al alcance que pidió el caller, y
    /// se ecoa en la respuesta — un número cuyo corte de día es invisible no es auditable.</summary>
    private async Task<(string ZonaId, TimeZoneInfo Zona)> ResolverZonaAsync(
        int idEmpresa, int? idPuntoVenta, CancellationToken ct)
    {
        var resuelto = await parametros.ResolverAsync(ParametroConocido.ZonaHoraria.Clave, idEmpresa, idPuntoVenta, ct);
        var zonaId = JsonSerializer.Deserialize<string>(resuelto.Valor)!;
        return (zonaId, TimeZoneInfo.FindSystemTimeZoneById(zonaId));
    }

    // ---- GET /api/reportes/articulos (reporte de completitud de catálogo) ------------------------

    public const int TamanioMaximoDePaginaDeArticulos = 200;

    /// <summary>Catálogo de artículos tenant-wide (sin <c>idEmpresa</c>/<c>idPuntoVenta</c>: los
    /// artículos no tienen esa columna, doc 10 §3) con los nombres de sus cinco clasificaciones ya
    /// resueltos. <paramref name="soloIncompletos"/> es un OR de las cinco ausencias (spec del
    /// owner: "sin proveedor, sin marca, sin categoría, sin grupo", extendido a área por la misma
    /// noción de "efectivamente sin asignar" que sus pares) — un artículo con UNA sola
    /// clasificación faltante ya aparece.</summary>
    public async Task<PaginaDe<ArticuloDeReporte>> ListarArticulosAsync(
        int? idArea,
        bool sinArea,
        int? idCategoria,
        bool sinCategoria,
        int? idMarca,
        bool sinMarca,
        int? idGrupo,
        bool sinGrupo,
        int? idProveedor,
        bool sinProveedor,
        bool soloIncompletos,
        bool? activo,
        int pagina = 1,
        int tamanio = 25,
        string? orden = null,
        string? direccion = null,
        CancellationToken ct = default)
    {
        var ordenPedido = OrdenDeListado.Interpretar(orden, direccion, ClavesDeOrdenDeArticulos);

        pagina = Math.Max(pagina, 1);
        tamanio = Math.Clamp(tamanio, 1, TamanioMaximoDePaginaDeArticulos);

        var query = await ConstruirQueryDeArticulosAsync(
            idArea, sinArea, idCategoria, sinCategoria, idMarca, sinMarca, idGrupo, sinGrupo, idProveedor, sinProveedor,
            soloIncompletos, activo, ct);

        var total = await query.CountAsync(ct);

        // Una columna propia del artículo (o ningún orden) ordena y pagina sobre `articulos` y recién después
        // une los catálogos de esa página; solo una columna de catálogo necesita la unión antes de paginar.
        var ordenaPorColumnaPropia = ordenPedido is null
            or { Columna: ColumnaDeOrdenDeArticulosDeReporte.Codigo or ColumnaDeOrdenDeArticulosDeReporte.Nombre
                or ColumnaDeOrdenDeArticulosDeReporte.Estado };

        IQueryable<ArticuloConCatalogos> paginaDeArticulos = ordenaPorColumnaPropia
            ? Ordenar(
                UnirConCatalogos(OrdenarArticulos(query, ordenPedido).Skip((pagina - 1) * tamanio).Take(tamanio)),
                ordenPedido)
            : Ordenar(UnirConCatalogos(query), ordenPedido).Skip((pagina - 1) * tamanio).Take(tamanio);
        var items = await AArticuloDeReporte(paginaDeArticulos).ToListAsync(ct);

        return new PaginaDe<ArticuloDeReporte>(items, total, pagina, tamanio);
    }

    /// <summary>Export sibling — mismo patrón Contar → rechazar → <c>Take(tope + 1)</c> → rechazar
    /// que <see cref="Caja.ServicioDeHistoricoDeCajas.ListarCierresParaExportacionAsync"/>, reusando
    /// <see cref="ConstruirQueryDeArticulosAsync"/> tal cual — nunca una segunda declaración del
    /// filtro (un catálogo de artículos no es acotado como un punto de venta o un medio de pago, así
    /// que el paginado de <see cref="ListarArticulosAsync"/> (tope duro de
    /// <see cref="TamanioMaximoDePaginaDeArticulos"/>) truncaría en silencio una exportación más
    /// grande sin este método).</summary>
    public async Task<IReadOnlyList<ArticuloDeReporte>> ListarArticulosParaExportacionAsync(
        int? idArea,
        bool sinArea,
        int? idCategoria,
        bool sinCategoria,
        int? idMarca,
        bool sinMarca,
        int? idGrupo,
        bool sinGrupo,
        int? idProveedor,
        bool sinProveedor,
        bool soloIncompletos,
        bool? activo,
        int topeDeFilas,
        CancellationToken ct = default)
    {
        var query = await ConstruirQueryDeArticulosAsync(
            idArea, sinArea, idCategoria, sinCategoria, idMarca, sinMarca, idGrupo, sinGrupo, idProveedor, sinProveedor,
            soloIncompletos, activo, ct);

        var cantidad = await query.CountAsync(ct);
        GuardaDeTope.Exigir(cantidad, topeDeFilas);

        var recorte = Ordenar(UnirConCatalogos(query), null).Take(topeDeFilas + 1);
        var items = await AArticuloDeReporte(recorte).ToListAsync(ct);

        GuardaDeTope.Exigir(items.Count, topeDeFilas);

        return items;
    }

    /// <summary>Filtro compartido de <see cref="ListarArticulosAsync"/> y
    /// <see cref="ListarArticulosParaExportacionAsync"/> — cada guarda <c>id</c>/<c>sin</c> es un
    /// conjunct AND independiente (mismo criterio que <c>ServicioDeArticulos.ListarAsync</c>).
    /// <c>idCategoria</c> reusa <see cref="CadenaDeCategorias.ConstruirDescendientes"/> tal cual
    /// (<c>ServicioDeArticulos.ListarAsync:106-118</c>) — nunca una segunda expansión de
    /// descendientes.
    ///
    /// Cada <c>sin*</c> y <c>soloIncompletos</c> comparten UNA sola noción de "efectivamente sin
    /// asignar" (design decisión soft-delete-sin-guarda, stage 13 #12): el FK es <c>null</c> O
    /// apunta a una fila dada de baja lógica — los catálogos y proveedores se eliminan sin guarda
    /// de uso, así que un artículo puede quedar con un FK no nulo que ya no resuelve a ninguna fila
    /// visible. La expresión <c>!idsVisibles.Contains(a.IdX.Value)</c> alcanza sola para las dos
    /// mitades: la compensación de semántica de <c>null</c> que EF Core aplica por defecto (sin
    /// <c>UseRelationalNulls</c>, no configurado en este proyecto) traduce
    /// <c>NOT (columna = ANY(ids))</c> con <c>columna IS NULL</c> como verdadero — un
    /// <c>a.IdX == null ||</c> explícito sería puro código muerto, nunca discriminable por ningún
    /// test (confirmado corriendo la mutación: quitarlo no rompe ni el caso FK <c>null</c> ni el
    /// caso FK colgante). <c>idArea</c> es la única columna NOT NULL de las cinco, así que
    /// <c>sinArea</c> solo puede significar la baja lógica del área referenciada. La misma noción
    /// la aplica ya <see cref="ProyectarArticulosDeReporteAsync"/> vía <c>LEFT JOIN</c> contra los
    /// catálogos (filtrados por baja lógica) — este método existe para que el FILTRO vea lo mismo
    /// que muestra la proyección.
    ///
    /// Cada guarda <c>id*</c> también exige que el id pedido esté en <c>idsDeXVisibles</c>
    /// (dangling-fk-read-models regla 2): un id de baja lógica no matchea ninguna fila, nunca la
    /// misma que devuelve <c>sin*</c> (judgment-day ronda 2). Para <c>idCategoria</c> esto incluye
    /// la raíz de la expansión: un id de categoría no visible no matchea nada, ni siquiera vía
    /// descendientes de una categoría hija cuyo padre haya quedado colgante — la decisión más
    /// simple y consistente con las demás columnas.</summary>
    private async Task<IQueryable<Articulo>> ConstruirQueryDeArticulosAsync(
        int? idArea,
        bool sinArea,
        int? idCategoria,
        bool sinCategoria,
        int? idMarca,
        bool sinMarca,
        int? idGrupo,
        bool sinGrupo,
        int? idProveedor,
        bool sinProveedor,
        bool soloIncompletos,
        bool? activo,
        CancellationToken ct)
    {
        ExigirFiltroExclusivo(idArea, sinArea, "idArea", "sinArea");
        ExigirFiltroExclusivo(idCategoria, sinCategoria, "idCategoria", "sinCategoria");
        ExigirFiltroExclusivo(idMarca, sinMarca, "idMarca", "sinMarca");
        ExigirFiltroExclusivo(idGrupo, sinGrupo, "idGrupo", "sinGrupo");
        ExigirFiltroExclusivo(idProveedor, sinProveedor, "idProveedor", "sinProveedor");

        // Ids visibles de cada catálogo (ya filtrados por BajaLogica), buscados solo cuando hacen
        // falta: la propia guarda sin* de la clasificación, la guarda id* (dangling-fk-read-models
        // regla 2 — un id pedido tiene que ser visible para matchear algo), o soloIncompletos (que
        // necesita las cinco a la vez).
        var idsDeAreasVisibles = idArea is not null || sinArea || soloIncompletos
            ? (await db.Areas.Select(x => x.Id).ToListAsync(ct)).ToHashSet()
            : null;
        // HashSet<int?> (no <int>): así el Where compara contra el FK nullable directo, sin
        // `.Value` — EF Core traduce `!idsVisibles.Contains(a.IdX)` a `NOT (id_x = ANY(ids))`, y su
        // compensación de semántica de null (default, sin UseRelationalNulls) ya trata
        // `id_x IS NULL` como verdadero ahí: cubre el FK null Y el FK colgante con la MISMA
        // comparación, sin un `a.IdX == null ||` extra (confirmado: sería código muerto, ninguna
        // mutación sobre él es detectable).
        var idsDeCategoriasVisibles = idCategoria is not null || sinCategoria || soloIncompletos
            ? (await db.Categorias.Select(x => (int?)x.Id).ToListAsync(ct)).ToHashSet()
            : null;
        var idsDeMarcasVisibles = idMarca is not null || sinMarca || soloIncompletos
            ? (await db.Marcas.Select(x => (int?)x.Id).ToListAsync(ct)).ToHashSet()
            : null;
        var idsDeGruposVisibles = idGrupo is not null || sinGrupo || soloIncompletos
            ? (await db.Grupos.Select(x => (int?)x.Id).ToListAsync(ct)).ToHashSet()
            : null;
        var idsDeProveedoresVisibles = idProveedor is not null || sinProveedor || soloIncompletos
            ? (await db.Proveedores.Select(x => (int?)x.Id).ToListAsync(ct)).ToHashSet()
            : null;

        var query = db.Articulos.AsQueryable();

        // dangling-fk-read-models regla 2: un id pedido que ya no es visible (dado de baja lógica)
        // no matchea ninguna fila — nunca la misma fila que sin<X>=true devuelve.
        if (idArea is { } idAreaValor)
        {
            query = idsDeAreasVisibles!.Contains(idAreaValor)
                ? query.Where(a => a.IdArea == idAreaValor)
                : query.Where(a => false);
        }
        else if (sinArea)
        {
            query = query.Where(a => !idsDeAreasVisibles!.Contains(a.IdArea));
        }

        if (idCategoria is { } idCategoriaValor)
        {
            // Mismo criterio que idArea: un id de categoría de baja lógica no matchea nada, ni
            // siquiera vía la expansión de descendientes — una categoría hija cuyo padre ahora
            // apunta a un id colgante no puede contar como resultado de un id que ya no existe.
            if (!idsDeCategoriasVisibles!.Contains(idCategoriaValor))
            {
                query = query.Where(a => false);
            }
            else
            {
                // Una sola proyección id→id_padre de TODO el tenant, mismo criterio que
                // ServicioDeArticulos.ListarAsync — la expansión de descendientes corre en memoria.
                var padrePorCategoria = await db.Categorias
                    .Select(c => new { c.Id, c.IdCategoriaPadre })
                    .ToDictionaryAsync(c => c.Id, c => c.IdCategoriaPadre, ct);

                var descendientes = CadenaDeCategorias.ConstruirDescendientes(idCategoriaValor, padrePorCategoria);

                query = query.Where(a => a.IdCategoria != null && descendientes.Contains(a.IdCategoria.Value));
            }
        }
        else if (sinCategoria)
        {
            query = query.Where(a => !idsDeCategoriasVisibles!.Contains(a.IdCategoria));
        }

        if (idMarca is { } idMarcaValor)
        {
            query = idsDeMarcasVisibles!.Contains(idMarcaValor)
                ? query.Where(a => a.IdMarca == idMarcaValor)
                : query.Where(a => false);
        }
        else if (sinMarca)
        {
            query = query.Where(a => !idsDeMarcasVisibles!.Contains(a.IdMarca));
        }

        if (idGrupo is { } idGrupoValor)
        {
            query = idsDeGruposVisibles!.Contains(idGrupoValor)
                ? query.Where(a => a.IdGrupo == idGrupoValor)
                : query.Where(a => false);
        }
        else if (sinGrupo)
        {
            query = query.Where(a => !idsDeGruposVisibles!.Contains(a.IdGrupo));
        }

        if (idProveedor is { } idProveedorValor)
        {
            query = idsDeProveedoresVisibles!.Contains(idProveedorValor)
                ? query.Where(a => a.IdProveedorHabitual == idProveedorValor)
                : query.Where(a => false);
        }
        else if (sinProveedor)
        {
            query = query.Where(a => !idsDeProveedoresVisibles!.Contains(a.IdProveedorHabitual));
        }

        if (soloIncompletos)
        {
            query = query.Where(a =>
                !idsDeAreasVisibles!.Contains(a.IdArea)
                || !idsDeCategoriasVisibles!.Contains(a.IdCategoria)
                || !idsDeMarcasVisibles!.Contains(a.IdMarca)
                || !idsDeGruposVisibles!.Contains(a.IdGrupo)
                || !idsDeProveedoresVisibles!.Contains(a.IdProveedorHabitual));
        }

        if (activo is { } activoValor)
        {
            query = query.Where(a => a.Activo == activoValor);
        }

        return query;
    }

    /// <summary>Una sola proyección con LEFT JOIN contra los cinco catálogos — todos los nombres
    /// salen de ACÁ, nunca de un lookup por fila (design: "All names from one SQL projection, no
    /// N+1"), mismo patrón de <c>into ... from ... DefaultIfEmpty()</c> que
    /// <c>ServicioDeReportesDeStock.ConstruirQueryDeReposicion</c>. Área usa LEFT JOIN igual que
    /// las otras cuatro pese a que <c>IdArea</c> es NOT NULL en <c>articulos</c>: un área dada de
    /// baja lógica deja de existir en <c>db.Areas</c> (filtro global BajaLogica), y un INNER JOIN
    /// contra ella dropearía la fila del artículo entero — <c>total</c> (contado antes del join)
    /// seguiría contándola, dejando un <c>total</c> mayor a <c>items.Count</c> (bug reproducido por
    /// judgment-day, ronda 1). <see cref="Proveedor.NombreFantasia"/> gana sobre
    /// <see cref="Proveedor.RazonSocial"/> cuando no es nulo/vacío.</summary>
    private IQueryable<ArticuloConCatalogos> UnirConCatalogos(IQueryable<Articulo> query) =>
        from a in query
        join area in db.Areas on a.IdArea equals area.Id into areasUnidas
        from area in areasUnidas.DefaultIfEmpty()
        join categoria in db.Categorias on a.IdCategoria equals categoria.Id into categoriasUnidas
        from categoria in categoriasUnidas.DefaultIfEmpty()
        join marca in db.Marcas on a.IdMarca equals marca.Id into marcasUnidas
        from marca in marcasUnidas.DefaultIfEmpty()
        join grupo in db.Grupos on a.IdGrupo equals grupo.Id into gruposUnidos
        from grupo in gruposUnidos.DefaultIfEmpty()
        join proveedor in db.Proveedores on a.IdProveedorHabitual equals proveedor.Id into proveedoresUnidos
        from proveedor in proveedoresUnidos.DefaultIfEmpty()
        select new ArticuloConCatalogos
        {
            A = a,
            Area = area != null ? area.Nombre : null,
            Categoria = categoria != null ? categoria.Nombre : null,
            Marca = marca != null ? marca.Nombre : null,
            Grupo = grupo != null ? grupo.Nombre : null,
            Proveedor = proveedor != null
                ? (!string.IsNullOrWhiteSpace(proveedor.NombreFantasia) ? proveedor.NombreFantasia : proveedor.RazonSocial)
                : null,
        };

    private static IQueryable<ArticuloDeReporte> AArticuloDeReporte(IQueryable<ArticuloConCatalogos> query) =>
        query.Select(x => new ArticuloDeReporte(
            x.A.Id, x.A.CodigoInterno, x.A.Nombre, x.Area, x.Categoria, x.Marca, x.Grupo, x.Proveedor, x.A.Activo));

    /// <summary>Orden de SQL, aplicado antes de paginar y sobre las MISMAS etiquetas que muestra la
    /// grilla (las del LEFT JOIN de <see cref="UnirConCatalogos"/>). Sin orden pedido: Nombre, luego Id
    /// (también el orden del export). Con orden: la columna pedida y SIEMPRE Id ascendente como
    /// desempate, para que dos páginas consecutivas nunca repitan ni salteen una fila. Una clasificación
    /// sin asignar, dada de baja o con FK colgante llega como null y va al final en las dos direcciones.
    /// Los textos usan la colación de la base, igual que el orden por nombre de siempre.</summary>
    private static IOrderedQueryable<ArticuloConCatalogos> Ordenar(
        IQueryable<ArticuloConCatalogos> query, OrdenDeListado<ColumnaDeOrdenDeArticulosDeReporte>? orden)
    {
        if (orden is not { } pedido)
        {
            return query.OrderBy(x => x.A.Nombre).ThenBy(x => x.A.Id);
        }

        var desc = pedido.Descendente;
        IOrderedQueryable<ArticuloConCatalogos> ordenado = pedido.Columna switch
        {
            ColumnaDeOrdenDeArticulosDeReporte.Codigo => desc
                ? query.OrderByDescending(x => x.A.CodigoInterno)
                : query.OrderBy(x => x.A.CodigoInterno),
            ColumnaDeOrdenDeArticulosDeReporte.Nombre => desc
                ? query.OrderByDescending(x => x.A.Nombre)
                : query.OrderBy(x => x.A.Nombre),
            ColumnaDeOrdenDeArticulosDeReporte.Area => desc
                ? query.OrderBy(x => x.Area == null).ThenByDescending(x => x.Area)
                : query.OrderBy(x => x.Area == null).ThenBy(x => x.Area),
            ColumnaDeOrdenDeArticulosDeReporte.Categoria => desc
                ? query.OrderBy(x => x.Categoria == null).ThenByDescending(x => x.Categoria)
                : query.OrderBy(x => x.Categoria == null).ThenBy(x => x.Categoria),
            ColumnaDeOrdenDeArticulosDeReporte.Marca => desc
                ? query.OrderBy(x => x.Marca == null).ThenByDescending(x => x.Marca)
                : query.OrderBy(x => x.Marca == null).ThenBy(x => x.Marca),
            ColumnaDeOrdenDeArticulosDeReporte.Grupo => desc
                ? query.OrderBy(x => x.Grupo == null).ThenByDescending(x => x.Grupo)
                : query.OrderBy(x => x.Grupo == null).ThenBy(x => x.Grupo),
            ColumnaDeOrdenDeArticulosDeReporte.Proveedor => desc
                ? query.OrderBy(x => x.Proveedor == null).ThenByDescending(x => x.Proveedor)
                : query.OrderBy(x => x.Proveedor == null).ThenBy(x => x.Proveedor),
            ColumnaDeOrdenDeArticulosDeReporte.Estado => desc
                ? query.OrderBy(x => x.A.Activo)
                : query.OrderByDescending(x => x.A.Activo),
            _ => query.OrderBy(x => x.A.Nombre),
        };

        return ordenado.ThenBy(x => x.A.Id);
    }

    /// <summary>Mismo orden que <see cref="Ordenar"/> para las columnas propias del artículo, sobre
    /// <c>articulos</c> a secas (sin unir catálogos). Estado ordena por el texto de la celda: "Activo" antes
    /// que "Inactivo" en ascendente.</summary>
    private static IOrderedQueryable<Articulo> OrdenarArticulos(
        IQueryable<Articulo> query, OrdenDeListado<ColumnaDeOrdenDeArticulosDeReporte>? orden)
    {
        if (orden is not { } pedido)
        {
            return query.OrderBy(a => a.Nombre).ThenBy(a => a.Id);
        }

        var desc = pedido.Descendente;
        IOrderedQueryable<Articulo> ordenado = pedido.Columna switch
        {
            ColumnaDeOrdenDeArticulosDeReporte.Codigo => desc
                ? query.OrderByDescending(a => a.CodigoInterno)
                : query.OrderBy(a => a.CodigoInterno),
            ColumnaDeOrdenDeArticulosDeReporte.Estado => desc
                ? query.OrderBy(a => a.Activo)
                : query.OrderByDescending(a => a.Activo),
            ColumnaDeOrdenDeArticulosDeReporte.Nombre => desc
                ? query.OrderByDescending(a => a.Nombre)
                : query.OrderBy(a => a.Nombre),
            _ => query.OrderBy(a => a.Nombre),
        };

        return ordenado.ThenBy(a => a.Id);
    }

    public static readonly IReadOnlyDictionary<string, ColumnaDeOrdenDeArticulosDeReporte> ClavesDeOrdenDeArticulos =
        new Dictionary<string, ColumnaDeOrdenDeArticulosDeReporte>(StringComparer.Ordinal)
        {
            ["codigo"] = ColumnaDeOrdenDeArticulosDeReporte.Codigo,
            ["nombre"] = ColumnaDeOrdenDeArticulosDeReporte.Nombre,
            ["area"] = ColumnaDeOrdenDeArticulosDeReporte.Area,
            ["categoria"] = ColumnaDeOrdenDeArticulosDeReporte.Categoria,
            ["marca"] = ColumnaDeOrdenDeArticulosDeReporte.Marca,
            ["grupo"] = ColumnaDeOrdenDeArticulosDeReporte.Grupo,
            ["proveedor"] = ColumnaDeOrdenDeArticulosDeReporte.Proveedor,
            ["estado"] = ColumnaDeOrdenDeArticulosDeReporte.Estado,
        };

    private sealed class ArticuloConCatalogos
    {
        public Articulo A { get; init; } = null!;

        public string? Area { get; init; }

        public string? Categoria { get; init; }

        public string? Marca { get; init; }

        public string? Grupo { get; init; }

        public string? Proveedor { get; init; }
    }

    private static void ExigirFiltroExclusivo(int? id, bool sin, string nombreId, string nombreSin)
    {
        if (id is not null && sin)
        {
            throw new ErrorDominio(
                "filtro_incompatible",
                $"No podés combinar {nombreId} con {nombreSin} en la misma consulta.",
                400);
        }
    }
}

public enum ColumnaDeOrdenDeArticulosDeReporte
{
    Codigo,
    Nombre,
    Area,
    Categoria,
    Marca,
    Grupo,
    Proveedor,
    Estado,
}
