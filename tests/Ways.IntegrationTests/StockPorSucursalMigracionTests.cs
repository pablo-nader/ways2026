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
/// La migración <c>StockPorSucursal</c> sobre una base con datos: <c>stock</c> y <c>stock_lotes</c> pasan a la sucursal
/// de su punto de venta, <c>movimientos_stock</c> gana la sucursal (y la de destino en las transferencias) conservando la
/// caja, y los invariantes del ledger siguen valiendo por sucursal. Base AISLADA migrada hasta la migración anterior, con
/// las filas sembradas por SQL crudo.
/// </summary>
[Collection("Ways.IntegrationTests secuencial")]
public class StockPorSucursalMigracionTests(WaysApiFixture fixture) : IClassFixture<WaysApiFixture>
{
    private const string MigracionAnterior = "20261031015930_Sucursales";
    private const string MigracionPropia = "20261031031017_StockPorSucursal";

    private sealed record Sembrado(
        int IdTenant, int IdPv1, int IdPv2, int IdSucursal1, int IdSucursal2, int IdArticulo, int IdArticuloConLote, int IdLote);

    [Fact]
    public async Task UpLlevaElStockALaSucursalConLosInvariantesYDownLoDevuelveAlPuntoDeVenta()
    {
        await using var entorno = await EntornoAislado.CrearAsync(fixture);
        await using var owner = new NpgsqlConnection(entorno.CadenaOwner);
        await owner.OpenAsync();
        var s = await SembrarAsync(owner);

        await entorno.MigrarAsync(MigracionPropia);

        await VerificarElBackfillAsync(owner, s);
        Assert.Equal(
            0L, await EscalarAsync<long>(
                owner,
                "SELECT count(*) FROM information_schema.columns " +
                "WHERE table_name IN ('stock', 'stock_lotes') AND column_name = 'id_punto_venta'"));
        Assert.Equal(
            "NO", await EscalarAsync<string>(
                owner,
                "SELECT is_nullable FROM information_schema.columns " +
                "WHERE table_name = 'movimientos_stock' AND column_name = 'id_sucursal'"));
        Assert.Equal(
            "PRIMARY KEY (id_articulo, id_sucursal)",
            await EscalarAsync<string>(owner, "SELECT pg_get_constraintdef(oid) FROM pg_constraint WHERE conname = 'pk_stock'"));
        Assert.Equal(
            4L, await EscalarAsync<long>(
                owner,
                "SELECT count(*) FROM pg_constraint WHERE conname IN " +
                "('fk_stock_sucursal', 'fk_stock_lotes_sucursal', 'fk_movimientos_stock_sucursal', " +
                "'fk_movimientos_stock_sucursal_destino')"));

        await entorno.MigrarAsync(MigracionAnterior);

        Assert.Equal(
            [(s.IdArticulo, s.IdPv1, 7m), (s.IdArticulo, s.IdPv2, 3m), (s.IdArticuloConLote, s.IdPv1, 5m)],
            await FilasAsync(owner, "SELECT id_articulo, id_punto_venta, cantidad FROM stock ORDER BY id_articulo, id_punto_venta"));
        Assert.Equal(
            [(s.IdArticuloConLote, s.IdPv1, 5m)],
            await FilasAsync(owner, "SELECT id_articulo, id_punto_venta, cantidad FROM stock_lotes"));
        Assert.Equal(
            0L, await EscalarAsync<long>(
                owner,
                "SELECT count(*) FROM information_schema.columns WHERE table_name = 'movimientos_stock' AND column_name LIKE 'id_sucursal%'"));

        // Subir de nuevo reconstruye lo mismo: la migración es repetible.
        await entorno.MigrarAsync(MigracionPropia);
        await VerificarElBackfillAsync(owner, s);
    }

    /// <summary>Si dos puntos de venta con stock propio ya compartieran sucursal, sus filas caerían sobre la misma clave:
    /// la migración aborta nombrando el problema en vez de fusionar saldos en silencio, y la base queda como estaba.</summary>
    [Fact]
    public async Task DosFilasQueCaeriamSobreLaMismaClaveAbortanLaMigracion()
    {
        await using var entorno = await EntornoAislado.CrearAsync(fixture);
        await using var owner = new NpgsqlConnection(entorno.CadenaOwner);
        await owner.OpenAsync();
        var s = await SembrarAsync(owner);
        await EscalarAsync<int>(
            owner, "UPDATE puntos_venta SET id_sucursal = $1 WHERE id_punto_venta = $2 RETURNING id_punto_venta",
            s.IdSucursal1, s.IdPv2);

        var error = await Assert.ThrowsAsync<PostgresException>(() => entorno.MigrarAsync(MigracionPropia));

        Assert.Equal("P0001", error.SqlState);
        Assert.Contains("stock_por_sucursal", error.MessageText, StringComparison.Ordinal);
        Assert.Equal(
            1L, await EscalarAsync<long>(
                owner,
                "SELECT count(*) FROM information_schema.columns WHERE table_name = 'stock' AND column_name = 'id_punto_venta'"));
    }

