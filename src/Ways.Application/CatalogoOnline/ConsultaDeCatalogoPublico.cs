using System.Linq.Expressions;
using Microsoft.EntityFrameworkCore;
using Ways.Application.Abstracciones;
using Ways.Domain.Articulos;
using Ways.Application.Ofertas;
using Ways.Domain.Catalogos;
using Ways.Domain.Ofertas;
using Ways.Domain.Precios;
using static Ways.Application.Busqueda.BusquedaSinAcentos;

namespace Ways.Application.CatalogoOnline;

/// <summary>
/// Las consultas del catálogo público de la tienda. Corren sobre el contexto que se les inyecta: en
/// producción, el de un alcance en modo de acceso <c>publico</c> que abre <see cref="ILectorDeCatalogoPublico"/>,
/// donde RLS deja leer solo lo publicado del tenant del host. Quién decide qué:
/// <list type="bullet">
/// <item>Las policies <c>*_publico_lectura</c> deciden lo que es del tenant, está vivo y, en <c>articulos</c>,
/// está disponible online y a la venta y activo (<c>disponible_online AND disponible_venta AND activo</c>), en
/// <c>ofertas</c> disponible online y activa, y en <c>vitrinas</c> que la vitrina está publicada.
/// Esas cláusulas NO se repiten acá, como en <see cref="Vitrinas.ILectorDeVitrinaPublica"/>: una prueba contra
/// el endpoint no las distinguiría de la policy, y las prueba la conexión cruda.</item>
/// <item>Esta clase decide lo que la policy no puede: que el artículo esté disponible para la empresa de la
/// vitrina, que tenga un precio vigente en la lista de la tienda, la búsqueda, la categoría, y que las listas y
/// las categorías estén activas y sean visibles para esa empresa. La búsqueda mira solo el nombre: el código
/// interno no se publica en el DTO, y buscar por él sería un oráculo sobre un dato que la tienda no muestra.</item>
/// </list>
///
/// <para><b>Precio.</b> No se instancia <c>ServicioDeOfertas</c> ni el motor del POS
/// (<c>ServicioDeInstantaneaDePos</c>): necesitan el usuario de la request y leen tablas que el modo público no ve.
/// Lo que se reusa es lo puro: el precio vigente de una lista fija es la fila con <c>vigente_desde &lt;= ahora</c> y
/// <c>vigente_hasta</c> nulo o futuro (<c>ServicioDePrecios</c>), y el de una derivada sale de la fila de su lista base
/// con <see cref="ResolvedorDePrecios.ResolverPrecioDerivado"/>, el mismo redondeo (2 decimales, lejos de cero). La
/// existencia del precio se decide en SQL, antes de paginar, para que <c>total</c> y las cuentas de categoría cuenten
/// solo artículos que se pueden vender; el monto se resuelve solo para la página.</para>
///
/// <para><b>Ofertas.</b> Sobre el precio de lista de cada artículo de la página se corre el resolvedor puro del
/// dominio (<see cref="ResolvedorDeOfertas"/>) a cantidad 1, con las ofertas que la policy deja ver (disponibles online
/// y activas), sus listas objetivo, la jerarquía de categorías y la hora local del servidor
/// (<see cref="ServicioDeOfertas.DescomponerHoraLocal"/>, la misma que usa el POS). La lista que se le pasa al
/// resolvedor es la ORIGINAL de la vitrina, también cuando es derivada: así una oferta dirigida a esa lista aplica. Es
/// una vista previa por artículo: las ofertas con cantidad mínima mayor que 1 no aplican, y las escalonadas por
/// cantidad o por familia en el carrito quedan para el checkout.</para>
///
/// <para><b>Lista de la tienda.</b> La de la vitrina (<c>id_lista_precio</c>) o, si no tiene, la default compartida
/// del tenant (<c>id_empresa IS NULL AND es_default</c>, la misma que usa la grilla del back-office). Tiene que
/// estar activa y ser compartida o de la empresa de la vitrina; una derivada necesita porcentaje y una base fija.
/// Si no se cumple, el catálogo sale vacío en vez de fallar: es una configuración a corregir, no un error del
/// visitante.</para>
/// </summary>
public class ConsultaDeCatalogoPublico(IWaysDbContext db, IRelojDelSistema reloj)
{
    public const int TamanoPorDefecto = 24;
    public const int TamanoMaximo = 60;
    public const int LargoMaximoDeLaBusqueda = 100;

