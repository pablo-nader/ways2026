using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;
using Ways.Domain.Articulos;
using Ways.Domain.Caja;
using Ways.Domain.Catalogos;
using Ways.Domain.Clientes;
using Ways.Domain.Compras;
using Ways.Domain.CuentaCorriente;
using Ways.Domain.Fiscal;
using Ways.Domain.Gastos;
using Ways.Domain.Organizacion;
using Ways.Domain.Stock;
using Ways.Domain.Usuarios;
using Ways.Domain.Ventas;
using Ways.Infrastructure.Multitenancy;
using Ways.Infrastructure.Persistencia;

namespace Ways.IntegrationTests;

/// <summary>
/// La migración <c>DisponibilidadOnlineYVenta</c> sobre una base con datos: la suposición del dueño es que toda fila
/// existente queda disponible online y a la venta, y que solo las excepciones se destildan. La migración no escribe
/// datos (las columnas nacen en <c>true</c> por su default), así que NO lleva <c>SET LOCAL</c> y
/// <c>rls-migration-backfills</c> no aplica; lo que sí hay que probar es que el default alcance a lo que ya existía,
/// también a lo que estaba publicado en <c>false</c> (el default viejo) y a lo dado de baja, y que la policy nueva de
/// <c>articulos</c> deje verlo al modo público. Base AISLADA, migrada hasta la migración anterior, con las filas
/// sembradas por SQL crudo del esquema de ese momento.
/// </summary>
[Collection("Ways.IntegrationTests secuencial")]
public class DisponibilidadOnlineMigracionTests(WaysApiFixture fixture) : IClassFixture<WaysApiFixture>
{
    private const string MigracionAnterior = "20261011233811_CatalogoOnline";

