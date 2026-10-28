using Npgsql;
using Ways.Domain.Catalogos;
using Ways.Domain.Organizacion;
using Ways.Domain.Proveedores;
using Ways.Infrastructure.Multitenancy;

namespace Ways.IntegrationTests;

/// <summary>
/// E4 de pagos y aplicaciones, esquema: el valor <c>redondeo</c> del enum <c>tipo_movimiento_cc_proveedor</c> y la CHECK
/// <c>ck_movimientos_cuenta_corriente_proveedor_redondeo_un_destino</c> (db-error-backstops: SQLSTATE y nombre de la
/// constraint). Los inserts crudos usan un empleado inexistente a propósito: Postgres evalúa las CHECK antes de que dispare la
/// FK, así que lo que se observa primero es la violación de la CHECK, y las pruebas de «pasa la CHECK» terminan en la FK 23503.
/// </summary>
[Collection("Ways.IntegrationTests secuencial")]
public class RedondeoEsquemaTests(WaysApiFixture fixture) : IClassFixture<WaysApiFixture>
{
    private sealed record Escenario(int IdTenant, int IdProveedor, int IdPuntoVenta);

    private async Task<Escenario> SembrarAsync(string nombre)
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

        var condicionFiscal = new CondicionFiscal { Codigo = $"{nombre}-CF", Nombre = nombre, CreatedAt = ahora, UpdatedAt = ahora };
        db.CondicionesFiscales.Add(condicionFiscal);
        await db.SaveChangesAsync();

        var proveedor = new Proveedor
        {
            IdTenant = tenant.Id, RazonSocial = nombre, IdCondicionFiscal = condicionFiscal.Id,
            CreatedAt = ahora, UpdatedAt = ahora
        };
        db.Proveedores.Add(proveedor);
        await db.SaveChangesAsync();

        return new Escenario(tenant.Id, proveedor.Id, puntoVenta.Id);
    }

    private async Task<PostgresException> InsertarRedondeoAsync(Escenario e, int? idCompra, int? idPago)
    {
        await using var cruda = await fixture.AbrirConexionCrudaAsync("tenant", e.IdTenant);
        await using var comando = cruda.CreateCommand();
        comando.CommandText =
            "INSERT INTO movimientos_cuenta_corriente_proveedor " +
            "(id_tenant, id_proveedor, fecha, id_punto_venta, id_empleado, tipo, id_comprobante_compra, id_pago, importe, saldo_resultante, detalle) " +
            "VALUES ($1, $2, now(), $3, $4, 'redondeo', $5, $6, -3.50, -3.50, 'redondeo de prueba')";
        comando.Parameters.Add(new NpgsqlParameter { Value = e.IdTenant });
        comando.Parameters.Add(new NpgsqlParameter { Value = e.IdProveedor });
        comando.Parameters.Add(new NpgsqlParameter { Value = e.IdPuntoVenta });
        comando.Parameters.Add(new NpgsqlParameter { Value = 999_999_999 });
        comando.Parameters.Add(new NpgsqlParameter { Value = (object?)idCompra ?? DBNull.Value });
        comando.Parameters.Add(new NpgsqlParameter { Value = (object?)idPago ?? DBNull.Value });

        return await Assert.ThrowsAsync<PostgresException>(() => comando.ExecuteNonQueryAsync());
    }

    [Fact]
    public async Task ElEnumDeLaCuentaDelProveedorDeclaraElRedondeo()
    {
        var e = await SembrarAsync(nameof(ElEnumDeLaCuentaDelProveedorDeclaraElRedondeo));

        await using var cruda = await fixture.AbrirConexionCrudaAsync("tenant", e.IdTenant);
        await using var comando = new NpgsqlCommand(
            "SELECT string_agg(e::text, ',' ORDER BY e::text) FROM unnest(enum_range(NULL::tipo_movimiento_cc_proveedor)) e",
            cruda);

        Assert.Equal("ajuste,apertura,compra,pago,redondeo", await comando.ExecuteScalarAsync());
    }

    [Fact]
    public async Task UnRedondeoSinCompraNiPagoViolaLaCheckDeUnDestino()
    {
        var e = await SembrarAsync(nameof(UnRedondeoSinCompraNiPagoViolaLaCheckDeUnDestino));

        var excepcion = await InsertarRedondeoAsync(e, idCompra: null, idPago: null);

        Assert.Equal("23514", excepcion.SqlState);
        Assert.Equal("ck_movimientos_cuenta_corriente_proveedor_redondeo_un_destino", excepcion.ConstraintName);
    }

    [Fact]
    public async Task UnRedondeoConCompraYPagoALaVezViolaLaCheckDeUnDestino()
    {
        var e = await SembrarAsync(nameof(UnRedondeoConCompraYPagoALaVezViolaLaCheckDeUnDestino));

        var excepcion = await InsertarRedondeoAsync(e, idCompra: 999_999_998, idPago: 999_999_997);

        Assert.Equal("23514", excepcion.SqlState);
        Assert.Equal("ck_movimientos_cuenta_corriente_proveedor_redondeo_un_destino", excepcion.ConstraintName);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task UnRedondeoConUnSoloDestinoPasaLaCheckYSeDetieneEnLaFk(bool conCompra)
    {
        var e = await SembrarAsync(nameof(UnRedondeoConUnSoloDestinoPasaLaCheckYSeDetieneEnLaFk) + conCompra);

        var excepcion = await InsertarRedondeoAsync(
            e, idCompra: conCompra ? 999_999_998 : null, idPago: conCompra ? null : 999_999_997);

        Assert.Equal("23503", excepcion.SqlState);
    }
}
