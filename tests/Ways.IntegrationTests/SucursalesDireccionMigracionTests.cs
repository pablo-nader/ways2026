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
/// La migración <c>DireccionDeSucursal</c> en las dos direcciones sobre una base con una sucursal: el <c>Up</c> agrega las ocho
/// columnas opcionales y los tres CHECK sin tocar las filas existentes, y el <c>Down</c> las retira. Base AISLADA migrada hasta
/// el último estado, con la sucursal sembrada por SQL crudo.
/// </summary>
[Collection("Ways.IntegrationTests secuencial")]
public class SucursalesDireccionMigracionTests(WaysApiFixture fixture) : IClassFixture<WaysApiFixture>
{
    private const string MigracionAnterior = "20261101031204_ConciliacionesDeTesoreria";

    private static readonly string[] Columnas =
        ["calle", "altura", "ciudad", "provincia", "codigo_postal", "latitud", "longitud", "referencia"];

    private static readonly string[] Checks =
        ["ck_sucursales_latitud", "ck_sucursales_longitud", "ck_sucursales_coordenadas_completas"];

    [Fact]
    public async Task UpDownUpAgregaYRetiraLaDireccionYConservaLasSucursalesExistentes()
    {
        await using var entorno = await EntornoAislado.CrearAsync(fixture);
        await using var owner = new NpgsqlConnection(entorno.CadenaOwner);
        await owner.OpenAsync();

        await entorno.MigrarAsync(MigracionAnterior);
        Assert.Equal(0L, await ContarColumnasAsync(owner));
        Assert.Equal(0L, await ContarChecksAsync(owner));
        await EjecutarAsync(owner, "SET session_replication_role = replica");
        await EjecutarAsync(
            owner,
            "INSERT INTO sucursales (id_tenant, id_empresa, nombre, created_at, updated_at) VALUES (1, 1, 'Centro', now(), now())");

        await entorno.MigrarAsync();

        Assert.Equal(8L, await ContarColumnasAsync(owner));
        Assert.Equal(3L, await ContarChecksAsync(owner));
        Assert.Equal(
            1L,
            await EscalarAsync<long>(
                owner,
                "SELECT count(*) FROM sucursales WHERE nombre = 'Centro' AND calle IS NULL AND provincia IS NULL " +
                "AND latitud IS NULL AND longitud IS NULL"));
        Assert.Equal(
            "numeric", await EscalarAsync<string>(
                owner,
                "SELECT data_type FROM information_schema.columns WHERE table_name = 'sucursales' AND column_name = 'latitud'"));
        Assert.Equal(
            6, await EscalarAsync<int>(
                owner,
                "SELECT numeric_scale FROM information_schema.columns WHERE table_name = 'sucursales' AND column_name = 'longitud'"));

        await entorno.MigrarAsync(MigracionAnterior);

        Assert.Equal(0L, await ContarColumnasAsync(owner));
        Assert.Equal(0L, await ContarChecksAsync(owner));
        Assert.Equal(1L, await EscalarAsync<long>(owner, "SELECT count(*) FROM sucursales WHERE nombre = 'Centro'"));

        await entorno.MigrarAsync();
        Assert.Equal(8L, await ContarColumnasAsync(owner));
        Assert.Equal(3L, await ContarChecksAsync(owner));
    }

    private static Task<long> ContarColumnasAsync(NpgsqlConnection owner) =>
        EscalarAsync<long>(
            owner,
            "SELECT count(*) FROM information_schema.columns WHERE table_name = 'sucursales' AND column_name = ANY($1)",
            (object)Columnas);

    private static Task<long> ContarChecksAsync(NpgsqlConnection owner) =>
        EscalarAsync<long>(owner, "SELECT count(*) FROM pg_constraint WHERE conname = ANY($1)", (object)Checks);

    private static async Task EjecutarAsync(NpgsqlConnection conexion, string sql)
    {
        await using var comando = conexion.CreateCommand();
        comando.CommandText = sql;
        await comando.ExecuteNonQueryAsync();
    }

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

    /// <summary>Una base propia, migrada hasta el último estado, que se descarta al final.</summary>
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
            var nombreBase = $"ways_direccion_sucursal_migracion_{Guid.NewGuid():N}";
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

            await entorno.MigrarAsync();
            return entorno;
        }

        /// <summary>Migra hasta <paramref name="destino"/>, o hasta la última migración si es <c>null</c>.</summary>
        public async Task MigrarAsync(string? destino = null)
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