    /// <summary>
    /// LA CLÁUSULA (rls-migration-backfills): el <c>SET LOCAL app.acceso = 'plataforma'</c> dentro del mismo bloque que
    /// llena las columnas. Sobre <c>ways_app</c> (NOBYPASSRLS) y sin el GUC, los UPDATE no ven ninguna fila y reportan
    /// éxito. Se agregan a mano las columnas nulas que la migración agrega antes del backfill y se corre la constante REAL
    /// con y sin su primera línea.
    /// </summary>
    [Fact]
    public async Task ElBackfillSoloAlcanzaLasFilasConElGucDePlataformaSobreWaysApp()
    {
        await using var entorno = await EntornoAislado.CrearAsync(fixture);
        Sembrado s;
        await using (var owner = new NpgsqlConnection(entorno.CadenaOwner))
        {
            await owner.OpenAsync();
            s = await SembrarAsync(owner);
            await using var preparacion = owner.CreateCommand();
            preparacion.CommandText =
                """
                ALTER TABLE stock ADD COLUMN id_sucursal integer;
                ALTER TABLE stock_lotes ADD COLUMN id_sucursal integer;
                ALTER TABLE movimientos_stock ADD COLUMN id_sucursal integer;
                ALTER TABLE movimientos_stock ADD COLUMN id_sucursal_destino integer;
                GRANT USAGE ON SCHEMA public TO ways_app;
                GRANT SELECT, INSERT, UPDATE, DELETE ON ALL TABLES IN SCHEMA public TO ways_app;
                """;
            await preparacion.ExecuteNonQueryAsync();
        }

        var bloque = StockPorSucursal.SqlDelBackfill;
        Assert.StartsWith("SET LOCAL app.acceso = 'plataforma';", bloque, StringComparison.Ordinal);
        var sinElSetLocal = string.Join(
            '\n', bloque.Split('\n').Where(l => !l.TrimStart().StartsWith("SET LOCAL", StringComparison.Ordinal)));

        await EjecutarComoWaysAppAsync(entorno, sinElSetLocal);
        await using (var owner = new NpgsqlConnection(entorno.CadenaOwner))
        {
            await owner.OpenAsync();
            Assert.Equal(
                0L, await EscalarAsync<long>(
                    owner,
                    "SELECT (SELECT count(*) FROM stock WHERE id_sucursal IS NOT NULL) + " +
                    "(SELECT count(*) FROM stock_lotes WHERE id_sucursal IS NOT NULL) + " +
                    "(SELECT count(*) FROM movimientos_stock WHERE id_sucursal IS NOT NULL)"));
        }

        await EjecutarComoWaysAppAsync(entorno, bloque);
        await using (var owner = new NpgsqlConnection(entorno.CadenaOwner))
        {
            await owner.OpenAsync();
            Assert.Equal(
                0L, await EscalarAsync<long>(
                    owner,
                    "SELECT (SELECT count(*) FROM stock WHERE id_sucursal IS NULL) + " +
                    "(SELECT count(*) FROM stock_lotes WHERE id_sucursal IS NULL) + " +
                    "(SELECT count(*) FROM movimientos_stock WHERE id_sucursal IS NULL) + " +
                    "(SELECT count(*) FROM movimientos_stock WHERE id_punto_venta_destino IS NOT NULL AND id_sucursal_destino IS NULL)"));
            Assert.Equal(
                s.IdSucursal2,
                await EscalarAsync<int>(owner, "SELECT id_sucursal FROM stock WHERE id_punto_venta = $1", s.IdPv2));
        }

        // Idempotencia: una segunda corrida no encuentra filas sin sucursal y no falla.
        await EjecutarComoWaysAppAsync(entorno, bloque);
    }

