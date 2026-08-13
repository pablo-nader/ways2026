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
/// stage-12-lotes-vencimientos, Slice 1 (task 1.19, mutation-proof-tests rule 5, design
/// decisión 20): RLS de <c>lotes</c>/<c>stock_lotes</c> sobre la conexión <c>ways_app</c>
/// (<c>NOSUPERUSER NOBYPASSRLS</c>) — la única bajo la cual una prueba de RLS prueba algo real
/// (ver <c>WaysApiFixture</c>). <c>lotes</c> hereda <c>EntidadTenant</c> (filtro genérico de
/// <c>WaysDbContext.AplicarFiltroDeTenant</c>); <c>stock_lotes</c> es PK-only con el filtro
/// escrito a mano (<c>AplicarFiltroDeTenantEnStockLote</c>) — mismo criterio que
/// <c>StockRlsTests</c>.
/// </summary>
[Collection("Ways.IntegrationTests secuencial")]
public class LotesRlsTests(WaysApiFixture fixture) : IClassFixture<WaysApiFixture>
{
    private sealed record Escenario(int IdTenant, int IdArticulo, int IdPuntoVenta, int IdLote);

    private async Task<Escenario> SembrarEscenarioAsync(string nombre)
    {
        using var _ = fixture.CreateClient(); // arranca el host (siembra roles, alícuotas)

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
            ControlaLote = true,
            CreatedAt = ahora,
            UpdatedAt = ahora
        };
        db.Articulos.Add(articulo);
        await db.SaveChangesAsync();

        var lote = new Lote
        {
            IdTenant = tenant.Id,
            IdArticulo = articulo.Id,
            Codigo = "2026-12-31",
            FechaVencimiento = new DateOnly(2026, 12, 31),
            EsSinIdentificar = false,
            CreatedAt = ahora,
            UpdatedAt = ahora
        };
        db.Lotes.Add(lote);
        await db.SaveChangesAsync();

        db.StockLotes.Add(new StockLote
        {
            IdArticulo = articulo.Id, IdPuntoVenta = puntoVenta.Id, IdLote = lote.Id, IdTenant = tenant.Id, Cantidad = 10m
        });
        await db.SaveChangesAsync();