    /// <summary>Tope de la página pedida. Con el tamaño máximo el desplazamiento llega a unos 600.000 registros
    /// y cabe en un <c>int</c>; sin tope, un número de página enorme desborda el producto y Postgres rechaza el
    /// <c>OFFSET</c> negativo. Una página más allá del final del catálogo sale vacía, con el <c>total</c> real.</summary>
    public const int PaginaMaxima = 10_000;

    /// <summary>La lista donde están las filas de <c>precios</c> (la propia si es fija, la base si es derivada),
    /// el porcentaje que se aplica a ese monto (<c>null</c> si es fija) y la lista ORIGINAL de la tienda, la que
    /// miran las ofertas dirigidas (<c>ofertas_listas</c>) aunque sea derivada.</summary>
    internal sealed record ListaDePrecios(int IdListaConPrecios, decimal? Porcentaje, int IdListaOriginal);

    /// <summary>
    /// El catálogo de la vitrina publicada del tenant. <c>null</c> cuando no hay vitrina publicada.
    /// <see cref="CatalogoPublico.Categorias"/> son las categorías de los artículos que coinciden con la búsqueda
    /// IGNORANDO el filtro de categoría, para que los chips conserven sus cuentas mientras hay una seleccionada;
    /// <see cref="CatalogoPublico.Total"/> y la página sí lo aplican. Un <c>idCategoria</c> que no es una
    /// categoría visible no coincide con nada.
    /// </summary>
    public async Task<CatalogoPublico?> ObtenerCatalogoAsync(ConsultaDeCatalogo consulta, CancellationToken ct = default)
    {
        var pagina = Math.Clamp(consulta.Pagina, 1, PaginaMaxima);
        var tamano = Math.Clamp(consulta.Tamano, 1, TamanoMaximo);

        var vitrina = await db.Vitrinas
            .AsNoTracking()
            .Select(v => new { v.IdEmpresa, v.IdListaPrecio })
            .FirstOrDefaultAsync(ct);

        if (vitrina is null)
        {
            return null;
        }

        var idEmpresa = vitrina.IdEmpresa;

        if (await ResolverListaAsync(idEmpresa, vitrina.IdListaPrecio, ct) is not { } lista)
        {
            return new CatalogoPublico([], [], 0, pagina, tamano);
        }

        var idLista = lista.IdListaConPrecios;
        var ahora = reloj.Ahora;

        var publicados = db.Articulos
            .AsNoTracking()
            .Where(a => a.UnidadVenta != UnidadVenta.Importe)
            .Where(a => a.DisponibleParaTodas
                || db.ArticulosEmpresas.Any(ae => ae.IdArticulo == a.Id && ae.IdEmpresa == idEmpresa))
            .Where(a => db.Precios.Where(PrecioVigenteEn(ahora)).Any(p => p.IdArticulo == a.Id && p.IdListaPrecio == idLista));

        var buscar = Normalizar(consulta.Buscar);
        if (buscar is not null)
        {
            var patron = PatronDeContiene(buscar);
            publicados = publicados.Where(a => Coincide(a.Nombre, patron));
        }

        var categoriasVisibles = await db.Categorias
            .AsNoTracking()
            .Where(c => c.Activo && (c.IdEmpresa == null || c.IdEmpresa == idEmpresa))
            .Select(c => new { c.Id, c.Nombre, c.Orden })
            .ToListAsync(ct);
        var idsVisibles = categoriasVisibles.Select(c => c.Id).ToHashSet();

        var cuentas = await publicados
            .Where(a => a.IdCategoria != null)
            .GroupBy(a => a.IdCategoria!.Value)
            .Select(g => new { IdCategoria = g.Key, Cantidad = g.Count() })
            .ToListAsync(ct);

        var categorias = categoriasVisibles
            .Join(cuentas, c => c.Id, n => n.IdCategoria, (c, n) => (c.Orden, c.Nombre, Categoria: new CategoriaPublica(c.Id, c.Nombre, n.Cantidad)))
            .OrderBy(x => x.Orden)
            .ThenBy(x => x.Nombre, StringComparer.OrdinalIgnoreCase)
            .ThenBy(x => x.Categoria.Id)
            .Select(x => x.Categoria)
            .ToList();

        var filtrados = consulta.IdCategoria switch
        {
            null => publicados,
            { } id when idsVisibles.Contains(id) => publicados.Where(a => a.IdCategoria == id),
            _ => publicados.Where(_ => false)
        };

        var total = await filtrados.CountAsync(ct);

        var filas = await filtrados
            .OrderBy(a => a.Nombre)
            .ThenBy(a => a.Id)
            .Skip((pagina - 1) * tamano)
            .Take(tamano)
            .Select(a => new
            {
                a.Id,
                a.Nombre,
                a.Descripcion,
                a.IdCategoria,
                a.IdGrupo,
                a.IdFamilia,
                a.UnidadVenta,
                ImagenActualizadaEn = db.ImagenesArticulo
                    .Where(i => i.IdArticulo == a.Id)
                    .Select(i => (DateTimeOffset?)i.ActualizadoEn)
                    .FirstOrDefault()
            })
            .ToListAsync(ct);

        var montos = await MontosVigentesAsync([.. filas.Select(f => f.Id)], idLista, ahora, ct);

        var conPrecio = filas
            .Where(f => montos.ContainsKey(f.Id))
            .Select(f => (Fila: f, Precio: PrecioDeLista(montos[f.Id], lista)))
            .ToList();

        var ofertas = await ResolverOfertasAsync(
            [.. conPrecio.Select(x => new ArticuloConPrecio(x.Fila.Id, x.Fila.IdCategoria, x.Fila.IdGrupo, x.Fila.IdFamilia, x.Precio))],
            lista.IdListaOriginal, idEmpresa, ahora, ct);

        var articulos = conPrecio
            .Select(x =>
            {
                var oferta = ofertas.GetValueOrDefault(x.Fila.Id);

                return new ArticuloPublico(
                    x.Fila.Id,
                    x.Fila.Nombre,
                    x.Fila.Descripcion,
                    x.Fila.IdCategoria is { } idCategoria && idsVisibles.Contains(idCategoria) ? idCategoria : null,
                    oferta?.Precio ?? x.Precio,
                    x.Fila.UnidadVenta,
                    TieneImagen: x.Fila.ImagenActualizadaEn is not null,
                    ImagenVersion: x.Fila.ImagenActualizadaEn is { } actualizada ? ImagenDeArticulo.Version(actualizada) : null,
                    PrecioOriginal: oferta is null ? null : x.Precio,
                    Oferta: oferta?.Nombre);
            })
            .ToList();

        return new CatalogoPublico(categorias, articulos, total, pagina, tamano);
    }

