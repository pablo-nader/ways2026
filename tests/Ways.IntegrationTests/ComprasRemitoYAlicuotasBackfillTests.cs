using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Microsoft.EntityFrameworkCore.Migrations.Operations;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;
using Ways.Domain.Caja;
using Ways.Domain.Catalogos;
using Ways.Domain.Compras;
using Ways.Domain.CuentaCorriente;
using Ways.Domain.Gastos;
using Ways.Domain.Organizacion;
using Ways.Domain.Stock;
using Ways.Domain.Usuarios;
using Ways.Domain.Ventas;
using Ways.Domain.Articulos;
using Ways.Domain.Clientes;
using Ways.Infrastructure.Multitenancy;
using Ways.Infrastructure.Persistencia;
using Ways.Infrastructure.Persistencia.Migraciones;

namespace Ways.IntegrationTests;

/// <summary>
/// rls-migration-backfills: los bloques de datos de <c>ComprasRemitoYAlicuotas</c> corren sobre
/// <c>ways_app</c> (NOSUPERUSER NOBYPASSRLS) y solo alcanzan filas con el GUC de plataforma. El SQL
/// no está duplicado: se toma de las operaciones reales de la migración (<c>UpOperations</c>), así que
/// quitar el <c>SET LOCAL</c> de la migración pone esta prueba en rojo. Corre sobre una base aislada
/// migrada hasta HEAD donde se reproduce a mano el estado previo al backfill (comprobantes con
/// <c>discrimina_iva = false</c> y sin desglose).
/// </summary>
[Collection("Ways.IntegrationTests secuencial")]
public class ComprasRemitoYAlicuotasBackfillTests(WaysApiFixture fixture) : IClassFixture<WaysApiFixture>
{
    private const string Guc = "SET LOCAL app.acceso = 'plataforma';";

    private static IReadOnlyList<string> SqlDeLaMigracion() =>
        new ComprasRemitoYAlicuotas().UpOperations.OfType<SqlOperation>().Select(o => o.Sql).ToList();

    private static string BloqueQueContiene(string fragmento) =>
        Assert.Single(SqlDeLaMigracion(), s => s.Contains(fragmento, StringComparison.Ordinal));

