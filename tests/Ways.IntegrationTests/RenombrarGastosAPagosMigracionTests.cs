using System.Runtime.CompilerServices;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;
using Ways.Domain.Caja;
using Ways.Domain.Articulos;
using Ways.Domain.Catalogos;
using Ways.Domain.Clientes;
using Ways.Domain.Compras;
using Ways.Domain.CuentaCorriente;
using Ways.Domain.Fiscal;
using Ways.Domain.Organizacion;
using Ways.Domain.Plantel;
using Ways.Domain.Stock;
using Ways.Domain.Ventas;
using Ways.Domain.Pagos;
using Ways.Domain.Usuarios;
using Ways.Infrastructure.Multitenancy;
using Ways.Infrastructure.Persistencia;

namespace Ways.IntegrationTests;

/// <summary>
/// La migración <c>RenombrarGastosAPagos</c> sobre una base con datos: solo renombra, así que la fila
/// de <c>gastos</c> tiene que reaparecer en <c>pagos</c> con el mismo id y el mismo contenido, los
/// tipos nativos y todos los objetos con "gasto" en el nombre tienen que haberse renombrado, y los
/// códigos de auditoría viejos tienen que seguir filtrables bajo el código nuevo. La reversión
/// (<c>Down</c>) devuelve cada cosa a su nombre anterior.
///
/// Base AISLADA migrada hasta la migración inmediatamente anterior, con las filas sembradas por SQL
/// crudo del esquema de ese momento.
/// </summary>
[Collection("Ways.IntegrationTests secuencial")]
public class RenombrarGastosAPagosMigracionTests(WaysApiFixture fixture) : IClassFixture<WaysApiFixture>
{
    private const string MigracionAnterior = "20261027204421_ImpuestosInternos";
    private const string ArchivoDeLaMigracion = "20261028180308_RenombrarGastosAPagos.cs";

    [Fact]
    public async Task LaFilaDelGastoReapareceComoPagoYNingunObjetoConservaElNombreViejo()
    {
        await using var entorno = await EntornoAislado.CrearAsync(fixture);

        await using var owner = new NpgsqlConnection(entorno.CadenaOwner);
        await owner.OpenAsync();
        var sembrado = await SembrarAsync(owner);

        await entorno.MigrarAsync(null);

        // La fila, con el mismo id y el mismo contenido, ahora en `pagos`.
        Assert.Null(await EscalarAsync<string?>(owner, "SELECT to_regclass('public.gastos')::text"));
        Assert.Equal(
            ("otros", "tesoreria", 123.45m),
            await LeerTresAsync(
                owner, "SELECT categoria::text, origen_fondos::text, importe FROM pagos WHERE id_pago = $1", sembrado.IdPago));

        // El movimiento de tesorería sigue enlazado: el tipo se llama "pago" y el id viaja por la columna nueva.
        Assert.Equal(
            ("pago", sembrado.IdPago),
            await LeerParAsync(
                owner,
                "SELECT m.tipo::text, p.id_pago FROM movimientos_tesoreria m JOIN pagos p ON p.id_pago = m.id_pago " +
                "WHERE m.id_movimiento = $1",
                sembrado.IdMovimiento));

        // Ningún objeto de la base conserva "gasto" en el nombre.
        foreach (var (consulta, descripcion) in new[]
        {
            ("SELECT count(*) FROM pg_class c JOIN pg_namespace n ON n.oid = c.relnamespace " +
             "WHERE n.nspname = 'public' AND c.relname ILIKE '%gasto%'", "tablas, índices y secuencias"),
            ("SELECT count(*) FROM pg_constraint WHERE conname ILIKE '%gasto%'", "restricciones"),
            ("SELECT count(*) FROM pg_policies WHERE policyname ILIKE '%gasto%'", "policies de RLS"),
            ("SELECT count(*) FROM pg_type WHERE typname ILIKE '%gasto%'", "tipos"),
            ("SELECT count(*) FROM pg_enum WHERE enumlabel ILIKE '%gasto%'", "valores de enum"),
            ("SELECT count(*) FROM information_schema.columns " +
             "WHERE table_schema = 'public' AND column_name ILIKE '%gasto%'", "columnas"),
        })
        {
            Assert.True(0L == await EscalarAsync<long>(owner, consulta), $"Quedaron {descripcion} con 'gasto' en el nombre.");
        }

        Assert.Equal(1L, await EscalarAsync<long>(owner, "SELECT count(*) FROM pg_policies WHERE policyname = 'pagos_tenant' AND tablename = 'pagos'"));
        Assert.Equal(2L, await EscalarAsync<long>(owner, "SELECT count(*) FROM pg_type WHERE typname IN ('categoria_pago', 'origen_fondos_pago')"));

        // Los códigos de auditoría viejos pasan al nuevo; la fila ajena no se toca.
        Assert.Equal(
            new[] { ("pago.baja", "pago"), ("pago.edicion", "pago"), ("precio.cambio", "articulo") },
            await LeerCodigosDeAuditoriaAsync(owner));

        // Reversión: cada cosa vuelve a su nombre anterior.
        await entorno.MigrarAsync(MigracionAnterior);

        Assert.Equal(
            ("otros", "tesoreria", 123.45m),
            await LeerTresAsync(
                owner, "SELECT categoria::text, origen_fondos::text, importe FROM gastos WHERE id_gasto = $1", sembrado.IdPago));
        Assert.Equal(
            "gasto",
            await EscalarAsync<string>(owner, "SELECT tipo::text FROM movimientos_tesoreria WHERE id_movimiento = $1", sembrado.IdMovimiento));
        Assert.Equal(
            new[] { ("gasto.baja", "gasto"), ("gasto.edicion", "gasto"), ("precio.cambio", "articulo") },
            await LeerCodigosDeAuditoriaAsync(owner));
        Assert.Equal(1L, await EscalarAsync<long>(owner, "SELECT count(*) FROM pg_policies WHERE policyname = 'gastos_tenant'"));
    }

