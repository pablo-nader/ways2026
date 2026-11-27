using Microsoft.EntityFrameworkCore;
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
/// Las ofertas con cantidad mínima mayor a 1 salen en <c>Promociones</c> con el precio unitario que resuelve
/// <see cref="ServicioDeOfertas"/> a esa cantidad. Sobre InMemory (la composición es read-only).
/// </summary>
public class PromocionesPorCantidadTests
{
    private const int IdTenant = 1;
    private const decimal Lista = 1000m;
    private static readonly DateTimeOffset Ahora = new(2026, 8, 4, 12, 0, 0, TimeSpan.Zero);

    private sealed class RelojFijo : IRelojDelSistema
    {
        public DateTimeOffset Ahora => PromocionesPorCantidadTests.Ahora;
    }

    private sealed class ContextoFijo : IContextoDeUsuario
    {
        public bool EstaAutenticado => true;
        public int UsuarioId => 999;
        public string NombreUsuario => "actor-de-prueba";
        public RolConocido Rol => RolConocido.Admin;
        public int? IdTenant => PromocionesPorCantidadTests.IdTenant;
    }

    private static WaysDbContext CrearContexto(string nombreDeBase, ITenantActual tenantActual) =>
        new(new DbContextOptionsBuilder<WaysDbContext>().UseInMemoryDatabase(nombreDeBase).Options, tenantActual);

    private static ServicioDeEtiquetas CrearServicio(string nombreDeBase)
    {
        var tenantActual = new TenantActualFijo(ModoDeAcceso.Tenant, IdTenant);
        var db = CrearContexto(nombreDeBase, tenantActual);
        var reloj = new RelojFijo();
        var contexto = new ContextoFijo();
        var servicioDePrecios = new ServicioDePrecios(db, reloj, contexto);
        var servicioDeOfertas = new ServicioDeOfertas(db, reloj, contexto, servicioDePrecios);
        var servicioDeArticulos = new ServicioDeArticulos(
            db, reloj, contexto, new ServicioDeLotes(db, reloj, contexto),
            new GuardaDeReferencias(db, new InspectorDeUso(db)), servicioDePrecios,
            new Ways.Application.Parametros.ServicioDeParametros(db, reloj, new ServicioDeLotes(db, reloj, contexto)));

        return new ServicioDeEtiquetas(db, reloj, servicioDeArticulos, servicioDeOfertas);
    }

    private static async Task<(int IdLista, int IdPuntoVenta)> SembrarBaseAsync(string nombreDeBase)
    {
        await using var siembra = CrearContexto(nombreDeBase, TenantActualFijo.Plataforma);

        var lista = new ListaPrecio
        {
            IdTenant = IdTenant, Nombre = "General", EsDefault = true, Modo = ModoLista.Fija,
            CreatedAt = Ahora, UpdatedAt = Ahora
        };
        var puntoVenta = new PuntoVenta
        {
            IdTenant = IdTenant, IdEmpresa = 1, Nombre = "Local 1", CreatedAt = Ahora, UpdatedAt = Ahora
        };
        siembra.ListasPrecio.Add(lista);
        siembra.PuntosVenta.Add(puntoVenta);
        await siembra.SaveChangesAsync();

        return (lista.Id, puntoVenta.Id);
    }

    private static async Task<int> SembrarArticuloAsync(string nombreDeBase, int idLista)
    {
        await using var siembra = CrearContexto(nombreDeBase, TenantActualFijo.Plataforma);

        var articulo = new Articulo
        {
            IdTenant = IdTenant, CodigoInterno = $"art-{Guid.NewGuid():N}", Nombre = "Artículo",
            IdArea = 1, IdAlicuotaIva = 1, UnidadVenta = UnidadVenta.Unidad, EsProducto = true,
            Activo = true, CreatedAt = Ahora, UpdatedAt = Ahora
        };
        siembra.Articulos.Add(articulo);
        await siembra.SaveChangesAsync();

        siembra.Precios.Add(new Precio
        {
            IdTenant = IdTenant, IdArticulo = articulo.Id, IdListaPrecio = idLista, Monto = Lista,
            VigenteDesde = Ahora.AddDays(-1), VigenteHasta = null, CreatedAt = Ahora, UpdatedAt = Ahora
        });
        await siembra.SaveChangesAsync();

        return articulo.Id;
    }

    private static async Task<int> SembrarOfertaAsync(
        string nombreDeBase, int idArticulo, decimal? cantidadMinima, decimal? precioUnitario = null,
        decimal? porcentaje = null, int? idListaDeLaOferta = null)
    {
        await using var siembra = CrearContexto(nombreDeBase, TenantActualFijo.Plataforma);

        var oferta = new Oferta
        {
            IdTenant = IdTenant, Nombre = "Oferta", IdArticulo = idArticulo, CantidadMinima = cantidadMinima,
            PrecioUnitario = precioUnitario, Porcentaje = porcentaje, Activo = true,
            CreatedAt = Ahora, UpdatedAt = Ahora
        };
        siembra.Ofertas.Add(oferta);
        await siembra.SaveChangesAsync();

        if (idListaDeLaOferta is { } idLista)
        {
            siembra.OfertasListas.Add(new OfertaLista { IdOferta = oferta.Id, IdListaPrecio = idLista, IdTenant = IdTenant });
            await siembra.SaveChangesAsync();
        }

        return oferta.Id;
    }

