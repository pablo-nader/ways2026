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
/// La migración <c>AplicarCobrosHistoricosPorAntiguedad</c> (E7-S4) sobre una base con datos: cada recibo histórico se aplica
/// a las ventas en cuenta corriente más viejas del mismo cliente, del más viejo al más nuevo, partiendo importes, hasta que
/// se acaba uno de los dos lados; las aplicaciones quedan con <c>origen = 'historial'</c>. Base AISLADA migrada hasta la
/// migración anterior, con las filas sembradas por SQL crudo en una sesión <c>session_replication_role = replica</c> (sin
/// claves foráneas a clientes, puntos de venta ni roles, que el reparto no lee). El reparto en sí corre en otra sesión, con
/// los constraint triggers de <c>AplicacionesDeCobro</c> activos: si escribiera de más, fallaría.
/// </summary>
[Collection("Ways.IntegrationTests secuencial")]
public class AplicarCobrosHistoricosMigracionTests(WaysApiFixture fixture) : IClassFixture<WaysApiFixture>
{
    private const string MigracionAnterior = "20261030190011_AplicacionesDeCobro";
    private const string MigracionPropia = "20261030202550_AplicarCobrosHistoricosPorAntiguedad";

    private static readonly DateTimeOffset T0 = new(2026, 1, 10, 12, 0, 0, TimeSpan.Zero);

    private sealed record Aplicacion(int IdRecibo, int IdVenta, decimal Importe, string Origen, int IdEmpleado, DateTime Fecha);

    private sealed record Sembrado(IReadOnlyList<Aplicacion> Esperadas, Aplicacion Manual, int IdAplicacionManualDadaDeBaja);

    [Fact]
    public async Task UpAplicaLosRecibosALasVentasMasViejasYDownRetiraSoloLasHistoricasYLaColumna()
    {
        await using var entorno = await EntornoAislado.CrearAsync(fixture);

        await using var owner = new NpgsqlConnection(entorno.CadenaOwner);
        await owner.OpenAsync();
        var sembrado = await SembrarAsync(owner);

        await entorno.MigrarAsync(MigracionPropia);

        await VerificarAplicacionesAsync(owner, sembrado, sembrado.Esperadas);

        await entorno.MigrarAsync(MigracionAnterior);

        Assert.Equal(
            0L, await EscalarAsync<long>(
                owner,
                "SELECT count(*) FROM information_schema.columns WHERE table_name = 'aplicaciones_cobro' AND column_name = 'origen'"));
        Assert.Equal(0L, await EscalarAsync<long>(owner, "SELECT count(*) FROM pg_type WHERE typname = 'origen_aplicacion_cobro'"));
        Assert.Equal(
            1L, await EscalarAsync<long>(owner, "SELECT count(*) FROM aplicaciones_cobro WHERE deleted_at IS NULL"));
        Assert.Equal(
            1L, await EscalarAsync<long>(
                owner,
                "SELECT count(*) FROM aplicaciones_cobro WHERE id_comprobante_cobro = $1 AND id_comprobante_venta = $2 " +
                "AND importe = $3 AND deleted_at IS NULL",
                sembrado.Manual.IdRecibo, sembrado.Manual.IdVenta, sembrado.Manual.Importe));

        // Subir de nuevo arma el mismo reparto: lo calcula de los recibos, las ventas y lo que ya estaba aplicado.
        await entorno.MigrarAsync(MigracionPropia);
        await VerificarAplicacionesAsync(owner, sembrado, sembrado.Esperadas);
    }

