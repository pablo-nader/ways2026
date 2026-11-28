using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Ways.Application.Abstracciones;
using Ways.Application.Articulos;
using Ways.Application.Bajas;
using Ways.Application.Etiquetas;
using Ways.Application.Ofertas;
using Ways.Application.Organizacion;
using Ways.Application.Precios;
using Ways.Application.Stock;
using Ways.Domain.Articulos;
using Ways.Domain.Catalogos;
using Ways.Domain.Common;
using Ways.Domain.Ofertas;
using Ways.Domain.Organizacion;
using Ways.Domain.Precios;
using Ways.Domain.Usuarios;
using Ways.Infrastructure.Multitenancy;
using Ways.Infrastructure.Persistencia;

namespace Ways.Application.Tests.Etiquetas;

/// <summary>
/// El filtro <c>conOfertaVigente</c> de la grilla de artículos usa la misma definición de oferta vigente que la hoja de
/// etiquetas (<see cref="ServicioDeEtiquetas.IdsConOfertaVigenteAsync"/>): oferta aplicada a una unidad o promoción por
/// cantidad que baja el precio. Sobre InMemory; la traducción a SQL del resto de la grilla la cubren los tests de integración.
/// </summary>
public class GrillaConOfertaVigenteTests
{
    private const int IdTenant = 1;
    private static readonly DateTimeOffset Ahora = new(2026, 8, 4, 12, 0, 0, TimeSpan.Zero);

    private sealed class RelojFijo : IRelojDelSistema
    {
        public DateTimeOffset Ahora => GrillaConOfertaVigenteTests.Ahora;
    }

    private sealed class ContextoFijo : IContextoDeUsuario
    {
        public bool EstaAutenticado => true;
        public int UsuarioId => 999;
        public string NombreUsuario => "actor-de-prueba";
        public RolConocido Rol => RolConocido.Admin;
        public int? IdTenant => GrillaConOfertaVigenteTests.IdTenant;
    }

    private static WaysDbContext CrearContexto(string nombreDeBase, ITenantActual tenantActual) =>
        new(new DbContextOptionsBuilder<WaysDbContext>().UseInMemoryDatabase(nombreDeBase).Options, tenantActual);

    private static (ServicioDeGrillaDeArticulos Grilla, ServicioDeEtiquetas Etiquetas) CrearServicios(
        string nombreDeBase, OpcionesDeGrillaDeArticulos? opciones = null)
    {
        var db = CrearContexto(nombreDeBase, new TenantActualFijo(ModoDeAcceso.Tenant, IdTenant));
        var reloj = new RelojFijo();
        var contexto = new ContextoFijo();
        var servicioDePrecios = new ServicioDePrecios(db, reloj, contexto);
        var servicioDeOfertas = new ServicioDeOfertas(db, reloj, contexto, servicioDePrecios);
        var servicioDeArticulos = new ServicioDeArticulos(
            db, reloj, contexto, new ServicioDeLotes(db, reloj, contexto),
            new GuardaDeReferencias(db, new InspectorDeUso(db)), servicioDePrecios,
            new Ways.Application.Parametros.ServicioDeParametros(db, reloj, new ServicioDeLotes(db, reloj, contexto)));
        var etiquetas = new ServicioDeEtiquetas(db, reloj, servicioDeArticulos, servicioDeOfertas);
        var grilla = new ServicioDeGrillaDeArticulos(
            db, reloj, servicioDePrecios, contexto, Options.Create(opciones ?? new OpcionesDeGrillaDeArticulos()), etiquetas);

        return (grilla, etiquetas);
    }

    private sealed record Base(int IdLista, int IdPuntoVenta);

    private static async Task<Base> SembrarBaseAsync(string nombreDeBase)
    {
        await using var siembra = CrearContexto(nombreDeBase, TenantActualFijo.Plataforma);
        var lista = new ListaPrecio
        {
            IdTenant = IdTenant, Nombre = "General", EsDefault = true, Modo = ModoLista.Fija, CreatedAt = Ahora, UpdatedAt = Ahora
        };
        var puntoVenta = new PuntoVenta { IdTenant = IdTenant, IdEmpresa = 1, Nombre = "Local 1", CreatedAt = Ahora, UpdatedAt = Ahora };
        siembra.ListasPrecio.Add(lista);
        siembra.PuntosVenta.Add(puntoVenta);
        await siembra.SaveChangesAsync();
        return new Base(lista.Id, puntoVenta.Id);
    }

