using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Ways.Domain.Articulos;
using Ways.Domain.Catalogos;
using Ways.Domain.Common;
using Ways.Infrastructure.Multitenancy;

namespace Ways.IntegrationTests;

/// <summary>
/// Siembra y clientes compartidos por las pruebas de la tienda online (publicación, imagen y catálogo público).
/// Todo se inserta directo en la base en modo plataforma, porque es el estado de partida de cada prueba y no lo
/// que se prueba. Un comercio de prueba se arma con <see cref="ApoyoDeVitrinas"/> (tenant con su admin, una
/// empresa y un dominio verificado propio); acá se le agregan lo que un catálogo necesita: área, listas de
/// precios, categorías, artículos, precios e imágenes.
/// </summary>
internal sealed class ApoyoDeCatalogoOnline(WaysApiFixture fixture)
{
    public ApoyoDeVitrinas Vitrinas { get; } = new(fixture);

    public ApoyoDeDominios Dominios => Vitrinas.Dominios;

    /// <summary>Un comercio con su vitrina publicada, un área y la lista default compartida (fija).</summary>
    public sealed record Tienda(ApoyoDeVitrinas.Comercio Comercio, int IdArea, int IdAlicuotaIva, int IdListaDefault)
    {
        public int IdTenant => Comercio.IdTenant;

        public int IdEmpresa => Comercio.IdEmpresa;

        public string Host => Comercio.Host;
    }

    public async Task<Tienda> SembrarTiendaAsync(string prefijo, bool vitrinaPublicada = true)
    {
        var comercio = await Vitrinas.SembrarComercioAsync(prefijo);
        var idArea = await SembrarAreaAsync(comercio.IdTenant);
        var idListaDefault = await SembrarListaAsync(comercio.IdTenant, "General", esDefault: true);

        if (vitrinaPublicada)
        {
            await Vitrinas.SembrarVitrinaAsync(comercio.IdTenant, comercio.IdEmpresa, publicada: true, tagline: prefijo);
        }

        await using var db = fixture.CrearContextoDeAplicacion(TenantActualFijo.Plataforma);
        var idAlicuotaIva = await db.AlicuotasIva.Select(a => a.Id).FirstAsync();

        return new Tienda(comercio, idArea, idAlicuotaIva, idListaDefault);
    }

    public async Task<int> SembrarAreaAsync(int idTenant)
    {
        var ahora = DateTimeOffset.UtcNow;

        await using var db = fixture.CrearContextoDeAplicacion(TenantActualFijo.Plataforma);
        var area = new Area
        {
            IdTenant = idTenant,
            Nombre = $"Área {Guid.NewGuid():N}",
            Orden = 1,
            CreatedAt = ahora,
            UpdatedAt = ahora
        };
        db.Areas.Add(area);
        await db.SaveChangesAsync();

        return area.Id;
    }

    public async Task<int> SembrarListaAsync(
        int idTenant,
        string nombre,
        bool esDefault = false,
        bool activo = true,
        int? idEmpresa = null,
        ModoLista modo = ModoLista.Fija,
        int? idListaBase = null,
        decimal? porcentaje = null,
        bool eliminada = false)
    {
        var ahora = DateTimeOffset.UtcNow;

        await using var db = fixture.CrearContextoDeAplicacion(TenantActualFijo.Plataforma);
        var lista = new ListaPrecio
        {
            IdTenant = idTenant,
            Nombre = nombre,
            IdEmpresa = idEmpresa,
            EsDefault = esDefault,
            Activo = activo,
            Modo = modo,
            IdListaBase = idListaBase,
            Porcentaje = porcentaje,
            DeletedAt = eliminada ? ahora : null,
            CreatedAt = ahora,
            UpdatedAt = ahora
        };
        db.ListasPrecio.Add(lista);
        await db.SaveChangesAsync();

        return lista.Id;
    }

    public async Task<int> SembrarCategoriaAsync(
        int idTenant, string nombre, int orden = 1, int? idEmpresa = null, bool activo = true, bool eliminada = false)
    {
        var ahora = DateTimeOffset.UtcNow;

        await using var db = fixture.CrearContextoDeAplicacion(TenantActualFijo.Plataforma);
        var categoria = new Categoria
        {
            IdTenant = idTenant,
            Nombre = nombre,
            Orden = orden,
            IdEmpresa = idEmpresa,
            Activo = activo,
            DeletedAt = eliminada ? ahora : null,
            CreatedAt = ahora,
            UpdatedAt = ahora
        };
        db.Categorias.Add(categoria);
        await db.SaveChangesAsync();

        return categoria.Id;
    }