    /// <summary>Tres claves de stock en dos sucursales, cada una igual a la suma de sus movimientos; el lote suma lo mismo
    /// que su agregado; la transferencia conserva sus cajas y gana la sucursal de destino.</summary>
    private static async Task VerificarElBackfillAsync(NpgsqlConnection owner, Sembrado s)
    {
        Assert.Equal(
            [(s.IdArticulo, s.IdSucursal1, 7m), (s.IdArticulo, s.IdSucursal2, 3m), (s.IdArticuloConLote, s.IdSucursal1, 5m)],
            await FilasAsync(owner, "SELECT id_articulo, id_sucursal, cantidad FROM stock ORDER BY id_articulo, id_sucursal"));

        Assert.Equal(
            0L, await EscalarAsync<long>(
                owner,
                "SELECT count(*) FROM stock s WHERE s.cantidad <> (SELECT COALESCE(sum(m.cantidad), 0) " +
                "FROM movimientos_stock m WHERE m.id_articulo = s.id_articulo AND m.id_sucursal = s.id_sucursal)"));
        Assert.Equal(
            0L, await EscalarAsync<long>(
                owner,
                "SELECT count(*) FROM stock s WHERE s.id_articulo = $1 AND s.cantidad <> (SELECT COALESCE(sum(l.cantidad), 0) " +
                "FROM stock_lotes l WHERE l.id_articulo = s.id_articulo AND l.id_sucursal = s.id_sucursal)",
                s.IdArticuloConLote));
        Assert.Equal(
            [(s.IdArticuloConLote, s.IdSucursal1, 5m)],
            await FilasAsync(owner, "SELECT id_articulo, id_sucursal, cantidad FROM stock_lotes"));

        Assert.Equal(
            0L, await EscalarAsync<long>(
                owner,
                "SELECT count(*) FROM movimientos_stock m JOIN puntos_venta pv ON pv.id_punto_venta = m.id_punto_venta " +
                "WHERE m.id_sucursal <> pv.id_sucursal"));
        Assert.Equal(
            2L, await EscalarAsync<long>(
                owner,
                "SELECT count(*) FROM movimientos_stock WHERE id_punto_venta_destino = $1 AND id_sucursal_destino = $2",
                s.IdPv2, s.IdSucursal2));
    }

