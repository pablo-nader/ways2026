using Microsoft.EntityFrameworkCore;
using Npgsql;
using Ways.Application.Abstracciones;
using Ways.Domain.Articulos;
using Ways.Domain.Caja;
using Ways.Domain.Catalogos;
using Ways.Domain.Clientes;
using Ways.Domain.Compras;
using Ways.Domain.CuentaCorriente;
using Ways.Domain.Gastos;
using Ways.Domain.Organizacion;
using Ways.Domain.Stock;
using Ways.Domain.Usuarios;
using Ways.Domain.Ventas;
using Ways.Infrastructure.Multitenancy;
using Ways.Infrastructure.Persistencia;

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

    /// <summary>Judgment-day fix (juez B, MAJOR): un <see cref="WaysDbContext"/> sobre
    /// <c>ways_owner</c> (bypassea RLS) para que las dos pruebas de "el filtro de EF nunca
    /// devuelve filas ajenas" prueben genuinamente el query filter — sobre <c>ways_app</c>
    /// RLS filtra igual aunque el filtro de EF no exista, así esas pruebas no distinguían
    /// nada (comprobado comentando <c>SetQueryFilter</c> y viendo los 8 tests pasar
    /// igual).</summary>
    private WaysDbContext CrearContextoDeOwner(ITenantActual tenantActual)
    {
        var opciones = new DbContextOptionsBuilder<WaysDbContext>()
            .UseNpgsql(fixture.OwnerConnectionString, npgsql =>
            {
                npgsql.MapEnum<EstadoUsuario>("estado_usuario");
                npgsql.MapEnum<EstadoTenant>("estado_tenant");
                npgsql.MapEnum<ComportamientoMedioPago>("comportamiento_medio_pago");
                npgsql.MapEnum<ClaseComprobante>("clase_comprobante");
                npgsql.MapEnum<TipoDocumento>("tipo_documento");
                npgsql.MapEnum<ModoLista>("modo_lista");
                npgsql.MapEnum<UnidadVenta>("unidad_venta");
                npgsql.MapEnum<EstadoComprobante>("estado_comprobante");
                npgsql.MapEnum<MotivoStock>("motivo_stock");
                npgsql.MapEnum<TipoMovimientoCc>("tipo_movimiento_cc");
                npgsql.MapEnum<EstadoTurno>("estado_turno");
                npgsql.MapEnum<TipoMovimientoCaja>("tipo_movimiento_caja");
                npgsql.MapEnum<TipoMovimientoTesoreria>("tipo_movimiento_tesoreria");
                npgsql.MapEnum<CategoriaGasto>("categoria_gasto");
                npgsql.MapEnum<EstadoCompra>("estado_compra");
            })
            .AddInterceptors(new InterceptorDeContextoDeTenant(tenantActual))
            .Options;

        return new WaysDbContext(opciones, tenantActual);
    }

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
    /// manual.
    ///
    /// Judgment-day fix (juez B, MAJOR): corre sobre <c>ways_owner</c> (bypassea RLS,
    /// <see cref="CrearContextoDeOwner"/>), no sobre <c>ways_app</c> — bajo <c>ways_app</c>
    /// RLS excluye la fila ajena igual aunque el query filter de EF no exista, así que la
    /// prueba original no probaba nada del filtro. Seed cross-tenant (dos escenarios
    /// completos) más el control de <c>count &gt; 0</c> del propio tenant evita que la
    /// prueba pase en vacío.
    ///
    /// Evidencia de mutación: comentando <c>entidad.SetQueryFilter("Tenant", filtro);</c> en
    /// <c>WaysDbContext.AplicarFiltroDeTenant</c> y corriendo el filtro
    /// <c>--filter "FullyQualifiedName~LotesRlsTests"</c>, este test (y el análogo de
    /// <c>stock_lotes</c>) fallaron: <c>visibleAjeno</c> pasó a ser <c>true</c> (mutant
    /// caught). Revertida la mutación, la suite vuelve a estar verde.</summary>
    [Fact]
    public async Task ElFiltroDeEfNuncaDevuelveLotesDeOtroTenant()
    {
        var escenarioA = await SembrarEscenarioAsync(nameof(ElFiltroDeEfNuncaDevuelveLotesDeOtroTenant) + "-A");
        var escenarioB = await SembrarEscenarioAsync(nameof(ElFiltroDeEfNuncaDevuelveLotesDeOtroTenant) + "-B");

        await using var sesionB = CrearContextoDeOwner(new TenantActualFijo(ModoDeAcceso.Tenant, escenarioB.IdTenant));

        var visibleAjeno = await sesionB.Lotes.AnyAsync(l => l.Id == escenarioA.IdLote);
        Assert.False(visibleAjeno);

        var visiblePropio = await sesionB.Lotes.AnyAsync(l => l.Id == escenarioB.IdLote);
        Assert.True(visiblePropio);
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
    /// decisión 20, no hereda <c>EntidadTenant</c>).
    ///
    /// Judgment-day fix (juez B, MAJOR): mismo criterio que
    /// <see cref="ElFiltroDeEfNuncaDevuelveLotesDeOtroTenant"/> — corre sobre
    /// <c>ways_owner</c> para que solo el query filter de EF pueda excluir la fila ajena.
    ///
    /// Evidencia de mutación: comentando <c>entidad.SetQueryFilter("Tenant", filtro);</c> en
    /// <c>WaysDbContext.AplicarFiltroDeTenantEnStockLote</c> y corriendo el filtro
    /// <c>--filter "FullyQualifiedName~LotesRlsTests"</c>, este test falló:
    /// <c>visibleAjeno</c> pasó a ser <c>true</c> (mutant caught). Revertida la mutación, la
    /// suite vuelve a estar verde.</summary>
    [Fact]
    public async Task ElFiltroDeEfNuncaDevuelveStockLotesDeOtroTenant()
    {
        var escenarioA = await SembrarEscenarioAsync(nameof(ElFiltroDeEfNuncaDevuelveStockLotesDeOtroTenant) + "-A");
        var escenarioB = await SembrarEscenarioAsync(nameof(ElFiltroDeEfNuncaDevuelveStockLotesDeOtroTenant) + "-B");

        await using var sesionB = CrearContextoDeOwner(new TenantActualFijo(ModoDeAcceso.Tenant, escenarioB.IdTenant));

        var visibleAjeno = await sesionB.StockLotes.AnyAsync(s =>
            s.IdArticulo == escenarioA.IdArticulo && s.IdPuntoVenta == escenarioA.IdPuntoVenta && s.IdLote == escenarioA.IdLote);
        Assert.False(visibleAjeno);

        var visiblePropio = await sesionB.StockLotes.AnyAsync(s =>
            s.IdArticulo == escenarioB.IdArticulo && s.IdPuntoVenta == escenarioB.IdPuntoVenta && s.IdLote == escenarioB.IdLote);
        Assert.True(visiblePropio);
    }
}