    /// <summary>El monto de la fila de <c>precios</c> llevado a la lista de la tienda: el mismo si es fija, el derivado si no.</summary>
    private static decimal PrecioDeLista(decimal monto, ListaDePrecios lista) =>
        lista.Porcentaje is { } porcentaje ? ResolvedorDePrecios.ResolverPrecioDerivado(monto, porcentaje) : monto;

    /// <summary>Lo que el resolvedor de ofertas necesita saber de un artículo además de su precio.</summary>
    internal sealed record DimensionesDeArticulo(int Id, int? IdCategoria, int? IdGrupo, int? IdFamilia);

    /// <summary>
    /// El precio final de tienda de cada artículo (el de lista con la oferta online aplicada, a cantidad 1), por la misma
    /// ruta que el catálogo. Un artículo sin precio vigente en la lista no figura.
    /// </summary>
    internal async Task<Dictionary<int, decimal>> PreciosFinalesAsync(
        IReadOnlyList<DimensionesDeArticulo> articulos, ListaDePrecios lista, int idEmpresa, DateTimeOffset ahora,
        CancellationToken ct)
    {
        var montos = await MontosVigentesAsync([.. articulos.Select(a => a.Id)], lista.IdListaConPrecios, ahora, ct);

        var conPrecio = articulos
            .Where(a => montos.ContainsKey(a.Id))
            .Select(a => new ArticuloConPrecio(a.Id, a.IdCategoria, a.IdGrupo, a.IdFamilia, PrecioDeLista(montos[a.Id], lista)))
            .ToList();

        var ofertas = await ResolverOfertasAsync(conPrecio, lista.IdListaOriginal, idEmpresa, ahora, ct);

        return conPrecio.ToDictionary(a => a.Id, a => ofertas.GetValueOrDefault(a.Id)?.Precio ?? a.Precio);
    }

