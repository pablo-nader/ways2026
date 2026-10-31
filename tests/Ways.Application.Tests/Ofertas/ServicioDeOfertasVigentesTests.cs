using Microsoft.EntityFrameworkCore;
using Ways.Application.Abstracciones;
using Ways.Application.Ofertas;
using Ways.Application.Precios;
using Ways.Domain.Articulos;
using Ways.Domain.Catalogos;
using Ways.Domain.Common;
using Ways.Domain.Ofertas;
using Ways.Domain.Usuarios;
using Ways.Infrastructure.Multitenancy;
using Ways.Infrastructure.Persistencia;

namespace Ways.Application.Tests.Ofertas;

/// <summary><see cref="ServicioDeOfertas.ListarVigentesAsync"/> sobre InMemory: la validación del pedido y el matching
/// sin cantidad. El recorrido con Postgres real (RLS, hora local, listas) está en <c>OfertasVigentesTests</c>.</summary>
public class ServicioDeOfertasVigentesTests
{
    private static readonly DateTimeOffset Ahora = new(2026, 8, 4, 12, 0, 0, TimeSpan.Zero);

    private sealed class RelojFijo(DateTimeOffset ahora) : IRelojDelSistema
    {
        public DateTimeOffset Ahora { get; } = ahora;
    }

    private sealed class ContextoFijo(int? idTenant) : IContextoDeUsuario
    {
        public bool EstaAutenticado => true;
        public int UsuarioId => 999;
        public string NombreUsuario => "actor-de-prueba";
        public RolConocido Rol => RolConocido.Vendedor;
        public int? IdTenant { get; } = idTenant;
    }

    private static WaysDbContext CrearContexto(string nombreDeBase, ITenantActual tenantActual) =>
        new(new DbContextOptionsBuilder<WaysDbContext>().UseInMemoryDatabase(nombreDeBase).Options, tenantActual);

    private static ServicioDeOfertas CrearServicio(string nombreDeBase) =>
        new(
            CrearContexto(nombreDeBase, new TenantActualFijo(ModoDeAcceso.Tenant, 1)),
            new RelojFijo(Ahora),
            new ContextoFijo(1),
            new ServicioDePrecios(
                CrearContexto(nombreDeBase, new TenantActualFijo(ModoDeAcceso.Tenant, 1)),
                new RelojFijo(Ahora),
                new ContextoFijo(1)));

    private static async Task<int> SembrarArticuloAsync(string nombreDeBase, int? idFamilia = null)
    {
        await using var db = CrearContexto(nombreDeBase, TenantActualFijo.Plataforma);
        var articulo = new Articulo
        {
            IdTenant = 1, CodigoInterno = "cod", Nombre = "articulo", IdArea = 1, IdAlicuotaIva = 1,
            IdFamilia = idFamilia, UnidadVenta = UnidadVenta.Unidad, EsProducto = true, CreatedAt = Ahora, UpdatedAt = Ahora
        };
        db.Articulos.Add(articulo);
        await db.SaveChangesAsync();
        return articulo.Id;
    }

    private static async Task<int> SembrarOfertaAsync(string nombreDeBase, Oferta oferta)
    {
        await using var db = CrearContexto(nombreDeBase, TenantActualFijo.Plataforma);
        oferta.IdTenant = 1;
        oferta.CreatedAt = Ahora;
        oferta.UpdatedAt = Ahora;
        db.Ofertas.Add(oferta);
        await db.SaveChangesAsync();
        return oferta.Id;
    }

    [Theory]
    [InlineData(null, 1, "ids_articulos_requeridos")]
    [InlineData(new int[0], 1, "ids_articulos_requeridos")]
    [InlineData(new[] { 1, 1 }, 1, "ids_articulos_invalidos")]
    [InlineData(new[] { 1, -2 }, 1, "ids_articulos_invalidos")]
    [InlineData(new[] { 1 }, 0, "empresa_requerida")]
    public async Task UnPedidoInvalidoSeRechazaConSuCodigo(int[]? ids, int idEmpresa, string codigo)
    {
        var servicio = CrearServicio(Guid.NewGuid().ToString());

        var error = await Assert.ThrowsAsync<ErrorDominio>(
            () => servicio.ListarVigentesAsync(new SolicitudDeVigentes(ids, idEmpresa)));

        Assert.Equal(codigo, error.Codigo);
        Assert.Equal(400, error.EstadoHttp);
    }

    [Fact]
    public async Task UnPedidoNuloSeRechazaComoRequerido()
    {
        var servicio = CrearServicio(Guid.NewGuid().ToString());

        var error = await Assert.ThrowsAsync<ErrorDominio>(() => servicio.ListarVigentesAsync(null));

        Assert.Equal("ids_articulos_requeridos", error.Codigo);
    }

    [Fact]
    public async Task ElTopeEsDe200ArticulosInclusive()
    {
        var servicio = CrearServicio(Guid.NewGuid().ToString());

        var error = await Assert.ThrowsAsync<ErrorDominio>(
            () => servicio.ListarVigentesAsync(new SolicitudDeVigentes([.. Enumerable.Range(1, 201)], 1)));
        Assert.Equal("ids_articulos_excedidos", error.Codigo);

        Assert.Empty(await servicio.ListarVigentesAsync(new SolicitudDeVigentes([.. Enumerable.Range(1, 200)], 1)));
    }

    [Fact]
    public async Task LaOfertaPorCantidadSaleConSuCondicionYLaDeFamiliaSoloSiElArticuloLaTiene()
    {
        var nombreDeBase = Guid.NewGuid().ToString();
        var conFamilia = await SembrarArticuloAsync(nombreDeBase, idFamilia: 7);
        var sinFamilia = await SembrarArticuloAsync(nombreDeBase);
        var porCantidad = await SembrarOfertaAsync(nombreDeBase, new Oferta
        {
            Nombre = "llevando 3", IdArticulo = conFamilia, Porcentaje = 10m, CantidadMinima = 3m, SoloMultiplos = true
        });
        var deFamilia = await SembrarOfertaAsync(nombreDeBase, new Oferta
        {
            Nombre = "familia", IdFamilia = 7, ImporteFijo = 50m, CantidadMinima = 6m, Prioridad = 5
        });

        var resultado = await CrearServicio(nombreDeBase)
            .ListarVigentesAsync(new SolicitudDeVigentes([conFamilia, sinFamilia], 1, Momento: Ahora));

        var item = Assert.Single(resultado);
        Assert.Equal(conFamilia, item.IdArticulo);
        Assert.Equal(new[] { deFamilia, porCantidad }, item.Ofertas.Select(o => o.IdOferta));
        Assert.Equal(("Familia", 6m, 50m), (item.Ofertas[0].Alcance, item.Ofertas[0].CantidadMinima, item.Ofertas[0].ImporteFijo));
        Assert.Equal(("Articulo", 3m, true), (item.Ofertas[1].Alcance, item.Ofertas[1].CantidadMinima, item.Ofertas[1].SoloMultiplos));
    }
}
