using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;
using Ways.Domain.Caja;
using Ways.Domain.Catalogos;
using Ways.Domain.Clientes;
using Ways.Domain.CuentaCorriente;
using Ways.Domain.Fiscal;
using Ways.Domain.Pagos;
using Ways.Domain.Organizacion;
using Ways.Domain.Plantel;
using Ways.Domain.Stock;
using Ways.Domain.Usuarios;
using Ways.Domain.Ventas;
using Ways.Domain.Articulos;
using Ways.Domain.Compras;
using Ways.Infrastructure.Multitenancy;
using Ways.Infrastructure.Persistencia;

namespace Ways.IntegrationTests;

/// <summary>
/// Migración <c>CuentasDeTesoreria</c> sobre una base dedicada: migra hasta la anterior, siembra medios
/// de pago con las columnas de ANTES (SQL crudo, nunca vía EF con el modelo HEAD, que incluiría las
/// columnas nuevas y rompería contra el esquema viejo con 42703), aplica la migración y verifica que los
/// medios existentes quedan con porcentajes en cero y sin cuenta, y que el Down la deshace.
/// </summary>
[Collection("Ways.IntegrationTests secuencial")]
public class MediosPagoCuentaMigracionTests(WaysApiFixture fixture) : IClassFixture<WaysApiFixture>
{
    private const string MigracionAnterior = "20261012153420_PorImporteEnItemsDeVenta";
    private const string MigracionBajoPrueba = "20261012172924_CuentasDeTesoreria";