    /// <summary>Tipo y marca de carga de la imagen de un artículo publicado y disponible para la empresa de la
    /// vitrina publicada, sin leer los bytes. <c>null</c> en cualquier otro caso.</summary>
    public async Task<VersionDeImagen?> ObtenerVersionDeImagenAsync(int idArticulo, CancellationToken ct = default)
    {
        if (await EmpresaDeLaVitrinaAsync(ct) is not { } idEmpresa)
        {
            return null;
        }

        return await ImagenesDisponibles(idArticulo, idEmpresa)
            .Select(i => new VersionDeImagen(i.ContentType, i.ActualizadoEn))
            .FirstOrDefaultAsync(ct);
    }

    /// <summary>Los bytes de la imagen, con la misma regla que <see cref="ObtenerVersionDeImagenAsync"/>.</summary>
    public async Task<ImagenServida?> ObtenerImagenAsync(int idArticulo, CancellationToken ct = default)
    {
        if (await EmpresaDeLaVitrinaAsync(ct) is not { } idEmpresa)
        {
            return null;
        }

        return await ImagenesDisponibles(idArticulo, idEmpresa)
            .Select(i => new ImagenServida(i.Contenido, i.ContentType, i.ActualizadoEn))
            .FirstOrDefaultAsync(ct);
    }

    /// <summary>Un artículo de la página con su precio de lista ya resuelto, listo para el resolvedor de ofertas.</summary>
    private sealed record ArticuloConPrecio(int Id, int? IdCategoria, int? IdGrupo, int? IdFamilia, decimal Precio);

    /// <summary>La oferta que rebaja un artículo: <paramref name="Precio"/> es el final, <paramref name="Nombre"/> el de la
    /// aplicada de mayor prioridad que descuenta algo.</summary>
    private sealed record OfertaDelArticulo(decimal Precio, string Nombre);

