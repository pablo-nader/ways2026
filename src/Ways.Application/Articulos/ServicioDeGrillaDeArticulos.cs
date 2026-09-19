using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Ways.Application.Abstracciones;
using Ways.Application.Precios;
using Ways.Domain.Common;

namespace Ways.Application.Articulos;

/// <summary>
/// Consulta dedicada de <c>GET /api/articulos/grilla</c> — el back-office necesita precio (lista
/// default del tenant) y nombre de proveedor en la misma fila, algo que
/// <see cref="ServicioDeArticulos.ListarAsync"/> no resuelve (ese endpoint sigue usado tal cual
/// por el POS y otras pantallas — su contrato no se toca en esta slice). Clase dedicada en vez de
/// un método más en <see cref="ServicioDeArticulos"/> (que ya es grande): compone
/// <see cref="Precios.ServicioDePrecios"/> (<see cref="Precios.ServicioDePrecios.PreciosVigentesEnLoteAsync"/>,
/// presupuesto fijo de 3 consultas) en vez de reimplementar la resolución fija/derivada — mismo
/// criterio de composición que <see cref="Etiquetas.ServicioDeEtiquetas"/> (que compone
/// <c>ServicioDeArticulos</c> + <c>ServicioDeOfertas</c> por el mismo motivo).
///
/// <para>Dos caminos: SIN filtro de precio, el filtro/count/paginado corren enteros en SQL y el
/// precio se resuelve solo para los ids de la página ya paginada. CON filtro de precio, los demás
/// filtros corren en SQL pero el precio no es expresable en SQL sin reimplementar las reglas
/// fija/derivada de <c>ServicioDePrecios</c> — se resuelve para TODOS los candidatos en un solo
/// batch y el filtro de precio + count + paginado corren en memoria.
/// <see cref="OpcionesDeGrillaDeArticulos"/> pone un tope al tamaño de ese batch: un cap RECHAZA,
/// nunca trunca (mismo criterio que <see cref="Exportacion.OpcionesDeExportacion.TopeDeFilas"/>).
/// </para>
/// </summary>
public class ServicioDeGrillaDeArticulos(
    IWaysDbContext db,
    IRelojDelSistema reloj,
    ServicioDePrecios servicioDePrecios,
    IOptions<OpcionesDeGrillaDeArticulos> opciones)
{
    public async Task<PaginaDeArticulosGrilla> ListarAsync(
        string? codigo = null,
        string? nombre = null,
        decimal? precioDesde = null,
        decimal? precioHasta = null,
        int? idProveedor = null,
        bool sinProveedor = false,
        bool? activo = null,
        int pagina = 1,
        int tamanio = 25,
        CancellationToken ct = default)
    {
        if (idProveedor is not null && sinProveedor)
        {
            throw new ErrorDominio(
                "filtro_proveedor_ambiguo", "No se puede combinar idProveedor con sinProveedor.", 400);
        }

        pagina = Math.Max(pagina, 1);
        tamanio = Math.Clamp(tamanio, 1, ServicioDeArticulos.TamanioMaximoDePagina);

        var listaDefault = await BuscarListaDefaultAsync(ct);
        var query = ConstruirQuery(codigo, nombre, idProveedor, sinProveedor, activo);

        var hayFiltroDePrecio = precioDesde is not null || precioHasta is not null;

        return hayFiltroDePrecio
            ? await ListarConFiltroDePrecioAsync(query, listaDefault, precioDesde, precioHasta, pagina, tamanio, ct)
            : await ListarSinFiltroDePrecioAsync(query, listaDefault, pagina, tamanio, ct);
    }

    /// <summary>"Lista default del tenant" (Context de esta slice) = la COMPARTIDA
    /// (<c>id_empresa IS NULL</c>, <c>ux_listas_precio_default_compartido</c>) — esta grilla no
    /// tiene <c>idEmpresa</c> (a diferencia de <c>GET /api/articulos</c>), así que no hay un
    /// alcance de empresa del que preferir un default propio por sobre el compartido.</summary>
    private async Task<ListaDefault?> BuscarListaDefaultAsync(CancellationToken ct)
    {
        var lista = await db.ListasPrecio
            .Where(l => l.IdEmpresa == null && l.EsDefault)
            .Select(l => new { l.Id, l.Nombre })
            .FirstOrDefaultAsync(ct);

        return lista is null ? null : new ListaDefault(lista.Id, lista.Nombre);
    }

    private async Task<PaginaDeArticulosGrilla> ListarSinFiltroDePrecioAsync(
        IQueryable<FilaCandidata> query, ListaDefault? listaDefault, int pagina, int tamanio, CancellationToken ct)
    {
        var total = await query.CountAsync(ct);

        var paginaDeCandidatos = await query
            .Skip((pagina - 1) * tamanio)
            .Take(tamanio)
            .ToListAsync(ct);

        var precios = await ResolverPreciosAsync(paginaDeCandidatos, listaDefault, ct);

        var items = paginaDeCandidatos
            .Select(c => Proyectar(c, PrecioDe(c.Id, precios, listaDefault)))
            .ToList();

        return new PaginaDeArticulosGrilla(items, total, pagina, tamanio, listaDefault?.Nombre);
    }

    private async Task<PaginaDeArticulosGrilla> ListarConFiltroDePrecioAsync(
        IQueryable<FilaCandidata> query, ListaDefault? listaDefault, decimal? precioDesde, decimal? precioHasta,
        int pagina, int tamanio, CancellationToken ct)
    {
        var totalCandidatos = await query.CountAsync(ct);
        var tope = opciones.Value.TopeDeCandidatosPorFiltroDePrecio;

        if (totalCandidatos > tope)
        {
            throw new ErrorDominio(
                "filtro_precio_excede_tope",
                $"El filtro de precio no puede evaluar más de {tope} artículos candidatos; "
                    + "refiná codigo/nombre/idProveedor/sinProveedor/activo antes de acotar por precio.",
                400);
        }

        var candidatos = await query.ToListAsync(ct);
        var precios = await ResolverPreciosAsync(candidatos, listaDefault, ct);

        var filtrados = candidatos
            .Select(c => (Candidato: c, Precio: PrecioDe(c.Id, precios, listaDefault)))
            .Where(x => CumpleFiltroDePrecio(x.Precio, precioDesde, precioHasta))
            .ToList();

        var items = filtrados
            .Skip((pagina - 1) * tamanio)
            .Take(tamanio)
            .Select(x => Proyectar(x.Candidato, x.Precio))
            .ToList();

        return new PaginaDeArticulosGrilla(items, filtrados.Count, pagina, tamanio, listaDefault?.Nombre);
    }

    /// <summary>Un artículo sin precio resuelto (<paramref name="precio"/> <c>null</c>) nunca
    /// matchea un filtro de precio ACTIVO — ni <paramref name="precioDesde"/> ni
    /// <paramref name="precioHasta"/> tienen un valor con el que comparar.</summary>
    private static bool CumpleFiltroDePrecio(decimal? precio, decimal? precioDesde, decimal? precioHasta)
    {
        if (precioDesde is not null && (precio is not { } desde || desde < precioDesde))
        {
            return false;
        }

        if (precioHasta is not null && (precio is not { } hasta || hasta > precioHasta))
        {
            return false;
        }

        return true;
    }

    private async Task<IReadOnlyDictionary<(int IdArticulo, int IdListaPrecio), decimal?>> ResolverPreciosAsync(
        IReadOnlyList<FilaCandidata> candidatos, ListaDefault? listaDefault, CancellationToken ct)
    {
        if (listaDefault is not { } lista || candidatos.Count == 0)
        {
            return new Dictionary<(int, int), decimal?>();
        }

        return await servicioDePrecios.PreciosVigentesEnLoteAsync(
            candidatos.Select(c => c.Id).ToList(), [lista.Id], reloj.Ahora, ct);
    }

    private static decimal? PrecioDe(
        int idArticulo,
        IReadOnlyDictionary<(int IdArticulo, int IdListaPrecio), decimal?> precios,
        ListaDefault? listaDefault) =>
        listaDefault is { } lista && precios.TryGetValue((idArticulo, lista.Id), out var precio) ? precio : null;

    private static ArticuloGrillaFila Proyectar(FilaCandidata c, decimal? precio) => new(
        c.Id, c.CodigoInterno, c.Nombre, precio, c.IdProveedorHabitual,
        EtiquetaProveedor(c.ProveedorNombreFantasia, c.ProveedorRazonSocial), c.Activo);

    /// <summary>Regla de etiqueta de proveedor (spec de la slice, compartida con el selector de
    /// proveedor del front): <c>NombreFantasia</c> cuando no es nulo NI blanco, si no
    /// <c>RazonSocial</c>. <c>IsNullOrWhiteSpace</c> y no solo <c>== null</c> porque un fixture
    /// (o una fila sembrada fuera del ABM, que normaliza blanco a <c>null</c> en la escritura)
    /// puede traer <c>NombreFantasia</c> en blanco sin ser técnicamente <c>null</c>.</summary>
    private static string? EtiquetaProveedor(string? nombreFantasia, string? razonSocial) =>
        string.IsNullOrWhiteSpace(nombreFantasia) ? razonSocial : nombreFantasia;

    /// <summary>Filtros de SQL (codigo/nombre/proveedor/activo) + el LEFT JOIN a
    /// <c>proveedores</c> resuelto en la MISMA consulta (sin N+1) — un proveedor soft-eliminado o
    /// una FK colgante quedan invisibles para <c>db.Proveedores</c> (filtro global de EF) y el
    /// LEFT JOIN los proyecta como <c>null</c>, nunca revienta (<see cref="EtiquetaProveedor"/>
    /// ya es null-safe). Orden estable (Nombre, luego Id) aplicado acá para que ambos caminos de
    /// <see cref="ListarAsync"/> paginen sobre la misma secuencia.</summary>
    private IQueryable<FilaCandidata> ConstruirQuery(
        string? codigo, string? nombre, int? idProveedor, bool sinProveedor, bool? activo)
    {
        var query = db.Articulos.AsQueryable();

        if (!string.IsNullOrWhiteSpace(codigo))
        {
            var termino = codigo.Trim();
            query = query.Where(a =>
                a.CodigoInterno.Contains(termino) ||
                db.CodigosBarra.Any(c => c.IdArticulo == a.Id && c.Codigo.Contains(termino)));
        }

        if (!string.IsNullOrWhiteSpace(nombre))
        {
            var terminoNombre = nombre.Trim();
            query = query.Where(a => a.Nombre.Contains(terminoNombre));
        }

        if (idProveedor is { } idProveedorValor)
        {
            query = query.Where(a => a.IdProveedorHabitual == idProveedorValor);
        }
        else if (sinProveedor)
        {
            query = query.Where(a => a.IdProveedorHabitual == null);
        }

        if (activo is { } activoValor)
        {
            query = query.Where(a => a.Activo == activoValor);
        }

        // El orden va DENTRO de la query de LINQ (orderby ... select), no encadenado DESPUÉS de
        // proyectar a FilaCandidata: EF Core no logra traducir un `.OrderBy(f => f.Nombre)`
        // aplicado sobre un `IQueryable<FilaCandidata>` ya proyectado (el record positional
        // termina adentro del key selector entero, "InvalidOperationException: could not be
        // translated") — ordenar sobre las columnas crudas de `a`/`proveedor` ANTES del `select`
        // es la forma que EF sí traduce a SQL.
        return
            from a in query
            join p in db.Proveedores on a.IdProveedorHabitual equals p.Id into proveedores
            from proveedor in proveedores.DefaultIfEmpty()
            orderby a.Nombre, a.Id
            select new FilaCandidata(
                a.Id, a.CodigoInterno, a.Nombre, a.IdProveedorHabitual,
                proveedor != null ? proveedor.NombreFantasia : null,
                proveedor != null ? proveedor.RazonSocial : null,
                a.Activo);
    }

    private readonly record struct ListaDefault(int Id, string Nombre);

    private sealed record FilaCandidata(
        int Id,
        string CodigoInterno,
        string Nombre,
        int? IdProveedorHabitual,
        string? ProveedorNombreFantasia,
        string? ProveedorRazonSocial,
        bool Activo);
}
