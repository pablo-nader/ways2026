using Microsoft.EntityFrameworkCore;
using Ways.Application.Abstracciones;
using Ways.Application.Precios;
using Ways.Domain.Articulos;
using Ways.Domain.Catalogos;
using Ways.Domain.Common;
using Ways.Domain.Precios;
using Ways.Domain.Usuarios;
using Ways.Infrastructure.Multitenancy;
using Ways.Infrastructure.Persistencia;

namespace Ways.Application.Tests.Precios;

/// <summary>
/// judgment-day ronda 1 (item 4, "Derived resolution hardening") —
/// <see cref="ServicioDePrecios.PrecioVigenteAsync"/> resolviendo una lista <c>derivada</c>
/// contra el proveedor InMemory: ni <see cref="ServicioDePrecios.ResolverPrecioAsync"/> (privado,
/// se llega vía <c>PrecioVigenteAsync</c>) ni <c>PrecioVigenteAsync</c> abren transacción, así
/// que InMemory cubre el camino completo (mismo criterio que
/// <c>ServicioDeArticulosTests.ActualizarAsync</c>).
/// </summary>
public class ServicioDePreciosResolucionTests
{
    private static readonly DateTimeOffset Ahora = new(2026, 8, 2, 12, 0, 0, TimeSpan.Zero);
    private const int IdTenant = 1;

    private sealed class RelojFijo(DateTimeOffset ahora) : IRelojDelSistema
    {
        public DateTimeOffset Ahora { get; } = ahora;
    }

    private sealed class ContextoFijo(int? idTenant) : IContextoDeUsuario
    {
        public bool EstaAutenticado => true;
        public int UsuarioId => 999;
        public string NombreUsuario => "actor-de-prueba";
        public RolConocido Rol => RolConocido.Admin;
        public int? IdTenant { get; } = idTenant;
    }

    private static WaysDbContext CrearContexto(string nombreDeBase, ITenantActual tenantActual) =>
        new(new DbContextOptionsBuilder<WaysDbContext>().UseInMemoryDatabase(nombreDeBase).Options, tenantActual);

    private static ServicioDePrecios CrearServicio(string nombreDeBase) =>
        new(
            CrearContexto(nombreDeBase, new TenantActualFijo(ModoDeAcceso.Tenant, IdTenant)),
            new RelojFijo(Ahora),
            new ContextoFijo(IdTenant));

    private static async Task<int> SembrarArticuloAsync(string nombreDeBase)
    {
        await using var siembra = CrearContexto(nombreDeBase, TenantActualFijo.Plataforma);

        var articulo = new Articulo
        {
            IdTenant = IdTenant,
            CodigoInterno = "1",
            Nombre = "Artículo de prueba",
            IdArea = 1,
            IdAlicuotaIva = 1,
            UnidadVenta = UnidadVenta.Unidad,
            EsProducto = true,
            CreatedAt = Ahora,
            UpdatedAt = Ahora
        };
        siembra.Articulos.Add(articulo);
        await siembra.SaveChangesAsync();
        return articulo.Id;
    }

    private static async Task<int> SembrarListaFijaConPrecioAsync(string nombreDeBase, int idArticulo, decimal precio)
    {
        await using var siembra = CrearContexto(nombreDeBase, TenantActualFijo.Plataforma);

        var lista = new ListaPrecio
        {
            IdTenant = IdTenant, Nombre = "General", EsDefault = true, Modo = ModoLista.Fija,
            CreatedAt = Ahora, UpdatedAt = Ahora
        };
        siembra.ListasPrecio.Add(lista);
        await siembra.SaveChangesAsync();

        siembra.Precios.Add(new Precio
        {
            IdTenant = IdTenant,
            IdArticulo = idArticulo,
            IdListaPrecio = lista.Id,
            Monto = precio,
            VigenteDesde = Ahora.AddDays(-1),
            VigenteHasta = null,
            CreatedAt = Ahora,
            UpdatedAt = Ahora
        });
        await siembra.SaveChangesAsync();

        return lista.Id;
    }

    /// <summary>Invariante violado: una lista <c>derivada</c> sin <c>Porcentaje</c> configurado
    /// (solo alcanzable escribiendo directo por fuera de <c>ServicioDeListasPrecio</c>, que
    /// llega en la Slice 4) — antes reventaba con un <see cref="NullReferenceException"/> vía
    /// <c>Porcentaje!.Value</c>; ahora da el mismo error de dominio limpio que un precio derivado
    /// negativo.</summary>
    [Fact]
    public async Task UnaListaDerivadaSinPorcentajeConfiguradoDaUnErrorDeDominioLimpio()
    {
        var nombreDeBase = nameof(UnaListaDerivadaSinPorcentajeConfiguradoDaUnErrorDeDominioLimpio);
        var idArticulo = await SembrarArticuloAsync(nombreDeBase);
        var idListaBase = await SembrarListaFijaConPrecioAsync(nombreDeBase, idArticulo, 100m);

        int idListaDerivada;
        await using (var siembra = CrearContexto(nombreDeBase, TenantActualFijo.Plataforma))
        {
            var derivada = new ListaPrecio
            {
                IdTenant = IdTenant, Nombre = "Derivada sin porcentaje", EsDefault = false,
                Modo = ModoLista.Derivada, IdListaBase = idListaBase, Porcentaje = null,
                CreatedAt = Ahora, UpdatedAt = Ahora
            };
            siembra.ListasPrecio.Add(derivada);
            await siembra.SaveChangesAsync();
            idListaDerivada = derivada.Id;
        }

        var servicio = CrearServicio(nombreDeBase);

        var error = await Assert.ThrowsAsync<ErrorDominio>(() =>
            servicio.PrecioVigenteAsync(idArticulo, idListaDerivada, fecha: null));

        Assert.Equal("precio_derivado_invalido", error.Codigo);
    }

    /// <summary>Un porcentaje menor a -100% sobre un precio base existente da un precio derivado
    /// negativo — <see cref="ResolvedorDePrecios.ResolverPrecioDerivado"/> lo rechaza y el error
    /// de dominio se propaga sin traducir a través de <c>PrecioVigenteAsync</c>.</summary>
    [Fact]
    public async Task UnPorcentajeMenorAMenos100PropagaElErrorDeDominioDelPrecioDerivado()
    {
        var nombreDeBase = nameof(UnPorcentajeMenorAMenos100PropagaElErrorDeDominioDelPrecioDerivado);
        var idArticulo = await SembrarArticuloAsync(nombreDeBase);
        var idListaBase = await SembrarListaFijaConPrecioAsync(nombreDeBase, idArticulo, 100m);

        int idListaDerivada;
        await using (var siembra = CrearContexto(nombreDeBase, TenantActualFijo.Plataforma))
        {
            var derivada = new ListaPrecio
            {
                IdTenant = IdTenant, Nombre = "Derivada con descuento imposible", EsDefault = false,
                Modo = ModoLista.Derivada, IdListaBase = idListaBase, Porcentaje = -150m,
                CreatedAt = Ahora, UpdatedAt = Ahora
            };
            siembra.ListasPrecio.Add(derivada);
            await siembra.SaveChangesAsync();
            idListaDerivada = derivada.Id;
        }

        var servicio = CrearServicio(nombreDeBase);

        var error = await Assert.ThrowsAsync<ErrorDominio>(() =>
            servicio.PrecioVigenteAsync(idArticulo, idListaDerivada, fecha: null));

        Assert.Equal("precio_derivado_invalido", error.Codigo);
    }
}
