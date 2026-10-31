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
using Ways.Domain.Pagos;
using Ways.Domain.Organizacion;
using Ways.Domain.Stock;
using Ways.Domain.Usuarios;
using Ways.Domain.Ventas;
using Ways.Infrastructure.Multitenancy;
using Ways.Infrastructure.Persistencia;
using Migracion = Ways.Infrastructure.Persistencia.Migraciones.ImpuestoInternoDelArticuloEsUnImporte;

namespace Ways.IntegrationTests;

/// <summary>
/// La migración <c>ImpuestoInternoDelArticuloEsUnImporte</c>: el porcentaje de impuestos internos del artículo pasa a
/// ser un importe. Se convierte solo donde el porcentaje se aplicaba (costo de lista sin IVA y porcentaje distinto de
/// cero); el resto queda en 0. El backfill escribe <c>articulos</c>, que corre bajo FORCE ROW LEVEL SECURITY, así que
/// se prueba también sobre <c>ways_app</c> (skill <c>rls-migration-backfills</c>).
/// </summary>
[Collection("Ways.IntegrationTests secuencial")]
public class ImpuestoInternoDelArticuloMigracionTests(WaysApiFixture fixture) : IClassFixture<WaysApiFixture>
{
    private const string MigracionAnterior = "20261028194859_QuitarMedioDeLaCabeceraDelPago";
    private const string RolApp = "ways_app";
    private const string PasswordApp = "ways_app_password";

    [Fact]
    public async Task ElPorcentajeSeConvierteEnImporteSoloDondeSeAplicabaYLaColumnaViejaDesaparece()
    {
        await using var entorno = await CrearEntornoAsync();
        await entorno.MigrarAsync(MigracionAnterior);
        await SembrarAsync(entorno.Owner);

        await entorno.MigrarAsync(null);

        Assert.Equal(
            new Dictionary<string, decimal>
            {
                ["SIN-IVA-5"] = 5m,
                ["SIN-IVA-REDONDEO"] = 3.35m,
                ["CON-IVA"] = 0m,
                ["SIN-COSTO"] = 0m,
                ["SIN-PORCENTAJE"] = 0m,
                ["BAJA"] = 5m,
                ["OTRO-TENANT"] = 10m
            },
            await LeerImportesAsync(entorno.Owner));
        Assert.Equal(
            0L,
            await ContarAsync(
                entorno.Owner,
                "SELECT count(*) FROM information_schema.columns WHERE table_name = 'articulos' AND column_name = 'porcentaje_impuesto_interno'"));
    }

    [Fact]
    public async Task ElBackfillAlcanzaTodosLosTenantsBajoRlsRealSoloGraciasAlSetLocalDePlataforma()
    {
        await using var entorno = await CrearEntornoAsync();
        await entorno.MigrarAsync(MigracionAnterior);
        await using (var preparacion = entorno.Owner.CreateCommand())
        {
            preparacion.CommandText =
                $"""
                ALTER TABLE articulos ADD COLUMN impuesto_interno numeric(14,2) NOT NULL DEFAULT 0;
                GRANT USAGE ON SCHEMA public TO {RolApp};
                GRANT SELECT, INSERT, UPDATE, DELETE ON ALL TABLES IN SCHEMA public TO {RolApp};
                """;
            await preparacion.ExecuteNonQueryAsync();
        }

        await SembrarAsync(entorno.Owner);

        await using (var comoWaysApp = new NpgsqlConnection(entorno.CadenaWaysApp))
        {
            await comoWaysApp.OpenAsync();
            await using var comando = comoWaysApp.CreateCommand();
            comando.CommandText = Migracion.SqlDelBackfill;
            await comando.ExecuteNonQueryAsync();
        }

        var importes = await LeerImportesAsync(entorno.Owner);
        Assert.Equal(5m, importes["SIN-IVA-5"]);
        Assert.Equal(10m, importes["OTRO-TENANT"]);
        Assert.Equal(0m, importes["CON-IVA"]);
    }

    private async Task<Entorno> CrearEntornoAsync()
    {
        var nombreBase = $"ways_impuesto_interno_{Guid.NewGuid():N}";
        var cadenaAdmin = new NpgsqlConnectionStringBuilder(fixture.OwnerConnectionString) { Database = "postgres" }.ConnectionString;
        var cadenaOwner = new NpgsqlConnectionStringBuilder(fixture.OwnerConnectionString) { Database = nombreBase }.ConnectionString;
        var cadenaWaysApp = new NpgsqlConnectionStringBuilder(fixture.OwnerConnectionString)
        {
            Database = nombreBase, Username = RolApp, Password = PasswordApp
        }.ConnectionString;

        await using (var admin = new NpgsqlConnection(cadenaAdmin))
        {
            await admin.OpenAsync();
            await using var crear = admin.CreateCommand();
            crear.CommandText = $"CREATE DATABASE \"{nombreBase}\"";
            await crear.ExecuteNonQueryAsync();
        }

        var owner = new NpgsqlConnection(cadenaOwner);
        await owner.OpenAsync();

        return new Entorno(cadenaAdmin, nombreBase, cadenaOwner, cadenaWaysApp, owner);
    }

