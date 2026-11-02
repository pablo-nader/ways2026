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
/// La migración <c>PagoDeVentaSinVerificarMercadoPago</c> (E5) sobre una base que ya corrió la etapa anterior con pagos que se apoyaban
/// en un cobro de Mercado Pago: la migración los marca <c>mercadopago</c> antes de agregar el CHECK que liga la marca al cobro. El
/// UPDATE corre bajo <c>ways_app</c> con el GUC de plataforma (<c>rls-migration-backfills</c>). Base AISLADA con las filas sembradas por
/// SQL crudo.
/// </summary>
[Collection("Ways.IntegrationTests secuencial")]
public class MercadoPagoVerificacionMigracionTests(WaysApiFixture fixture) : IClassFixture<WaysApiFixture>
{
    private const string MigracionAnterior = "20261101230531_CobroMercadoPagoPoint";

    [Fact]
    public async Task SubirLaMigracionMarcaLosPagosConCobroYDejaLosChecksYElIndice()
    {
        await using var entorno = await EntornoAislado.CrearAsync(fixture);
        await entorno.MigrarAsync(MigracionAnterior);
        await using var owner = new NpgsqlConnection(entorno.CadenaOwner);
        await owner.OpenAsync();
        await SembrarAsync(owner);

        await entorno.MigrarAsync();

        Assert.Equal(
            "mercadopago", await EscalarAsync<string>(owner, "SELECT verificacion_mercadopago FROM cobros WHERE importe = 1"));
        Assert.Null(await EscalarAsync<string>(owner, "SELECT verificacion_mercadopago FROM cobros WHERE importe = 2"));
        Assert.Equal(
            2L,
            await EscalarAsync<long>(
                owner,
                "SELECT count(*) FROM pg_constraint WHERE conname IN " +
                "('ck_cobros_verificacion_mercadopago', 'ck_cobros_verificacion_mercadopago_cobro')"));
        Assert.Equal(
            1L, await EscalarAsync<long>(owner, "SELECT count(*) FROM pg_indexes WHERE indexname = 'ix_cobros_sin_verificar'"));
    }

    [Fact]
    public async Task LaMarcaDeLosPagosCobradosSoloAlcanzaFilasEnModoPlataformaYEsIdempotente()
    {
        await using var entorno = await EntornoAislado.CrearAsync(fixture);
        await entorno.MigrarAsync(MigracionAnterior);
        await using (var owner = new NpgsqlConnection(entorno.CadenaOwner))
        {
            await owner.OpenAsync();
            await SembrarAsync(owner);
            await using var preparacion = owner.CreateCommand();
            preparacion.CommandText =
                """
                ALTER TABLE cobros ADD COLUMN verificacion_mercadopago varchar(20) NULL;
                GRANT USAGE ON SCHEMA public TO ways_app;
                GRANT SELECT, INSERT, UPDATE, DELETE ON ALL TABLES IN SCHEMA public TO ways_app;
                """;
            await preparacion.ExecuteNonQueryAsync();
        }

        var bloque = PagoDeVentaSinVerificarMercadoPago.SqlDeVerificacionDeLosPagosCobrados;
        Assert.StartsWith("SET LOCAL app.acceso = 'plataforma';", bloque, StringComparison.Ordinal);
        var sinElSetLocal = string.Join(
            '\n', bloque.Split('\n').Where(l => !l.TrimStart().StartsWith("SET LOCAL", StringComparison.Ordinal)));

        // Sin el GUC, ways_app (NOBYPASSRLS) no ve ninguna fila y el UPDATE no escribe nada, sin error.
        Assert.Equal(0, await EjecutarSobreWaysAppAsync(entorno, sinElSetLocal));
        Assert.Equal(1, await EjecutarSobreWaysAppAsync(entorno, bloque));
        Assert.Equal(0, await EjecutarSobreWaysAppAsync(entorno, bloque));

        await using var verificacion = new NpgsqlConnection(entorno.CadenaOwner);
        await verificacion.OpenAsync();
        Assert.Equal(
            "mercadopago", await EscalarAsync<string>(verificacion, "SELECT verificacion_mercadopago FROM cobros WHERE importe = 1"));
        Assert.Null(await EscalarAsync<string>(verificacion, "SELECT verificacion_mercadopago FROM cobros WHERE importe = 2"));
    }

    private static async Task<int> EjecutarSobreWaysAppAsync(EntornoAislado entorno, string sql)
    {
        await using var app = new NpgsqlConnection(entorno.CadenaApp);
        await app.OpenAsync();
        await using var comando = app.CreateCommand();
        comando.CommandText = sql;
        return await comando.ExecuteNonQueryAsync();
    }

    /// <summary>Importe 1: un pago con cobro de Mercado Pago (hay que marcarlo); importe 2: un pago común. Sin los padres: la sesión de
    /// siembra no dispara las FK.</summary>
    private static async Task SembrarAsync(NpgsqlConnection owner)
    {
        await EscalarAsync<object>(owner, "SET session_replication_role = replica");
        await EscalarAsync<object>(
            owner,
            "INSERT INTO cobros (id_tenant, id_comprobante_venta, id_medio_pago, importe, vuelto, id_cobro_mercadopago, created_at, updated_at) " +
            "VALUES (1, 1, 1, 1, 0, 1, now(), now()), (1, 1, 1, 2, 0, NULL, now(), now())");
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

    /// <summary>Una base propia sin migrar (cada prueba elige hasta dónde), que se descarta al final.</summary>
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
            var nombreBase = $"ways_mp_verificacion_migracion_{Guid.NewGuid():N}";
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
