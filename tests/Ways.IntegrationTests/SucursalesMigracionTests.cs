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
using Ways.Domain.Organizacion;
using Ways.Domain.Pagos;
using Ways.Domain.Plantel;
using Ways.Domain.Stock;
using Ways.Domain.Usuarios;
using Ways.Domain.Ventas;
using Ways.Infrastructure.Multitenancy;
using Ways.Infrastructure.Persistencia;
using Ways.Infrastructure.Persistencia.Migraciones;

namespace Ways.IntegrationTests;

/// <summary>
/// La migración <c>Sucursales</c> sobre una base con datos: cada punto de venta existente (vivo o dado de baja) recibe su
/// propia sucursal con su tenant, empresa, nombre y marcas de tiempo, y la columna queda NOT NULL con su FK. Base AISLADA
/// migrada hasta la migración inmediatamente anterior, con las filas sembradas por SQL crudo.
/// </summary>
[Collection("Ways.IntegrationTests secuencial")]
public class SucursalesMigracionTests(WaysApiFixture fixture) : IClassFixture<WaysApiFixture>
{
    private const string MigracionAnterior = "20261030202550_AplicarCobrosHistoricosPorAntiguedad";
    private const string MigracionPropia = "20261031015930_Sucursales";

    private sealed record Sembrado(int IdPvCentro, int IdPvCerrado, int IdPvCentroRepetido, int IdPvOtroTenant);

    [Fact]
    public async Task UpCreaUnaSucursalPorPuntoDeVentaYDownLaRetira()
    {
        await using var entorno = await EntornoAislado.CrearAsync(fixture);

        await using var owner = new NpgsqlConnection(entorno.CadenaOwner);
        await owner.OpenAsync();
        var sembrado = await SembrarAsync(owner);

        await entorno.MigrarAsync(MigracionPropia);

        await VerificarElBackfillAsync(owner, sembrado);
        Assert.Equal(
            "NO", await EscalarAsync<string>(
                owner,
                "SELECT is_nullable FROM information_schema.columns " +
                "WHERE table_name = 'puntos_venta' AND column_name = 'id_sucursal'"));
        Assert.Equal(
            2L, await EscalarAsync<long>(
                owner,
                "SELECT count(*) FROM pg_constraint WHERE conname IN ('fk_puntos_venta_sucursal', 'fk_sucursales_empresa')"));
        Assert.Equal(
            1L, await EscalarAsync<long>(owner, "SELECT count(*) FROM pg_policies WHERE tablename = 'sucursales'"));

        await entorno.MigrarAsync(MigracionAnterior);

        Assert.Null(await EscalarAsync<string?>(owner, "SELECT to_regclass('public.sucursales')::text"));
        Assert.Equal(
            0L, await EscalarAsync<long>(
                owner,
                "SELECT count(*) FROM information_schema.columns WHERE table_name = 'puntos_venta' AND column_name = 'id_sucursal'"));

        // Subir de nuevo reconstruye lo mismo: la migración es repetible.
        await entorno.MigrarAsync(MigracionPropia);
        await VerificarElBackfillAsync(owner, sembrado);
    }

    /// <summary>
    /// LA CLÁUSULA (rls-migration-backfills): el <c>SET LOCAL app.acceso = 'plataforma'</c> dentro del mismo bloque que
    /// inserta las sucursales. Las tablas corren bajo FORCE ROW LEVEL SECURITY y el rol de aplicación no tiene BYPASSRLS:
    /// sin el GUC el bloque no ve ningún punto de venta y reporta éxito. Se corre la constante REAL de la migración sobre
    /// <c>ways_app</c> con y sin su primera línea, después de devolver las tablas al estado previo al backfill.
    /// </summary>
    [Fact]
    public async Task ElBackfillSoloAlcanzaLasFilasConElGucDePlataformaSobreWaysApp()
    {
        await using var entorno = await EntornoAislado.CrearAsync(fixture);

        Sembrado sembrado;
        await using (var owner = new NpgsqlConnection(entorno.CadenaOwner))
        {
            await owner.OpenAsync();
            sembrado = await SembrarAsync(owner);
            await entorno.MigrarAsync(MigracionPropia);

            await using var preparacion = owner.CreateCommand();
            preparacion.CommandText =
                """
                ALTER TABLE puntos_venta DROP CONSTRAINT fk_puntos_venta_sucursal;
                ALTER TABLE puntos_venta ALTER COLUMN id_sucursal DROP NOT NULL;
                UPDATE puntos_venta SET id_sucursal = NULL;
                DELETE FROM sucursales;
                GRANT USAGE ON SCHEMA public TO ways_app;
                GRANT SELECT, INSERT, UPDATE, DELETE ON ALL TABLES IN SCHEMA public TO ways_app;
                """;
            await preparacion.ExecuteNonQueryAsync();
        }

        var bloque = Sucursales.SqlDelBackfill;
        Assert.StartsWith("SET LOCAL app.acceso = 'plataforma';", bloque, StringComparison.Ordinal);
        var sinElSetLocal = string.Join(
            '\n', bloque.Split('\n').Where(l => !l.TrimStart().StartsWith("SET LOCAL", StringComparison.Ordinal)));

        // (a) SIN el SET LOCAL: ways_app sin GUC no ve ningún punto de venta, y no hay error.
        await EjecutarComoWaysAppAsync(entorno, sinElSetLocal);
        await using (var owner = new NpgsqlConnection(entorno.CadenaOwner))
        {
            await owner.OpenAsync();
            Assert.Equal(0L, await EscalarAsync<long>(owner, "SELECT count(*) FROM sucursales"));
            Assert.Equal(4L, await EscalarAsync<long>(owner, "SELECT count(*) FROM puntos_venta WHERE id_sucursal IS NULL"));
        }

        // (b) CON el bloque tal cual: una sucursal por cada uno de los cuatro puntos de venta.
        await EjecutarComoWaysAppAsync(entorno, bloque);
        await using (var owner = new NpgsqlConnection(entorno.CadenaOwner))
        {
            await owner.OpenAsync();
            await VerificarElBackfillAsync(owner, sembrado);
        }

        // (c) Idempotencia: otra corrida ya no encuentra puntos de venta sin sucursal.
        await EjecutarComoWaysAppAsync(entorno, bloque);
        await using (var owner = new NpgsqlConnection(entorno.CadenaOwner))
        {
            await owner.OpenAsync();
            Assert.Equal(4L, await EscalarAsync<long>(owner, "SELECT count(*) FROM sucursales"));
        }
    }