    private static async Task<Sembrado> SembrarAsync(NpgsqlConnection owner)
    {
        var idTenant = await EscalarAsync<int>(
            owner,
            "INSERT INTO tenants (nombre, estado, created_at, updated_at) " +
            "VALUES ('Tenant stock', 'activo'::estado_tenant, now(), now()) RETURNING id_tenant");
        var idEmpresa = await EscalarAsync<int>(
            owner,
            "INSERT INTO empresas (id_tenant, razon_social, created_at, updated_at) " +
            "VALUES ($1, 'Empresa stock', now(), now()) RETURNING id_empresa",
            idTenant);
        var (idPv1, idSucursal1) = await PuntoVentaAsync(owner, idTenant, idEmpresa, "Centro");
        var (idPv2, idSucursal2) = await PuntoVentaAsync(owner, idTenant, idEmpresa, "Norte");

        // `roles` y `alicuotas_iva` son globales y no se siembran solas en una base propia.
        await EscalarAsync<int>(
            owner,
            "INSERT INTO roles (id_rol, nombre, created_at, updated_at) VALUES ($1, 'admin', now(), now()) " +
            "ON CONFLICT DO NOTHING RETURNING id_rol",
            (int)RolConocido.Admin);
        var idUsuario = await EscalarAsync<int>(
            owner,
            "INSERT INTO usuarios (id_tenant, usuario, mail, id_rol, password_hash, password_algoritmo, " +
            "password_actualizado_el, created_at, updated_at) " +
            "VALUES ($1, 'admin', 'stock-migracion@ways.test', $2, 'hash', 'test', now(), now(), now()) RETURNING id_usuario",
            idTenant, (int)RolConocido.Admin);
        var idArea = await EscalarAsync<int>(
            owner,
            "INSERT INTO areas (id_tenant, nombre, orden, created_at, updated_at) VALUES ($1, 'Área', 1, now(), now()) " +
            "RETURNING id_area",
            idTenant);
        var idAlicuota = await EscalarAsync<int>(
            owner,
            "INSERT INTO alicuotas_iva (nombre, porcentaje, created_at, updated_at) VALUES ('21% stock', 21, now(), now()) " +
            "RETURNING id_alicuota_iva");

        async Task<int> ArticuloAsync(string codigo, bool controlaLote) =>
            await EscalarAsync<int>(
                owner,
                "INSERT INTO articulos (id_tenant, codigo_interno, nombre, id_area, id_alicuota_iva, unidad_venta, es_producto, " +
                "controla_lote, created_at, updated_at) " +
                "VALUES ($1, $2, $2, $3, $4, 'unidad'::unidad_venta, true, $5, now(), now()) RETURNING id_articulo",
                idTenant, codigo, idArea, idAlicuota, controlaLote);

        var idArticulo = await ArticuloAsync("YERBA", false);
        var idArticuloConLote = await ArticuloAsync("YOGUR", true);
        var idLote = await EscalarAsync<int>(
            owner,
            "INSERT INTO lotes (id_tenant, id_articulo, codigo, fecha_vencimiento, es_sin_identificar, created_at, updated_at) " +
            "VALUES ($1, $2, 'L1', DATE '2027-01-01', false, now(), now()) RETURNING id_lote",
            idTenant, idArticuloConLote);

        async Task MovimientoAsync(int articulo, int pv, decimal cantidad, string motivo, int? destino = null, int? lote = null) =>
            await EscalarAsync<int>(
                owner,
                "INSERT INTO movimientos_stock (id_tenant, id_articulo, id_punto_venta, cantidad, motivo, id_empleado, " +
                "id_punto_venta_destino, id_lote, creado_el) " +
                "VALUES ($1, $2, $3, $4, $5::motivo_stock, $6, $7, $8, now()) RETURNING id_movimiento",
                idTenant, articulo, pv, cantidad, motivo, idUsuario, destino, lote);

        await MovimientoAsync(idArticulo, idPv1, 10m, "ajuste");
        await MovimientoAsync(idArticulo, idPv1, -3m, "transferencia", destino: idPv2);
        await MovimientoAsync(idArticulo, idPv2, 3m, "transferencia", destino: idPv2);
        await MovimientoAsync(idArticuloConLote, idPv1, 5m, "ajuste", lote: idLote);

        async Task StockAsync(int articulo, int pv, decimal cantidad) =>
            await EscalarAsync<int>(
                owner,
                "INSERT INTO stock (id_articulo, id_punto_venta, id_tenant, cantidad) VALUES ($1, $2, $3, $4) RETURNING id_articulo",
                articulo, pv, idTenant, cantidad);

        await StockAsync(idArticulo, idPv1, 7m);
        await StockAsync(idArticulo, idPv2, 3m);
        await StockAsync(idArticuloConLote, idPv1, 5m);
        await EscalarAsync<int>(
            owner,
            "INSERT INTO stock_lotes (id_articulo, id_punto_venta, id_lote, id_tenant, cantidad) VALUES ($1, $2, $3, $4, 5) " +
            "RETURNING id_lote",
            idArticuloConLote, idPv1, idLote, idTenant);

        return new Sembrado(idTenant, idPv1, idPv2, idSucursal1, idSucursal2, idArticulo, idArticuloConLote, idLote);
    }

    private static async Task<(int IdPuntoVenta, int IdSucursal)> PuntoVentaAsync(
        NpgsqlConnection owner, int idTenant, int idEmpresa, string nombre)
    {
        var idSucursal = await EscalarAsync<int>(
            owner,
            "INSERT INTO sucursales (id_tenant, id_empresa, nombre, created_at, updated_at) " +
            "VALUES ($1, $2, $3, now(), now()) RETURNING id_sucursal",
            idTenant, idEmpresa, nombre);
        var idPuntoVenta = await EscalarAsync<int>(
            owner,
            "INSERT INTO puntos_venta (id_tenant, id_empresa, id_sucursal, nombre, modo, created_at, updated_at) " +
            "VALUES ($1, $2, $3, $4, 'web'::modo_punto_venta, now(), now()) RETURNING id_punto_venta",
            idTenant, idEmpresa, idSucursal, nombre);
        return (idPuntoVenta, idSucursal);
    }

    private static async Task<List<(int, int, decimal)>> FilasAsync(NpgsqlConnection conexion, string sql)
    {
        await using var comando = conexion.CreateCommand();
        comando.CommandText = sql;
        await using var lector = await comando.ExecuteReaderAsync();
        var filas = new List<(int, int, decimal)>();
        while (await lector.ReadAsync())
        {
            filas.Add((lector.GetInt32(0), lector.GetInt32(1), lector.GetDecimal(2)));
        }

        return filas;
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
            var nombreBase = $"ways_stock_sucursal_{Guid.NewGuid():N}";
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