    [Fact]
    public async Task LasFilasExistentesQuedanDisponiblesOnlineYALaVentaYElModoPublicoLasVeConLaPolicyNueva()
    {
        var nombreBase = $"ways_disponibilidad_{Guid.NewGuid():N}";
        var cadenaAdmin = new NpgsqlConnectionStringBuilder(fixture.OwnerConnectionString) { Database = "postgres" }.ConnectionString;
        var cadenaOwner = new NpgsqlConnectionStringBuilder(fixture.OwnerConnectionString) { Database = nombreBase }.ConnectionString;
        var cadenaApp = new NpgsqlConnectionStringBuilder(fixture.AppConnectionString) { Database = nombreBase }.ConnectionString;

        await using (var admin = new NpgsqlConnection(cadenaAdmin))
        {
            await admin.OpenAsync();
            await using var crear = admin.CreateCommand();
            crear.CommandText = $"CREATE DATABASE \"{nombreBase}\"";
            await crear.ExecuteNonQueryAsync();
        }

        try
        {
            await using (var migrando = new WaysDbContext(ConstruirOpciones(cadenaOwner), TenantActualFijo.Plataforma))
            {
                await migrando.Database.GetInfrastructure().GetRequiredService<IMigrator>().MigrateAsync(MigracionAnterior);
            }

            await using var owner = new NpgsqlConnection(cadenaOwner);
            await owner.OpenAsync();
            var (idTenant, vivos, eliminado) = await SembrarAsync(owner);

            // El estado de partida de verdad: dos publicados distintos, y el default viejo (false) en el resto.
            Assert.Equal(
                new[] { (vivos[0], true), (vivos[1], false), (vivos[2], false), (eliminado, false) },
                await LeerPublicadosAsync(owner, [vivos[0], vivos[1], vivos[2], eliminado]));

            await using (var migrando = new WaysDbContext(ConstruirOpciones(cadenaOwner), TenantActualFijo.Plataforma))
            {
                await migrando.Database.GetInfrastructure().GetRequiredService<IMigrator>().MigrateAsync();
            }

            await using (var concesion = owner.CreateCommand())
            {
                concesion.CommandText =
                    """
                    GRANT USAGE ON SCHEMA public TO ways_app;
                    GRANT SELECT, INSERT, UPDATE, DELETE ON ALL TABLES IN SCHEMA public TO ways_app;
                    """;
                await concesion.ExecuteNonQueryAsync();
            }

            // Toda fila, vigente o no, sale con las dos banderas en true.
            await using (var comando = owner.CreateCommand())
            {
                comando.CommandText =
                    "SELECT id_articulo, disponible_online, disponible_venta FROM articulos ORDER BY id_articulo";
                var filas = new List<(int, bool, bool)>();
                await using var lector = await comando.ExecuteReaderAsync();
                while (await lector.ReadAsync())
                {
                    filas.Add((lector.GetInt32(0), lector.GetBoolean(1), lector.GetBoolean(2)));
                }

                Assert.Equal(
                    new[] { vivos[0], vivos[1], vivos[2], eliminado }.Order().Select(id => (id, true, true)).ToArray(),
                    filas.ToArray());
            }

            // La columna vieja y su índice ya no existen; el índice nuevo y las dos policies públicas nuevas, sí.
            Assert.Equal(0L, await ContarAsync(owner, "SELECT count(*) FROM information_schema.columns WHERE table_name = 'articulos' AND column_name = 'publicado_web'"));
            Assert.Equal(1L, await ContarAsync(owner, "SELECT count(*) FROM pg_indexes WHERE indexname = 'ix_articulos_disponible_online'"));
            Assert.Equal(0L, await ContarAsync(owner, "SELECT count(*) FROM pg_indexes WHERE indexname = 'ix_articulos_publicado_web'"));
            Assert.Equal(
                4L,
                await ContarAsync(
                    owner,
                    "SELECT count(*) FROM pg_policies WHERE policyname IN " +
                    "('ofertas_publico_lectura', 'ofertas_listas_publico_lectura', 'combos_publico_lectura', 'combos_componentes_publico_lectura')"));

            // Modo público sobre ways_app: ve los tres vivos (antes solo veía el publicado) y no el dado de baja.
            await using var publica = new NpgsqlConnection(cadenaApp);
            await publica.OpenAsync();
            await using (var fijar = publica.CreateCommand())
            {
                fijar.CommandText =
                    "SELECT set_config('app.acceso', 'publico', false), set_config('app.tenant_publico', $1, false)";
                fijar.Parameters.Add(new NpgsqlParameter { Value = idTenant.ToString() });
                await fijar.ExecuteNonQueryAsync();
            }

            Assert.Equal(3L, await ContarAsync(publica, "SELECT count(*) FROM articulos"));
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

    private static async Task<long> ContarAsync(NpgsqlConnection conexion, string sql)
    {
        await using var comando = conexion.CreateCommand();
        comando.CommandText = sql;

        return (long)(await comando.ExecuteScalarAsync())!;
    }

    private static async Task<(int Id, bool Publicado)[]> LeerPublicadosAsync(NpgsqlConnection owner, int[] ids)
    {
        await using var comando = owner.CreateCommand();
        comando.CommandText =
            "SELECT id_articulo, publicado_web FROM articulos WHERE id_articulo = ANY($1) ORDER BY id_articulo";
        comando.Parameters.Add(new NpgsqlParameter { Value = ids });
        var filas = new List<(int, bool)>();
        await using var lector = await comando.ExecuteReaderAsync();
        while (await lector.ReadAsync())
        {
            filas.Add((lector.GetInt32(0), lector.GetBoolean(1)));
        }

        return [.. filas.OrderBy(f => ids.ToList().IndexOf(f.Item1))];
    }

    /// <summary>Un tenant con un área y una alícuota; tres artículos vivos (uno publicado y dos con el default viejo) y
    /// uno dado de baja. Todo por SQL crudo: el modelo de EF ya trae las columnas nuevas y esta base todavía no.</summary>
    private static async Task<(int IdTenant, int[] Vivos, int Eliminado)> SembrarAsync(NpgsqlConnection owner)
    {
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

        var idTenant = await EscalarAsync(
            "INSERT INTO tenants (nombre, estado, created_at, updated_at) " +
            "VALUES ('Tenant histórico', 'activo'::estado_tenant, now(), now()) RETURNING id_tenant");
        var idArea = await EscalarAsync(
            "INSERT INTO areas (id_tenant, nombre, orden, created_at, updated_at) " +
            "VALUES ($1, 'Área histórica', 1, now(), now()) RETURNING id_area",
            idTenant);
        var idAlicuota = await EscalarAsync(
            "INSERT INTO alicuotas_iva (nombre, porcentaje, created_at, updated_at) " +
            "VALUES ('21% histórica', 21, now(), now()) RETURNING id_alicuota_iva");

        async Task<int> ArticuloAsync(string codigo, bool publicado, bool eliminado = false) =>
            await EscalarAsync(
                "INSERT INTO articulos (id_tenant, codigo_interno, nombre, id_area, id_alicuota_iva, unidad_venta, " +
                "es_producto, publicado_web, created_at, updated_at, deleted_at) " +
                "VALUES ($1, $2, $2, $3, $4, 'unidad'::unidad_venta, true, $5, now(), now(), CASE WHEN $6 THEN now() END) " +
                "RETURNING id_articulo",
                idTenant, codigo, idArea, idAlicuota, publicado, eliminado);

        var publicado = await ArticuloAsync("HIST-1", publicado: true);
        var sinPublicarUno = await ArticuloAsync("HIST-2", publicado: false);
        var sinPublicarDos = await ArticuloAsync("HIST-3", publicado: false);
        var eliminado = await ArticuloAsync("HIST-4", publicado: false, eliminado: true);

        return (idTenant, [publicado, sinPublicarUno, sinPublicarDos], eliminado);
    }

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
                npgsql.MapEnum<EstadoOrdenCompra>("estado_orden_compra");
                npgsql.MapEnum<EstadoPresupuesto>("estado_presupuesto");
                npgsql.MapEnum<EstadoRemito>("estado_remito");
                npgsql.MapEnum<ResultadoFiscal>("resultado_fiscal");
                npgsql.MapEnum<AmbienteFiscal>("ambiente_fiscal");
                npgsql.MapEnum<MotivoDeFacturacion>("motivo_facturacion");
                npgsql.MapEnum<EstadoFacturacionDeVenta>("estado_facturacion_venta");
                npgsql.MapEnum<ModoPuntoVenta>("modo_punto_venta");
            })
            .ConfigureWarnings(w => w.Ignore(RelationalEventId.PendingModelChangesWarning))
            .Options;
}
