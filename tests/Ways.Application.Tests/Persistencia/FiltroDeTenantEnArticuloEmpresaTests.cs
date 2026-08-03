using Microsoft.EntityFrameworkCore;
using Ways.Application.Abstracciones;
using Ways.Domain.Articulos;
using Ways.Infrastructure.Multitenancy;
using Ways.Infrastructure.Persistencia;

namespace Ways.Application.Tests.Persistencia;

/// <summary>
/// Stage-3-articulos-y-precios (task 1.4/1.6, spec: Tenant Isolation for Articulos And
/// articulos_empresas): <see cref="ArticuloEmpresa"/> no hereda de
/// <see cref="Ways.Domain.Common.EntidadTenant"/> (junction PK-only, sin baja lógica), así que
/// el loop de <c>WaysDbContext.AplicarFiltroDeTenant</c> no la alcanza — necesita el filtro
/// manual <c>AplicarFiltroDeTenantEnArticuloEmpresa</c>. Este test prueba específicamente esa
/// variante escrita a mano, mismo patrón que <c>FiltroDeTenantTests</c> pero acotado a la
/// entidad que de verdad necesita cobertura nueva (las demás — <see cref="Articulo"/>,
/// <see cref="CodigoBarra"/>, <see cref="Ways.Domain.Precios.Precio"/> — heredan de
/// <c>EntidadTenant</c> y ya quedan cubiertas por el mecanismo genérico existente).
/// </summary>
public class FiltroDeTenantEnArticuloEmpresaTests
{
    private static WaysDbContext CrearContexto(string nombreDeBase, ITenantActual tenantActual)
    {
        var opciones = new DbContextOptionsBuilder<WaysDbContext>()
            .UseInMemoryDatabase(nombreDeBase)
            .Options;

        return new WaysDbContext(opciones, tenantActual);
    }

    [Fact]
    public async Task ElFiltroManualAislaArticuloEmpresaPorTenant()
    {
        var nombreDeBase = Guid.NewGuid().ToString();

        await using (var siembra = CrearContexto(nombreDeBase, TenantActualFijo.Plataforma))
        {
            siembra.ArticulosEmpresas.AddRange(
                new ArticuloEmpresa { IdArticulo = 1, IdEmpresa = 1, IdTenant = 1 },
                new ArticuloEmpresa { IdArticulo = 2, IdEmpresa = 2, IdTenant = 2 });

            await siembra.SaveChangesAsync();
        }

        await using var tenant1 = CrearContexto(nombreDeBase, new TenantActualFijo(ModoDeAcceso.Tenant, 1));

        var visibles = await tenant1.ArticulosEmpresas.ToListAsync();

        Assert.Single(visibles);
        Assert.Equal(1, visibles[0].IdArticulo);

        await using var plataforma = CrearContexto(nombreDeBase, TenantActualFijo.Plataforma);
        var todas = await plataforma.ArticulosEmpresas.ToListAsync();
        Assert.Equal(2, todas.Count);
    }
}