    private static async Task EjecutarComoWaysAppAsync(EntornoAislado entorno, string sql)
    {
        await using var app = new NpgsqlConnection(entorno.CadenaApp);
        await app.OpenAsync();
        await using var comando = app.CreateCommand();
        comando.CommandText = sql;
        await comando.ExecuteNonQueryAsync();
    }

    /// <summary>Cuatro puntos de venta, cuatro sucursales: cada una con el tenant, la empresa, el nombre y las marcas de
    /// su punto de venta, el dado de baja con su sucursal también dada de baja, y ninguna compartida.</summary>
    private static async Task VerificarElBackfillAsync(NpgsqlConnection owner, Sembrado s)
    {
        Assert.Equal(4L, await EscalarAsync<long>(owner, "SELECT count(*) FROM sucursales"));
        Assert.Equal(4L, await EscalarAsync<long>(owner, "SELECT count(DISTINCT id_sucursal) FROM puntos_venta"));
        Assert.Equal(
            4L, await EscalarAsync<long>(
                owner,
                "SELECT count(*) FROM puntos_venta p JOIN sucursales s " +
                "ON s.id_sucursal = p.id_sucursal AND s.id_tenant = p.id_tenant AND s.id_empresa = p.id_empresa " +
                "AND s.nombre = p.nombre AND s.created_at = p.created_at AND s.updated_at = p.updated_at " +
                "AND s.deleted_at IS NOT DISTINCT FROM p.deleted_at"));
        Assert.Equal(
            1L, await EscalarAsync<long>(
                owner,
                "SELECT count(*) FROM puntos_venta p JOIN sucursales s ON s.id_sucursal = p.id_sucursal " +
                "WHERE p.id_punto_venta = $1 AND s.deleted_at IS NOT NULL",
                s.IdPvCerrado));
        Assert.Equal(
            0L, await EscalarAsync<long>(
                owner,
                "SELECT count(*) FROM puntos_venta p JOIN sucursales s ON s.id_sucursal = p.id_sucursal " +
                "WHERE p.id_punto_venta IN ($1, $2) AND s.deleted_at IS NOT NULL",
                s.IdPvCentro, s.IdPvOtroTenant));
        Assert.NotEqual(
            await EscalarAsync<int>(owner, "SELECT id_sucursal FROM puntos_venta WHERE id_punto_venta = $1", s.IdPvCentro),
            await EscalarAsync<int>(owner, "SELECT id_sucursal FROM puntos_venta WHERE id_punto_venta = $1", s.IdPvCentroRepetido));
    }