    /// <summary>
    /// LA CLÁUSULA (rls-migration-backfills): el <c>SET LOCAL app.acceso = 'plataforma'</c> dentro del
    /// mismo bloque <c>Sql()</c> que reescribe <c>auditoria</c>. La tabla corre bajo FORCE ROW LEVEL
    /// SECURITY y el rol de aplicación no tiene BYPASSRLS: sin el GUC el UPDATE ve cero filas y
    /// reporta éxito. El bloque se EXTRAE del archivo real de la migración y se corre sobre
    /// <c>ways_app</c> con y sin su primera línea, así que la única diferencia entre las dos mitades es
    /// la línea bajo prueba.
    /// </summary>
    [Fact]
    public async Task ElBackfillDeAuditoriaSoloAlcanzaLasFilasConElGucDePlataformaSobreWaysApp()
    {
        await using var entorno = await EntornoAislado.CrearAsync(fixture);

        await using (var owner = new NpgsqlConnection(entorno.CadenaOwner))
        {
            await owner.OpenAsync();
            await SembrarAsync(owner);
            await using var concesion = owner.CreateCommand();
            concesion.CommandText =
                """
                GRANT USAGE ON SCHEMA public TO ways_app;
                GRANT SELECT, INSERT, UPDATE, DELETE ON ALL TABLES IN SCHEMA public TO ways_app;
                """;
            await concesion.ExecuteNonQueryAsync();
        }

        var bloque = LeerBloqueDeBackfillDesdeElArchivoDeLaMigracion();

        // (a) SIN el SET LOCAL: ways_app sin GUC no ve ninguna fila de ningún tenant, y no hay error.
        await using (var cruda = new NpgsqlConnection(entorno.CadenaApp))
        {
            await cruda.OpenAsync();
            await using var comando = cruda.CreateCommand();
            comando.CommandText = SinElSetLocal(bloque);
            Assert.Equal(0, await comando.ExecuteNonQueryAsync());
        }

        // (b) CON el bloque tal cual: alcanza las dos acciones y las dos entidades, y solo a ellas.
        await using (var cruda = new NpgsqlConnection(entorno.CadenaApp))
        {
            await cruda.OpenAsync();
            await using var comando = cruda.CreateCommand();
            comando.CommandText = bloque;
            Assert.Equal(4, await comando.ExecuteNonQueryAsync());
        }

        await using (var owner = new NpgsqlConnection(entorno.CadenaOwner))
        {
            await owner.OpenAsync();
            Assert.Equal(
                new[] { ("pago.baja", "pago"), ("pago.edicion", "pago"), ("precio.cambio", "articulo") },
                await LeerCodigosDeAuditoriaAsync(owner));
        }

        // (c) Idempotencia: otra corrida ya no encuentra nada que tocar.
        await using (var cruda = new NpgsqlConnection(entorno.CadenaApp))
        {
            await cruda.OpenAsync();
            await using var comando = cruda.CreateCommand();
            comando.CommandText = bloque;
            Assert.Equal(0, await comando.ExecuteNonQueryAsync());
        }
    }

