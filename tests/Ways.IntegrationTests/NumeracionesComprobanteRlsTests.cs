using Microsoft.EntityFrameworkCore;
using Npgsql;
using Ways.Application.Abstracciones;
using Ways.Application.Ventas;
using Ways.Domain.Organizacion;
using Ways.Infrastructure.Multitenancy;

namespace Ways.IntegrationTests;

/// <summary>
/// stage-5-pos-ventas, Slice 2 (task 2.12, design: Table Shapes — write path A, decisiones 8/9):
/// mismo patrón que <c>ArticulosYPreciosRlsTests.NumeracionesArticulosEsInvisibleParaOtroTenant</c>/
/// <c>UnInsertEnNumeracionesArticulosConIdTenantAjenoSeRechaza</c> — <c>numeraciones_comprobante</c>
/// es PK-only (<c>id_punto_venta</c>, <c>tipo_comprobante</c>), sin columna <c>id</c> propia, así
/// que no entra en la tabla parametrizada genérica de otras RLS suites y necesita su propio test.
/// </summary>
[Collection("Ways.IntegrationTests secuencial")]
public class NumeracionesComprobanteRlsTests(WaysApiFixture fixture) : IClassFixture<WaysApiFixture>
{
    private const string TipoComprobante = "TX";

    private async Task<(int IdTenant, int IdPuntoVenta)> SembrarTenantConPuntoVentaAsync(string nombre)
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

        return (tenant.Id, puntoVenta.Id);
    }

    [Fact]
    public async Task NumeracionesComprobanteEsInvisibleParaOtroTenant()
    {
        var (idTenantA, idPuntoVentaA) = await SembrarTenantConPuntoVentaAsync(
            nameof(NumeracionesComprobanteEsInvisibleParaOtroTenant) + "-A");

        await using var db = fixture.CrearContextoDeAplicacion(TenantActualFijo.Plataforma);

        // AsignadorDeNumeroComprobante.AsegurarContadorAsync (SQL crudo), no
        // db.NumeracionesComprobante.Add: WaysDbContext.RechazarEscriturasDeNumeracionComprobante
        // rechaza cualquier Added/Modified que llegue por el ChangeTracker.
        await AsignadorDeNumeroComprobante.AsegurarContadorAsync(db, idTenantA, idPuntoVentaA, TipoComprobante);

        var tenantB = new Tenant
        {
            Nombre = nameof(NumeracionesComprobanteEsInvisibleParaOtroTenant) + "-B",
            Estado = EstadoTenant.Activo,
            CreatedAt = DateTimeOffset.UtcNow,
            UpdatedAt = DateTimeOffset.UtcNow
        };
        db.Tenants.Add(tenantB);
        await db.SaveChangesAsync();

        await using var cruda = await fixture.AbrirConexionCrudaAsync("tenant", tenantB.Id);

        await using var comando = cruda.CreateCommand();
        comando.CommandText = "SELECT count(*) FROM numeraciones_comprobante WHERE id_punto_venta = $1";
        comando.Parameters.Add(new NpgsqlParameter { Value = idPuntoVentaA });

        var total = (long)(await comando.ExecuteScalarAsync())!;

        Assert.Equal(0, total);
    }

    [Fact]
    public async Task UnInsertConIdTenantAjenoSeRechaza()
    {
        var (idTenantA, idPuntoVentaA) = await SembrarTenantConPuntoVentaAsync(
            nameof(UnInsertConIdTenantAjenoSeRechaza) + "-A");

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

        await using var cruda = await fixture.AbrirConexionCrudaAsync("tenant", idTenantA);

        await using var comando = cruda.CreateCommand();
        comando.CommandText =
            "INSERT INTO numeraciones_comprobante (id_tenant, id_punto_venta, tipo_comprobante, proximo_numero) " +
            "VALUES ($1, $2, 'TX', 1)";
        comando.Parameters.Add(new NpgsqlParameter { Value = tenantB.Id });
        comando.Parameters.Add(new NpgsqlParameter { Value = idPuntoVentaA });

        // 42501 = insufficient_privilege (violación de WITH CHECK) -- se dispara antes de la
        // FK compuesta a puntos_venta, mismo criterio que el resto de las RLS suites.
        var excepcion = await Assert.ThrowsAsync<PostgresException>(() => comando.ExecuteNonQueryAsync());
        Assert.Equal("42501", excepcion.SqlState);
    }

    [Fact]
    public async Task UnaSesionDeOtroTenantNoPuedeActualizarLaFila()
    {
        var (idTenantA, idPuntoVentaA) = await SembrarTenantConPuntoVentaAsync(
            nameof(UnaSesionDeOtroTenantNoPuedeActualizarLaFila) + "-A");

        await using var db = fixture.CrearContextoDeAplicacion(TenantActualFijo.Plataforma);
        await AsignadorDeNumeroComprobante.AsegurarContadorAsync(db, idTenantA, idPuntoVentaA, TipoComprobante);

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
        comando.CommandText =
            "UPDATE numeraciones_comprobante SET proximo_numero = proximo_numero + 1 " +
            "WHERE id_punto_venta = $1 AND tipo_comprobante = $2";
        comando.Parameters.Add(new NpgsqlParameter { Value = idPuntoVentaA });
        comando.Parameters.Add(new NpgsqlParameter { Value = TipoComprobante });

        var filas = await comando.ExecuteNonQueryAsync();
        Assert.Equal(0, filas);
    }

    /// <summary>Proof a nivel EF (LINQ) de que el filtro de tenant manual también bloquea la
    /// entidad que sí pasa por el ORM (no hereda <c>EntidadTenant</c>, ver
    /// <c>WaysDbContext.AplicarFiltroDeTenantEnNumeracionComprobante</c>).</summary>
    [Fact]
    public async Task ElFiltroDeEfNuncaDevuelveFilasDeOtroTenant()
    {
        var (idTenantA, idPuntoVentaA) = await SembrarTenantConPuntoVentaAsync(
            nameof(ElFiltroDeEfNuncaDevuelveFilasDeOtroTenant) + "-A");

        await using var db = fixture.CrearContextoDeAplicacion(TenantActualFijo.Plataforma);
        await AsignadorDeNumeroComprobante.AsegurarContadorAsync(db, idTenantA, idPuntoVentaA, TipoComprobante);

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
        var visibles = await sesionB.NumeracionesComprobante
            .Where(n => n.IdPuntoVenta == idPuntoVentaA).ToListAsync();

        Assert.Empty(visibles);
    }
}
