using Microsoft.EntityFrameworkCore;
using Npgsql;
using Ways.Domain.Organizacion;
using Ways.Infrastructure.Multitenancy;

namespace Ways.IntegrationTests;

/// <summary>
/// Las garantías de la base sobre <c>sucursales</c> y <c>puntos_venta.id_sucursal</c>: la sucursal de un punto de venta
/// es de SU empresa y de SU tenant (FK compuestas), y la tabla está aislada por tenant (RLS). Ninguna de las dos FK
/// recibe un id del cliente en esta etapa (no hay endpoint que lo acepte), así que no hay traducción 23503 en
/// <c>ManejadorDeErrores</c>: el SQLSTATE se afirma acá, contra la base.
/// </summary>
[Collection("Ways.IntegrationTests secuencial")]
public class SucursalesEsquemaTests(WaysApiFixture fixture) : IClassFixture<WaysApiFixture>
{
    private sealed record Siembra(
        int IdTenantA, int IdEmpresaA1, int IdEmpresaA2, int IdSucursalA1, int IdSucursalA2, int IdTenantB, int IdEmpresaB,
        int IdSucursalB);

    private async Task<Siembra> SembrarAsync(string nombre)
    {
        using var _ = fixture.CreateClient();

        await using var db = fixture.CrearContextoDeAplicacion(TenantActualFijo.Plataforma);
        var ahora = DateTimeOffset.UtcNow;

        var tenantA = new Tenant { Nombre = $"{nombre}-A", Estado = EstadoTenant.Activo, CreatedAt = ahora, UpdatedAt = ahora };
        var tenantB = new Tenant { Nombre = $"{nombre}-B", Estado = EstadoTenant.Activo, CreatedAt = ahora, UpdatedAt = ahora };
        db.Tenants.AddRange(tenantA, tenantB);
        await db.SaveChangesAsync();

        var empresaA1 = new Empresa { IdTenant = tenantA.Id, RazonSocial = $"{nombre}-A1", CreatedAt = ahora, UpdatedAt = ahora };
        var empresaA2 = new Empresa { IdTenant = tenantA.Id, RazonSocial = $"{nombre}-A2", CreatedAt = ahora, UpdatedAt = ahora };
        var empresaB = new Empresa { IdTenant = tenantB.Id, RazonSocial = $"{nombre}-B", CreatedAt = ahora, UpdatedAt = ahora };
        db.Empresas.AddRange(empresaA1, empresaA2, empresaB);
        await db.SaveChangesAsync();

        Sucursal Nueva(Tenant t, Empresa e, string n) =>
            new() { IdTenant = t.Id, IdEmpresa = e.Id, Nombre = n, CreatedAt = ahora, UpdatedAt = ahora };
        var sucursalA1 = Nueva(tenantA, empresaA1, "A1");
        var sucursalA2 = Nueva(tenantA, empresaA2, "A2");
        var sucursalB = Nueva(tenantB, empresaB, "B");
        db.Sucursales.AddRange(sucursalA1, sucursalA2, sucursalB);
        await db.SaveChangesAsync();

        return new Siembra(
            tenantA.Id, empresaA1.Id, empresaA2.Id, sucursalA1.Id, sucursalA2.Id, tenantB.Id, empresaB.Id, sucursalB.Id);
    }

    private async Task<PostgresException> SqlFallidoAsync(int idTenant, string sql, params object[] parametros)
    {
        await using var conexion = await fixture.AbrirConexionCrudaAsync("tenant", idTenant);
        await using var comando = conexion.CreateCommand();
        comando.CommandText = sql;
        foreach (var valor in parametros)
        {
            comando.Parameters.Add(new NpgsqlParameter { Value = valor });
        }

        return await Assert.ThrowsAsync<PostgresException>(() => comando.ExecuteNonQueryAsync());
    }

    private const string InsertarPuntoVenta =
        "INSERT INTO puntos_venta (id_tenant, id_empresa, id_sucursal, nombre, modo, created_at, updated_at) " +
        "VALUES ($1, $2, $3, 'PV', 'web'::modo_punto_venta, now(), now())";