    [Fact]
    public async Task ElBackfillDeDiscriminaIvaDelDesgloseYDeLosTiposSoloAlcanzaFilasEnModoPlataformaYEsIdempotente()
    {
        var sqlRegistra = BloqueQueContiene("UPDATE tipos_comprobante SET registra_libro_iva");
        var sqlTipoRemito = BloqueQueContiene("INSERT INTO tipos_comprobante");
        var sqlDiscrimina = BloqueQueContiene("UPDATE comprobantes_compra");
        var sqlDesglose = BloqueQueContiene("INSERT INTO alicuotas_comprobante_compra");

        // Cada bloque tiene que llevar el GUC adentro: sin él, la ruta de `dotnet ef database update`
        // (sin interceptor de tenant) toca cero filas y reporta éxito.
        Assert.All([sqlRegistra, sqlTipoRemito, sqlDiscrimina, sqlDesglose], sql => Assert.Contains(Guc, sql));

        var nombreBase = $"ways_remito_{Guid.NewGuid():N}";
        var cadenaAdmin = new NpgsqlConnectionStringBuilder(fixture.OwnerConnectionString) { Database = "postgres" }.ConnectionString;
        var cadenaOwner = new NpgsqlConnectionStringBuilder(fixture.OwnerConnectionString) { Database = nombreBase }.ConnectionString;

        await using (var admin = new NpgsqlConnection(cadenaAdmin))
        {
            await admin.OpenAsync();
            await using var crear = admin.CreateCommand();
            crear.CommandText = $"CREATE DATABASE \"{nombreBase}\"";
            await crear.ExecuteNonQueryAsync();
        }

        try
        {
            var opciones = new DbContextOptionsBuilder<WaysDbContext>()
                .UseNpgsql(cadenaOwner, npgsql =>
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
                    npgsql.MapEnum<TipoMovimientoCcProveedor>("tipo_movimiento_cc_proveedor");
                    npgsql.MapEnum<EstadoOrdenCompra>("estado_orden_compra");
                    npgsql.MapEnum<EstadoPresupuesto>("estado_presupuesto");
                    npgsql.MapEnum<EstadoRemito>("estado_remito");
                    npgsql.MapEnum<Ways.Domain.Fiscal.ResultadoFiscal>("resultado_fiscal");
                    npgsql.MapEnum<Ways.Domain.Fiscal.AmbienteFiscal>("ambiente_fiscal");
                    npgsql.MapEnum<ModoPuntoVenta>("modo_punto_venta");
                    npgsql.MapEnum<OrigenFondosGasto>("origen_fondos_gasto");
                })
                .Options;

            await using (var migrando = new WaysDbContext(opciones, TenantActualFijo.Plataforma))
            {
                await migrando.Database.GetInfrastructure().GetRequiredService<IMigrator>().MigrateAsync();
            }

            // ways_app existe a nivel cluster pero los GRANTs son por base.
            await using (var owner = new NpgsqlConnection(cadenaOwner))
            {
                await owner.OpenAsync();
                await using var comando = owner.CreateCommand();
                comando.CommandText =
                    """
                    GRANT USAGE ON SCHEMA public TO ways_app;
                    GRANT SELECT, INSERT, UPDATE, DELETE ON ALL TABLES IN SCHEMA public TO ways_app;
                    """;
                await comando.ExecuteNonQueryAsync();
            }

            int idCompraFacturaA1, idCompraFacturaA2, idCompraFacturaB;

            // Estado previo al backfill, sembrado como dueño: dos tenants con una factura A (el
            // flag de la fila todavía en false), uno de ellos con tres ítems en dos alícuotas, y
            // una factura B. Los tipos se siembran a mano: la base no pasó por el seeder.
            await using (var owner = new NpgsqlConnection(cadenaOwner))
            {
                await owner.OpenAsync();

                async Task<int> EscalarAsync(string sql, params object?[] parametros)
                {
                    await using var comando = owner.CreateCommand();
                    comando.CommandText = sql;
                    foreach (var valor in parametros)
                    {
                        comando.Parameters.Add(new NpgsqlParameter { Value = valor ?? DBNull.Value });
                    }

                    return (int)(await comando.ExecuteScalarAsync())!;
                }

                await EscalarAsync(
                    "INSERT INTO roles (id_rol, nombre, created_at, updated_at) VALUES ($1, 'admin', now(), now()) RETURNING id_rol",
                    (int)RolConocido.Admin);
                var idCondicion = await EscalarAsync(
                    "INSERT INTO condiciones_fiscales (codigo, nombre, created_at, updated_at) " +
                    "VALUES ('RI', 'Responsable inscripto', now(), now()) RETURNING id_condicion_fiscal");
                var idAlicuota21 = await EscalarAsync(
                    "INSERT INTO alicuotas_iva (nombre, porcentaje, created_at, updated_at) " +
                    "VALUES ('21%', 21, now(), now()) RETURNING id_alicuota_iva");
                var idAlicuota105 = await EscalarAsync(
                    "INSERT INTO alicuotas_iva (nombre, porcentaje, created_at, updated_at) " +
                    "VALUES ('10.5%', 10.5, now(), now()) RETURNING id_alicuota_iva");

                async Task<int> TipoAsync(string codigo, string letra, bool discrimina) =>
                    await EscalarAsync(
                        "INSERT INTO tipos_comprobante (clase, codigo, nombre, letra, signo, discrimina_iva, es_fiscal, " +
                        "afecta_stock, activo, created_at, updated_at) " +
                        "VALUES ('compra', $1, $1, $2, 1, $3, false, true, true, now(), now()) RETURNING id_tipo_comprobante",
                        codigo, letra, discrimina);

                var idTipoFa = await TipoAsync("C-FA", "A", true);
                var idTipoFb = await TipoAsync("C-FB", "B", false);

                async Task<(int IdTenant, int IdProveedor, int IdPuntoVenta, int IdUsuario)> TenantAsync(string nombre)
                {
                    var idTenant = await EscalarAsync(
                        "INSERT INTO tenants (nombre, estado, created_at, updated_at) " +
                        "VALUES ($1, 'activo'::estado_tenant, now(), now()) RETURNING id_tenant", nombre);
                    var idEmpresa = await EscalarAsync(
                        "INSERT INTO empresas (id_tenant, razon_social, created_at, updated_at) " +
                        "VALUES ($1, $2, now(), now()) RETURNING id_empresa", idTenant, nombre);
                    var idPuntoVenta = await EscalarAsync(
                        "INSERT INTO puntos_venta (id_tenant, id_empresa, nombre, modo, created_at, updated_at) " +
                        "VALUES ($1, $2, $3, 'web'::modo_punto_venta, now(), now()) RETURNING id_punto_venta",
                        idTenant, idEmpresa, nombre);
                    var idUsuario = await EscalarAsync(
                        "INSERT INTO usuarios (id_tenant, usuario, mail, id_rol, password_hash, password_algoritmo, " +
                        "password_actualizado_el, created_at, updated_at) " +
                        "VALUES ($1, 'admin', $2, $3, 'hash', 'test', now(), now(), now()) RETURNING id_usuario",
                        idTenant, $"{nombre}@ways.test", (int)RolConocido.Admin);
                    var idProveedor = await EscalarAsync(
                        "INSERT INTO proveedores (id_tenant, razon_social, id_condicion_fiscal, created_at, updated_at) " +
                        "VALUES ($1, $2, $3, now(), now()) RETURNING id_proveedor", idTenant, nombre, idCondicion);
                    return (idTenant, idProveedor, idPuntoVenta, idUsuario);
                }

                async Task<int> CompraAsync(
                    (int IdTenant, int IdProveedor, int IdPuntoVenta, int IdUsuario) t, int idTipo, decimal subtotal,
                    decimal? ivaTotal, params (int IdAlicuota, decimal Porcentaje, decimal Total)[] items)
                {
                    var idCompra = await EscalarAsync(
                        "INSERT INTO comprobantes_compra (id_tenant, id_proveedor, id_tipo_comprobante, id_punto_venta, " +
                        "id_empleado, subtotal, descuento_total, iva_total, total, estado, discrimina_iva, created_at, updated_at) " +
                        "VALUES ($1, $2, $3, $4, $5, $6, 0, $7, $8, 'borrador'::estado_compra, false, now(), now()) " +
                        "RETURNING id_comprobante_compra",
                        t.IdTenant, t.IdProveedor, idTipo, t.IdPuntoVenta, t.IdUsuario, subtotal, ivaTotal,
                        subtotal + (ivaTotal ?? 0m));

                    var orden = 1;
                    foreach (var (idAlicuota, porcentaje, total) in items)
                    {
                        await EscalarAsync(
                            "INSERT INTO items_comprobante_compra (id_tenant, id_comprobante_compra, orden, id_articulo, " +
                            "descripcion, cantidad, costo_unitario, descuento, id_alicuota_iva, porcentaje_iva, total, " +
                            "actualiza_costo, created_at, updated_at) " +
                            "VALUES ($1, $2, $3, NULL, 'concepto', 1, $4, 0, $5, $6, $4, false, now(), now()) RETURNING id_item",
                            t.IdTenant, idCompra, orden++, total, idAlicuota, porcentaje);
                    }

                    return idCompra;
                }

                var tenantUno = await TenantAsync("remito-uno");
                var tenantDos = await TenantAsync("remito-dos");

                // IVA del encabezado = suma del redondeo por línea: 0.50x3 al 21% da 0.11 por línea.
                idCompraFacturaA1 = await CompraAsync(
                    tenantUno, idTipoFa, 101.5m, 10.83m,
                    (idAlicuota21, 21m, 0.50m), (idAlicuota21, 21m, 0.50m), (idAlicuota21, 21m, 0.50m),
                    (idAlicuota105, 10.5m, 100m));
                idCompraFacturaA2 = await CompraAsync(tenantDos, idTipoFa, 200m, 42m, (idAlicuota21, 21m, 200m));
                idCompraFacturaB = await CompraAsync(tenantDos, idTipoFb, 300m, null, (idAlicuota21, 21m, 300m));
            }

            var cadenaApp = new NpgsqlConnectionStringBuilder(fixture.AppConnectionString) { Database = nombreBase }.ConnectionString;

            async Task<int> EjecutarComoAppAsync(string sql)
            {
                await using var cruda = new NpgsqlConnection(cadenaApp);
                await cruda.OpenAsync();
                await using var comando = cruda.CreateCommand();
                comando.CommandText = sql;
                return await comando.ExecuteNonQueryAsync();
            }

            string SinGuc(string sql) => sql.Replace(Guc, string.Empty, StringComparison.Ordinal);

            // (a) SIN el GUC: ways_app no ve ninguna fila de ningún tenant, los dos bloques de
            // tabla de tenant afectan CERO filas y no revientan.
            Assert.Equal(0, await EjecutarComoAppAsync(SinGuc(sqlDiscrimina)));
            Assert.Equal(0, await EjecutarComoAppAsync(SinGuc(sqlDesglose)));

            // (b) CON el GUC, tal cual la migración. Los tipos primero: los siembra el dueño sin
            // `registra_libro_iva` (solo C-FA y C-FB), y el bloque de remito necesita que el
            // catálogo no esté vacío.
            Assert.Equal(2, await EjecutarComoAppAsync(sqlRegistra));
            Assert.Equal(1, await EjecutarComoAppAsync(sqlTipoRemito));
            Assert.Equal(2, await EjecutarComoAppAsync(sqlDiscrimina));

            // (c) El desglose parte de comprobantes que ya discriminan: dos alícuotas del primero
            // y una del segundo. La factura B no genera filas.
            Assert.Equal(3, await EjecutarComoAppAsync(sqlDesglose));

            await using (var verificacion = new NpgsqlConnection(cadenaOwner))
            {
                await verificacion.OpenAsync();

                async Task<T> LeerAsync<T>(string sql, params object[] parametros)
                {
                    await using var comando = verificacion.CreateCommand();
                    comando.CommandText = sql;
                    foreach (var valor in parametros)
                    {
                        comando.Parameters.Add(new NpgsqlParameter { Value = valor });
                    }

                    return (T)(await comando.ExecuteScalarAsync())!;
                }

                Assert.True(await LeerAsync<bool>("SELECT discrimina_iva FROM comprobantes_compra WHERE id_comprobante_compra = $1", idCompraFacturaA1));
                Assert.True(await LeerAsync<bool>("SELECT discrimina_iva FROM comprobantes_compra WHERE id_comprobante_compra = $1", idCompraFacturaA2));
                Assert.False(await LeerAsync<bool>("SELECT discrimina_iva FROM comprobantes_compra WHERE id_comprobante_compra = $1", idCompraFacturaB));

                Assert.Equal(2L, await LeerAsync<long>("SELECT count(*) FROM tipos_comprobante WHERE registra_libro_iva"));
                Assert.False(await LeerAsync<bool>("SELECT registra_libro_iva FROM tipos_comprobante WHERE codigo = 'C-RM'"));
                Assert.False(await LeerAsync<bool>("SELECT discrimina_iva FROM tipos_comprobante WHERE codigo = 'C-RM'"));

                Assert.Equal(2L, await LeerAsync<long>("SELECT count(*) FROM alicuotas_comprobante_compra WHERE id_comprobante_compra = $1", idCompraFacturaA1));
                Assert.Equal(0L, await LeerAsync<long>("SELECT count(*) FROM alicuotas_comprobante_compra WHERE id_comprobante_compra = $1", idCompraFacturaB));

                // La suma del desglose reconstruye el encabezado: IVA 3 x 0.11 + 10.50 = 10.83 y neto
                // 101.50, con el mismo redondeo por línea con el que se había calculado.
                Assert.Equal(
                    await LeerAsync<decimal>("SELECT iva_total FROM comprobantes_compra WHERE id_comprobante_compra = $1", idCompraFacturaA1),
                    await LeerAsync<decimal>("SELECT sum(iva) FROM alicuotas_comprobante_compra WHERE id_comprobante_compra = $1", idCompraFacturaA1));
                Assert.Equal(
                    await LeerAsync<decimal>("SELECT subtotal FROM comprobantes_compra WHERE id_comprobante_compra = $1", idCompraFacturaA1),
                    await LeerAsync<decimal>("SELECT sum(neto) FROM alicuotas_comprobante_compra WHERE id_comprobante_compra = $1", idCompraFacturaA1));
            }

            // (d) Idempotencia: reejecutar con el GUC no encuentra nada que tocar.
            Assert.Equal(0, await EjecutarComoAppAsync(sqlRegistra));
            Assert.Equal(0, await EjecutarComoAppAsync(sqlTipoRemito));
            Assert.Equal(0, await EjecutarComoAppAsync(sqlDiscrimina));
            Assert.Equal(0, await EjecutarComoAppAsync(sqlDesglose));
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
