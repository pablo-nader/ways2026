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
/// La migración <c>ParametrosPorSucursal</c> sobre una base con datos: cada clave de sucursal toma el valor de la caja viva
/// de menor id que tenga fila, las filas por punto de venta de esas claves se dan de baja y las claves de caja no se tocan.
/// Base AISLADA migrada hasta la migración anterior, con las filas sembradas por SQL crudo.
/// </summary>
[Collection("Ways.IntegrationTests secuencial")]
public class ParametrosPorSucursalMigracionTests(WaysApiFixture fixture) : IClassFixture<WaysApiFixture>
{
    private const string MigracionAnterior = "20261031194144_FusionDeSucursales";
    private const string MigracionPropia = "20261031223110_ParametrosPorSucursal";

    private sealed record Sembrado(
        int IdTenant, int IdEmpresa, int IdSucursal1, int IdSucursal2, int IdSucursalBaja,
        int IdCaja1, int IdCaja2, int IdCaja3, int IdCajaDeLaSucursalDeBaja);

    /// <summary>
    /// LA CLÁUSULA DE ELECCIÓN: de las cajas vivas de una sucursal gana la de menor id QUE TENGA FILA para esa clave, no la
    /// de menor id a secas. Sucursal 1 = cajas 1 y 2: <c>dias_alerta_vencimiento</c> la tienen las dos (gana la 1, 10),
    /// <c>zona_horaria</c> solo la 2 (gana la 2, aunque la 1 sea la de menor id). Una fila de caja dada de baja no cuenta,
    /// una clave de caja no se toca y una sucursal dada de baja no recibe filas.
    /// </summary>
    [Fact]
    public async Task UpCopiaElValorDeLaCajaVivaDeMenorIdConFilaYDaDeBajaLasFilasPorCaja()
    {
        await using var entorno = await EntornoAislado.CrearAsync(fixture);
        await using var owner = new NpgsqlConnection(entorno.CadenaOwner);
        await owner.OpenAsync();
        var s = await SembrarAsync(owner);

        await entorno.MigrarAsync(MigracionPropia);

        Assert.Equal(
            [("dias_alerta_vencimiento", s.IdSucursal1, "10"), ("zona_horaria", s.IdSucursal1, "\"America/Lima\"")],
            await FilasDeSucursalAsync(owner, s.IdSucursal1));
        Assert.Empty(await FilasDeSucursalAsync(owner, s.IdSucursal2));
        Assert.Empty(await FilasDeSucursalAsync(owner, s.IdSucursalBaja));

        Assert.Equal(
            0L, await EscalarAsync<long>(
                owner,
                "SELECT count(*) FROM parametros WHERE id_punto_venta IS NOT NULL AND deleted_at IS NULL " +
                "AND clave IN ('zona_horaria', 'lotes_habilitado', 'dias_alerta_vencimiento', 'dias_rotacion', 'dias_cobertura_objetivo')"));
        Assert.Equal(
            "15", await EscalarAsync<string>(
                owner, "SELECT valor::text FROM parametros WHERE id_punto_venta = $1 AND clave = 'tolerancia_pago' AND deleted_at IS NULL",
                s.IdCaja3));
        Assert.Equal(
            "\"America/Bogota\"", await EscalarAsync<string>(
                owner, "SELECT valor::text FROM parametros WHERE id_empresa = $1 AND clave = 'zona_horaria' AND id_punto_venta IS NULL " +
                "AND id_sucursal IS NULL AND deleted_at IS NULL",
                s.IdEmpresa));
    }

    [Fact]
    public async Task DownDevuelveElValorATodasLasCajasDeLaSucursalYUpDeNuevoLoReconstruye()
    {
        await using var entorno = await EntornoAislado.CrearAsync(fixture);
        await using var owner = new NpgsqlConnection(entorno.CadenaOwner);
        await owner.OpenAsync();
        var s = await SembrarAsync(owner);

        await entorno.MigrarAsync(MigracionPropia);
        await entorno.MigrarAsync(MigracionAnterior);

        Assert.Equal(
            [(s.IdCaja1, "dias_alerta_vencimiento", "10"), (s.IdCaja1, "zona_horaria", "\"America/Lima\""),
             (s.IdCaja2, "dias_alerta_vencimiento", "10"), (s.IdCaja2, "zona_horaria", "\"America/Lima\"")],
            await FilasDeCajaAsync(owner, s.IdSucursal1));

        await entorno.MigrarAsync(MigracionPropia);

        Assert.Equal(
            [("dias_alerta_vencimiento", s.IdSucursal1, "10"), ("zona_horaria", s.IdSucursal1, "\"America/Lima\"")],
            await FilasDeSucursalAsync(owner, s.IdSucursal1));
    }