    private static async Task<int> SembrarArticuloAsync(
        string nombreDeBase, int idLista, string nombre, bool conPrecio = true, decimal? cantidadMinima = null, decimal? porcentaje = null)
    {
        await using var siembra = CrearContexto(nombreDeBase, TenantActualFijo.Plataforma);
        var articulo = new Articulo
        {
            IdTenant = IdTenant, CodigoInterno = $"art-{Guid.NewGuid():N}", Nombre = nombre, IdArea = 1, IdAlicuotaIva = 1,
            UnidadVenta = UnidadVenta.Unidad, EsProducto = true, Activo = true, CreatedAt = Ahora, UpdatedAt = Ahora
        };
        siembra.Articulos.Add(articulo);
        await siembra.SaveChangesAsync();

        if (conPrecio)
        {
            siembra.Precios.Add(new Precio
            {
                IdTenant = IdTenant, IdArticulo = articulo.Id, IdListaPrecio = idLista, Monto = 1000m,
                VigenteDesde = Ahora.AddDays(-1), CreatedAt = Ahora, UpdatedAt = Ahora
            });
        }

        if (porcentaje is not null)
        {
            siembra.Ofertas.Add(new Oferta
            {
                IdTenant = IdTenant, Nombre = "Oferta", IdArticulo = articulo.Id, CantidadMinima = cantidadMinima,
                Porcentaje = porcentaje, Activo = true, CreatedAt = Ahora, UpdatedAt = Ahora
            });
        }

        await siembra.SaveChangesAsync();
        return articulo.Id;
    }

    [Fact]
    public async Task ElHelperDevuelveLosConOfertaPorUnidadYLosConPromocionPorCantidad()
    {
        var bd = Guid.NewGuid().ToString();
        var b = await SembrarBaseAsync(bd);
        var sinOferta = await SembrarArticuloAsync(bd, b.IdLista, "Sin oferta");
        var porUnidad = await SembrarArticuloAsync(bd, b.IdLista, "Por unidad", porcentaje: 10m);
        var porCantidad = await SembrarArticuloAsync(bd, b.IdLista, "Por cantidad", cantidadMinima: 3m, porcentaje: 10m);
        var sinPrecio = await SembrarArticuloAsync(bd, b.IdLista, "Sin precio", conPrecio: false, porcentaje: 10m);

        var (_, etiquetas) = CrearServicios(bd);
        var ids = await etiquetas.IdsConOfertaVigenteAsync([sinOferta, porUnidad, porCantidad, sinPrecio], b.IdPuntoVenta, b.IdLista);

        Assert.Equal([porUnidad, porCantidad], ids.Order());
    }

    [Fact]
    public async Task ElHelperCoincideConLoQueComponeLaHojaDeEtiquetas()
    {
        var bd = Guid.NewGuid().ToString();
        var b = await SembrarBaseAsync(bd);
        var ids = new List<int>
        {
            await SembrarArticuloAsync(bd, b.IdLista, "A"),
            await SembrarArticuloAsync(bd, b.IdLista, "B", porcentaje: 10m),
            await SembrarArticuloAsync(bd, b.IdLista, "C", cantidadMinima: 2m, porcentaje: 20m),
        };

        var (_, etiquetas) = CrearServicios(bd);
        var hoja = await etiquetas.ComponerAsync(new SolicitudDeEtiquetas(b.IdPuntoVenta, b.IdLista, ids, null));
        var segunLaHoja = hoja.Filas.Where(ServicioDeEtiquetas.TieneOfertaVigente).Select(f => f.IdArticulo).Order();

        Assert.Equal(segunLaHoja, (await etiquetas.IdsConOfertaVigenteAsync(ids, b.IdPuntoVenta, b.IdLista)).Order());
    }

    [Fact]
    public async Task ElHelperRechazaUnPuntoDeVentaOUnaListaInexistentes()
    {
        var bd = Guid.NewGuid().ToString();
        var b = await SembrarBaseAsync(bd);
        var (_, etiquetas) = CrearServicios(bd);

        var sinPuntoDeVenta = await Assert.ThrowsAsync<ErrorDominio>(() => etiquetas.IdsConOfertaVigenteAsync([1], 999, b.IdLista));
        var sinLista = await Assert.ThrowsAsync<ErrorDominio>(() => etiquetas.IdsConOfertaVigenteAsync([1], b.IdPuntoVenta, 999));

        Assert.Equal(404, sinPuntoDeVenta.EstadoHttp);
        Assert.Equal(404, sinLista.EstadoHttp);
    }