    private sealed record Sembrado(int IdPago, int IdMovimiento);

    /// <summary>Un tenant con un gasto de origen tesorería, su movimiento de tesorería y tres filas de
    /// auditoría: dos del gasto y una ajena. Todo por SQL crudo del esquema de la migración anterior.</summary>
    private static async Task<Sembrado> SembrarAsync(NpgsqlConnection owner)
    {
        // `roles` es global y no se siembra sola en una base propia: hace falta para fk_usuarios_rol.
        await EscalarAsync<int>(
            owner,
            "INSERT INTO roles (id_rol, nombre, created_at, updated_at) VALUES ($1, 'admin', now(), now()) RETURNING id_rol",
            (int)RolConocido.Admin);
        var idTenant = await EscalarAsync<int>(
            owner,
            "INSERT INTO tenants (nombre, estado, created_at, updated_at) " +
            "VALUES ('Tenant histórico', 'activo'::estado_tenant, now(), now()) RETURNING id_tenant");
        var idEmpresa = await EscalarAsync<int>(
            owner,
            "INSERT INTO empresas (id_tenant, razon_social, created_at, updated_at) " +
            "VALUES ($1, 'Empresa histórica', now(), now()) RETURNING id_empresa",
            idTenant);
        var idUsuario = await EscalarAsync<int>(
            owner,
            "INSERT INTO usuarios (id_tenant, usuario, mail, id_rol, password_hash, password_algoritmo, " +
            "password_actualizado_el, created_at, updated_at) " +
            "VALUES ($1, 'admin', 'historico@ways.test', $2, 'hash', 'test', now(), now(), now()) RETURNING id_usuario",
            idTenant, (int)RolConocido.Admin);
        var idMedioPago = await EscalarAsync<int>(
            owner,
            "INSERT INTO medios_pago (id_tenant, nombre, orden, comportamiento, admite_vuelto, requiere_referencia, " +
            "activo, created_at, updated_at) " +
            "VALUES ($1, 'Efectivo', 1, 'efectivo'::comportamiento_medio_pago, false, false, true, now(), now()) " +
            "RETURNING id_medio_pago",
            idTenant);
        var idPago = await EscalarAsync<int>(
            owner,
            "INSERT INTO gastos (id_tenant, fecha, id_empresa, id_empleado, categoria, origen_fondos, concepto, " +
            "id_medio_pago, importe, created_at, updated_at) " +
            "VALUES ($1, now(), $2, $3, 'otros'::categoria_gasto, 'tesoreria'::origen_fondos_gasto, 'Gasto histórico', " +
            "$4, 123.45, now(), now()) RETURNING id_gasto",
            idTenant, idEmpresa, idUsuario, idMedioPago);
        var idMovimiento = await EscalarAsync<int>(
            owner,
            "INSERT INTO movimientos_tesoreria (id_tenant, id_empresa, fecha, tipo, id_gasto, concepto, inicio, " +
            "ingreso, egreso, final, id_empleado) " +
            "VALUES ($1, $2, now(), 'gasto'::tipo_movimiento_tesoreria, $3, 'Gasto histórico', 0, 0, 123.45, -123.45, $4) " +
            "RETURNING id_movimiento",
            idTenant, idEmpresa, idPago, idUsuario);

        foreach (var (accion, entidad) in new[]
        {
            ("gasto.edicion", "gasto"), ("gasto.baja", "gasto"), ("precio.cambio", "articulo")
        })
        {
            await EscalarAsync<long>(
                owner,
                "INSERT INTO auditoria (id_tenant, id_actor, accion, entidad, id_entidad, valor_nuevo, creado_el) " +
                "VALUES ($1, $2, $3, $4, $5, '{}'::jsonb, now()) RETURNING id_auditoria",
                idTenant, idUsuario, accion, entidad, idPago);
        }

        return new Sembrado(idPago, idMovimiento);
    }