    private static async Task<Sembrado> SembrarAsync(NpgsqlConnection owner)
    {
        var idTenantA = await EscalarAsync<int>(
            owner,
            "INSERT INTO tenants (nombre, estado, created_at, updated_at) " +
            "VALUES ('Tenant A', 'activo'::estado_tenant, now(), now()) RETURNING id_tenant");
        var idTenantB = await EscalarAsync<int>(
            owner,
            "INSERT INTO tenants (nombre, estado, created_at, updated_at) " +
            "VALUES ('Tenant B', 'activo'::estado_tenant, now(), now()) RETURNING id_tenant");
        var idEmpresaA = await EscalarAsync<int>(
            owner,
            "INSERT INTO empresas (id_tenant, razon_social, created_at, updated_at) " +
            "VALUES ($1, 'Empresa A', now(), now()) RETURNING id_empresa",
            idTenantA);
        var idEmpresaB = await EscalarAsync<int>(
            owner,
            "INSERT INTO empresas (id_tenant, razon_social, created_at, updated_at) " +
            "VALUES ($1, 'Empresa B', now(), now()) RETURNING id_empresa",
            idTenantB);

        var t0 = new DateTimeOffset(2026, 3, 1, 12, 0, 0, TimeSpan.Zero);
        var centro = await SembrarPuntoVentaAsync(owner, idTenantA, idEmpresaA, "Centro", t0, null);
        var cerrado = await SembrarPuntoVentaAsync(owner, idTenantA, idEmpresaA, "Cerrado", t0.AddDays(1), t0.AddDays(30));
        var centroRepetido = await SembrarPuntoVentaAsync(owner, idTenantA, idEmpresaA, "Centro", t0.AddDays(2), null);
        var otroTenant = await SembrarPuntoVentaAsync(owner, idTenantB, idEmpresaB, "Otro", t0.AddDays(3), null);

        return new Sembrado(centro, cerrado, centroRepetido, otroTenant);
    }

    private static Task<int> SembrarPuntoVentaAsync(
        NpgsqlConnection owner, int idTenant, int idEmpresa, string nombre, DateTimeOffset alta, DateTimeOffset? baja) =>
        EscalarAsync<int>(
            owner,
            "INSERT INTO puntos_venta (id_tenant, id_empresa, nombre, modo, created_at, updated_at, deleted_at) " +
            "VALUES ($1, $2, $3, 'web'::modo_punto_venta, $4, $4, $5) RETURNING id_punto_venta",
            idTenant, idEmpresa, nombre, alta, baja);

    private static async Task<T> EscalarAsync<T>(NpgsqlConnection conexion, string sql, params object?[] parametros)
    {
        await using var comando = conexion.CreateCommand();
        comando.CommandText = sql;
        foreach (var valor in parametros)
        {
            comando.Parameters.Add(new NpgsqlParameter { Value = valor ?? DBNull.Value });
        }

        var resultado = await comando.ExecuteScalarAsync();
        return resultado is null or DBNull ? default! : (T)resultado;
    }

    /// <summary>Una base propia, migrada hasta la migración anterior, que se descarta al final.</summary>
    private sealed class EntornoAislado : IAsyncDisposable
    {
        private readonly string _cadenaAdmin;
        private readonly string _nombreBase;

        private EntornoAislado(string cadenaAdmin, string nombreBase, string cadenaOwner, string cadenaApp)
        {
            _cadenaAdmin = cadenaAdmin;
            _nombreBase = nombreBase;
            CadenaOwner = cadenaOwner;
            CadenaApp = cadenaApp;
        }

        public string CadenaOwner { get; }

        public string CadenaApp { get; }

        public static async Task<EntornoAislado> CrearAsync(WaysApiFixture fixture)
        {
            var nombreBase = $"ways_sucursales_{Guid.NewGuid():N}";
            var cadenaAdmin = new NpgsqlConnectionStringBuilder(fixture.OwnerConnectionString) { Database = "postgres" }
                .ConnectionString;
            var entorno = new EntornoAislado(
                cadenaAdmin,
                nombreBase,
                new NpgsqlConnectionStringBuilder(fixture.OwnerConnectionString) { Database = nombreBase }.ConnectionString,
                new NpgsqlConnectionStringBuilder(fixture.AppConnectionString) { Database = nombreBase }.ConnectionString);

            await using (var admin = new NpgsqlConnection(cadenaAdmin))
            {
                await admin.OpenAsync();
                await using var crear = admin.CreateCommand();
                crear.CommandText = $"CREATE DATABASE \"{nombreBase}\"";
                await crear.ExecuteNonQueryAsync();
            }

            await entorno.MigrarAsync(MigracionAnterior);
            return entorno;
        }

        /// <summary>Migra hasta <paramref name="destino"/>.</summary>
        public async Task MigrarAsync(string destino)
        {
            await using var contexto = new WaysDbContext(ConstruirOpciones(CadenaOwner), TenantActualFijo.Plataforma);
            var migrador = contexto.Database.GetInfrastructure().GetRequiredService<IMigrator>();
            await migrador.MigrateAsync(destino);
        }

        public async ValueTask DisposeAsync()
        {
            NpgsqlConnection.ClearAllPools();
            await using var admin = new NpgsqlConnection(_cadenaAdmin);
            await admin.OpenAsync();
            await using var dropear = admin.CreateCommand();
            dropear.CommandText = $"DROP DATABASE IF EXISTS \"{_nombreBase}\" WITH (FORCE)";
            await dropear.ExecuteNonQueryAsync();
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
                    npgsql.MapEnum<OrigenFondosPago>("origen_fondos_pago");
                    npgsql.MapEnum<OrigenAplicacionPago>("origen_aplicacion_pago");
                    npgsql.MapEnum<OrigenAplicacionCobro>("origen_aplicacion_cobro");
                })
                .ConfigureWarnings(w => w.Ignore(RelationalEventId.PendingModelChangesWarning))
                .Options;
    }
}