    /// <summary>Un artículo. Con <paramref name="idEmpresaDisponible"/> queda restringido a esa empresa
    /// (<c>disponible_para_todas = false</c> más su fila de <c>articulos_empresas</c>).</summary>
    public async Task<int> SembrarArticuloAsync(
        Tienda tienda,
        string nombre,
        bool publicado = true,
        bool activo = true,
        bool eliminado = false,
        int? idEmpresaDisponible = null,
        bool restringidoSinEmpresas = false,
        int? idCategoria = null,
        UnidadVenta unidadVenta = UnidadVenta.Unidad,
        string? descripcion = null,
        string? codigoInterno = null,
        int? idTenant = null,
        int? idArea = null)
    {
        var ahora = DateTimeOffset.UtcNow;
        var tenant = idTenant ?? tienda.IdTenant;

        await using var db = fixture.CrearContextoDeAplicacion(TenantActualFijo.Plataforma);
        var articulo = new Articulo
        {
            IdTenant = tenant,
            CodigoInterno = codigoInterno ?? $"C-{Guid.NewGuid():N}"[..20],
            Nombre = nombre,
            Descripcion = descripcion,
            IdArea = idArea ?? tienda.IdArea,
            IdCategoria = idCategoria,
            IdAlicuotaIva = tienda.IdAlicuotaIva,
            UnidadVenta = unidadVenta,
            EsProducto = true,
            PublicadoWeb = publicado,
            Activo = activo,
            DisponibleParaTodas = idEmpresaDisponible is null && !restringidoSinEmpresas,
            DeletedAt = eliminado ? ahora : null,
            CreatedAt = ahora,
            UpdatedAt = ahora
        };
        db.Articulos.Add(articulo);
        await db.SaveChangesAsync();

        if (idEmpresaDisponible is { } idEmpresa)
        {
            db.ArticulosEmpresas.Add(new ArticuloEmpresa { IdArticulo = articulo.Id, IdEmpresa = idEmpresa, IdTenant = tenant });
            await db.SaveChangesAsync();
        }

        return articulo.Id;
    }

    /// <summary>Una fila de <c>precios</c>. Por defecto vigente desde ayer y sin cierre.</summary>
    public async Task<int> SembrarPrecioAsync(
        int idTenant,
        int idArticulo,
        int idLista,
        decimal monto,
        DateTimeOffset? desde = null,
        DateTimeOffset? hasta = null,
        bool eliminado = false)
    {
        var ahora = DateTimeOffset.UtcNow;

        await using var db = fixture.CrearContextoDeAplicacion(TenantActualFijo.Plataforma);
        var precio = new Ways.Domain.Precios.Precio
        {
            IdTenant = idTenant,
            IdArticulo = idArticulo,
            IdListaPrecio = idLista,
            Monto = monto,
            VigenteDesde = desde ?? ahora.AddDays(-1),
            VigenteHasta = hasta,
            DeletedAt = eliminado ? ahora : null,
            CreatedAt = ahora,
            UpdatedAt = ahora
        };
        db.Precios.Add(precio);
        await db.SaveChangesAsync();

        return precio.Id;
    }

    /// <summary>Atajo: un artículo publicado y con precio vigente en la lista default de la tienda.</summary>
    public async Task<int> SembrarVendibleAsync(
        Tienda tienda,
        string nombre,
        decimal precio,
        int? idCategoria = null,
        UnidadVenta unidadVenta = UnidadVenta.Unidad,
        string? descripcion = null,
        string? codigoInterno = null,
        bool publicado = true)
    {
        var id = await SembrarArticuloAsync(
            tienda, nombre, publicado: publicado, idCategoria: idCategoria, unidadVenta: unidadVenta,
            descripcion: descripcion, codigoInterno: codigoInterno);
        await SembrarPrecioAsync(tienda.IdTenant, id, tienda.IdListaDefault, precio);

        return id;
    }