    /// <summary>
    /// Las ofertas que rebajan cada artículo de la página, a cantidad 1 y en la lista original de la tienda. Tres
    /// consultas fijas (categorías, ofertas, listas de las ofertas) sin importar el tamaño de la página; el resto del
    /// matching es <see cref="ResolvedorDeOfertas.Coincide"/> en memoria. Solo figuran los artículos cuyo precio baja:
    /// una oferta que no descuenta (por ejemplo, un precio unitario mayor que el de lista) no cuenta.
    /// </summary>
    private async Task<Dictionary<int, OfertaDelArticulo>> ResolverOfertasAsync(
        IReadOnlyList<ArticuloConPrecio> articulos, int idLista, int idEmpresa, DateTimeOffset ahora, CancellationToken ct)
    {
        if (articulos.Count == 0)
        {
            return [];
        }

        var padrePorCategoria = await db.Categorias
            .AsNoTracking()
            .Select(c => new { c.Id, c.IdCategoriaPadre })
            .ToDictionaryAsync(c => c.Id, c => c.IdCategoriaPadre, ct);

        var categoriasPorArticulo = articulos.ToDictionary(
            a => a.Id,
            a => a.IdCategoria is { } idCategoria
                ? CadenaDeCategorias.ConstruirAncestros(idCategoria, padrePorCategoria)
                : (IReadOnlySet<int>)new HashSet<int>());

        var idsArticulo = articulos.Select(a => a.Id).ToList();
        var idsGrupo = articulos.Where(a => a.IdGrupo.HasValue).Select(a => a.IdGrupo!.Value).Distinct().ToList();
        var idsFamilia = articulos.Where(a => a.IdFamilia.HasValue).Select(a => a.IdFamilia!.Value).Distinct().ToList();
        var idsCategoria = categoriasPorArticulo.Values.SelectMany(c => c).Distinct().ToList();

        var ofertas = await db.Ofertas
            .AsNoTracking()
            .Where(o => (o.IdArticulo != null && idsArticulo.Contains(o.IdArticulo.Value))
                || (o.IdGrupo != null && idsGrupo.Contains(o.IdGrupo.Value))
                || (o.IdFamilia != null && idsFamilia.Contains(o.IdFamilia.Value))
                || (o.IdCategoria != null && idsCategoria.Contains(o.IdCategoria.Value)))
            .Where(o => o.IdEmpresa == null || o.IdEmpresa == idEmpresa)
            .ToListAsync(ct);

        if (ofertas.Count == 0)
        {
            return [];
        }

        var idsOferta = ofertas.Select(o => o.Id).ToList();
        var listasPorOferta = (await db.OfertasListas
                .AsNoTracking()
                .Where(ol => idsOferta.Contains(ol.IdOferta))
                .Select(ol => new { ol.IdOferta, ol.IdListaPrecio })
                .ToListAsync(ct))
            .GroupBy(ol => ol.IdOferta)
            .ToDictionary(g => g.Key, g => (IReadOnlySet<int>)g.Select(x => x.IdListaPrecio).ToHashSet());

        var candidatas = ofertas
            .Select(o => new OfertaCandidata(
                o.Id, o.Nombre, o.Prioridad, o.Acumulable,
                ReglaDeOfertas.LeerAlcance(o), ReglaDeOfertas.LeerBeneficio(o), o.CantidadMinima,
                o.FechaDesde, o.FechaHasta, o.HoraDesde, o.HoraHasta,
                ReglaDeOfertas.LeerDiasSemana(o.DiasSemana),
                listasPorOferta.TryGetValue(o.Id, out var listas) ? listas : new HashSet<int>()))
            .ToList();

        var (fecha, hora, diaSemana) = ServicioDeOfertas.DescomponerHoraLocal(ahora);
        var resultado = new Dictionary<int, OfertaDelArticulo>();

        foreach (var articulo in articulos)
        {
            var linea = new LineaAResolver(
                articulo.Id, articulo.IdGrupo, categoriasPorArticulo[articulo.Id].ToList(), idLista, Cantidad: 1m,
                articulo.Precio, fecha, hora, diaSemana, articulo.IdFamilia, CantidadDeFamilia: 1m);

            var resolucion = ResolvedorDeOfertas.Resolver(linea, candidatas);

            if (resolucion.Aplicadas.FirstOrDefault(a => a.DescuentoUnitario > 0) is { Nombre: { } nombre })
            {
                resultado[articulo.Id] = new OfertaDelArticulo(resolucion.PrecioFinal, nombre);
            }
        }

        return resultado;
    }

    internal async Task<int?> EmpresaDeLaVitrinaAsync(CancellationToken ct) =>
        await db.Vitrinas.AsNoTracking().Select(v => (int?)v.IdEmpresa).FirstOrDefaultAsync(ct);

