using Microsoft.EntityFrameworkCore;
using Ways.Application.Abstracciones;
using Ways.Application.Etiquetas;
using Ways.Application.Ofertas;
using Ways.Domain.Organizacion;
using Ways.Application.Precios;
using Ways.Domain.Articulos;
using Ways.Domain.Catalogos;
using Ways.Domain.Common;
using Ways.Domain.Ofertas;
using Ways.Domain.Precios;
using Ways.Domain.Usuarios;
using Ways.Infrastructure.Multitenancy;
using Ways.Infrastructure.Persistencia;

namespace Ways.Application.Tests.Etiquetas;

/// <summary>
/// Una etiqueta precia UNA unidad de UN artículo: el lote de etiquetas no es un documento, así que
/// una oferta de familia con <c>cantidad_minima</c> no se completa sumando artículos distintos de
/// la hoja. Sobre InMemory (la composición es read-only).
/// </summary>
public class EtiquetasPorFamiliaTests
{
    private const int IdTenant = 1;
    private const decimal Lista = 2000m;
    private const decimal PrecioConOferta = 1750m;
    private static readonly DateTimeOffset Ahora = new(2026, 8, 4, 12, 0, 0, TimeSpan.Zero);

    private sealed class RelojFijo : IRelojDelSistema
    {
        public DateTimeOffset Ahora => EtiquetasPorFamiliaTests.Ahora;
    }

    private sealed class ContextoFijo : IContextoDeUsuario
    {
        public bool EstaAutenticado => true;
        public int UsuarioId => 999;
        public string NombreUsuario => "actor-de-prueba";
        public RolConocido Rol => RolConocido.Admin;
        public int? IdTenant => EtiquetasPorFamiliaTests.IdTenant;
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

    private static async Task<int> SembrarPuntoVentaAsync(string nombreDeBase)
    {
        await using var siembra = CrearContexto(nombreDeBase, TenantActualFijo.Plataforma);

        var puntoVenta = new PuntoVenta
        {
            IdTenant = IdTenant, IdEmpresa = 1, Nombre = "Local 1", CreatedAt = Ahora, UpdatedAt = Ahora
        };
        siembra.PuntosVenta.Add(puntoVenta);
        await siembra.SaveChangesAsync();

        return puntoVenta.Id;
    }

    [Fact]
    public async Task DosArticulosDeUnaFamiliaConUnaOfertaDeMinimoDosSalenAlPrecioDeListaEnLaHoja()
    {
        var nombreDeBase = Guid.NewGuid().ToString();
        var idLista = await SembrarListaAsync(nombreDeBase);
        var idPuntoVenta = await SembrarPuntoVentaAsync(nombreDeBase);
        var a = await SembrarArticuloAsync(nombreDeBase, idLista, idFamilia: 7);
        var b = await SembrarArticuloAsync(nombreDeBase, idLista, idFamilia: 7);
        await SembrarOfertaAsync(nombreDeBase, idFamilia: 7);
        var tenantActual = new TenantActualFijo(ModoDeAcceso.Tenant, IdTenant);
        var servicio = new ServicioDeEtiquetas(
            CrearContexto(nombreDeBase, tenantActual), new RelojFijo(), null!, CrearServicio(nombreDeBase));

        var datos = await servicio.ComponerAsync(new SolicitudDeEtiquetas(idPuntoVenta, idLista, [a, b], null));

        Assert.Equal([Lista, Lista], datos.Filas.Select(f => f.PrecioFinal).ToList());
        Assert.All(datos.Filas, f => Assert.Empty(f.Ofertas));
    }
}