    private static Task<DatosDeEtiquetas> ComponerAsync(
        string nombreDeBase, int idPuntoVenta, int idLista, params int[] ids) =>
        CrearServicio(nombreDeBase).ComponerAsync(new SolicitudDeEtiquetas(idPuntoVenta, idLista, ids, null));

    [Fact]
    public async Task UnaOfertaDeTresConPrecioUnitarioSaleConCantidadTresYSuPrecio()
    {
        var bd = Guid.NewGuid().ToString();
        var (idLista, idPuntoVenta) = await SembrarBaseAsync(bd);
        var idArticulo = await SembrarArticuloAsync(bd, idLista);
        var idOferta = await SembrarOfertaAsync(bd, idArticulo, cantidadMinima: 3m, precioUnitario: 900m);

        var fila = Assert.Single((await ComponerAsync(bd, idPuntoVenta, idLista, idArticulo)).Filas);

        var promocion = Assert.Single(fila.Promociones);
        Assert.Equal(new PromocionPorCantidad(idOferta, "Oferta", 3m, false, 900m), promocion);
        Assert.Empty(fila.Ofertas);
        Assert.Equal(Lista, fila.PrecioFinal);
    }

    [Fact]
    public async Task UnaOfertaPorcentualConCantidadMinimaSaleConElPrecioConDescuento()
    {
        var bd = Guid.NewGuid().ToString();
        var (idLista, idPuntoVenta) = await SembrarBaseAsync(bd);
        var idArticulo = await SembrarArticuloAsync(bd, idLista);
        await SembrarOfertaAsync(bd, idArticulo, cantidadMinima: 2m, porcentaje: 15m);

        var fila = Assert.Single((await ComponerAsync(bd, idPuntoVenta, idLista, idArticulo)).Filas);

        var promocion = Assert.Single(fila.Promociones);
        Assert.Equal(2m, promocion.Cantidad);
        Assert.Equal(850m, promocion.PrecioUnitarioFinal);
    }

    [Fact]
    public async Task UnaOfertaDeCantidadMinimaUnoNoEsUnaPromocionPorCantidad()
    {
        var bd = Guid.NewGuid().ToString();
        var (idLista, idPuntoVenta) = await SembrarBaseAsync(bd);
        var idArticulo = await SembrarArticuloAsync(bd, idLista);
        await SembrarOfertaAsync(bd, idArticulo, cantidadMinima: 1m, porcentaje: 10m);

        var fila = Assert.Single((await ComponerAsync(bd, idPuntoVenta, idLista, idArticulo)).Filas);

        Assert.Empty(fila.Promociones);
        Assert.Single(fila.Ofertas);
    }

    [Fact]
    public async Task UnArticuloSinOfertasTieneLasPromocionesVacias()
    {
        var bd = Guid.NewGuid().ToString();
        var (idLista, idPuntoVenta) = await SembrarBaseAsync(bd);
        var idArticulo = await SembrarArticuloAsync(bd, idLista);

        var fila = Assert.Single((await ComponerAsync(bd, idPuntoVenta, idLista, idArticulo)).Filas);

        Assert.Empty(fila.Promociones);
        Assert.Empty(fila.Ofertas);
    }

    [Fact]
    public async Task UnaOfertaDeOtraListaNoSeInformaEnLaListaPedida()
    {
        var bd = Guid.NewGuid().ToString();
        var (idLista, idPuntoVenta) = await SembrarBaseAsync(bd);
        var idArticulo = await SembrarArticuloAsync(bd, idLista);
        await SembrarOfertaAsync(bd, idArticulo, cantidadMinima: 3m, precioUnitario: 900m, idListaDeLaOferta: idLista + 100);

        var fila = Assert.Single((await ComponerAsync(bd, idPuntoVenta, idLista, idArticulo)).Filas);

        Assert.Empty(fila.Promociones);
    }

    [Fact]
    public async Task UnaOfertaQueNoBajaElPrecioNoSeInforma()
    {
        var bd = Guid.NewGuid().ToString();
        var (idLista, idPuntoVenta) = await SembrarBaseAsync(bd);
        var idArticulo = await SembrarArticuloAsync(bd, idLista);
        await SembrarOfertaAsync(bd, idArticulo, cantidadMinima: 3m, precioUnitario: Lista);

        var fila = Assert.Single((await ComponerAsync(bd, idPuntoVenta, idLista, idArticulo)).Filas);

        Assert.Empty(fila.Promociones);
    }

    [Fact]
    public async Task SoloConOfertaVigenteIncluyeAlArticuloQueSoloTieneUnaOfertaPorCantidad()
    {
        var bd = Guid.NewGuid().ToString();
        var (idLista, idPuntoVenta) = await SembrarBaseAsync(bd);
        var idConPromocion = await SembrarArticuloAsync(bd, idLista);
        var idSinOferta = await SembrarArticuloAsync(bd, idLista);
        await SembrarOfertaAsync(bd, idConPromocion, cantidadMinima: 3m, precioUnitario: 900m);

        var datos = await CrearServicio(bd).ComponerAsync(new SolicitudDeEtiquetas(
            idPuntoVenta, idLista, null,
            new FiltroDeEtiquetas(null, null, null, null, SoloConOfertaVigente: true)));

        Assert.Equal([idConPromocion], datos.Filas.Select(f => f.IdArticulo).ToList());
        Assert.DoesNotContain(datos.Filas, f => f.IdArticulo == idSinOferta);
    }
}
