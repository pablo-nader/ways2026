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
/// La migración <c>RenovacionDeConexionesMercadoPago</c> en las dos direcciones sobre una base con filas en los tres estados.
/// El <c>Down()</c> tiene que convertir las <c>requiere_reconexion</c> en <c>desconectada</c> (con la fecha de la marca como fecha
/// de desconexión) antes de que vuelva el CHECK de dos valores, y ese UPDATE corre bajo <c>ways_app</c> con el GUC de plataforma
/// (<see cref="RenovacionDeConexionesMercadoPago.SqlDeBajaDeRequiereReconexion"/>). Base AISLADA migrada hasta el último estado,
/// con las filas sembradas por SQL crudo.
/// </summary>
[Collection("Ways.IntegrationTests secuencial")]
public class MercadoPagoMigracionTests(WaysApiFixture fixture) : IClassFixture<WaysApiFixture>
{
    private const string MigracionAnterior = "20261031145103_ConexionesMercadoPago";

    private static readonly DateTimeOffset Marcada = new(2026, 10, 30, 15, 0, 0, TimeSpan.Zero);

    [Fact]
    public async Task UpDownUpConvierteLasQueRequierenReconexionEnDesconectadasYRestauraElCheckDeDosValores()
    {
        await using var entorno = await EntornoAislado.CrearAsync(fixture);
        await using var owner = new NpgsqlConnection(entorno.CadenaOwner);
        await owner.OpenAsync();
        await SembrarAsync(owner);

        await entorno.MigrarAsync(MigracionAnterior);

        Assert.Equal(
            0L, await EscalarAsync<long>(owner, "SELECT count(*) FROM conexiones_mercadopago WHERE estado = 'requiere_reconexion'"));
        Assert.Equal(
            Marcada.UtcDateTime,
            await EscalarAsync<DateTime>(
                owner, "SELECT fecha_desconexion FROM conexiones_mercadopago WHERE id_empresa = 2 AND estado = 'desconectada'"));
        Assert.Equal(
            1L, await EscalarAsync<long>(owner, "SELECT count(*) FROM conexiones_mercadopago WHERE id_empresa = 1 AND estado = 'activa'"));
        Assert.Equal(
            1L,
            await EscalarAsync<long>(
                owner,
                "SELECT count(*) FROM conexiones_mercadopago " +
                "WHERE id_empresa = 3 AND estado = 'desconectada' AND fecha_desconexion IS NOT NULL"));
        Assert.Equal(
            0L,
            await EscalarAsync<long>(
                owner,
                "SELECT count(*) FROM information_schema.columns " +
                "WHERE table_name = 'conexiones_mercadopago' AND column_name = 'fecha_requiere_reconexion'"));

        await using (var rechazo = owner.CreateCommand())
        {
            rechazo.CommandText = "UPDATE conexiones_mercadopago SET estado = 'requiere_reconexion' WHERE id_empresa = 3";
            var error = await Assert.ThrowsAsync<PostgresException>(() => rechazo.ExecuteNonQueryAsync());
            Assert.Equal("ck_conexiones_mercadopago_estado", error.ConstraintName);
        }

        await entorno.MigrarAsync();

        Assert.Equal(3L, await EscalarAsync<long>(owner, "SELECT count(*) FROM conexiones_mercadopago"));
        Assert.Equal(
            0L, await EscalarAsync<long>(owner, "SELECT count(*) FROM conexiones_mercadopago WHERE estado = 'requiere_reconexion'"));
        Assert.Equal(
            2L,
            await EscalarAsync<long>(
                owner,
                "SELECT count(*) FROM information_schema.columns " +
                "WHERE table_name = 'conexiones_mercadopago' " +
                "AND column_name IN ('fecha_requiere_reconexion', 'id_usuario_desconecto')"));
    }

    [Fact]
    public async Task LaBajaDeRequiereReconexionSoloAlcanzaFilasEnModoPlataformaYEsIdempotente()
    {
        await using var entorno = await EntornoAislado.CrearAsync(fixture);
        await using (var owner = new NpgsqlConnection(entorno.CadenaOwner))
        {
            await owner.OpenAsync();
            await SembrarAsync(owner);
            await using var preparacion = owner.CreateCommand();
            preparacion.CommandText =
                """
                GRANT USAGE ON SCHEMA public TO ways_app;
                GRANT SELECT, INSERT, UPDATE, DELETE ON ALL TABLES IN SCHEMA public TO ways_app;
                """;
            await preparacion.ExecuteNonQueryAsync();
        }

        var bloque = RenovacionDeConexionesMercadoPago.SqlDeBajaDeRequiereReconexion;
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
            Marcada.UtcDateTime,
            await EscalarAsync<DateTime>(
                verificacion,
                "SELECT fecha_desconexion FROM conexiones_mercadopago WHERE id_empresa = 2 AND estado = 'desconectada'"));
    }

    private static async Task<int> EjecutarSobreWaysAppAsync(EntornoAislado entorno, string sql)
    {
        await using var app = new NpgsqlConnection(entorno.CadenaApp);
        await app.OpenAsync();
        await using var comando = app.CreateCommand();
        comando.CommandText = sql;
        return await comando.ExecuteNonQueryAsync();
    }

    /// <summary>Una activa (empresa 1), una que requiere reconexión (empresa 2) y una desconectada (empresa 3). Sin los padres: la
    /// sesión de siembra no dispara las FK.</summary>
    private static async Task SembrarAsync(NpgsqlConnection owner)
    {
        await EscalarAsync<object>(owner, "SET session_replication_role = replica");

        await InsertarAsync(owner, 1, 1001, "activa", conMaterial: true, null, null);
        await InsertarAsync(owner, 2, 1002, "requiere_reconexion", conMaterial: false, null, Marcada);
        await InsertarAsync(owner, 3, 1003, "desconectada", conMaterial: false, Marcada.AddDays(-7), null);
    }

    private static Task InsertarAsync(
        NpgsqlConnection owner, int idEmpresa, long idUsuarioMercadoPago, string estado, bool conMaterial,
        DateTimeOffset? fechaDesconexion, DateTimeOffset? fechaRequiereReconexion) =>
        EscalarAsync<object>(
            owner,
            "INSERT INTO conexiones_mercadopago (id_tenant, id_empresa, id_cuenta_tesoreria, id_usuario_mercadopago, " +
            "access_token_cifrado, access_token_nonce, access_token_tag, refresh_token_cifrado, refresh_token_nonce, " +
            "refresh_token_tag, id_clave_maestra, access_token_expira_en, estado, id_usuario_conecto, fecha_conexion, " +
            "fecha_desconexion, fecha_requiere_reconexion, created_at, updated_at) " +
            "VALUES (1, $1, 1, $2, $3, $4, $5, $3, $4, $5, 't1', now(), $6, 1, now(), $7, $8, now(), now()) " +
            "RETURNING id_conexion_mercadopago",
            idEmpresa,
            idUsuarioMercadoPago,
            conMaterial ? new byte[24] : null,
            conMaterial ? new byte[12] : null,
            conMaterial ? new byte[16] : null,
            estado,
            fechaDesconexion,
            fechaRequiereReconexion);

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
            var nombreBase = $"ways_mercadopago_migracion_{Guid.NewGuid():N}";
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