    private sealed class Entorno(string cadenaAdmin, string nombreBase, string cadenaOwner, string cadenaWaysApp, NpgsqlConnection owner)
        : IAsyncDisposable
    {
        public NpgsqlConnection Owner { get; } = owner;
        public string CadenaWaysApp { get; } = cadenaWaysApp;

        public async Task MigrarAsync(string? destino)
        {
            await using var db = new WaysDbContext(ConstruirOpciones(cadenaOwner), TenantActualFijo.Plataforma);
            var migrador = db.Database.GetInfrastructure().GetRequiredService<IMigrator>();
            if (destino is null)
            {
                await migrador.MigrateAsync();
            }
            else
            {
                await migrador.MigrateAsync(destino);
            }
        }

        public async ValueTask DisposeAsync()
        {
            await Owner.DisposeAsync();
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

    private static async Task<Dictionary<string, decimal>> LeerImportesAsync(NpgsqlConnection owner)
    {
        await using var comando = owner.CreateCommand();
        comando.CommandText = "SELECT codigo_interno::text, impuesto_interno FROM articulos";
        var filas = new Dictionary<string, decimal>();
        await using var lector = await comando.ExecuteReaderAsync();
        while (await lector.ReadAsync())
        {
            filas[lector.GetString(0)] = lector.GetDecimal(1);
        }

        return filas;
    }

    /// <summary>Dos tenants con artículos con porcentaje: sin IVA (se convierte), sin IVA con redondeo, con IVA (queda
    /// en 0), sin costo de lista, sin porcentaje y uno dado de baja. Por SQL crudo: el esquema es el anterior.</summary>
    private static async Task SembrarAsync(NpgsqlConnection owner)
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

        var idAlicuota = await EscalarAsync(
            "INSERT INTO alicuotas_iva (nombre, porcentaje, created_at, updated_at) " +
            "VALUES ('21% histórica', 21, now(), now()) RETURNING id_alicuota_iva");

        async Task<(int IdTenant, int IdArea)> TenantAsync(string nombre)
        {
            var idTenant = await EscalarAsync(
                "INSERT INTO tenants (nombre, estado, created_at, updated_at) " +
                "VALUES ($1, 'activo'::estado_tenant, now(), now()) RETURNING id_tenant",
                nombre);
            var idArea = await EscalarAsync(
                "INSERT INTO areas (id_tenant, nombre, orden, created_at, updated_at) " +
                "VALUES ($1, 'Área histórica', 1, now(), now()) RETURNING id_area",
                idTenant);

            return (idTenant, idArea);
        }

        async Task ArticuloAsync(
            (int IdTenant, int IdArea) t, string codigo, decimal? costoLista, bool incluyeIva, decimal porcentaje, bool baja = false) =>
            await EscalarAsync(
                "INSERT INTO articulos (id_tenant, codigo_interno, nombre, id_area, id_alicuota_iva, unidad_venta, es_producto, " +
                "costo_lista, costo_lista_incluye_iva, porcentaje_impuesto_interno, created_at, updated_at, deleted_at) " +
                "VALUES ($1, $2, $2, $3, $4, 'unidad'::unidad_venta, true, $5, $6, $7, now(), now(), CASE WHEN $8 THEN now() END) " +
                "RETURNING id_articulo",
                t.IdTenant, codigo, t.IdArea, idAlicuota, costoLista, incluyeIva, porcentaje, baja);

        var primero = await TenantAsync("Tenant histórico 1");
        await ArticuloAsync(primero, "SIN-IVA-5", 100m, false, 5m);
        await ArticuloAsync(primero, "SIN-IVA-REDONDEO", 33.5m, false, 10m);
        await ArticuloAsync(primero, "CON-IVA", 121m, true, 5m);
        await ArticuloAsync(primero, "SIN-COSTO", null, false, 5m);
        await ArticuloAsync(primero, "SIN-PORCENTAJE", 100m, false, 0m);
        await ArticuloAsync(primero, "BAJA", 100m, false, 5m, baja: true);

        var segundo = await TenantAsync("Tenant histórico 2");
        await ArticuloAsync(segundo, "OTRO-TENANT", 200m, false, 5m);
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
                npgsql.MapEnum<CategoriaPago>("categoria_pago");
                npgsql.MapEnum<EstadoCompra>("estado_compra");
                npgsql.MapEnum<OrigenCompra>("origen_compra");
                npgsql.MapEnum<Ways.Domain.Ventas.OrigenAplicacionCobro>("origen_aplicacion_cobro");
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
