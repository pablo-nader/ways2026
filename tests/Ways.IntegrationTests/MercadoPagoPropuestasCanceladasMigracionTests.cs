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
/// La migración <c>CanceladoPorMercadoPagoEnPropuestas</c> sobre una base que ya corrió la de las propuestas de tesorería: marca como canceladas por Mercado Pago
/// las reversiones, los retiros que una cancelación descartó y los retiros aplicados que tienen su reversión, y deja sin marca el resto (un retiro descartado a
/// mano, uno pendiente y el de otra empresa con la misma clave). El UPDATE corre bajo <c>ways_app</c> con el GUC de plataforma (<c>rls-migration-backfills</c>).
/// Base AISLADA con las filas sembradas por SQL crudo.
/// </summary>
[Collection("Ways.IntegrationTests secuencial")]
public class MercadoPagoPropuestasCanceladasMigracionTests(WaysApiFixture fixture) : IClassFixture<WaysApiFixture>
{
    private const string MigracionAnterior = "20261102070932_PropuestasDeTesoreriaMercadoPago";

    // Clausula: subir la migracion marca exactamente las propuestas que una cancelacion ya habia tocado (reversion, retiro descartado por cancelacion y retiro
    // aplicado con reversion en la misma empresa) y no las demas.
    [Fact]
    public async Task SubirLaMigracionMarcaLasPropuestasQueUnaCancelacionYaHabiaTocado()
    {
        await using var entorno = await EntornoAislado.CrearAsync(fixture);
        await entorno.MigrarAsync(MigracionAnterior);
        await using var owner = new NpgsqlConnection(entorno.CadenaOwner);
        await owner.OpenAsync();
        await SembrarAsync(owner);

        await entorno.MigrarAsync();

        Assert.Equal(
            new[] { "9001:retiro", "9001:retiro_cancelado", "9002:retiro" },
            await MarcadasAsync(owner));
    }

    // Clausula: el backfill escribe bajo ways_app (NOBYPASSRLS) solo con el GUC de plataforma, y es idempotente.
    [Fact]
    public async Task ElBackfillSoloAlcanzaFilasEnModoPlataformaYEsIdempotente()
    {
        await using var entorno = await EntornoAislado.CrearAsync(fixture);
        await entorno.MigrarAsync(MigracionAnterior);
        await using (var owner = new NpgsqlConnection(entorno.CadenaOwner))
        {
            await owner.OpenAsync();
            await SembrarAsync(owner);
            await entorno.MigrarAsync();
            await using var preparacion = owner.CreateCommand();
            preparacion.CommandText =
                """
                UPDATE propuestas_tesoreria_mercadopago SET cancelado_por_mercadopago = false;
                GRANT USAGE ON SCHEMA public TO ways_app;
                GRANT SELECT, INSERT, UPDATE, DELETE ON ALL TABLES IN SCHEMA public TO ways_app;
                """;
            await preparacion.ExecuteNonQueryAsync();
        }

        var bloque = CanceladoPorMercadoPagoEnPropuestas.SqlDeBackfill;
        Assert.StartsWith("SET LOCAL app.acceso = 'plataforma';", bloque, StringComparison.Ordinal);
        var sinElSetLocal = string.Join(
            '\n', bloque.Split('\n').Where(l => !l.TrimStart().StartsWith("SET LOCAL", StringComparison.Ordinal)));

        // Sin el GUC, ways_app no ve ninguna fila y el UPDATE no escribe nada, sin error.
        Assert.Equal(0, await EjecutarSobreWaysAppAsync(entorno, sinElSetLocal));
        Assert.Equal(3, await EjecutarSobreWaysAppAsync(entorno, bloque));
        Assert.Equal(0, await EjecutarSobreWaysAppAsync(entorno, bloque));

        await using var verificacion = new NpgsqlConnection(entorno.CadenaOwner);
        await verificacion.OpenAsync();
        Assert.Equal(new[] { "9001:retiro", "9001:retiro_cancelado", "9002:retiro" }, await MarcadasAsync(verificacion));
    }