    private static DbContextOptions<WaysDbContext> ConstruirOpciones(string cadena) =>
        new DbContextOptionsBuilder<WaysDbContext>()
            .UseNpgsql(cadena, npgsql =>
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
                npgsql.MapEnum<CategoriaPago>("categoria_pago");
                npgsql.MapEnum<OrigenFondosPago>("origen_fondos_pago");
                npgsql.MapEnum<OrigenAplicacionPago>("origen_aplicacion_pago");
                npgsql.MapEnum<EstadoCompra>("estado_compra");
                npgsql.MapEnum<OrigenCompra>("origen_compra");
                npgsql.MapEnum<TipoMovimientoCcProveedor>("tipo_movimiento_cc_proveedor");
                npgsql.MapEnum<TipoMovimientoPersonal>("tipo_movimiento_personal");
                npgsql.MapEnum<TipoMovimientoCuenta>("tipo_movimiento_cuenta");
                npgsql.MapEnum<EstadoOrdenCompra>("estado_orden_compra");
                npgsql.MapEnum<EstadoPresupuesto>("estado_presupuesto");
                npgsql.MapEnum<EstadoRemito>("estado_remito");
                npgsql.MapEnum<ResultadoFiscal>("resultado_fiscal");
                npgsql.MapEnum<AmbienteFiscal>("ambiente_fiscal");
                npgsql.MapEnum<MotivoDeFacturacion>("motivo_facturacion");
                npgsql.MapEnum<EstadoFacturacionDeVenta>("estado_facturacion_venta");
                npgsql.MapEnum<ModoPuntoVenta>("modo_punto_venta");
            })
            .Options;

    private static async Task<long> ContarAsync(NpgsqlConnection cruda, string sql)
    {
        await using var comando = cruda.CreateCommand();
        comando.CommandText = sql;
        return (long)(await comando.ExecuteScalarAsync())!;
    }

    [Fact]
    public async Task LaMigracionDejaLosMediosExistentesConPorcentajesEnCeroYSinCuentaYElDownLaDeshace()
    {
        var nombreBase = $"ways_cuentas_{Guid.NewGuid():N}";
        var cadenaAdmin = new NpgsqlConnectionStringBuilder(fixture.OwnerConnectionString) { Database = "postgres" }.ConnectionString;
        var cadenaNueva = new NpgsqlConnectionStringBuilder(fixture.OwnerConnectionString) { Database = nombreBase }.ConnectionString;

        await using (var admin = new NpgsqlConnection(cadenaAdmin))
        {
            await admin.OpenAsync();
            await using var crear = admin.CreateCommand();
            crear.CommandText = $"CREATE DATABASE \"{nombreBase}\"";
            await crear.ExecuteNonQueryAsync();
        }

        try
        {
            var opciones = ConstruirOpciones(cadenaNueva);

            await using (var db = new WaysDbContext(opciones, TenantActualFijo.Plataforma))
            {
                var migrador = db.Database.GetInfrastructure().GetRequiredService<IMigrator>();
                await migrador.MigrateAsync(MigracionAnterior);
            }

            await using var cruda = new NpgsqlConnection(cadenaNueva);
            await cruda.OpenAsync();

            Assert.Equal(0L, await ContarAsync(cruda, "SELECT count(*) FROM pg_type WHERE typname = 'tipo_movimiento_cuenta'"));
            Assert.Equal(0L, await ContarAsync(cruda, "SELECT count(*) FROM information_schema.tables WHERE table_name IN ('cuentas_tesoreria', 'movimientos_cuenta')"));

            await using (var sembrar = cruda.CreateCommand())
            {
                sembrar.CommandText =
                    "INSERT INTO tenants (nombre, estado, created_at, updated_at) VALUES ('t-cuentas', 'activo', now(), now())";
                await sembrar.ExecuteNonQueryAsync();
            }

            var idTenant = (int)(await new NpgsqlCommand("SELECT id_tenant FROM tenants WHERE nombre = 't-cuentas'", cruda).ExecuteScalarAsync())!;
            await using (var medios = cruda.CreateCommand())
            {
                medios.CommandText =
                    "INSERT INTO medios_pago (id_tenant, nombre, orden, comportamiento, admite_vuelto, requiere_referencia, " +
                    "created_at, updated_at) VALUES " +
                    "($1, 'Efectivo', 1, 'efectivo', true, false, now(), now()), " +
                    "($1, 'QR', 2, 'electronico', false, true, now(), now()), " +
                    "($1, 'Cuenta corriente', 3, 'cuenta_corriente', false, false, now(), now())";
                medios.Parameters.Add(new NpgsqlParameter { Value = idTenant });
                await medios.ExecuteNonQueryAsync();
            }

            await using (var db = new WaysDbContext(opciones, TenantActualFijo.Plataforma))
            {
                var migrador = db.Database.GetInfrastructure().GetRequiredService<IMigrator>();
                await migrador.MigrateAsync(MigracionBajoPrueba);

                Assert.False(db.Database.HasPendingModelChanges());
            }

            Assert.Equal(3L, await ContarAsync(cruda, "SELECT count(*) FROM medios_pago WHERE id_tenant = " + idTenant));
            Assert.Equal(3L, await ContarAsync(cruda,
                "SELECT count(*) FROM medios_pago WHERE id_tenant = " + idTenant +
                " AND comision_porcentaje = 0 AND retencion_iibb_porcentaje = 0 AND retencion_iva_porcentaje = 0 " +
                "AND retencion_ganancias_porcentaje = 0 AND id_cuenta_tesoreria IS NULL"));
            Assert.Equal(1L, await ContarAsync(cruda, "SELECT count(*) FROM pg_type WHERE typname = 'tipo_movimiento_cuenta'"));
            Assert.Equal(2L, await ContarAsync(cruda,
                "SELECT count(*) FROM pg_class WHERE relname IN ('cuentas_tesoreria', 'movimientos_cuenta') AND relrowsecurity AND relforcerowsecurity"));

            await using (var db = new WaysDbContext(opciones, TenantActualFijo.Plataforma))
            {
                var migrador = db.Database.GetInfrastructure().GetRequiredService<IMigrator>();
                await migrador.MigrateAsync(MigracionAnterior);
            }

            Assert.Equal(0L, await ContarAsync(cruda, "SELECT count(*) FROM pg_type WHERE typname = 'tipo_movimiento_cuenta'"));
            Assert.Equal(0L, await ContarAsync(cruda, "SELECT count(*) FROM information_schema.tables WHERE table_name IN ('cuentas_tesoreria', 'movimientos_cuenta')"));
            Assert.Equal(0L, await ContarAsync(cruda,
                "SELECT count(*) FROM information_schema.columns WHERE table_name = 'medios_pago' " +
                "AND column_name IN ('id_cuenta_tesoreria', 'comision_porcentaje', 'retencion_iibb_porcentaje', 'retencion_iva_porcentaje', 'retencion_ganancias_porcentaje')"));
            Assert.Equal(3L, await ContarAsync(cruda, "SELECT count(*) FROM medios_pago WHERE id_tenant = " + idTenant));
        }
        finally
        {
            NpgsqlConnection.ClearAllPools();
            await using var admin = new NpgsqlConnection(cadenaAdmin);
            await admin.OpenAsync();
            await using var dropear = admin.CreateCommand();
            dropear.CommandText = $"DROP DATABASE IF EXISTS \"{nombreBase}\" WITH (FORCE)";
            await dropear.ExecuteNonQueryAsync();
        }
    }
}
