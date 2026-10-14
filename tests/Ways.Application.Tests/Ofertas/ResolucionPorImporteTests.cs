using Microsoft.EntityFrameworkCore;
using Ways.Application.Abstracciones;
using Ways.Application.Ofertas;
using Ways.Application.Precios;
using Ways.Domain.Articulos;
using Ways.Domain.Catalogos;
using Ways.Domain.Common;
using Ways.Domain.Usuarios;
using Ways.Infrastructure.Multitenancy;
using Ways.Infrastructure.Persistencia;

namespace Ways.Application.Tests.Ofertas;

/// <summary>
/// El precio de una línea por importe en <see cref="ServicioDeOfertas"/>: el monto tipeado con el porcentaje de la lista del
/// cliente si es derivada, igual si es fija, y tal cual en una devolución. Sobre InMemory (la resolución es query-only).
/// </summary>
public class ResolucionPorImporteTests
{
    private const int IdTenant = 1;
    private static readonly DateTimeOffset Ahora = new(2026, 8, 4, 12, 0, 0, TimeSpan.Zero);

    private sealed class RelojFijo : IRelojDelSistema
    {
        public DateTimeOffset Ahora => ResolucionPorImporteTests.Ahora;
    }

    private sealed class ContextoFijo : IContextoDeUsuario
    {
        public bool EstaAutenticado => true;
        public int UsuarioId => 999;
        public string NombreUsuario => "actor-de-prueba";
        public RolConocido Rol => RolConocido.Admin;
        public int? IdTenant => ResolucionPorImporteTests.IdTenant;
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

    private static async Task<int> SembrarListaAsync(
        string nombreDeBase, ModoLista modo, int? idListaBase = null, decimal? porcentaje = null)
    {
        await using var siembra = CrearContexto(nombreDeBase, TenantActualFijo.Plataforma);

        var lista = new ListaPrecio
        {
            IdTenant = IdTenant, Nombre = $"Lista-{Guid.NewGuid():N}", Modo = modo, IdListaBase = idListaBase,
            Porcentaje = porcentaje, CreatedAt = Ahora, UpdatedAt = Ahora
        };
        siembra.ListasPrecio.Add(lista);
        await siembra.SaveChangesAsync();

        return lista.Id;
    }

    private static async Task<int> SembrarArticuloPorImporteAsync(string nombreDeBase)
    {
        await using var siembra = CrearContexto(nombreDeBase, TenantActualFijo.Plataforma);

        var articulo = new Articulo
        {
            IdTenant = IdTenant, CodigoInterno = $"art-{Guid.NewGuid():N}", Nombre = "Fiambrería",
            IdArea = 1, IdAlicuotaIva = 1, UnidadVenta = UnidadVenta.Importe, AcumulaEnVenta = false,
            EsProducto = false, Activo = true, CreatedAt = Ahora, UpdatedAt = Ahora
        };
        siembra.Articulos.Add(articulo);
        await siembra.SaveChangesAsync();

        return articulo.Id;
    }

    private static async Task<(decimal? Original, decimal? Final, decimal Descuento)> ResolverAsync(
        string nombreDeBase, int idArticulo, int idLista, decimal importe, bool aplicarLaLista = true)
    {
        IReadOnlyList<LineaDeResolucion> lineas = [new LineaDeResolucion(idArticulo, null, idLista, 1m, Importe: importe)];
        var servicio = CrearServicio(nombreDeBase);
        var resultado = Assert.Single(aplicarLaLista
            ? await servicio.ResolverDocumentoAsync(lineas, momento: null)
            : await servicio.ResolverDocumentoConImporteCobradoAsync(lineas, momento: null));

        return (resultado.PrecioOriginal, resultado.PrecioFinal, resultado.DescuentoUnitario);
    }

    [Theory]
    [InlineData(-10, 1500, 1350)]
    [InlineData(10, 1500, 1650)]
    [InlineData(-10, 10.05, 9.05)]
    [InlineData(-10, 0.05, 0.05)]
    public async Task UnaListaDerivadaAplicaSuPorcentajeAlImporteConDosDecimales(double porcentaje, double importe, double esperado)
    {
        var nombreDeBase = Guid.NewGuid().ToString();
        var baseFija = await SembrarListaAsync(nombreDeBase, ModoLista.Fija);
        var derivada = await SembrarListaAsync(nombreDeBase, ModoLista.Derivada, baseFija, (decimal)porcentaje);
        var articulo = await SembrarArticuloPorImporteAsync(nombreDeBase);

        var (original, final, descuento) = await ResolverAsync(nombreDeBase, articulo, derivada, (decimal)importe);

        Assert.Equal(((decimal)esperado, (decimal)esperado, 0m), (original, final, descuento));
    }

    [Fact]
    public async Task UnaListaFijaDejaElImporteComoSeTipeo()
    {
        var nombreDeBase = Guid.NewGuid().ToString();
        var fija = await SembrarListaAsync(nombreDeBase, ModoLista.Fija);
        var articulo = await SembrarArticuloPorImporteAsync(nombreDeBase);

        var (original, final, _) = await ResolverAsync(nombreDeBase, articulo, fija, 1500m);

        Assert.Equal((1500m, 1500m), (original, final));
    }

    /// <summary>Una devolución toma el importe cobrado tal cual: reaplicar el -10 % a 1350 daría 1215.</summary>
    [Fact]
    public async Task ElImporteCobradoDeUnaDevolucionNoReaplicaElPorcentaje()
    {
        var nombreDeBase = Guid.NewGuid().ToString();
        var baseFija = await SembrarListaAsync(nombreDeBase, ModoLista.Fija);
        var derivada = await SembrarListaAsync(nombreDeBase, ModoLista.Derivada, baseFija, -10m);
        var articulo = await SembrarArticuloPorImporteAsync(nombreDeBase);

        var (original, final, _) = await ResolverAsync(nombreDeBase, articulo, derivada, 1350m, aplicarLaLista: false);

        Assert.Equal((1350m, 1350m), (original, final));
    }

    /// <summary>Una derivada sin porcentaje es una fila corrupta: se rechaza con el mismo código que el resto del módulo de precios.</summary>
    [Fact]
    public async Task UnaListaDerivadaSinPorcentajeSeRechazaAunqueElArticuloNoTengaPrecio()
    {
        var nombreDeBase = Guid.NewGuid().ToString();
        var baseFija = await SembrarListaAsync(nombreDeBase, ModoLista.Fija);
        var derivada = await SembrarListaAsync(nombreDeBase, ModoLista.Derivada, baseFija, porcentaje: null);
        var articulo = await SembrarArticuloPorImporteAsync(nombreDeBase);

        var error = await Assert.ThrowsAsync<ErrorDominio>(() => ResolverAsync(nombreDeBase, articulo, derivada, 1500m));

        Assert.Equal("precio_derivado_invalido", error.Codigo);
    }

    [Fact]
    public async Task UnaListaInexistenteSeRechazaComoReferenciaInvalida()
    {
        var nombreDeBase = Guid.NewGuid().ToString();
        var articulo = await SembrarArticuloPorImporteAsync(nombreDeBase);

        var error = await Assert.ThrowsAsync<ErrorDominio>(() => ResolverAsync(nombreDeBase, articulo, idLista: 999, 1500m));

        Assert.Equal("referencia_invalida", error.Codigo);
    }
}