    private static async Task<List<string>> MarcadasAsync(NpgsqlConnection owner)
    {
        var marcadas = new List<string>();
        await using var comando = owner.CreateCommand();
        comando.CommandText =
            "SELECT clave_origen || ':' || tipo FROM propuestas_tesoreria_mercadopago WHERE cancelado_por_mercadopago ORDER BY clave_origen, tipo, id_empresa";
        await using var lector = await comando.ExecuteReaderAsync();
        while (await lector.ReadAsync())
        {
            marcadas.Add(lector.GetString(0));
        }

        return marcadas;
    }

    private static async Task<int> EjecutarSobreWaysAppAsync(EntornoAislado entorno, string sql)
    {
        await using var app = new NpgsqlConnection(entorno.CadenaApp);
        await app.OpenAsync();
        await using var comando = app.CreateCommand();
        comando.CommandText = sql;
        return await comando.ExecuteNonQueryAsync();
    }

    /// <summary>Empresa 1: 9001 aplicado + su reversión (marcados), 9002 descartado por cancelación (marcado), 9003 descartado a mano y 9004 pendiente (sin
    /// marca). Empresa 2: 9001 aplicado sin reversión (sin marca: la reversión de la empresa 1 no es suya). Sin los padres: la sesión de siembra no dispara las FK.</summary>
    private static async Task SembrarAsync(NpgsqlConnection owner)
    {
        await EscalarAsync<object>(owner, "SET session_replication_role = replica");

        await InsertarAsync(owner, 1, 1, "retiro", "9001", "aplicada", 1, null, 10, 11, 1, 2, "u", null);
        await InsertarAsync(owner, 1, 2, "retiro_cancelado", "9001", "pendiente", 2, null, 10, 11, null, null, null, null);
        await InsertarAsync(owner, 1, 3, "retiro", "9002", "descartada", 3, null, null, null, null, null, "u", "cancelado por Mercado Pago");
        await InsertarAsync(owner, 1, 4, "retiro", "9003", "descartada", 4, null, null, null, null, null, "u", null);
        await InsertarAsync(owner, 1, 5, "retiro", "9004", "pendiente", 5, null, null, null, null, null, null, null);
        await InsertarAsync(owner, 2, 6, "retiro", "9001", "aplicada", 6, null, 20, 21, 3, 4, "u", null);
    }

    private static Task InsertarAsync(
        NpgsqlConnection owner, int idEmpresa, int idConciliacion, string tipo, string clave, string estado, int idFila, string? detalle,
        int? origen, int? destino, int? movimiento, int? movimientoDestino, string? resuelta, string? motivo) =>
        EscalarAsync<object>(
            owner,
            "INSERT INTO propuestas_tesoreria_mercadopago (id_tenant, id_empresa, id_conciliacion_mercadopago, tipo, clave_origen, importe, " +
            "id_fila_conciliacion_mercadopago, id_cuenta_origen, id_cuenta_destino, estado, id_movimiento_cuenta, id_movimiento_cuenta_destino, " +
            "id_usuario_resolucion, fecha_resolucion, motivo_resolucion, created_at, updated_at) " +
            "VALUES (1, $1, $2, $3, $4, 40, $5, $6, $7, $8, $9, $10, CASE WHEN $11::text IS NULL THEN NULL ELSE 1 END, " +
            "CASE WHEN $11::text IS NULL THEN NULL ELSE now() END, $12, now(), now()) RETURNING id_propuesta_tesoreria_mercadopago",
            idEmpresa,
            idConciliacion,
            tipo,
            clave,
            idFila,
            origen,
            destino,
            estado,
            movimiento,
            movimientoDestino,
            resuelta,
            motivo);

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
            var nombreBase = $"ways_prop_cancelada_migracion_{Guid.NewGuid():N}";
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