    /// <summary>La imagen del artículo si el artículo se ve (la policy deja solo los publicados y activos) y
    /// está disponible para la empresa.</summary>
    private IQueryable<ImagenDeArticulo> ImagenesDisponibles(int idArticulo, int idEmpresa) =>
        from i in db.ImagenesArticulo.AsNoTracking()
        join a in db.Articulos on i.IdArticulo equals a.Id
        where a.Id == idArticulo
            && (a.DisponibleParaTodas
                || db.ArticulosEmpresas.Any(ae => ae.IdArticulo == a.Id && ae.IdEmpresa == idEmpresa))
        select i;

    internal async Task<ListaDePrecios?> ResolverListaAsync(int idEmpresa, int? idListaDeLaVitrina, CancellationToken ct)
    {
        var visibles = db.ListasPrecio
            .AsNoTracking()
            .Where(l => l.Activo && (l.IdEmpresa == null || l.IdEmpresa == idEmpresa));

        var elegida = idListaDeLaVitrina is { } idLista
            ? visibles.Where(l => l.Id == idLista)
            : visibles.Where(l => l.IdEmpresa == null && l.EsDefault);

        var lista = await elegida
            .Select(l => new { l.Id, l.Modo, l.IdListaBase, l.Porcentaje })
            .FirstOrDefaultAsync(ct);

        if (lista is null)
        {
            return null;
        }

        if (lista.Modo == ModoLista.Fija)
        {
            return new ListaDePrecios(lista.Id, Porcentaje: null, lista.Id);
        }

        if (lista.Porcentaje is not { } porcentaje || lista.IdListaBase is not { } idBase)
        {
            return null;
        }

        var baseEsFija = await db.ListasPrecio
            .AsNoTracking()
            .AnyAsync(l => l.Id == idBase && l.Modo == ModoLista.Fija, ct);

        return baseEsFija ? new ListaDePrecios(idBase, porcentaje, lista.Id) : null;
    }

    /// <summary>La definición de "precio vigente" de <c>ServicioDePrecios.PreciosVigentesEnLoteAsync</c>, una sola vez
    /// para la existencia del precio (que decide qué artículos aparecen) y para su monto.</summary>
    internal static Expression<Func<Precio, bool>> PrecioVigenteEn(DateTimeOffset ahora) =>
        p => p.VigenteDesde <= ahora && (p.VigenteHasta == null || p.VigenteHasta > ahora);

    /// <summary>El monto vigente de cada artículo en la lista, con el mismo desempate que
    /// <c>ServicioDePrecios.PreciosVigentesEnLoteAsync</c>: la fila que empezó más tarde.</summary>
    internal async Task<Dictionary<int, decimal>> MontosVigentesAsync(
        List<int> idsArticulo, int idLista, DateTimeOffset ahora, CancellationToken ct)
    {
        if (idsArticulo.Count == 0)
        {
            return [];
        }

        var filas = await db.Precios
            .AsNoTracking()
            .Where(PrecioVigenteEn(ahora))
            .Where(p => idsArticulo.Contains(p.IdArticulo) && p.IdListaPrecio == idLista)
            .Select(p => new { p.IdArticulo, p.Monto, p.VigenteDesde })
            .ToListAsync(ct);

        return filas
            .GroupBy(f => f.IdArticulo)
            .ToDictionary(g => g.Key, g => g.OrderByDescending(f => f.VigenteDesde).First().Monto);
    }

    /// <summary>Quita los caracteres de control (un <c> </c> que llegara a Postgres lo rechaza con un error) y
    /// recorta los espacios; lo que queda vacío es "sin búsqueda". El largo se acota para que un término enorme no
    /// llegue al <c>LIKE</c>.</summary>
    public static string? Normalizar(string? buscar)
    {
        if (buscar is null)
        {
            return null;
        }

        var texto = string.Concat(buscar.Where(c => !char.IsControl(c))).Trim();

        if (texto.Length == 0)
        {
            return null;
        }

        return texto.Length > LargoMaximoDeLaBusqueda ? texto[..LargoMaximoDeLaBusqueda] : texto;
    }
}