    /// <summary>Un punto de venta no puede colgar de la sucursal de OTRA empresa del mismo tenant. Mutación corrida:
    /// sacar <c>id_empresa</c> de <c>fk_puntos_venta_sucursal</c> deja pasar el INSERT y la aserción cae.</summary>
    [Fact]
    public async Task UnPuntoDeVentaNoPuedeColgarDeLaSucursalDeOtraEmpresa()
    {
        var s = await SembrarAsync(nameof(UnPuntoDeVentaNoPuedeColgarDeLaSucursalDeOtraEmpresa));

        var error = await SqlFallidoAsync(s.IdTenantA, InsertarPuntoVenta, s.IdTenantA, s.IdEmpresaA1, s.IdSucursalA2);

        Assert.Equal(PostgresErrorCodes.ForeignKeyViolation, error.SqlState);
        Assert.Equal("fk_puntos_venta_sucursal", error.ConstraintName);
    }

    [Fact]
    public async Task UnPuntoDeVentaNoPuedeColgarDeLaSucursalDeOtroTenant()
    {
        var s = await SembrarAsync(nameof(UnPuntoDeVentaNoPuedeColgarDeLaSucursalDeOtroTenant));

        var error = await SqlFallidoAsync(s.IdTenantA, InsertarPuntoVenta, s.IdTenantA, s.IdEmpresaA1, s.IdSucursalB);

        Assert.Equal(PostgresErrorCodes.ForeignKeyViolation, error.SqlState);
        Assert.Equal("fk_puntos_venta_sucursal", error.ConstraintName);
    }

    [Fact]
    public async Task UnPuntoDeVentaSinSucursalNoEntra()
    {
        var s = await SembrarAsync(nameof(UnPuntoDeVentaSinSucursalNoEntra));

        var error = await SqlFallidoAsync(
            s.IdTenantA,
            "INSERT INTO puntos_venta (id_tenant, id_empresa, nombre, modo, created_at, updated_at) " +
            "VALUES ($1, $2, 'PV', 'web'::modo_punto_venta, now(), now())",
            s.IdTenantA, s.IdEmpresaA1);

        Assert.Equal(PostgresErrorCodes.NotNullViolation, error.SqlState);
        Assert.Equal("id_sucursal", error.ColumnName);
    }

    [Fact]
    public async Task UnaSucursalNoPuedeColgarDeLaEmpresaDeOtroTenant()
    {
        var s = await SembrarAsync(nameof(UnaSucursalNoPuedeColgarDeLaEmpresaDeOtroTenant));

        var error = await SqlFallidoAsync(
            s.IdTenantA,
            "INSERT INTO sucursales (id_tenant, id_empresa, nombre, created_at, updated_at) VALUES ($1, $2, 'X', now(), now())",
            s.IdTenantA, s.IdEmpresaB);

        Assert.Equal(PostgresErrorCodes.ForeignKeyViolation, error.SqlState);
        Assert.Equal("fk_sucursales_empresa", error.ConstraintName);
    }

    /// <summary>RLS sobre <c>sucursales</c>: <c>ways_app</c> con el GUC del tenant A no ve la del tenant B y no puede
    /// insertar una con <c>id_tenant</c> ajeno (42501, el WITH CHECK de la policy).</summary>
    [Fact]
    public async Task UnTenantNoVeNiEscribeLasSucursalesDeOtro()
    {
        var s = await SembrarAsync(nameof(UnTenantNoVeNiEscribeLasSucursalesDeOtro));

        await using (var conexion = await fixture.AbrirConexionCrudaAsync("tenant", s.IdTenantA))
        {
            await using var comando = conexion.CreateCommand();
            comando.CommandText = "SELECT count(*) FROM sucursales WHERE id_sucursal = $1";
            comando.Parameters.Add(new NpgsqlParameter { Value = s.IdSucursalB });
            Assert.Equal(0L, await comando.ExecuteScalarAsync());

            comando.Parameters[0].Value = s.IdSucursalA1;
            Assert.Equal(1L, await comando.ExecuteScalarAsync());
        }

        var error = await SqlFallidoAsync(
            s.IdTenantA,
            "INSERT INTO sucursales (id_tenant, id_empresa, nombre, created_at, updated_at) VALUES ($1, $2, 'X', now(), now())",
            s.IdTenantB, s.IdEmpresaB);

        Assert.Equal(PostgresErrorCodes.InsufficientPrivilege, error.SqlState);
    }
}