    /// <summary>
    /// LA CLÁUSULA (rls-migration-backfills): el <c>SET LOCAL app.acceso = 'plataforma'</c> del mismo bloque. Sobre
    /// <c>ways_app</c> (NOBYPASSRLS) y sin el GUC, el INSERT ... SELECT y el UPDATE no ven ninguna fila y reportan éxito. Se
    /// migra una base vacía, se siembra después con filas por caja de un tenant nuevo y se corre la constante REAL con y sin
    /// su primera línea.
    /// </summary>
    [Fact]
    public async Task ElBackfillSoloAlcanzaLasFilasConElGucDePlataformaSobreWaysApp()
    {
        await using var entorno = await EntornoAislado.CrearAsync(fixture);
        await entorno.MigrarAsync(MigracionPropia);

        await using var owner = new NpgsqlConnection(entorno.CadenaOwner);
        await owner.OpenAsync();
        await EjecutarComoOwnerAsync(owner, "GRANT USAGE ON SCHEMA public TO ways_app; " +
            "GRANT SELECT, INSERT, UPDATE, DELETE ON ALL TABLES IN SCHEMA public TO ways_app;");
        var s = await SembrarAsync(owner);

        var bloque = ParametrosPorSucursal.SqlDelBackfill;
        Assert.StartsWith("SET LOCAL app.acceso = 'plataforma';", bloque, StringComparison.Ordinal);
        var sinElSetLocal = string.Join(
            '\n', bloque.Split('\n').Where(l => !l.TrimStart().StartsWith("SET LOCAL", StringComparison.Ordinal)));

        await EjecutarComoWaysAppAsync(entorno, sinElSetLocal);
        Assert.Empty(await FilasDeSucursalAsync(owner, s.IdSucursal1));
        Assert.Equal(
            5L, await EscalarAsync<long>(
                owner, "SELECT count(*) FROM parametros WHERE id_punto_venta IS NOT NULL AND deleted_at IS NULL AND id_tenant = $1",
                s.IdTenant));

        await EjecutarComoWaysAppAsync(entorno, bloque);
        Assert.Equal(
            [("dias_alerta_vencimiento", s.IdSucursal1, "10"), ("zona_horaria", s.IdSucursal1, "\"America/Lima\"")],
            await FilasDeSucursalAsync(owner, s.IdSucursal1));
        Assert.Equal(
            1L, await EscalarAsync<long>(
                owner, "SELECT count(*) FROM parametros WHERE id_punto_venta IS NOT NULL AND deleted_at IS NULL AND id_tenant = $1",
                s.IdTenant));

        // Idempotencia: una segunda corrida ni copia ni da de baja nada más.
        await EjecutarComoWaysAppAsync(entorno, bloque);
        Assert.Equal(
            2L, await EscalarAsync<long>(
                owner, "SELECT count(*) FROM parametros WHERE id_sucursal = $1 AND deleted_at IS NULL", s.IdSucursal1));
    }

    [Fact]
    public void LasClavesDeLaMigracionSonLasDeSucursalDelRegistro()
    {
        var delRegistro = new[]
        {
            ParametroConocido.ZonaHoraria, ParametroConocido.LotesHabilitado, ParametroConocido.DiasAlertaVencimiento,
            ParametroConocido.DiasRotacion, ParametroConocido.DiasCoberturaObjetivo
        }.Select(p => p.Clave).Order().ToList();

        var enElSql = System.Text.RegularExpressions.Regex
            .Matches(ParametrosPorSucursal.SqlDelBackfill, @"'([a-z_]+)'")
            .Select(m => m.Groups[1].Value)
            .Where(c => c != "plataforma")
            .Distinct().Order().ToList();

        Assert.Equal(delRegistro, enElSql);
        Assert.All(delRegistro, c => Assert.Equal(AlcanceDeParametro.Sucursal, ParametroConocido.Buscar(c).Alcance));
    }

