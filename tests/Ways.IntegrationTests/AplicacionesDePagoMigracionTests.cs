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
/// La migración <c>AplicacionesDePago</c> sobre una base con datos (E3-S1a): el backfill reconstruye las aplicaciones a
/// partir de <c>pagos.id_comprobante_compra</c> con el tope del total de la compra, y <c>Down</c> retira tabla, triggers y
/// enum. Base AISLADA migrada hasta la migración inmediatamente anterior, con las filas sembradas por SQL crudo.
/// </summary>
[Collection("Ways.IntegrationTests secuencial")]
public class AplicacionesDePagoMigracionTests(WaysApiFixture fixture) : IClassFixture<WaysApiFixture>
{
    private const string MigracionAnterior = "20261028194859_QuitarMedioDeLaCabeceraDelPago";
    private const string MigracionPropia = "20261028210201_AplicacionesDePago";

    private sealed record Sembrado(int IdPagoA, int IdPagoB, int IdPagoC, int IdCompra1);

    [Fact]
    public async Task UpReconstruyeLasAplicacionesConElTopeDelTotalYDownRetiraTablaTriggersYEnum()
    {
        await using var entorno = await EntornoAislado.CrearAsync(fixture);

        await using var owner = new NpgsqlConnection(entorno.CadenaOwner);
        await owner.OpenAsync();
        var sembrado = await SembrarAsync(owner);

        await entorno.MigrarAsync(MigracionPropia);

        await VerificarFilasDelBackfillAsync(owner, sembrado);
        Assert.Equal(
            5L, await EscalarAsync<long>(
                owner,
                "SELECT count(*) FROM pg_trigger WHERE tgname IN ('tg_aplicaciones_pago_no_excede_pago', " +
                "'tg_aplicaciones_pago_no_excede_compra', 'tg_pagos_cubren_aplicaciones', " +
                "'tg_comprobantes_compra_admiten_aplicaciones', 'tg_pagos_coherentes_con_aplicaciones')"));

        await entorno.MigrarAsync(MigracionAnterior);

        Assert.Null(await EscalarAsync<string?>(owner, "SELECT to_regclass('public.aplicaciones_pago')::text"));
        Assert.Equal(0L, await EscalarAsync<long>(owner, "SELECT count(*) FROM pg_type WHERE typname = 'origen_aplicacion_pago'"));
        Assert.Equal(0L, await EscalarAsync<long>(owner, "SELECT count(*) FROM pg_proc WHERE proname LIKE 'fn_aplicaciones_pago_%'"));
        Assert.Equal(
            0L, await EscalarAsync<long>(
                owner,
                "SELECT count(*) FROM pg_trigger WHERE tgname IN ('tg_pagos_cubren_aplicaciones', " +
                "'tg_comprobantes_compra_admiten_aplicaciones', 'tg_pagos_coherentes_con_aplicaciones')"));

        // Subir de nuevo reconstruye lo mismo desde los pagos: la migración es repetible.
        await entorno.MigrarAsync(MigracionPropia);
        await VerificarFilasDelBackfillAsync(owner, sembrado);
    }

