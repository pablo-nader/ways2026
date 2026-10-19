using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;
using Ways.Domain.Articulos;
using Ways.Domain.Caja;
using Ways.Domain.Clientes;
using Ways.Domain.Catalogos;
using Ways.Domain.Compras;
using Ways.Domain.CuentaCorriente;
using Ways.Domain.Fiscal;
using Ways.Domain.Gastos;
using Ways.Domain.Organizacion;
using Ways.Domain.Plantel;
using Ways.Domain.Stock;
using Ways.Domain.Usuarios;
using Ways.Domain.Ventas;
using Ways.Infrastructure.Multitenancy;
using Ways.Infrastructure.Persistencia;

namespace Ways.IntegrationTests;

/// <summary>
/// Migración <c>FacturacionDecididaEnVenta</c>: toda venta que existe al migrar tiene que leerse como ya decidida sin que
/// la migración reescriba <c>comprobantes_venta</c>, y las ventas que nazcan después arrancan en <c>NULL</c> (decisión
/// debida). Usa una base dedicada migrada hasta la migración anterior, siembra con un INSERT crudo que lista las columnas
/// de ANTES (el modelo actual de EF incluiría la columna nueva y rompería con 42703) y migra la nueva, igual que
/// <see cref="TurnosCajaMedioPagoEfectivoMigracionTests"/>.
/// </summary>
[Collection("Ways.IntegrationTests secuencial")]
public class FacturacionDecididaEnVentaMigracionTests(WaysApiFixture fixture) : IClassFixture<WaysApiFixture>
{
    private const string MigracionAnterior = "20261013020847_FacturacionPorMedioDePagoEsquema";
    private const string MigracionBajoPrueba = "20261013055342_FacturacionDecididaEnVenta";

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
                npgsql.MapEnum<CategoriaGasto>("categoria_gasto");
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
            })
            .Options;

    /// <summary>INSERT con las columnas de <c>comprobantes_venta</c> de ANTES de la migración. Las claves foráneas no se
    /// satisfacen a propósito: la sesión corre con <c>session_replication_role = replica</c> (el dueño de la base de la
    /// prueba es superusuario), porque lo que se prueba es la columna nueva y no las relaciones del comprobante.</summary>
    private static async Task<int> InsertarComprobanteAsync(NpgsqlConnection cruda, long numero)
    {
        await using var comando = cruda.CreateCommand();
        comando.CommandText =
            "INSERT INTO comprobantes_venta (id_tenant, id_tipo_comprobante, numero, fecha, id_punto_venta, id_empleado, " +
            "id_cliente, subtotal, descuento_total, total, estado, created_at, updated_at) " +
            "VALUES (1, 1, $1, now(), 1, 1, 1, 10, 0, 10, 'emitido'::estado_comprobante, now(), now()) " +
            "RETURNING id_comprobante_venta";
        comando.Parameters.Add(new NpgsqlParameter { Value = numero });
        return (int)(await comando.ExecuteScalarAsync())!;
    }

    private static async Task<object?> ScalarAsync(NpgsqlConnection cruda, string sql)
    {
        await using var comando = cruda.CreateCommand();
        comando.CommandText = sql;
        var valor = await comando.ExecuteScalarAsync();
        return valor is DBNull ? null : valor;
    }

    private static async Task<DateTime?> LeerMarcaAsync(NpgsqlConnection cruda, int id)
    {
        var valor = await ScalarAsync(cruda, $"SELECT facturacion_decidida_en FROM comprobantes_venta WHERE id_comprobante_venta = {id}");
        return valor as DateTime?;
    }

    [Fact]
    public async Task LasVentasExistentesQuedanDecididasSinReescribirLaTablaYLasNuevasNacenEnNull()
    {
        var nombreBase = $"ways_decidida_{Guid.NewGuid():N}";
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
                await db.Database.GetInfrastructure().GetRequiredService<IMigrator>().MigrateAsync(MigracionAnterior);
            }

            await using var cruda = new NpgsqlConnection(cadenaNueva);
            await cruda.OpenAsync();
            await ScalarAsync(cruda, "SET session_replication_role = replica");

            var idViejo1 = await InsertarComprobanteAsync(cruda, 1);
            var idViejo2 = await InsertarComprobanteAsync(cruda, 2);
            var archivoAntes = await ScalarAsync(cruda, "SELECT relfilenode::bigint FROM pg_class WHERE relname = 'comprobantes_venta'");
            var antesDeMigrar = (DateTime)(await ScalarAsync(cruda, "SELECT clock_timestamp()"))!;

            await using (var db = new WaysDbContext(opciones, TenantActualFijo.Plataforma))
            {
                await db.Database.GetInfrastructure().GetRequiredService<IMigrator>().MigrateAsync(MigracionBajoPrueba);
            }

            var idNuevo = await InsertarComprobanteAsync(cruda, 3);

            // Las filas anteriores se leen decididas, todas con el mismo valor (se evaluó una sola vez), posterior a su alta.
            var marca1 = await LeerMarcaAsync(cruda, idViejo1);
            var marca2 = await LeerMarcaAsync(cruda, idViejo2);
            Assert.NotNull(marca1);
            Assert.Equal(marca1, marca2);
            Assert.True(marca1 >= antesDeMigrar.AddSeconds(-1), "La marca no es la hora de la migración.");

            // La fila nacida después no tiene valor por defecto: la decisión está debida.
            Assert.Null(await LeerMarcaAsync(cruda, idNuevo));
            Assert.Null(await ScalarAsync(
                cruda,
                "SELECT d.oid FROM pg_attrdef d JOIN pg_attribute a ON a.attrelid = d.adrelid AND a.attnum = d.adnum " +
                "WHERE d.adrelid = 'comprobantes_venta'::regclass AND a.attname = 'facturacion_decidida_en'"));

            // Solo metadata: el archivo de la tabla es el mismo y PostgreSQL guarda el valor como "faltante" de la columna.
            Assert.Equal(archivoAntes, await ScalarAsync(cruda, "SELECT relfilenode::bigint FROM pg_class WHERE relname = 'comprobantes_venta'"));
            Assert.Equal(
                true,
                await ScalarAsync(
                    cruda,
                    "SELECT atthasmissing FROM pg_attribute WHERE attrelid = 'comprobantes_venta'::regclass " +
                    "AND attname = 'facturacion_decidida_en'"));

            // Las columnas de intentos fallidos: toda fila (vieja o nueva) arranca en 0 intentos y sin espera, y el contador
            // es NOT NULL con valor por defecto 0 (el modelo de EF lo describe igual).
            foreach (var id in new[] { idViejo1, idViejo2, idNuevo })
            {
                Assert.Equal(
                    0,
                    await ScalarAsync(cruda, $"SELECT facturacion_intentos FROM comprobantes_venta WHERE id_comprobante_venta = {id}"));
                Assert.Null(await ScalarAsync(
                    cruda, $"SELECT facturacion_proximo_intento_en FROM comprobantes_venta WHERE id_comprobante_venta = {id}"));
            }

            Assert.Equal(
                true,
                await ScalarAsync(
                    cruda,
                    "SELECT attnotnull FROM pg_attribute WHERE attrelid = 'comprobantes_venta'::regclass " +
                    "AND attname = 'facturacion_intentos'"));
            Assert.Equal(
                "integer",
                await ScalarAsync(
                    cruda,
                    "SELECT format_type(atttypid, atttypmod) FROM pg_attribute WHERE attrelid = 'comprobantes_venta'::regclass " +
                    "AND attname = 'facturacion_intentos'"));
            Assert.Equal(
                "timestamp with time zone",
                await ScalarAsync(
                    cruda,
                    "SELECT format_type(atttypid, atttypmod) FROM pg_attribute WHERE attrelid = 'comprobantes_venta'::regclass " +
                    "AND attname = 'facturacion_proximo_intento_en'"));

            // El índice parcial de las ventas debidas existe, es válido y filtra por la marca y por los espejos.
            var predicado = (string)(await ScalarAsync(
                cruda,
                "SELECT pg_get_expr(i.indpred, i.indrelid) FROM pg_index i JOIN pg_class c ON c.oid = i.indexrelid " +
                "WHERE c.relname = 'ix_comprobantes_venta_decision_debida' AND i.indisvalid"))!;
            Assert.Contains("facturacion_decidida_en IS NULL", predicado, StringComparison.Ordinal);
            Assert.Contains("id_venta_origen IS NULL", predicado, StringComparison.Ordinal);

            // La única venta debida tras migrar es la nacida después.
            Assert.Equal(
                1L,
                await ScalarAsync(
                    cruda,
                    "SELECT count(*) FROM comprobantes_venta WHERE facturacion_decidida_en IS NULL AND id_venta_origen IS NULL"));
        }
        finally
        {
            await using var admin = new NpgsqlConnection(cadenaAdmin);
            await admin.OpenAsync();
            await using var dropear = admin.CreateCommand();
            dropear.CommandText = $"DROP DATABASE IF EXISTS \"{nombreBase}\" WITH (FORCE)";
            await dropear.ExecuteNonQueryAsync();
        }
    }
}