        return new Escenario(tenant.Id, articulo.Id, puntoVenta.Id, lote.Id);
    }

    private async Task<Tenant> SembrarTenantBAsync(string nombre)
    {
        await using var db = fixture.CrearContextoDeAplicacion(TenantActualFijo.Plataforma);
        var tenantB = new Tenant
        {
            Nombre = nombre, Estado = EstadoTenant.Activo,
            CreatedAt = DateTimeOffset.UtcNow, UpdatedAt = DateTimeOffset.UtcNow
        };
        db.Tenants.Add(tenantB);
        await db.SaveChangesAsync();
        return tenantB;
    }

    // ---- lotes ------------------------------------------------------------------------------

    [Fact]
    public async Task UnaSesionDeOtroTenantNoVeElLotePorSelect()
    {
        var escenario = await SembrarEscenarioAsync(nameof(UnaSesionDeOtroTenantNoVeElLotePorSelect));
        var tenantB = await SembrarTenantBAsync(nameof(UnaSesionDeOtroTenantNoVeElLotePorSelect) + "-B");

        await using var cruda = await fixture.AbrirConexionCrudaAsync("tenant", tenantB.Id);
        await using var comando = cruda.CreateCommand();
        comando.CommandText = "SELECT count(*) FROM lotes WHERE id_lote = $1";
        comando.Parameters.Add(new NpgsqlParameter { Value = escenario.IdLote });

        var total = (long)(await comando.ExecuteScalarAsync())!;
        Assert.Equal(0, total);
    }

    [Fact]
    public async Task UnaSesionDeOtroTenantNoPuedeActualizarElLote()
    {
        var escenario = await SembrarEscenarioAsync(nameof(UnaSesionDeOtroTenantNoPuedeActualizarElLote));
        var tenantB = await SembrarTenantBAsync(nameof(UnaSesionDeOtroTenantNoPuedeActualizarElLote) + "-B");

        await using var cruda = await fixture.AbrirConexionCrudaAsync("tenant", tenantB.Id);
        await using var comando = cruda.CreateCommand();
        comando.CommandText = "UPDATE lotes SET codigo = 'tocado por intruso' WHERE id_lote = $1";
        comando.Parameters.Add(new NpgsqlParameter { Value = escenario.IdLote });

        var filas = await comando.ExecuteNonQueryAsync();
        Assert.Equal(0, filas);
    }

    [Fact]
    public async Task UnInsertDeLoteConIdTenantAjenoSeRechaza()
    {
        var escenario = await SembrarEscenarioAsync(nameof(UnInsertDeLoteConIdTenantAjenoSeRechaza));
        var tenantB = await SembrarTenantBAsync(nameof(UnInsertDeLoteConIdTenantAjenoSeRechaza) + "-B");

        // Sesión del tenant A intentando insertar con id_tenant del tenant B (ajeno).
        await using var cruda = await fixture.AbrirConexionCrudaAsync("tenant", escenario.IdTenant);
        var ahora = DateTimeOffset.UtcNow;

        await using var comando = cruda.CreateCommand();
        comando.CommandText =
            "INSERT INTO lotes (id_tenant, id_articulo, codigo, fecha_vencimiento, es_sin_identificar, " +
            "created_at, updated_at) VALUES ($1, $2, 'intruso', $3, false, $4, $4)";
        comando.Parameters.Add(new NpgsqlParameter { Value = tenantB.Id });
        comando.Parameters.Add(new NpgsqlParameter { Value = escenario.IdArticulo });
        comando.Parameters.Add(new NpgsqlParameter { Value = new DateOnly(2027, 1, 1) });
        comando.Parameters.Add(new NpgsqlParameter { Value = ahora });

        // 42501 = insufficient_privilege (violación de WITH CHECK) — se dispara antes de
        // cualquier FK/CHECK, sin importar que el resto de columnas referencien filas válidas
        // del tenant A.
        var excepcion = await Assert.ThrowsAsync<PostgresException>(() => comando.ExecuteNonQueryAsync());
        Assert.Equal("42501", excepcion.SqlState);
    }

    /// <summary>Proof a nivel EF (LINQ): <see cref="Lote"/> hereda <c>EntidadTenant</c>, así que
    /// el filtro genérico de <c>WaysDbContext.AplicarFiltroDeTenant</c> la cubre sin filtro
    /// manual.</summary>
    [Fact]
    public async Task ElFiltroDeEfNuncaDevuelveLotesDeOtroTenant()
    {
        var escenario = await SembrarEscenarioAsync(nameof(ElFiltroDeEfNuncaDevuelveLotesDeOtroTenant));
        var tenantB = await SembrarTenantBAsync(nameof(ElFiltroDeEfNuncaDevuelveLotesDeOtroTenant) + "-B");

        await using var sesionB = fixture.CrearContextoDeAplicacion(new TenantActualFijo(ModoDeAcceso.Tenant, tenantB.Id));
        var visible = await sesionB.Lotes.AnyAsync(l => l.Id == escenario.IdLote);

        Assert.False(visible);
    }

    // ---- stock_lotes --------------------------------------------------------------------------

    [Fact]
    public async Task UnaSesionDeOtroTenantNoVeElStockLotePorSelect()
    {
        var escenario = await SembrarEscenarioAsync(nameof(UnaSesionDeOtroTenantNoVeElStockLotePorSelect));
        var tenantB = await SembrarTenantBAsync(nameof(UnaSesionDeOtroTenantNoVeElStockLotePorSelect) + "-B");

        await using var cruda = await fixture.AbrirConexionCrudaAsync("tenant", tenantB.Id);
        await using var comando = cruda.CreateCommand();
        comando.CommandText =
            "SELECT count(*) FROM stock_lotes WHERE id_articulo = $1 AND id_punto_venta = $2 AND id_lote = $3";
        comando.Parameters.Add(new NpgsqlParameter { Value = escenario.IdArticulo });
        comando.Parameters.Add(new NpgsqlParameter { Value = escenario.IdPuntoVenta });
        comando.Parameters.Add(new NpgsqlParameter { Value = escenario.IdLote });

        var total = (long)(await comando.ExecuteScalarAsync())!;
        Assert.Equal(0, total);
    }

    [Fact]
    public async Task UnaSesionDeOtroTenantNoPuedeActualizarElStockLote()
    {
        var escenario = await SembrarEscenarioAsync(nameof(UnaSesionDeOtroTenantNoPuedeActualizarElStockLote));
        var tenantB = await SembrarTenantBAsync(nameof(UnaSesionDeOtroTenantNoPuedeActualizarElStockLote) + "-B");

        await using var cruda = await fixture.AbrirConexionCrudaAsync("tenant", tenantB.Id);
        await using var comando = cruda.CreateCommand();
        comando.CommandText =
            "UPDATE stock_lotes SET cantidad = cantidad + 1 " +
            "WHERE id_articulo = $1 AND id_punto_venta = $2 AND id_lote = $3";
        comando.Parameters.Add(new NpgsqlParameter { Value = escenario.IdArticulo });
        comando.Parameters.Add(new NpgsqlParameter { Value = escenario.IdPuntoVenta });
        comando.Parameters.Add(new NpgsqlParameter { Value = escenario.IdLote });

        var filas = await comando.ExecuteNonQueryAsync();
        Assert.Equal(0, filas);
    }

    [Fact]
    public async Task UnInsertDeStockLoteConIdTenantAjenoSeRechaza()
    {
        var escenario = await SembrarEscenarioAsync(nameof(UnInsertDeStockLoteConIdTenantAjenoSeRechaza));
        var tenantB = await SembrarTenantBAsync(nameof(UnInsertDeStockLoteConIdTenantAjenoSeRechaza) + "-B");

        await using var db = fixture.CrearContextoDeAplicacion(TenantActualFijo.Plataforma);

        // Un segundo lote del mismo tenant A, para no chocar con la fila ya sembrada por
        // SembrarEscenarioAsync (misma PK que stock_lotes usaría).
        var otroLote = new Lote
        {
            IdTenant = escenario.IdTenant,
            IdArticulo = escenario.IdArticulo,
            Codigo = "otro-lote",
            FechaVencimiento = new DateOnly(2027, 6, 1),
            EsSinIdentificar = false,
            CreatedAt = DateTimeOffset.UtcNow,
            UpdatedAt = DateTimeOffset.UtcNow
        };
        db.Lotes.Add(otroLote);
        await db.SaveChangesAsync();

        // Sesión del tenant A intentando insertar con id_tenant del tenant B (ajeno).
        await using var cruda = await fixture.AbrirConexionCrudaAsync("tenant", escenario.IdTenant);

        await using var comando = cruda.CreateCommand();
        comando.CommandText =
            "INSERT INTO stock_lotes (id_articulo, id_punto_venta, id_lote, id_tenant, cantidad) " +
            "VALUES ($1, $2, $3, $4, 0)";
        comando.Parameters.Add(new NpgsqlParameter { Value = escenario.IdArticulo });
        comando.Parameters.Add(new NpgsqlParameter { Value = escenario.IdPuntoVenta });
        comando.Parameters.Add(new NpgsqlParameter { Value = otroLote.Id });
        comando.Parameters.Add(new NpgsqlParameter { Value = tenantB.Id });

        var excepcion = await Assert.ThrowsAsync<PostgresException>(() => comando.ExecuteNonQueryAsync());
        Assert.Equal("42501", excepcion.SqlState);
    }

    /// <summary>Proof a nivel EF (LINQ) — <see cref="StockLote"/> usa el filtro manual (design
    /// decisión 20, no hereda <c>EntidadTenant</c>).</summary>
    [Fact]
    public async Task ElFiltroDeEfNuncaDevuelveStockLotesDeOtroTenant()
    {
        var escenario = await SembrarEscenarioAsync(nameof(ElFiltroDeEfNuncaDevuelveStockLotesDeOtroTenant));
        var tenantB = await SembrarTenantBAsync(nameof(ElFiltroDeEfNuncaDevuelveStockLotesDeOtroTenant) + "-B");

        await using var sesionB = fixture.CrearContextoDeAplicacion(new TenantActualFijo(ModoDeAcceso.Tenant, tenantB.Id));
        var visible = await sesionB.StockLotes.AnyAsync(s =>
            s.IdArticulo == escenario.IdArticulo && s.IdPuntoVenta == escenario.IdPuntoVenta && s.IdLote == escenario.IdLote);

        Assert.False(visible);
    }
}
