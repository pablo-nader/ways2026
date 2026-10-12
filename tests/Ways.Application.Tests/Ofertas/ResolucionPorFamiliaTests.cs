using Microsoft.EntityFrameworkCore;
using Ways.Application.Abstracciones;
using Ways.Application.Ofertas;
using Ways.Application.Precios;
using Ways.Domain.Articulos;
using Ways.Domain.Catalogos;
using Ways.Domain.Common;
using Ways.Domain.Ofertas;
using Ways.Domain.Precios;
using Ways.Domain.Usuarios;
using Ways.Infrastructure.Multitenancy;
using Ways.Infrastructure.Persistencia;

namespace Ways.Application.Tests.Ofertas;

/// <summary>
/// La <c>cantidad_minima</c> de una oferta de familia se compara contra la suma de cantidades de
/// TODAS las líneas de esa familia en el lote de <see cref="ServicioDeOfertas.ResolverDocumentoAsync"/>, no
/// contra la cantidad de una sola línea. Sobre InMemory, como <see cref="ResolucionConEscalonesTests"/>
/// (la resolución es query-only).
/// </summary>
public class ResolucionPorFamiliaTests
{
    private const int IdTenant = 1;
    private const decimal Lista = 2000m;
    private const decimal PrecioConOferta = 1750m;
    private static readonly DateTimeOffset Ahora = new(2026, 8, 4, 12, 0, 0, TimeSpan.Zero);

    private sealed class RelojFijo : IRelojDelSistema
    {
        public DateTimeOffset Ahora => ResolucionPorFamiliaTests.Ahora;
    }

    private sealed class ContextoFijo : IContextoDeUsuario
    {
        public bool EstaAutenticado => true;
        public int UsuarioId => 999;
        public string NombreUsuario => "actor-de-prueba";
        public RolConocido Rol => RolConocido.Admin;
        public int? IdTenant => ResolucionPorFamiliaTests.IdTenant;
    }

    private static WaysDbContext CrearContexto(string nombreDeBase, ITenantActual tenantActual) =>
        new(new DbContextOptionsBuilder<WaysDbContext>().UseInMemoryDatabase(nombreDeBase).Options, tenantActual);

    private static ServicioDeOfertas CrearServicio(string nombreDeBase)
    {
        var tenantActual = new TenantActualFijo(ModoDeAcceso.Tenant, IdTenant);

        return new ServicioDeOfertas(
            CrearContexto(nombreDeBase, tenantActual),
            new RelojFijo(),
            new ContextoFijo(),
            new ServicioDePrecios(CrearContexto(nombreDeBase, tenantActual), new RelojFijo(), new ContextoFijo()));
    }

    private static async Task<int> SembrarListaAsync(string nombreDeBase)
    {
        await using var siembra = CrearContexto(nombreDeBase, TenantActualFijo.Plataforma);

        var lista = new ListaPrecio
        {
            IdTenant = IdTenant, Nombre = "General", EsDefault = true, Modo = ModoLista.Fija,
            CreatedAt = Ahora, UpdatedAt = Ahora
        };
        siembra.ListasPrecio.Add(lista);
        await siembra.SaveChangesAsync();

        return lista.Id;
    }