    /// <summary>
    /// LA CLÁUSULA (rls-migration-backfills): el <c>SET LOCAL app.acceso = 'plataforma'</c> dentro del mismo bloque que
    /// inserta las aplicaciones. Las tablas corren bajo FORCE ROW LEVEL SECURITY y el rol de aplicación no tiene BYPASSRLS:
    /// sin el GUC el INSERT ... SELECT no ve ningún recibo y reporta éxito. Se corre la constante REAL de la migración sobre
    /// <c>ways_app</c> con y sin su primera línea, después de borrar las aplicaciones históricas que la migración ya había
    /// escrito.
    /// </summary>
    [Fact]
    public async Task ElRepartoSoloEscribeConElGucDePlataformaSobreWaysApp()
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
                DELETE FROM aplicaciones_cobro WHERE origen = 'historial';
                GRANT USAGE ON SCHEMA public TO ways_app;
                GRANT SELECT, INSERT, UPDATE, DELETE ON ALL TABLES IN SCHEMA public TO ways_app;
                """;
            await preparacion.ExecuteNonQueryAsync();
        }

        var bloque = AplicarCobrosHistoricosPorAntiguedad.SqlDelBackfill;
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

        // (b) CON el bloque tal cual: las siete aplicaciones históricas esperadas.
        await using (var app = new NpgsqlConnection(entorno.CadenaApp))
        {
            await app.OpenAsync();
            await using var comando = app.CreateCommand();
            comando.CommandText = bloque;
            Assert.Equal(sembrado.Esperadas.Count(a => a.Origen == "historial"), await comando.ExecuteNonQueryAsync());
        }

        await using (var owner = new NpgsqlConnection(entorno.CadenaOwner))
        {
            await owner.OpenAsync();
            await VerificarAplicacionesAsync(owner, sembrado, sembrado.Esperadas);
        }
    }

    /// <summary>Compara TODAS las aplicaciones vivas, columna por columna, con las esperadas; la manual dada de baja sigue
    /// igual.</summary>
    private static async Task VerificarAplicacionesAsync(
        NpgsqlConnection owner, Sembrado sembrado, IReadOnlyList<Aplicacion> esperadas)
    {
        var vivas = new List<Aplicacion>();
        await using (var comando = owner.CreateCommand())
        {
            comando.CommandText =
                "SELECT id_comprobante_cobro, id_comprobante_venta, importe, origen::text, id_empleado, fecha " +
                "FROM aplicaciones_cobro WHERE deleted_at IS NULL";
            await using var lector = await comando.ExecuteReaderAsync();
            while (await lector.ReadAsync())
            {
                vivas.Add(new Aplicacion(
                    lector.GetInt32(0), lector.GetInt32(1), lector.GetDecimal(2), lector.GetString(3), lector.GetInt32(4),
                    lector.GetDateTime(5)));
            }
        }

        Assert.Equal(Ordenar(esperadas), Ordenar(vivas));

        Assert.Equal(
            1L, await EscalarAsync<long>(
                owner,
                "SELECT count(*) FROM aplicaciones_cobro WHERE id_aplicacion_cobro = $1 AND deleted_at IS NOT NULL " +
                "AND importe = 10 AND origen = 'manual'",
                sembrado.IdAplicacionManualDadaDeBaja));

        static List<Aplicacion> Ordenar(IEnumerable<Aplicacion> aplicaciones) =>
            aplicaciones.OrderBy(a => a.IdRecibo).ThenBy(a => a.IdVenta).ToList();
    }

    /// <summary>
    /// Cliente 1 del tenant A, en orden de fecha: ventas V1 (día 1, 100 a cuenta), V2 (día 2, 50 a cuenta y 30 en efectivo),
    /// V3 (día 3, 80, con 20 ya aplicados a mano desde R2) y V4 (día 4, 40, más un cobro a cuenta dado de baja); recibos R1
    /// (día 1, 150 con 20 de vuelto y un cobro dado de baja), R2 (día 2, 60) y R3 (día 5, 200). Se insertan en otro orden
    /// que el de las fechas, así que ordenar por id daría otro reparto. Quedan fuera: la venta solo en efectivo, la venta y
    /// el recibo anulados, los espejos fiscales de una venta y de un recibo y la nota de crédito, todos del día 0 (los más
    /// viejos: si entraran, cambiarían todo el reparto). El reparto cae una vez sobre el par R2-V3, que ya tiene la
    /// aplicación manual: se saltea y esos 20 quedan sin aplicar de los dos lados. El cliente 2 del tenant A y el cliente
    /// del tenant B (con el MISMO id de cliente que el cliente 1) tienen recibos del día 0: si se mezclaran con el cliente 1,
    /// correrían su reparto.
    /// </summary>
    private static async Task<Sembrado> SembrarAsync(NpgsqlConnection owner)
    {
        await EscalarAsync<int>(owner, "SET session_replication_role = replica; SELECT 1");

        var idRc = await TipoAsync(owner, "RC", 1);
        var idTx = await TipoAsync(owner, "TX", 1);
        var idNc = await TipoAsync(owner, "NCX", -1);

        var idTenantA = await TenantAsync(owner, "Tenant A");
        var idTenantB = await TenantAsync(owner, "Tenant B");
        var cajeroA = await UsuarioAsync(owner, idTenantA, "cajero-a");
        var vendedorA = await UsuarioAsync(owner, idTenantA, "vendedor-a");
        var cajeroB = await UsuarioAsync(owner, idTenantB, "cajero-b");
        var efectivoA = await MedioAsync(owner, idTenantA, "Efectivo", "efectivo");
        var cuentaA = await MedioAsync(owner, idTenantA, "Cuenta corriente", "cuenta_corriente");
        var efectivoB = await MedioAsync(owner, idTenantB, "Efectivo", "efectivo");
        var cuentaB = await MedioAsync(owner, idTenantB, "Cuenta corriente", "cuenta_corriente");

        const int cliente1 = 1001;
        const int cliente2 = 1002;
        var numero = 0L;

        Task<int> Comprobante(
            int idTenant, int idTipo, int idCliente, int idEmpleado, int dia, string estado = "emitido",
            int? idVentaOrigen = null) =>
            ComprobanteAsync(owner, idTenant, idTipo, ++numero, T0.AddDays(dia), idEmpleado, idCliente, estado, idVentaOrigen);

        Task Cobro(int idTenant, int idComprobante, int idMedio, decimal importe, decimal vuelto = 0m, bool baja = false) =>
            CobroAsync(owner, idTenant, idComprobante, idMedio, importe, vuelto, baja);

        var r3 = await Comprobante(idTenantA, idRc, cliente1, cajeroA, 5);
        await Cobro(idTenantA, r3, efectivoA, 200m);
        var v3 = await Comprobante(idTenantA, idTx, cliente1, vendedorA, 3);
        await Cobro(idTenantA, v3, cuentaA, 80m);
        var r1 = await Comprobante(idTenantA, idRc, cliente1, cajeroA, 1);
        await Cobro(idTenantA, r1, efectivoA, 150m, vuelto: 20m);
        await Cobro(idTenantA, r1, efectivoA, 999m, baja: true);
        var v1 = await Comprobante(idTenantA, idTx, cliente1, vendedorA, 1);
        await Cobro(idTenantA, v1, cuentaA, 100m);
        var v4 = await Comprobante(idTenantA, idTx, cliente1, vendedorA, 4);
        await Cobro(idTenantA, v4, cuentaA, 40m);
        await Cobro(idTenantA, v4, cuentaA, 500m, baja: true);
        var r2 = await Comprobante(idTenantA, idRc, cliente1, cajeroA, 2);
        await Cobro(idTenantA, r2, efectivoA, 60m);
        var v2 = await Comprobante(idTenantA, idTx, cliente1, vendedorA, 2);
        await Cobro(idTenantA, v2, cuentaA, 50m);
        await Cobro(idTenantA, v2, efectivoA, 30m);

        var soloEfectivo = await Comprobante(idTenantA, idTx, cliente1, vendedorA, 0);
        await Cobro(idTenantA, soloEfectivo, efectivoA, 70m);
        var ventaAnulada = await Comprobante(idTenantA, idTx, cliente1, vendedorA, 0, estado: "anulado");
        await Cobro(idTenantA, ventaAnulada, cuentaA, 60m);
        var espejoDeVenta = await Comprobante(idTenantA, idTx, cliente1, vendedorA, 0, idVentaOrigen: v1);
        await Cobro(idTenantA, espejoDeVenta, cuentaA, 90m);
        var notaDeCredito = await Comprobante(idTenantA, idNc, cliente1, vendedorA, 0);
        await Cobro(idTenantA, notaDeCredito, cuentaA, 25m);
        var reciboAnulado = await Comprobante(idTenantA, idRc, cliente1, cajeroA, 0, estado: "anulado");
        await Cobro(idTenantA, reciboAnulado, efectivoA, 500m);
        var espejoDeRecibo = await Comprobante(idTenantA, idRc, cliente1, cajeroA, 0, idVentaOrigen: r1);
        await Cobro(idTenantA, espejoDeRecibo, efectivoA, 300m);

        var r4 = await Comprobante(idTenantA, idRc, cliente2, cajeroA, 0);
        await Cobro(idTenantA, r4, efectivoA, 50m);
        var v5 = await Comprobante(idTenantA, idTx, cliente2, vendedorA, 1);
        await Cobro(idTenantA, v5, cuentaA, 30m);

        var r5 = await Comprobante(idTenantB, idRc, cliente1, cajeroB, 0);
        await Cobro(idTenantB, r5, efectivoB, 70m);
        var v6 = await Comprobante(idTenantB, idTx, cliente1, cajeroB, 0);
        await Cobro(idTenantB, v6, cuentaB, 70m);

        var fechaManual = T0.AddDays(3);
        await AplicacionManualAsync(owner, idTenantA, r2, v3, 20m, vendedorA, fechaManual, baja: false);
        var idManualDadaDeBaja = await AplicacionManualAsync(owner, idTenantA, r1, v1, 10m, vendedorA, fechaManual, baja: true);

        DateTime Dia(int dia) => T0.AddDays(dia).UtcDateTime;
        var manual = new Aplicacion(r2, v3, 20m, "manual", vendedorA, fechaManual.UtcDateTime);
        Aplicacion Historica(int idRecibo, int idVenta, decimal importe, int idEmpleado, int dia) =>
            new(idRecibo, idVenta, importe, "historial", idEmpleado, Dia(dia));

        return new Sembrado(
            [
                Historica(r1, v1, 100m, cajeroA, 1),
                Historica(r1, v2, 30m, cajeroA, 2),
                Historica(r2, v2, 20m, cajeroA, 2),
                manual,
                Historica(r3, v3, 40m, cajeroA, 5),
                Historica(r3, v4, 40m, cajeroA, 5),
                Historica(r4, v5, 30m, cajeroA, 1),
                Historica(r5, v6, 70m, cajeroB, 0),
            ],
            manual,
            idManualDadaDeBaja);
    }

    private static async Task<int> TipoAsync(NpgsqlConnection owner, string codigo, short signo)
    {
        var existente = await EscalarAsync<int?>(
            owner, "SELECT id_tipo_comprobante FROM tipos_comprobante WHERE codigo = $1", codigo);
        return existente ?? await EscalarAsync<int>(
            owner,
            "INSERT INTO tipos_comprobante (clase, codigo, nombre, letra, signo, discrimina_iva, es_fiscal, afecta_stock, " +
            "activo, created_at, updated_at) VALUES ('venta', $1, $1, NULL, $2, false, false, false, true, now(), now()) " +
            "RETURNING id_tipo_comprobante",
            codigo, signo);
    }

    private static Task<int> TenantAsync(NpgsqlConnection owner, string nombre) =>
        EscalarAsync<int>(
            owner,
            "INSERT INTO tenants (nombre, estado, created_at, updated_at) " +
            "VALUES ($1, 'activo'::estado_tenant, now(), now()) RETURNING id_tenant",
            nombre);

    private static Task<int> UsuarioAsync(NpgsqlConnection owner, int idTenant, string usuario) =>
        EscalarAsync<int>(
            owner,
            "INSERT INTO usuarios (id_tenant, usuario, mail, id_rol, password_hash, password_algoritmo, " +
            "password_actualizado_el, created_at, updated_at) " +
            "VALUES ($1, $2, $3, $4, 'hash', 'test', now(), now(), now()) RETURNING id_usuario",
            idTenant, usuario, $"{usuario}-{Guid.NewGuid():N}@ways.test", (int)RolConocido.Admin);

    private static Task<int> MedioAsync(NpgsqlConnection owner, int idTenant, string nombre, string comportamiento) =>
        EscalarAsync<int>(
            owner,
            "INSERT INTO medios_pago (id_tenant, nombre, orden, comportamiento, admite_vuelto, requiere_referencia, " +
            "created_at, updated_at) VALUES ($1, $2, 1, $3::comportamiento_medio_pago, false, false, now(), now()) " +
            "RETURNING id_medio_pago",
            idTenant, nombre, comportamiento);

    /// <summary>Un comprobante; los importes del encabezado no los lee el reparto (solo los cobros). Un espejo fiscal lleva
    /// <c>resultado_fiscal</c>, como exige <c>ck_comprobantes_venta_espejo_fiscal</c>.</summary>
    private static Task<int> ComprobanteAsync(
        NpgsqlConnection owner, int idTenant, int idTipo, long numero, DateTimeOffset fecha, int idEmpleado, int idCliente,
        string estado, int? idVentaOrigen) =>
        EscalarAsync<int>(
            owner,
            "INSERT INTO comprobantes_venta (id_tenant, id_tipo_comprobante, numero, fecha, id_punto_venta, id_empleado, " +
            "id_cliente, subtotal, descuento_total, total, estado, id_venta_origen, resultado_fiscal, created_at, updated_at) " +
            "VALUES ($1, $2, $3, $4, 1, $5, $6, 0, 0, 0, $7::estado_comprobante, $8::integer, " +
            "CASE WHEN $8::integer IS NOT NULL THEN 'rechazado'::resultado_fiscal END, now(), now()) RETURNING id_comprobante_venta",
            idTenant, idTipo, numero, fecha, idEmpleado, idCliente, estado, idVentaOrigen);

    private static Task<int> CobroAsync(
        NpgsqlConnection owner, int idTenant, int idComprobante, int idMedio, decimal importe, decimal vuelto, bool baja) =>
        EscalarAsync<int>(
            owner,
            "INSERT INTO cobros (id_tenant, id_comprobante_venta, id_medio_pago, importe, vuelto, created_at, updated_at, " +
            "deleted_at) VALUES ($1, $2, $3, $4, $5, now(), now(), CASE WHEN $6 THEN now() END) RETURNING id_cobro",
            idTenant, idComprobante, idMedio, importe, vuelto, baja);

    /// <summary>Una aplicación hecha antes de la migración: todavía no tiene <c>origen</c>, la columna nueva le pone
    /// <c>manual</c>.</summary>
    private static Task<int> AplicacionManualAsync(
        NpgsqlConnection owner, int idTenant, int idRecibo, int idVenta, decimal importe, int idEmpleado,
        DateTimeOffset fecha, bool baja) =>
        EscalarAsync<int>(
            owner,
            "INSERT INTO aplicaciones_cobro (id_tenant, id_comprobante_cobro, id_comprobante_venta, importe, id_empleado, " +
            "fecha, created_at, updated_at, deleted_at) VALUES ($1, $2, $3, $4, $5, $6, now(), now(), " +
            "CASE WHEN $7 THEN now() END) RETURNING id_aplicacion_cobro",
            idTenant, idRecibo, idVenta, importe, idEmpleado, fecha, baja);

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
            var nombreBase = $"ways_cobros_historicos_{Guid.NewGuid():N}";
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
            await contexto.Database.GetInfrastructure().GetRequiredService<IMigrator>().MigrateAsync(destino);
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