    private static async Task<Sembrado> SembrarAsync(NpgsqlConnection owner)
    {
        var sufijo = Guid.NewGuid().ToString("N")[..8];
        var idTenant = await EscalarAsync<int>(
            owner,
            "INSERT INTO tenants (nombre, estado, created_at, updated_at) " +
            "VALUES ($1, 'activo'::estado_tenant, now(), now()) RETURNING id_tenant",
            $"Tenant parametros {sufijo}");
        var idEmpresa = await EscalarAsync<int>(
            owner,
            "INSERT INTO empresas (id_tenant, razon_social, created_at, updated_at) " +
            "VALUES ($1, 'Empresa parametros', now(), now()) RETURNING id_empresa",
            idTenant);

        async Task<int> SucursalAsync(string nombre, bool dadaDeBaja = false) =>
            await EscalarAsync<int>(
                owner,
                "INSERT INTO sucursales (id_tenant, id_empresa, nombre, created_at, updated_at, deleted_at) " +
                "VALUES ($1, $2, $3, now(), now(), CASE WHEN $4 THEN now() END) RETURNING id_sucursal",
                idTenant, idEmpresa, nombre, dadaDeBaja);

        async Task<int> CajaAsync(int idSucursal, string nombre, bool dadaDeBaja = false) =>
            await EscalarAsync<int>(
                owner,
                "INSERT INTO puntos_venta (id_tenant, id_empresa, id_sucursal, nombre, modo, created_at, updated_at, deleted_at) " +
                "VALUES ($1, $2, $3, $4, 'web'::modo_punto_venta, now(), now(), CASE WHEN $5 THEN now() END) RETURNING id_punto_venta",
                idTenant, idEmpresa, idSucursal, nombre, dadaDeBaja);

        var idSucursal1 = await SucursalAsync("Sucursal 1");
        var idSucursal2 = await SucursalAsync("Sucursal 2");
        var idSucursalBaja = await SucursalAsync("Sucursal dada de baja", dadaDeBaja: true);
        var idCaja1 = await CajaAsync(idSucursal1, "Caja 1");
        var idCaja2 = await CajaAsync(idSucursal1, "Caja 2");
        var idCaja3 = await CajaAsync(idSucursal2, "Caja 3");
        var idCajaDeLaSucursalDeBaja = await CajaAsync(idSucursalBaja, "Caja de la sucursal dada de baja", dadaDeBaja: true);

        async Task ParametroAsync(int? idPuntoVenta, string clave, string valor, bool dadaDeBaja = false) =>
            await EscalarAsync<int>(
                owner,
                "INSERT INTO parametros (id_tenant, id_empresa, id_punto_venta, clave, valor, created_at, updated_at, deleted_at) " +
                "VALUES ($1, $2, $3, $4, $5::jsonb, now(), now(), CASE WHEN $6 THEN now() END) RETURNING id_parametro",
                idTenant, idEmpresa, idPuntoVenta, clave, valor, dadaDeBaja);

        await ParametroAsync(null, "zona_horaria", "\"America/Bogota\"");
        await ParametroAsync(idCaja1, "dias_alerta_vencimiento", "10");
        await ParametroAsync(idCaja2, "dias_alerta_vencimiento", "20");
        await ParametroAsync(idCaja2, "zona_horaria", "\"America/Lima\"");
        await ParametroAsync(idCaja3, "tolerancia_pago", "15");
        await ParametroAsync(idCaja3, "zona_horaria", "\"America/Santiago\"", dadaDeBaja: true);
        await ParametroAsync(idCajaDeLaSucursalDeBaja, "lotes_habilitado", "true");

        return new Sembrado(
            idTenant, idEmpresa, idSucursal1, idSucursal2, idSucursalBaja, idCaja1, idCaja2, idCaja3, idCajaDeLaSucursalDeBaja);
    }

    private static async Task<List<(string, int, string)>> FilasDeSucursalAsync(NpgsqlConnection owner, int idSucursal)
    {
        await using var comando = owner.CreateCommand();
        comando.CommandText =
            "SELECT clave::text, id_sucursal, valor::text FROM parametros WHERE id_sucursal = $1 AND deleted_at IS NULL ORDER BY clave";
        comando.Parameters.Add(new NpgsqlParameter { Value = idSucursal });
        await using var lector = await comando.ExecuteReaderAsync();
        var filas = new List<(string, int, string)>();
        while (await lector.ReadAsync())
        {
            filas.Add((lector.GetString(0), lector.GetInt32(1), lector.GetString(2)));
        }

        return filas;
    }

    private static async Task<List<(int, string, string)>> FilasDeCajaAsync(NpgsqlConnection owner, int idSucursal)
    {
        await using var comando = owner.CreateCommand();
        comando.CommandText =
            "SELECT p.id_punto_venta, p.clave::text, p.valor::text FROM parametros p " +
            "JOIN puntos_venta pv ON pv.id_punto_venta = p.id_punto_venta " +
            "WHERE pv.id_sucursal = $1 AND p.deleted_at IS NULL ORDER BY p.id_punto_venta, p.clave";
        comando.Parameters.Add(new NpgsqlParameter { Value = idSucursal });
        await using var lector = await comando.ExecuteReaderAsync();
        var filas = new List<(int, string, string)>();
        while (await lector.ReadAsync())
        {
            filas.Add((lector.GetInt32(0), lector.GetString(1), lector.GetString(2)));
        }

        return filas;
    }

    private static async Task EjecutarComoOwnerAsync(NpgsqlConnection owner, string sql)
    {
        await using var comando = owner.CreateCommand();
        comando.CommandText = sql;
        await comando.ExecuteNonQueryAsync();
    }

    private static async Task EjecutarComoWaysAppAsync(EntornoAislado entorno, string sql)
    {
        await using var app = new NpgsqlConnection(entorno.CadenaApp);
        await app.OpenAsync();
        await using var transaccion = await app.BeginTransactionAsync();
        await using var comando = app.CreateCommand();
        comando.Transaction = transaccion;
        comando.CommandText = sql;
        await comando.ExecuteNonQueryAsync();
        await transaccion.CommitAsync();
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
            var nombreBase = $"ways_parametros_sucursal_{Guid.NewGuid():N}";
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
