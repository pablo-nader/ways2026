using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Ways.Application.Abstracciones;
using Ways.Application.Precios;
using Ways.Domain.Articulos;
using Ways.Domain.Common;
using Ways.Domain.Proveedores;
using Ways.Domain.Usuarios;
using static Ways.Application.Busqueda.BusquedaSinAcentos;

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
    IContextoDeUsuario contexto,
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
        CancellationToken ct = default,
        string? orden = null,
        string? direccion = null)
    {
        if (idProveedor is not null && sinProveedor)
        {
            throw new ErrorDominio(
                "filtro_proveedor_ambiguo", "No se puede combinar idProveedor con sinProveedor.", 400);
        }

        var ordenPedido = OrdenDeGrillaDeArticulos.Interpretar(orden, direccion, PuedeVerCostos);

        pagina = Math.Max(pagina, 1);
        tamanio = Math.Clamp(tamanio, 1, ServicioDeArticulos.TamanioMaximoDePagina);

        var listaDefault = await BuscarListaDefaultAsync(ct);
        var query = ConstruirQuery(codigo, nombre, idProveedor, sinProveedor, activo, ordenPedido);

        var hayFiltroDePrecio = precioDesde is not null || precioHasta is not null;
        var ordenaPorPrecio = ordenPedido is { Columna: ColumnaDeOrdenDeGrilla.Precio };

        return hayFiltroDePrecio || ordenaPorPrecio
            ? await ListarEnMemoriaAsync(
                query, listaDefault, precioDesde, precioHasta, hayFiltroDePrecio, ordenPedido, pagina, tamanio, ct)
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

    /// <summary>Camino en memoria: el precio no es expresable en SQL, así que tanto filtrar como
    /// ORDENAR por precio exigen resolverlo para todos los candidatos. El orden por precio se aplica
    /// acá, antes de paginar; cualquier otra columna ya viene ordenada desde SQL y el filtro
    /// (<c>Where</c>) conserva esa secuencia.</summary>
    private async Task<PaginaDeArticulosGrilla> ListarEnMemoriaAsync(
        IQueryable<FilaCandidata> query, ListaDefault? listaDefault, decimal? precioDesde, decimal? precioHasta,
        bool hayFiltroDePrecio, OrdenDeGrillaDeArticulos? orden, int pagina, int tamanio, CancellationToken ct)
    {
        var totalCandidatos = await query.CountAsync(ct);
        var tope = opciones.Value.TopeDeCandidatosPorFiltroDePrecio;

        if (totalCandidatos > tope)
        {
            throw hayFiltroDePrecio
                ? new ErrorDominio(
                    "filtro_precio_excede_tope",
                    $"El filtro de precio no puede evaluar más de {tope} artículos candidatos; "
                        + "refiná codigo/nombre/idProveedor/sinProveedor/activo antes de acotar por precio.",
                    400)
                : new ErrorDominio(
                    "orden_precio_excede_tope",
                    $"El orden por precio no puede evaluar más de {tope} artículos; "
                        + "refiná codigo/nombre/idProveedor/sinProveedor/activo antes de ordenar por precio.",
                    400);
        }

        var candidatos = await query.ToListAsync(ct);
        var precios = await ResolverPreciosAsync(candidatos, listaDefault, ct);

        var filtrados = candidatos
            .Select(c => (Candidato: c, Precio: PrecioDe(c.Id, precios, listaDefault)))
            .Where(x => CumpleFiltroDePrecio(x.Precio, precioDesde, precioHasta))
            .ToList();

        if (orden is { Columna: ColumnaDeOrdenDeGrilla.Precio } porPrecio)
        {
            var sinPrecioAlFinal = filtrados.OrderBy(x => x.Precio is null);
            filtrados = (porPrecio.Descendente
                    ? sinPrecioAlFinal.ThenByDescending(x => x.Precio)
                    : sinPrecioAlFinal.ThenBy(x => x.Precio))
                .ThenBy(x => x.Candidato.Id)
                .ToList();
        }

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

    /// <summary>El costo es del back-office (admin y supervisor): esta grilla hereda
    /// <c>OperacionDePos</c>, así que un vendedor que la llame recibe <c>null</c>.</summary>
    private bool PuedeVerCostos => contexto.Rol is RolConocido.Admin or RolConocido.Supervisor;

    private ArticuloGrillaFila Proyectar(FilaCandidata c, decimal? precio) => new(
        c.Id, c.CodigoInterno, c.Nombre, precio,
        PuedeVerCostos ? c.CostoNominal : null, c.IdProveedorHabitual,
        EtiquetaProveedor(c.ProveedorNombreFantasia, c.ProveedorRazonSocial), c.Activo, c.DisponibleOnline, c.DisponibleVenta, c.TieneImagen);

    /// <summary>Regla de etiqueta de proveedor (spec de la slice, compartida con el selector de
    /// proveedor del front): <c>NombreFantasia</c> cuando no es nulo NI blanco, si no
    /// <c>RazonSocial</c>. <c>IsNullOrWhiteSpace</c> y no solo <c>== null</c> porque un fixture
    /// (o una fila sembrada fuera del ABM, que normaliza blanco a <c>null</c> en la escritura)
    /// puede traer <c>NombreFantasia</c> en blanco sin ser técnicamente <c>null</c>.</summary>
    private static string? EtiquetaProveedor(string? nombreFantasia, string? razonSocial) =>
        string.IsNullOrWhiteSpace(nombreFantasia) ? razonSocial : nombreFantasia;

    /// <summary>Filtros de SQL (codigo/nombre/proveedor/activo) + el LEFT JOIN a
    /// <c>proveedores</c> resuelto en la MISMA consulta (sin N+1). Un proveedor soft-eliminado o
    /// una FK colgante quedan invisibles para <c>db.Proveedores</c> (filtro global de EF) — esa
    /// misma visibilidad decide TRES cosas, no solo la etiqueta (<see cref="EtiquetaProveedor"/>
    /// ya es null-safe): <c>idProveedor</c> solo matchea un proveedor VISIBLE (un id colgante no
    /// matchea nada), <c>sinProveedor</c> trata un FK colgante exactamente igual que un FK nulo,
    /// y el <c>IdProveedorHabitual</c> proyectado en <see cref="FilaCandidata"/> es <c>null</c>
    /// cuando el proveedor no es visible — para que la fila sea consistente consigo misma (un
    /// proveedor de baja lógica queda invisible en TODA la grilla, no solo en la etiqueta). Orden
    /// estable (Nombre, luego Id) aplicado acá para que ambos caminos de <see cref="ListarAsync"/>
    /// paginen sobre la misma secuencia.</summary>
    private IQueryable<FilaCandidata> ConstruirQuery(
        string? codigo, string? nombre, int? idProveedor, bool sinProveedor, bool? activo,
        OrdenDeGrillaDeArticulos? orden)
    {
        var query = db.Articulos.AsQueryable();

        if (!string.IsNullOrWhiteSpace(codigo))
        {
            var patron = PatronDeContiene(codigo.Trim());
            query = query.Where(a =>
                Coincide(a.CodigoInterno, patron) ||
                db.CodigosBarra.Any(c => c.IdArticulo == a.Id && Coincide(c.Codigo, patron)));
        }

        if (!string.IsNullOrWhiteSpace(nombre))
        {
            var patronNombre = PatronDeContiene(nombre.Trim());
            query = query.Where(a => Coincide(a.Nombre, patronNombre));
        }

        if (idProveedor is { } idProveedorValor)
        {
            query = query.Where(a =>
                a.IdProveedorHabitual == idProveedorValor
                && db.Proveedores.Any(p => p.Id == a.IdProveedorHabitual));
        }
        else if (sinProveedor)
        {
            query = query.Where(a =>
                a.IdProveedorHabitual == null || !db.Proveedores.Any(p => p.Id == a.IdProveedorHabitual));
        }

        if (activo is { } activoValor)
        {
            query = query.Where(a => a.Activo == activoValor);
        }

        // El orden va ANTES de proyectar a FilaCandidata: EF Core no logra traducir un
        // `.OrderBy(f => f.Nombre)` aplicado sobre un `IQueryable<FilaCandidata>` ya proyectado (el
        // record positional termina adentro del key selector entero, "InvalidOperationException:
        // could not be translated"). Se ordena sobre `Unido` (artículo + proveedor visible), una
        // clase con inicializador de miembros que EF sí traduce, y recién después se proyecta.
        var unido =
            from a in query
            join p in db.Proveedores on a.IdProveedorHabitual equals p.Id into proveedores
            from proveedor in proveedores.DefaultIfEmpty()
            select new Unido { A = a, P = proveedor };

        return Ordenar(unido, orden).Select(x => new FilaCandidata(
            x.A.Id, x.A.CodigoInterno, x.A.Nombre, x.P != null ? x.A.IdProveedorHabitual : null,
            x.P != null ? x.P.NombreFantasia : null,
            x.P != null ? x.P.RazonSocial : null,
            x.A.Activo, x.A.CostoNominal, x.A.DisponibleOnline, x.A.DisponibleVenta,
            db.ImagenesArticulo.Any(i => i.IdArticulo == x.A.Id)));
    }

    /// <summary>Orden de SQL, aplicado antes de paginar. Sin orden pedido: Nombre, luego Id (el orden de
    /// siempre). Con orden: la columna pedida y SIEMPRE Id ascendente como desempate, para que
    /// dos páginas consecutivas nunca repitan ni salteen una fila. Los vacíos van al final en las dos
    /// direcciones; un proveedor dado de baja o con FK colgante llega como <c>P == null</c> (el join va
    /// contra <c>db.Proveedores</c>, que ya excluye las bajas) y se ordena igual que "sin proveedor". Los
    /// textos usan la colación de la base, igual que el orden por nombre de siempre. El precio no se
    /// ordena acá: no es expresable en SQL y lo ordena <see cref="ListarEnMemoriaAsync"/>.</summary>
    private IOrderedQueryable<Unido> Ordenar(IQueryable<Unido> query, OrdenDeGrillaDeArticulos? orden)
    {
        if (orden is not { } pedido)
        {
            return query.OrderBy(x => x.A.Nombre).ThenBy(x => x.A.Id);
        }

        var desc = pedido.Descendente;
        IOrderedQueryable<Unido> ordenado = pedido.Columna switch
        {
            ColumnaDeOrdenDeGrilla.Codigo => desc
                ? query.OrderByDescending(x => x.A.CodigoInterno)
                : query.OrderBy(x => x.A.CodigoInterno),
            ColumnaDeOrdenDeGrilla.Costo => desc
                ? query.OrderBy(x => x.A.CostoNominal == null).ThenByDescending(x => x.A.CostoNominal)
                : query.OrderBy(x => x.A.CostoNominal == null).ThenBy(x => x.A.CostoNominal),
            ColumnaDeOrdenDeGrilla.Proveedor => desc
                ? query.OrderBy(x => x.P == null).ThenByDescending(x =>
                    x.P == null ? null : string.IsNullOrWhiteSpace(x.P.NombreFantasia) ? x.P.RazonSocial : x.P.NombreFantasia)
                : query.OrderBy(x => x.P == null).ThenBy(x =>
                    x.P == null ? null : string.IsNullOrWhiteSpace(x.P.NombreFantasia) ? x.P.RazonSocial : x.P.NombreFantasia),
            ColumnaDeOrdenDeGrilla.Disponibilidad => desc
                ? query.OrderByDescending(x => x.A.DisponibleVenta)
                    .ThenByDescending(x => x.A.DisponibleOnline && x.A.DisponibleVenta && x.A.Activo)
                    .ThenByDescending(x => db.ImagenesArticulo.Any(i => i.IdArticulo == x.A.Id))
                : query.OrderBy(x => x.A.DisponibleVenta)
                    .ThenBy(x => x.A.DisponibleOnline && x.A.DisponibleVenta && x.A.Activo)
                    .ThenBy(x => db.ImagenesArticulo.Any(i => i.IdArticulo == x.A.Id)),
            ColumnaDeOrdenDeGrilla.Estado => desc
                ? query.OrderByDescending(x => x.A.Activo)
                : query.OrderBy(x => x.A.Activo),
            ColumnaDeOrdenDeGrilla.Nombre => desc
                ? query.OrderByDescending(x => x.A.Nombre)
                : query.OrderBy(x => x.A.Nombre),
            _ => query.OrderBy(x => x.A.Nombre),
        };

        return ordenado.ThenBy(x => x.A.Id);
    }

    private sealed class Unido
    {
        public Articulo A { get; init; } = null!;

        public Proveedor? P { get; init; }
    }

    private readonly record struct ListaDefault(int Id, string Nombre);

    private sealed record FilaCandidata(
        int Id,
        string CodigoInterno,
        string Nombre,
        int? IdProveedorHabitual,
        string? ProveedorNombreFantasia,
        string? ProveedorRazonSocial,
        bool Activo,
        decimal? CostoNominal,
        bool DisponibleOnline,
        bool DisponibleVenta,
        bool TieneImagen);
}

public enum ColumnaDeOrdenDeGrilla
{
    Codigo,
    Nombre,
    Costo,
    Precio,
    Proveedor,
    Disponibilidad,
    Estado,
}

/// <summary>Orden pedido a <c>GET /api/articulos/grilla</c> (<c>orden</c> + <c>direccion</c>). Lista
/// cerrada: una clave o dirección desconocida se RECHAZA con 400, nunca se ignora. <c>costo</c> solo lo
/// puede pedir quien ve costos (admin y supervisor): ordenar por una columna que el rol no ve filtraría
/// su orden relativo.</summary>
public readonly record struct OrdenDeGrillaDeArticulos(ColumnaDeOrdenDeGrilla Columna, bool Descendente)
{
    public const string ValorAscendente = "ascendente";
    public const string ValorDescendente = "descendente";

    private static readonly IReadOnlyDictionary<string, ColumnaDeOrdenDeGrilla> Claves =
        new Dictionary<string, ColumnaDeOrdenDeGrilla>(StringComparer.Ordinal)
        {
            ["codigo"] = ColumnaDeOrdenDeGrilla.Codigo,
            ["nombre"] = ColumnaDeOrdenDeGrilla.Nombre,
            ["costo"] = ColumnaDeOrdenDeGrilla.Costo,
            ["precio"] = ColumnaDeOrdenDeGrilla.Precio,
            ["proveedor"] = ColumnaDeOrdenDeGrilla.Proveedor,
            ["disponibilidad"] = ColumnaDeOrdenDeGrilla.Disponibilidad,
            ["estado"] = ColumnaDeOrdenDeGrilla.Estado,
        };

    /// <summary>Sin <paramref name="orden"/> ni <paramref name="direccion"/> devuelve <c>null</c> (orden de
    /// siempre). <paramref name="direccion"/> omitida con <paramref name="orden"/> presente es ascendente.</summary>
    public static OrdenDeGrillaDeArticulos? Interpretar(string? orden, string? direccion, bool puedeVerCostos)
    {
        if (orden is null)
        {
            return direccion is null
                ? null
                : throw new ErrorDominio("direccion_sin_orden", "direccion exige indicar también orden.", 400);
        }

        if (!Claves.TryGetValue(orden, out var columna))
        {
            throw new ErrorDominio(
                "orden_invalido",
                $"orden '{orden}' no es válido; valores posibles: {string.Join(", ", Claves.Keys)}.",
                400);
        }

        if (columna == ColumnaDeOrdenDeGrilla.Costo && !puedeVerCostos)
        {
            throw new ErrorDominio("orden_no_permitido", "El rol actual no puede ordenar por costo.", 400);
        }

        var descendente = direccion switch
        {
            null or ValorAscendente => false,
            ValorDescendente => true,
            _ => throw new ErrorDominio(
                "direccion_invalida",
                $"direccion '{direccion}' no es válida; valores posibles: {ValorAscendente}, {ValorDescendente}.",
                400),
        };

        return new OrdenDeGrillaDeArticulos(columna, descendente);
    }
}