    [Fact]
    public async Task LaGrillaPaginaYCuentaSoloLosArticulosConOferta()
    {
        var bd = Guid.NewGuid().ToString();
        var b = await SembrarBaseAsync(bd);
        var conOferta = new List<int>();
        for (var i = 1; i <= 5; i++)
        {
            await SembrarArticuloAsync(bd, b.IdLista, $"Sin {i}");
            conOferta.Add(await SembrarArticuloAsync(bd, b.IdLista, $"Con {i}", porcentaje: 10m));
        }

        var (grilla, _) = CrearServicios(bd);
        var primera = await grilla.ListarAsync(pagina: 1, tamanio: 2, conOfertaVigente: true, idPuntoVenta: b.IdPuntoVenta, idListaPrecio: b.IdLista);
        var tercera = await grilla.ListarAsync(pagina: 3, tamanio: 2, conOfertaVigente: true, idPuntoVenta: b.IdPuntoVenta, idListaPrecio: b.IdLista);

        Assert.Equal(5, primera.Total);
        Assert.Equal(["Con 1", "Con 2"], primera.Items.Select(i => i.Nombre));
        Assert.Equal(["Con 5"], tercera.Items.Select(i => i.Nombre));
        Assert.All(primera.Items.Concat(tercera.Items), i => Assert.Contains(i.Id, conOferta));
    }

    [Fact]
    public async Task LaGrillaCombinaElFiltroDeOfertaConLosDemasFiltros()
    {
        var bd = Guid.NewGuid().ToString();
        var b = await SembrarBaseAsync(bd);
        await SembrarArticuloAsync(bd, b.IdLista, "Con oferta", porcentaje: 10m);
        await SembrarArticuloAsync(bd, b.IdLista, "Otro con oferta", porcentaje: 10m);
        await SembrarArticuloAsync(bd, b.IdLista, "Sin oferta");

        var (grilla, _) = CrearServicios(bd);
        var dentro = await grilla.ListarAsync(precioDesde: 900m, precioHasta: 1100m, conOfertaVigente: true, idPuntoVenta: b.IdPuntoVenta, idListaPrecio: b.IdLista);
        var fuera = await grilla.ListarAsync(precioDesde: 2000m, conOfertaVigente: true, idPuntoVenta: b.IdPuntoVenta, idListaPrecio: b.IdLista);

        Assert.Equal(["Con oferta", "Otro con oferta"], dentro.Items.Select(i => i.Nombre));
        Assert.Equal(0, fuera.Total);
    }

    [Fact]
    public async Task SinElFiltroLaGrillaNoToleraElContextoDeOfertaYConElFiltroLoExige()
    {
        var bd = Guid.NewGuid().ToString();
        var b = await SembrarBaseAsync(bd);
        var (grilla, _) = CrearServicios(bd);

        var sinContexto = await Assert.ThrowsAsync<ErrorDominio>(() => grilla.ListarAsync(conOfertaVigente: true, idPuntoVenta: b.IdPuntoVenta));
        var contextoSobrante = await Assert.ThrowsAsync<ErrorDominio>(() => grilla.ListarAsync(idListaPrecio: b.IdLista));

        Assert.Equal("filtro_oferta_sin_contexto", sinContexto.Codigo);
        Assert.Equal("contexto_de_oferta_sin_filtro", contextoSobrante.Codigo);
    }

    [Fact]
    public async Task LaGrillaRechazaElFiltroDeOfertaSiLosCandidatosExcedenElTope()
    {
        var bd = Guid.NewGuid().ToString();
        var b = await SembrarBaseAsync(bd);
        for (var i = 1; i <= 3; i++)
        {
            await SembrarArticuloAsync(bd, b.IdLista, $"Art {i}", porcentaje: 10m);
        }

        var (grilla, _) = CrearServicios(bd, new OpcionesDeGrillaDeArticulos { TopeDeCandidatosPorFiltroDeOferta = 2 });
        var error = await Assert.ThrowsAsync<ErrorDominio>(
            () => grilla.ListarAsync(conOfertaVigente: true, idPuntoVenta: b.IdPuntoVenta, idListaPrecio: b.IdLista));

        Assert.Equal("filtro_oferta_excede_tope", error.Codigo);
    }
}
