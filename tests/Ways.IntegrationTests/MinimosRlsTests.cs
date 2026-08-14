using Microsoft.EntityFrameworkCore;
using Npgsql;
using Ways.Application.Abstracciones;
using Ways.Domain.Articulos;
using Ways.Domain.Catalogos;
using Ways.Domain.Organizacion;
using Ways.Domain.Stock;
using Ways.Infrastructure.Multitenancy;

namespace Ways.IntegrationTests;

/// <summary>
/// stage-13-stock-inteligente, Slice 1 (task 1.18, mutation-proof-tests regla 5): cross-tenant
/// sobre <c>stock.minimo</c>/<c>reposicion</c> y sobre el NUEVO statement de <c>PUT
/// /api/stock/minimos</c> (<c>INSERT ... ON CONFLICT DO UPDATE</c>), corriendo sobre la conexión
/// <c>ways_app</c> (NOSUPERUSER NOBYPASSRLS) — misma disciplina que <c>StockRlsTests</c>. Esto
/// prueba que la fila y el statement NUEVOS respetan la política EXISTENTE de <c>stock</c>, no
/// que la política existe (ya cubierto desde stage-5).
/// </summary>
[Collection("Ways.IntegrationTests secuencial")]
public class MinimosRlsTests(WaysApiFixture fixture) : IClassFixture<WaysApiFixture>
{
    private async Task<(int IdTenant, int IdArticulo, int IdPuntoVenta)> SembrarStockConMinimoAsync(string nombre)
    {
        using var _ = fixture.CreateClient();

        await using var db = fixture.CrearContextoDeAplicacion(TenantActualFijo.Plataforma);
        var ahora = DateTimeOffset.UtcNow;

        var tenant = new Tenant { Nombre = nombre, Estado = EstadoTenant.Activo, CreatedAt = ahora, UpdatedAt = ahora };
        db.Tenants.Add(tenant);
        await db.SaveChangesAsync();

        var empresa = new Empresa { IdTenant = tenant.Id, RazonSocial = nombre, CreatedAt = ahora, UpdatedAt = ahora };
        db.Empresas.Add(empresa);
        await db.SaveChangesAsync();

        var puntoVenta = new PuntoVenta
        {
            IdTenant = tenant.Id, IdEmpresa = empresa.Id, Nombre = nombre, CreatedAt = ahora, UpdatedAt = ahora
        };
        db.PuntosVenta.Add(puntoVenta);
        await db.SaveChangesAsync();

        var area = new Area { IdTenant = tenant.Id, Nombre = nombre, Orden = 1, CreatedAt = ahora, UpdatedAt = ahora };
        db.Areas.Add(area);
        await db.SaveChangesAsync();

        var idAlicuotaIva = await db.AlicuotasIva.Select(a => a.Id).FirstAsync();

        var articulo = new Articulo
        {
            IdTenant = tenant.Id,
            CodigoInterno = $"{nombre}-cod",
            Nombre = nombre,
            IdArea = area.Id,
            IdAlicuotaIva = idAlicuotaIva,
            UnidadVenta = UnidadVenta.Unidad,
            EsProducto = true,
            CreatedAt = ahora,
            UpdatedAt = ahora
        };
        db.Articulos.Add(articulo);
        await db.SaveChangesAsync();

        db.Stock.Add(new Stock
        {
            IdArticulo = articulo.Id, IdPuntoVenta = puntoVenta.Id, IdTenant = tenant.Id, Cantidad = 10m, Minimo = 5m
        });
        await db.SaveChangesAsync();

        return (tenant.Id, articulo.Id, puntoVenta.Id);
    }

    private async Task<int> CrearTenantAjenoAsync(string nombre)
    {
        await using var db = fixture.CrearContextoDeAplicacion(TenantActualFijo.Plataforma);
        var tenant = new Tenant
        {
            Nombre = nombre, Estado = EstadoTenant.Activo, CreatedAt = DateTimeOffset.UtcNow, UpdatedAt = DateTimeOffset.UtcNow
        };
        db.Tenants.Add(tenant);
        await db.SaveChangesAsync();
        return tenant.Id;
    }

    [Fact]
    public async Task UnaSesionDeOtroTenantNoVeElMinimoDeLaFila()
    {
        var (idTenantA, idArticuloA, idPuntoVentaA) = await SembrarStockConMinimoAsync(nameof(UnaSesionDeOtroTenantNoVeElMinimoDeLaFila) + "-A");
        var idTenantB = await CrearTenantAjenoAsync(nameof(UnaSesionDeOtroTenantNoVeElMinimoDeLaFila) + "-B");

        await using var cruda = await fixture.AbrirConexionCrudaAsync("tenant", idTenantB);

        await using var comando = cruda.CreateCommand();
        comando.CommandText = "SELECT count(*) FROM stock WHERE id_articulo = $1 AND id_punto_venta = $2 AND minimo IS NOT NULL";
        comando.Parameters.Add(new NpgsqlParameter { Value = idArticuloA });
        comando.Parameters.Add(new NpgsqlParameter { Value = idPuntoVentaA });

        var total = (long)(await comando.ExecuteScalarAsync())!;

        Assert.Equal(0, total);
        Assert.NotEqual(idTenantA, idTenantB);
    }

