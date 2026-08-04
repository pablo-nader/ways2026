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
/// stage-5-pos-ventas, Slice 3 (task 3.18, spec: stock / Tenant Isolation implícito en Stock
/// Schema At Rest): mismo patrón que <c>NumeracionesComprobanteRlsTests</c> — <c>stock</c> es
/// PK-only (<c>id_articulo</c>, <c>id_punto_venta</c>), sin columna <c>id</c> propia, así que
/// no entra en la tabla parametrizada genérica de <see cref="VentasStockYCuentaCorrienteRlsTests"/>.
/// </summary>
[Collection("Ways.IntegrationTests secuencial")]
public class StockRlsTests(WaysApiFixture fixture) : IClassFixture<WaysApiFixture>
{
    private async Task<(int IdTenant, int IdArticulo, int IdPuntoVenta)> SembrarStockAsync(string nombre)
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

        db.Stock.Add(new Stock { IdArticulo = articulo.Id, IdPuntoVenta = puntoVenta.Id, IdTenant = tenant.Id, Cantidad = 10m });
        await db.SaveChangesAsync();

        return (tenant.Id, articulo.Id, puntoVenta.Id);
    }

    [Fact]
    public async Task StockEsInvisibleParaOtroTenant()
    {
        var (idTenantA, idArticuloA, idPuntoVentaA) = await SembrarStockAsync(nameof(StockEsInvisibleParaOtroTenant) + "-A");

        await using var db = fixture.CrearContextoDeAplicacion(TenantActualFijo.Plataforma);
        var tenantB = new Tenant
        {
            Nombre = nameof(StockEsInvisibleParaOtroTenant) + "-B",
            Estado = EstadoTenant.Activo,
            CreatedAt = DateTimeOffset.UtcNow,
            UpdatedAt = DateTimeOffset.UtcNow
        };
        db.Tenants.Add(tenantB);
        await db.SaveChangesAsync();

        await using var cruda = await fixture.AbrirConexionCrudaAsync("tenant", tenantB.Id);

        await using var comando = cruda.CreateCommand();
        comando.CommandText = "SELECT count(*) FROM stock WHERE id_articulo = $1 AND id_punto_venta = $2";
        comando.Parameters.Add(new NpgsqlParameter { Value = idArticuloA });
        comando.Parameters.Add(new NpgsqlParameter { Value = idPuntoVentaA });

        var total = (long)(await comando.ExecuteScalarAsync())!;

        Assert.Equal(0, total);
        Assert.NotEqual(idTenantA, tenantB.Id);
    }

    [Fact]
    public async Task UnaSesionDeOtroTenantNoPuedeActualizarLaFila()
    {
        var (_, idArticuloA, idPuntoVentaA) = await SembrarStockAsync(nameof(UnaSesionDeOtroTenantNoPuedeActualizarLaFila) + "-A");

        await using var db = fixture.CrearContextoDeAplicacion(TenantActualFijo.Plataforma);
        var tenantB = new Tenant
        {
            Nombre = nameof(UnaSesionDeOtroTenantNoPuedeActualizarLaFila) + "-B",
            Estado = EstadoTenant.Activo,
            CreatedAt = DateTimeOffset.UtcNow,
            UpdatedAt = DateTimeOffset.UtcNow
        };
        db.Tenants.Add(tenantB);
        await db.SaveChangesAsync();

        await using var cruda = await fixture.AbrirConexionCrudaAsync("tenant", tenantB.Id);

        await using var comando = cruda.CreateCommand();
        comando.CommandText = "UPDATE stock SET cantidad = cantidad + 1 WHERE id_articulo = $1 AND id_punto_venta = $2";
        comando.Parameters.Add(new NpgsqlParameter { Value = idArticuloA });
        comando.Parameters.Add(new NpgsqlParameter { Value = idPuntoVentaA });

        var filas = await comando.ExecuteNonQueryAsync();
        Assert.Equal(0, filas);
    }

    [Fact]
    public async Task UnInsertConIdTenantAjenoSeRechaza()
    {
        var (idTenantA, idArticuloA, idPuntoVentaA) = await SembrarStockAsync(nameof(UnInsertConIdTenantAjenoSeRechaza) + "-A");

        await using var db = fixture.CrearContextoDeAplicacion(TenantActualFijo.Plataforma);
        var tenantB = new Tenant
        {
            Nombre = nameof(UnInsertConIdTenantAjenoSeRechaza) + "-B",
            Estado = EstadoTenant.Activo,
            CreatedAt = DateTimeOffset.UtcNow,
            UpdatedAt = DateTimeOffset.UtcNow
        };
        db.Tenants.Add(tenantB);
        await db.SaveChangesAsync();

        var otroArticulo = new Articulo
        {
            IdTenant = idTenantA,
            CodigoInterno = nameof(UnInsertConIdTenantAjenoSeRechaza) + "-otro",
            Nombre = "otro",
            IdArea = (await db.Articulos.Where(a => a.Id == idArticuloA).Select(a => a.IdArea).FirstAsync()),
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
            "INSERT INTO stock (id_articulo, id_punto_venta, id_tenant, cantidad) VALUES ($1, $2, $3, 0)";
        comando.Parameters.Add(new NpgsqlParameter { Value = otroArticulo.Id });
        comando.Parameters.Add(new NpgsqlParameter { Value = idPuntoVentaA });
        comando.Parameters.Add(new NpgsqlParameter { Value = tenantB.Id });

        // 42501 = insufficient_privilege (violación de WITH CHECK) -- se dispara antes de
        // cualquier FK compuesta.
        var excepcion = await Assert.ThrowsAsync<PostgresException>(() => comando.ExecuteNonQueryAsync());
        Assert.Equal("42501", excepcion.SqlState);
    }

    /// <summary>Proof a nivel EF (LINQ) — <see cref="Stock"/> usa el filtro manual (no hereda
    /// <c>EntidadTenant</c>, ver <c>WaysDbContext.AplicarFiltroDeTenantEnStock</c>).</summary>
    [Fact]
    public async Task ElFiltroDeEfNuncaDevuelveFilasDeOtroTenant()
    {
        var (_, idArticuloA, idPuntoVentaA) = await SembrarStockAsync(nameof(ElFiltroDeEfNuncaDevuelveFilasDeOtroTenant) + "-A");

        await using var db = fixture.CrearContextoDeAplicacion(TenantActualFijo.Plataforma);
        var tenantB = new Tenant
        {
            Nombre = nameof(ElFiltroDeEfNuncaDevuelveFilasDeOtroTenant) + "-B",
            Estado = EstadoTenant.Activo,
            CreatedAt = DateTimeOffset.UtcNow,
            UpdatedAt = DateTimeOffset.UtcNow
        };
        db.Tenants.Add(tenantB);
        await db.SaveChangesAsync();

        await using var sesionB = fixture.CrearContextoDeAplicacion(new TenantActualFijo(ModoDeAcceso.Tenant, tenantB.Id));
        var visible = await sesionB.Stock.AnyAsync(s => s.IdArticulo == idArticuloA && s.IdPuntoVenta == idPuntoVentaA);

        Assert.False(visible);
    }
}
