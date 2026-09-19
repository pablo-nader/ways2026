using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Ways.Application.Abstracciones;
using Ways.Application.Exportacion;
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
/// <c>Tenant</c>/<c>BajaLogica</c> de EF aplican gratis sobre ambas tablas (design decisión 1) —
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
            : await db.ItemsComprobanteVenta
                .Where(i => i.IdArticulo != null)
                .Join(db.ComprobantesVenta, i => i.IdComprobanteVenta, c => c.Id, (i, c) => new { Item = i, Comprobante = c })
                .Join(db.TiposComprobante, x => x.Comprobante.IdTipoComprobante, t => t.Id, (x, t) => new { x.Item, x.Comprobante, Tipo = t })
                // spec: "clase venta, estado <> anulado" — mismo par de filtros que
                // ventas/por-punto-venta y ventas/por-vendedor (design: Raw-SQL Invariant
                // Checklist, fila articulos/top: "idem").
                .Where(x => x.Tipo.Clase == ClaseComprobante.Venta
                    && x.Comprobante.Estado != EstadoComprobante.Anulado
                    && idsPuntoVenta.Contains(x.Comprobante.IdPuntoVenta)
                    && x.Comprobante.Fecha >= rango.DesdeUtc
                    && x.Comprobante.Fecha < rango.HastaUtcExclusivo)
                .Select(x => new { x.Item.IdArticulo, x.Item.Descripcion, x.Item.Cantidad, x.Item.Total, x.Comprobante.Fecha })
                .ToListAsync(ct);

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
        CancellationToken ct = default)
    {
        pagina = Math.Max(pagina, 1);
        tamanio = Math.Clamp(tamanio, 1, TamanioMaximoDePaginaDeArticulos);

        var query = await ConstruirQueryDeArticulosAsync(
            idArea, sinArea, idCategoria, sinCategoria, idMarca, sinMarca, idGrupo, sinGrupo, idProveedor, sinProveedor,
            soloIncompletos, activo, ct);

        var total = await query.CountAsync(ct);

        var paginaDeArticulos = query.OrderBy(a => a.Nombre).ThenBy(a => a.Id).Skip((pagina - 1) * tamanio).Take(tamanio);
        var items = await ProyectarArticulosDeReporteAsync(paginaDeArticulos, ct);

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

        var recorte = query.OrderBy(a => a.Nombre).ThenBy(a => a.Id).Take(topeDeFilas + 1);
        var items = await ProyectarArticulosDeReporteAsync(recorte, ct);

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
    private async Task<List<ArticuloDeReporte>> ProyectarArticulosDeReporteAsync(IQueryable<Articulo> query, CancellationToken ct)
    {
        var proyeccion =
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
            orderby a.Nombre, a.Id
            select new ArticuloDeReporte(
                a.Id,
                a.CodigoInterno,
                a.Nombre,
                area != null ? area.Nombre : null,
                categoria != null ? categoria.Nombre : null,
                marca != null ? marca.Nombre : null,
                grupo != null ? grupo.Nombre : null,
                proveedor != null
                    ? (!string.IsNullOrWhiteSpace(proveedor.NombreFantasia) ? proveedor.NombreFantasia : proveedor.RazonSocial)
                    : null,
                a.Activo);

        return await proyeccion.ToListAsync(ct);
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