    [Fact]
    public async Task UnaSesionDeOtroTenantNoPuedeActualizarElMinimo()
    {
        var (_, idArticuloA, idPuntoVentaA) = await SembrarStockConMinimoAsync(nameof(UnaSesionDeOtroTenantNoPuedeActualizarElMinimo) + "-A");
        var idTenantB = await CrearTenantAjenoAsync(nameof(UnaSesionDeOtroTenantNoPuedeActualizarElMinimo) + "-B");

        await using var cruda = await fixture.AbrirConexionCrudaAsync("tenant", idTenantB);

        await using var comando = cruda.CreateCommand();
        comando.CommandText = "UPDATE stock SET minimo = 999 WHERE id_articulo = $1 AND id_punto_venta = $2";
        comando.Parameters.Add(new NpgsqlParameter { Value = idArticuloA });
        comando.Parameters.Add(new NpgsqlParameter { Value = idPuntoVentaA });

        var filas = await comando.ExecuteNonQueryAsync();
        Assert.Equal(0, filas);
    }

    /// <summary>Prueba el statement NUEVO de <c>PUT /api/stock/minimos</c> — el mismo <c>INSERT
    /// ... ON CONFLICT DO UPDATE</c> que <c>ServicioDeStock.UpsertParametrosDeReposicionAsync</c>
    /// ejecuta, corrido a mano con un <c>id_tenant</c> ajeno para probar el <c>WITH CHECK</c> del
    /// <c>INSERT</c> — camino que un cruce de tenant vía <c>ResolverArticuloAsync</c>/<c>
    /// ResolverPuntoVentaAsync</c> nunca alcanza (esos ya devuelven 400/404 antes), así que esta
    /// es la única prueba que ejercita el <c>WITH CHECK</c> de este statement específico.</summary>
    [Fact]
    public async Task UnInsertDeMinimosConIdTenantAjenoSeRechaza()
    {
        var (idTenantA, idArticuloA, idPuntoVentaA) = await SembrarStockConMinimoAsync(nameof(UnInsertDeMinimosConIdTenantAjenoSeRechaza) + "-A");
        var idTenantB = await CrearTenantAjenoAsync(nameof(UnInsertDeMinimosConIdTenantAjenoSeRechaza) + "-B");

        await using var db = fixture.CrearContextoDeAplicacion(TenantActualFijo.Plataforma);
        var otroArticulo = new Articulo
        {
            IdTenant = idTenantA,
            CodigoInterno = nameof(UnInsertDeMinimosConIdTenantAjenoSeRechaza) + "-otro",
            Nombre = "otro",
            IdArea = await db.Articulos.Where(a => a.Id == idArticuloA).Select(a => a.IdArea).FirstAsync(),
            IdAlicuotaIva = await db.AlicuotasIva.Select(a => a.Id).FirstAsync(),
            UnidadVenta = UnidadVenta.Unidad,
            EsProducto = true,
            CreatedAt = DateTimeOffset.UtcNow,
            UpdatedAt = DateTimeOffset.UtcNow
        };
        db.Articulos.Add(otroArticulo);
        await db.SaveChangesAsync();

        await using var cruda = await fixture.AbrirConexionCrudaAsync("tenant", idTenantA);

        await using var comando = cruda.CreateCommand();
        comando.CommandText =
            "INSERT INTO stock (id_articulo, id_punto_venta, id_tenant, cantidad, minimo, reposicion) " +
            "VALUES ($1, $2, $3, 0, $4, $5) " +
            "ON CONFLICT (id_articulo, id_punto_venta) DO UPDATE " +
            "SET minimo = EXCLUDED.minimo, reposicion = EXCLUDED.reposicion";
        comando.Parameters.Add(new NpgsqlParameter { Value = otroArticulo.Id });
        comando.Parameters.Add(new NpgsqlParameter { Value = idPuntoVentaA });
        comando.Parameters.Add(new NpgsqlParameter { Value = idTenantB });
        comando.Parameters.Add(new NpgsqlParameter { Value = 15m });
        comando.Parameters.Add(new NpgsqlParameter { Value = (object?)DBNull.Value });

        // 42501 = insufficient_privilege (violación de WITH CHECK) — se dispara antes de
        // cualquier FK compuesta, mismo criterio que StockRlsTests.UnInsertConIdTenantAjenoSeRechaza.
        var excepcion = await Assert.ThrowsAsync<PostgresException>(() => comando.ExecuteNonQueryAsync());
        Assert.Equal("42501", excepcion.SqlState);
    }
}