    private static async Task<(string Accion, string Entidad)[]> LeerCodigosDeAuditoriaAsync(NpgsqlConnection owner)
    {
        await using var comando = owner.CreateCommand();
        comando.CommandText = "SELECT accion, entidad FROM auditoria ORDER BY accion";
        var filas = new List<(string, string)>();
        await using var lector = await comando.ExecuteReaderAsync();
        while (await lector.ReadAsync())
        {
            filas.Add((lector.GetString(0), lector.GetString(1)));
        }

        return [.. filas];
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

    private static async Task<(string, string, decimal)> LeerTresAsync(
        NpgsqlConnection conexion, string sql, params object?[] parametros)
    {
        await using var comando = conexion.CreateCommand();
        comando.CommandText = sql;
        foreach (var valor in parametros)
        {
            comando.Parameters.Add(new NpgsqlParameter { Value = valor ?? DBNull.Value });
        }

        await using var lector = await comando.ExecuteReaderAsync();
        Assert.True(await lector.ReadAsync());
        return (lector.GetString(0), lector.GetString(1), lector.GetDecimal(2));
    }

    private static async Task<(string, int)> LeerParAsync(
        NpgsqlConnection conexion, string sql, params object?[] parametros)
    {
        await using var comando = conexion.CreateCommand();
        comando.CommandText = sql;
        foreach (var valor in parametros)
        {
            comando.Parameters.Add(new NpgsqlParameter { Value = valor ?? DBNull.Value });
        }

        await using var lector = await comando.ExecuteReaderAsync();
        Assert.True(await lector.ReadAsync());
        return (lector.GetString(0), lector.GetInt32(1));
    }

    /// <summary>El bloque <c>"""..."""</c> que reescribe <c>auditoria</c>, tomado del archivo real de la
    /// migración (primer bloque del <c>Up</c>) — una copia a mano ejecutaría el SQL correcto sin importar
    /// lo que la migración diga.</summary>
    private static string LeerBloqueDeBackfillDesdeElArchivoDeLaMigracion()
    {
        var ruta = Path.Combine(
            Path.GetDirectoryName(RutaDeEsteArchivo())!,
            "..", "..", "src", "Ways.Infrastructure", "Persistencia", "Migraciones", ArchivoDeLaMigracion);

        Assert.True(File.Exists(ruta), $"No se encontró la migración en {ruta}");

        var fuente = File.ReadAllText(ruta);
        const string delimitador = "\"\"\"";

        var apertura = fuente.IndexOf(delimitador, StringComparison.Ordinal) + delimitador.Length;
        var cierre = fuente.IndexOf(delimitador, apertura, StringComparison.Ordinal);
        var bloque = fuente[apertura..cierre].Trim();

        Assert.Contains("UPDATE auditoria", bloque, StringComparison.Ordinal);
        Assert.StartsWith("SET LOCAL app.acceso = 'plataforma';", bloque, StringComparison.Ordinal);
        return bloque;
    }

    private static string SinElSetLocal(string bloque) =>
        string.Join(
            '\n',
            bloque.Split('\n').Where(l => !l.TrimStart().StartsWith("SET LOCAL", StringComparison.Ordinal)));

    private static string RutaDeEsteArchivo([CallerFilePath] string ruta = "") => ruta;

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
            var nombreBase = $"ways_renombrar_pagos_{Guid.NewGuid():N}";
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
                })
                .ConfigureWarnings(w => w.Ignore(RelationalEventId.PendingModelChangesWarning))
                .Options;
    }
}