    /// <summary>
    /// LA CLÁUSULA (rls-migration-backfills): el <c>SET LOCAL app.acceso = 'plataforma'</c> dentro del mismo bloque que
    /// inserta las aplicaciones. Las tablas corren bajo FORCE ROW LEVEL SECURITY y el rol de aplicación no tiene BYPASSRLS:
    /// sin el GUC el INSERT ... SELECT no ve ningún pago y reporta éxito. Se corre la constante REAL de la migración sobre
    /// <c>ways_app</c> con y sin su primera línea, después de vaciar la tabla que la migración ya había rellenado.
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
                DELETE FROM aplicaciones_pago;
                GRANT USAGE ON SCHEMA public TO ways_app;
                GRANT SELECT, INSERT, UPDATE, DELETE ON ALL TABLES IN SCHEMA public TO ways_app;
                """;
            await preparacion.ExecuteNonQueryAsync();
        }

        var bloque = AplicacionesDePago.SqlDelBackfill;
        Assert.StartsWith("SET LOCAL app.acceso = 'plataforma';", bloque, StringComparison.Ordinal);
        var sinElSetLocal = string.Join(
            '\n', bloque.Split('\n').Where(l => !l.TrimStart().StartsWith("SET LOCAL", StringComparison.Ordinal)));

        // (a) SIN el SET LOCAL: ways_app sin GUC no ve ninguna fila de ningún tenant, y no hay error.
        await using (var app = new NpgsqlConnection(entorno.CadenaApp))
        {
            await app.OpenAsync();
            await using var comando = app.CreateCommand();
            comando.CommandText = sinElSetLocal;
            Assert.Equal(0, await comando.ExecuteNonQueryAsync());
        }

        // (b) CON el bloque tal cual: las dos aplicaciones esperadas.
        await using (var app = new NpgsqlConnection(entorno.CadenaApp))
        {
            await app.OpenAsync();
            await using var comando = app.CreateCommand();
            comando.CommandText = bloque;
            Assert.Equal(2, await comando.ExecuteNonQueryAsync());
        }

        await using (var owner = new NpgsqlConnection(entorno.CadenaOwner))
        {
            await owner.OpenAsync();
            await VerificarFilasDelBackfillAsync(owner, sembrado);
        }

        // (c) Idempotencia: otra corrida ya no encuentra nada que insertar.
        await using (var app = new NpgsqlConnection(entorno.CadenaApp))
        {
            await app.OpenAsync();
            await using var comando = app.CreateCommand();
            comando.CommandText = bloque;
            Assert.Equal(0, await comando.ExecuteNonQueryAsync());
        }
    }

    /// <summary>Los dos primeros pagos de la compra 1 (total 100) aplican 70 y 30 (el segundo, topado); el tercero no aplica
    /// nada; el pago dado de baja, el de la compra anulada, el que no tiene compra y los de otro proveedor, otra empresa y
    /// otra categoría quedan sin aplicar.</summary>
    private static async Task VerificarFilasDelBackfillAsync(NpgsqlConnection owner, Sembrado s)
    {
        Assert.Equal(2L, await EscalarAsync<long>(owner, "SELECT count(*) FROM aplicaciones_pago"));
        Assert.Equal(
            1L, await EscalarAsync<long>(
                owner,
                "SELECT count(*) FROM aplicaciones_pago WHERE id_pago = $1 AND id_comprobante_compra = $2 AND importe = 70 " +
                "AND origen = 'migracion' AND deleted_at IS NULL",
                s.IdPagoA, s.IdCompra1));
        Assert.Equal(
            1L, await EscalarAsync<long>(
                owner,
                "SELECT count(*) FROM aplicaciones_pago WHERE id_pago = $1 AND id_comprobante_compra = $2 AND importe = 30 " +
                "AND origen = 'migracion' AND deleted_at IS NULL",
                s.IdPagoB, s.IdCompra1));
        Assert.Equal(0L, await EscalarAsync<long>(owner, "SELECT count(*) FROM aplicaciones_pago WHERE id_pago = $1", s.IdPagoC));
        Assert.Equal(
            1L, await EscalarAsync<long>(
                owner,
                "SELECT count(*) FROM aplicaciones_pago a JOIN pagos p ON p.id_pago = a.id_pago " +
                "WHERE a.id_pago = $1 AND a.fecha = p.fecha AND a.id_empleado = p.id_empleado",
                s.IdPagoA));
    }

    private static async Task<Sembrado> SembrarAsync(NpgsqlConnection owner)
    {
        // `roles` y `condiciones_fiscales` son globales y no se siembran solas en una base propia.
        await EscalarAsync<int>(
            owner,
            "INSERT INTO roles (id_rol, nombre, created_at, updated_at) VALUES ($1, 'admin', now(), now()) RETURNING id_rol",
            (int)RolConocido.Admin);
        var idCondicion = await EscalarAsync<int>(
            owner,
            "INSERT INTO condiciones_fiscales (codigo, nombre, created_at, updated_at) " +
            "VALUES ('RI', 'Responsable inscripto', now(), now()) RETURNING id_condicion_fiscal");
        var idTipo = await EscalarAsync<int>(
            owner,
            "INSERT INTO tipos_comprobante (clase, codigo, nombre, letra, signo, discrimina_iva, es_fiscal, afecta_stock, " +
            "activo, created_at, updated_at) VALUES ('compra', 'C-FA', 'C-FA', 'A', 1, false, false, true, true, now(), now()) " +
            "RETURNING id_tipo_comprobante");

        var idTenant = await EscalarAsync<int>(
            owner,
            "INSERT INTO tenants (nombre, estado, created_at, updated_at) " +
            "VALUES ('Tenant histórico', 'activo'::estado_tenant, now(), now()) RETURNING id_tenant");
        var idEmpresa1 = await EscalarAsync<int>(
            owner,
            "INSERT INTO empresas (id_tenant, razon_social, created_at, updated_at) " +
            "VALUES ($1, 'Empresa 1', now(), now()) RETURNING id_empresa",
            idTenant);
        var idEmpresa2 = await EscalarAsync<int>(
            owner,
            "INSERT INTO empresas (id_tenant, razon_social, created_at, updated_at) " +
            "VALUES ($1, 'Empresa 2', now(), now()) RETURNING id_empresa",
            idTenant);
        var idPuntoVenta1 = await EscalarAsync<int>(
            owner,
            "INSERT INTO puntos_venta (id_tenant, id_empresa, nombre, modo, created_at, updated_at) " +
            "VALUES ($1, $2, 'PV 1', 'web'::modo_punto_venta, now(), now()) RETURNING id_punto_venta",
            idTenant, idEmpresa1);
        var idUsuario = await EscalarAsync<int>(
            owner,
            "INSERT INTO usuarios (id_tenant, usuario, mail, id_rol, password_hash, password_algoritmo, " +
            "password_actualizado_el, created_at, updated_at) " +
            "VALUES ($1, 'admin', 'historico@ways.test', $2, 'hash', 'test', now(), now(), now()) RETURNING id_usuario",
            idTenant, (int)RolConocido.Admin);
        var idProveedor1 = await EscalarAsync<int>(
            owner,
            "INSERT INTO proveedores (id_tenant, razon_social, id_condicion_fiscal, created_at, updated_at) " +
            "VALUES ($1, 'Proveedor 1', $2, now(), now()) RETURNING id_proveedor",
            idTenant, idCondicion);
        var idProveedor2 = await EscalarAsync<int>(
            owner,
            "INSERT INTO proveedores (id_tenant, razon_social, id_condicion_fiscal, created_at, updated_at) " +
            "VALUES ($1, 'Proveedor 2', $2, now(), now()) RETURNING id_proveedor",
            idTenant, idCondicion);

        var compra1 = await SembrarCompraAsync(owner, idTenant, idProveedor1, idTipo, idPuntoVenta1, idUsuario, 100m, "confirmada", "A-1");
        var compraAnulada = await SembrarCompraAsync(owner, idTenant, idProveedor1, idTipo, idPuntoVenta1, idUsuario, 100m, "anulada", "A-2");
        var compra3 = await SembrarCompraAsync(owner, idTenant, idProveedor1, idTipo, idPuntoVenta1, idUsuario, 100m, "confirmada", "A-3");

        var t0 = new DateTimeOffset(2026, 10, 1, 12, 0, 0, TimeSpan.Zero);
        Task<int> Pago(DateTimeOffset fecha, decimal importe, int? idProveedor, int idEmpresa, int? idCompra,
            string categoria = "proveedor", bool baja = false) =>
            SembrarPagoAsync(owner, idTenant, idUsuario, fecha, importe, idProveedor, idEmpresa, idCompra, categoria, baja);

        // Un pago dado de baja anterior a todos: no tiene que consumir el tope de la compra.
        await Pago(t0.AddHours(-1), 90m, idProveedor1, idEmpresa1, compra1, baja: true);
        var pagoA = await Pago(t0, 70m, idProveedor1, idEmpresa1, compra1);
        var pagoB = await Pago(t0.AddHours(1), 60m, idProveedor1, idEmpresa1, compra1);
        var pagoC = await Pago(t0.AddHours(2), 50m, idProveedor1, idEmpresa1, compra1);
        await Pago(t0, 40m, idProveedor1, idEmpresa1, compraAnulada);
        await Pago(t0, 25m, idProveedor1, idEmpresa1, null);
        await Pago(t0, 20m, idProveedor2, idEmpresa1, compra3);
        await Pago(t0, 20m, idProveedor1, idEmpresa2, compra3);
        await Pago(t0, 20m, idProveedor1, idEmpresa1, compra3, categoria: "otros");

        return new Sembrado(pagoA, pagoB, pagoC, compra1);
    }

    private static Task<int> SembrarCompraAsync(
        NpgsqlConnection owner, int idTenant, int idProveedor, int idTipo, int idPuntoVenta, int idUsuario, decimal total,
        string estado, string numeroExterno) =>
        EscalarAsync<int>(
            owner,
            "INSERT INTO comprobantes_compra (id_tenant, id_proveedor, id_tipo_comprobante, numero_externo, " +
            "fecha_comprobante, fecha_recepcion, id_punto_venta, id_empleado, subtotal, descuento_total, total, estado, " +
            "discrimina_iva, created_at, updated_at) " +
            "VALUES ($1, $2, $3, $4::citext, $5, $6, $7, $8, $9, 0, $9, $10::estado_compra, false, now(), now()) " +
            "RETURNING id_comprobante_compra",
            idTenant, idProveedor, idTipo, numeroExterno, new DateOnly(2026, 10, 1), DateTimeOffset.UtcNow, idPuntoVenta,
            idUsuario, total, estado);

    private static Task<int> SembrarPagoAsync(
        NpgsqlConnection owner, int idTenant, int idUsuario, DateTimeOffset fecha, decimal importe, int? idProveedor,
        int idEmpresa, int? idCompra, string categoria, bool baja) =>
        EscalarAsync<int>(
            owner,
            "INSERT INTO pagos (id_tenant, fecha, id_empresa, id_empleado, categoria, origen_fondos, concepto, " +
            "id_proveedor, importe, id_comprobante_compra, created_at, updated_at, deleted_at) " +
            "VALUES ($1, $2, $3, $4, $5::categoria_pago, 'tesoreria'::origen_fondos_pago, 'Pago histórico', $6, $7, $8, " +
            "now(), now(), CASE WHEN $9 THEN now() END) RETURNING id_pago",
            idTenant, fecha, idEmpresa, idUsuario, categoria, idProveedor, importe, idCompra, baja);

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
            var nombreBase = $"ways_aplicaciones_pago_{Guid.NewGuid():N}";
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

        /// <summary>Migra hasta <paramref name="destino"/> (o hasta HEAD si es <c>null</c>).</summary>
        public async Task MigrarAsync(string? destino)
        {
            await using var contexto = new WaysDbContext(ConstruirOpciones(CadenaOwner), TenantActualFijo.Plataforma);
            var migrador = contexto.Database.GetInfrastructure().GetRequiredService<IMigrator>();
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