    public async Task<int> SembrarImagenAsync(
        int idTenant,
        int idArticulo,
        byte[] contenido,
        string tipo = ValidadorDeImagen.TipoPng,
        DateTimeOffset? actualizadoEn = null,
        bool eliminada = false)
    {
        var ahora = DateTimeOffset.UtcNow;

        await using var db = fixture.CrearContextoDeAplicacion(TenantActualFijo.Plataforma);
        var imagen = new ImagenDeArticulo
        {
            IdTenant = idTenant,
            IdArticulo = idArticulo,
            Contenido = contenido,
            ContentType = tipo,
            ActualizadoEn = actualizadoEn ?? ahora,
            DeletedAt = eliminada ? ahora : null,
            CreatedAt = ahora,
            UpdatedAt = ahora
        };
        db.ImagenesArticulo.Add(imagen);
        await db.SaveChangesAsync();

        return imagen.Id;
    }

    /// <summary>Fija la lista de precios de la tienda de la vitrina de la empresa (<c>null</c> = la default), aun
    /// si la vitrina está dada de baja.</summary>
    public async Task FijarListaDeLaVitrinaAsync(int idEmpresa, int? idLista)
    {
        await using var db = fixture.CrearContextoDeAplicacion(TenantActualFijo.Plataforma);

        var vitrina = await db.Vitrinas.IgnoreQueryFilters(["BajaLogica"]).SingleAsync(v => v.IdEmpresa == idEmpresa);
        vitrina.IdListaPrecio = idLista;
        await db.SaveChangesAsync();
    }

    public async Task ModificarListaAsync(int idLista, Action<ListaPrecio> cambio)
    {
        await using var db = fixture.CrearContextoDeAplicacion(TenantActualFijo.Plataforma);

        var lista = await db.ListasPrecio.SingleAsync(l => l.Id == idLista);
        cambio(lista);
        await db.SaveChangesAsync();
    }

    public async Task ModificarCategoriaAsync(int idCategoria, Action<Categoria> cambio)
    {
        await using var db = fixture.CrearContextoDeAplicacion(TenantActualFijo.Plataforma);

        var categoria = await db.Categorias.SingleAsync(c => c.Id == idCategoria);
        cambio(categoria);
        await db.SaveChangesAsync();
    }

    public async Task<Articulo> LeerArticuloAsync(int idArticulo)
    {
        await using var db = fixture.CrearContextoDeAplicacion(TenantActualFijo.Plataforma);

        return await db.Articulos.IgnoreQueryFilters(["BajaLogica"]).AsNoTracking().SingleAsync(a => a.Id == idArticulo);
    }

    /// <summary>Todas las filas de imagen del artículo, incluidas las dadas de baja.</summary>
    public async Task<List<ImagenDeArticulo>> LeerImagenesAsync(int idArticulo)
    {
        await using var db = fixture.CrearContextoDeAplicacion(TenantActualFijo.Plataforma);

        return await db.ImagenesArticulo
            .IgnoreQueryFilters(["BajaLogica"])
            .AsNoTracking()
            .Where(i => i.IdArticulo == idArticulo)
            .OrderBy(i => i.Id)
            .ToListAsync();
    }

    public async Task MarcarArticuloComoEliminadoAsync(int idArticulo)
    {
        await using var db = fixture.CrearContextoDeAplicacion(TenantActualFijo.Plataforma);

        var articulo = await db.Articulos.SingleAsync(a => a.Id == idArticulo);
        articulo.DeletedAt = DateTimeOffset.UtcNow;
        await db.SaveChangesAsync();
    }

    // ---- HTTP -------------------------------------------------------------------------------------

    public const string RutaCatalogo = "/api/publico/catalogo";

    public static string RutaImagenPublica(int idArticulo) => $"/api/publico/articulos/{idArticulo}/imagen";

    public static async Task<JsonElement> CatalogoAsync(HttpClient cliente, string consulta = "")
    {
        var respuesta = await cliente.GetAsync(RutaCatalogo + consulta);
        Assert.Equal(HttpStatusCode.OK, respuesta.StatusCode);

        return await respuesta.Content.ReadFromJsonAsync<JsonElement>();
    }

    public static string[] Nombres(JsonElement catalogo) =>
        [.. catalogo.GetProperty("articulos").EnumerateArray().Select(a => a.GetProperty("nombre").GetString()!)];
}