    private static async Task<int> SembrarArticuloAsync(string nombreDeBase, int idLista, int? idFamilia)
    {
        await using var siembra = CrearContexto(nombreDeBase, TenantActualFijo.Plataforma);

        var articulo = new Articulo
        {
            IdTenant = IdTenant, CodigoInterno = $"art-{Guid.NewGuid():N}", Nombre = "Artículo",
            IdArea = 1, IdAlicuotaIva = 1, UnidadVenta = UnidadVenta.Unidad, EsProducto = true,
            Activo = true, IdFamilia = idFamilia, CreatedAt = Ahora, UpdatedAt = Ahora
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

    private static async Task SembrarOfertaAsync(
        string nombreDeBase, int? idFamilia = null, int? idArticulo = null, decimal cantidadMinima = 2m)
    {
        await using var siembra = CrearContexto(nombreDeBase, TenantActualFijo.Plataforma);

        siembra.Ofertas.Add(new Oferta
        {
            IdTenant = IdTenant,
            Nombre = "2 x 3500",
            IdFamilia = idFamilia,
            IdArticulo = idArticulo,
            PrecioUnitario = PrecioConOferta,
            CantidadMinima = cantidadMinima,
            Activo = true,
            CreatedAt = Ahora,
            UpdatedAt = Ahora
        });
        await siembra.SaveChangesAsync();
    }

    private static async Task<IReadOnlyList<decimal?>> PreciosFinalesAsync(
        string nombreDeBase, int idLista, params (int IdArticulo, decimal Cantidad)[] lineas)
    {
        var resultado = await CrearServicio(nombreDeBase).ResolverDocumentoAsync(
            lineas.Select(l => new LineaDeResolucion(l.IdArticulo, null, idLista, l.Cantidad)).ToList(),
            momento: null);

        return resultado.Select(r => r.PrecioFinal).ToList();
    }

    [Fact]
    public async Task DosArticulosDeLaMismaFamiliaCompletanEntreSiLaCantidadMinima()
    {
        var nombreDeBase = Guid.NewGuid().ToString();
        var idLista = await SembrarListaAsync(nombreDeBase);
        var a = await SembrarArticuloAsync(nombreDeBase, idLista, idFamilia: 7);
        var b = await SembrarArticuloAsync(nombreDeBase, idLista, idFamilia: 7);
        await SembrarOfertaAsync(nombreDeBase, idFamilia: 7);

        var precios = await PreciosFinalesAsync(nombreDeBase, idLista, (a, 1m), (b, 1m));

        Assert.Equal([PrecioConOferta, PrecioConOferta], precios);
    }

    [Fact]
    public async Task LaCurvaTraeLaGrillaDeFamiliaDeUnaOfertaConCantidadMinima()
    {
        var nombreDeBase = Guid.NewGuid().ToString();
        var idLista = await SembrarListaAsync(nombreDeBase);
        var a = await SembrarArticuloAsync(nombreDeBase, idLista, idFamilia: 7);
        var sinFamilia = await SembrarArticuloAsync(nombreDeBase, idLista, idFamilia: null);
        await SembrarOfertaAsync(nombreDeBase, idFamilia: 7);

        var resolucion = await CrearServicio(nombreDeBase).ResolverConEscalonesAsync(
            [new LineaDeResolucion(a, null, idLista, 1m), new LineaDeResolucion(sinFamilia, null, idLista, 1m)],
            momento: null);

        Assert.Equal(Lista, resolucion[0].Resultado.PrecioFinal);
        Assert.Equal(
            [(2m, 1m, PrecioConOferta)],
            resolucion[0].EscalonesDeFamilia!.Select(e => (e.CantidadDeFamiliaDesde, e.CantidadDesde, e.PrecioFinal)));
        Assert.Null(resolucion[1].EscalonesDeFamilia);
    }

    [Fact]
    public async Task UnSoloArticuloDeLaFamiliaConCantidadUnoNoCompletaElMinimo()
    {
        var nombreDeBase = Guid.NewGuid().ToString();
        var idLista = await SembrarListaAsync(nombreDeBase);
        var a = await SembrarArticuloAsync(nombreDeBase, idLista, idFamilia: 7);
        await SembrarOfertaAsync(nombreDeBase, idFamilia: 7);

        var precios = await PreciosFinalesAsync(nombreDeBase, idLista, (a, 1m));

        Assert.Equal([Lista], precios);
    }

    [Fact]
    public async Task UnArticuloDeOtraFamiliaOSinFamiliaNoSumaALaFamiliaDeLaOferta()
    {
        var nombreDeBase = Guid.NewGuid().ToString();
        var idLista = await SembrarListaAsync(nombreDeBase);
        var a = await SembrarArticuloAsync(nombreDeBase, idLista, idFamilia: 7);
        var deOtraFamilia = await SembrarArticuloAsync(nombreDeBase, idLista, idFamilia: 8);
        var sinFamilia = await SembrarArticuloAsync(nombreDeBase, idLista, idFamilia: null);
        await SembrarOfertaAsync(nombreDeBase, idFamilia: 7);

        var precios = await PreciosFinalesAsync(nombreDeBase, idLista, (a, 1m), (deOtraFamilia, 1m), (sinFamilia, 1m));

        Assert.Equal([Lista, Lista, Lista], precios);
    }

    [Fact]
    public async Task DosLineasDelMismoArticuloNoCompletanUnaOfertaDeArticulo()
    {
        var nombreDeBase = Guid.NewGuid().ToString();
        var idLista = await SembrarListaAsync(nombreDeBase);
        var a = await SembrarArticuloAsync(nombreDeBase, idLista, idFamilia: 7);
        await SembrarOfertaAsync(nombreDeBase, idArticulo: a);

        var precios = await PreciosFinalesAsync(nombreDeBase, idLista, (a, 1m), (a, 1m));

        Assert.Equal([Lista, Lista], precios);
    }

    [Fact]
    public async Task LaCantidadDeLaFamiliaSumaTambienLasCantidadesMayoresAUno()
    {
        var nombreDeBase = Guid.NewGuid().ToString();
        var idLista = await SembrarListaAsync(nombreDeBase);
        var a = await SembrarArticuloAsync(nombreDeBase, idLista, idFamilia: 7);
        var b = await SembrarArticuloAsync(nombreDeBase, idLista, idFamilia: 7);
        await SembrarOfertaAsync(nombreDeBase, idFamilia: 7, cantidadMinima: 3m);

        var precios = await PreciosFinalesAsync(nombreDeBase, idLista, (a, 2m), (b, 1m));

        Assert.Equal([PrecioConOferta, PrecioConOferta], precios);
    }

    /// <summary>Una línea por importe no suma a la cantidad de su familia: la oferta de familia de mínimo 2 no se completa
    /// con un artículo común y otro por importe de la misma familia (un cambio de unidad de un solo artículo la deja
    /// ahí).</summary>
    [Fact]
    public async Task UnaLineaPorImporteNoSumaALaCantidadDeLaFamilia()
    {
        var nombreDeBase = Guid.NewGuid().ToString();
        var idLista = await SembrarListaAsync(nombreDeBase);
        var comun = await SembrarArticuloAsync(nombreDeBase, idLista, idFamilia: 7);
        var porImporte = await SembrarArticuloAsync(nombreDeBase, idLista, idFamilia: 7);
        await SembrarOfertaAsync(nombreDeBase, idFamilia: 7);

        await using (var edicion = CrearContexto(nombreDeBase, TenantActualFijo.Plataforma))
        {
            var articulo = await edicion.Articulos.SingleAsync(a => a.Id == porImporte);
            articulo.UnidadVenta = UnidadVenta.Importe;
            articulo.AcumulaEnVenta = false;
            await edicion.SaveChangesAsync();
        }

        var resultado = await CrearServicio(nombreDeBase).ResolverDocumentoAsync(
            [
                new LineaDeResolucion(comun, null, idLista, 1m),
                new LineaDeResolucion(porImporte, null, idLista, 1m, Importe: 150m)
            ],
            momento: null);

        Assert.Equal([Lista, 150m], resultado.Select(r => r.PrecioFinal));
    }
}
