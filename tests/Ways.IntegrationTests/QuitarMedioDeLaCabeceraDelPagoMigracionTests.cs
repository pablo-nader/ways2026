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
using Ways.Infrastructure.Persistencia.Migraciones;

namespace Ways.IntegrationTests;

/// <summary>
/// La migración <c>QuitarMedioDeLaCabeceraDelPago</c> sobre una base con datos: <c>Up</c> elimina <c>pagos.id_medio_pago</c>
/// con su FK y su índice sin tocar los ítems, y <c>Down</c> los recrea rellenando la columna con el medio del ítem vivo de
/// menor id de cada pago (o, si no tiene ítems vivos, el del ítem de menor id).
///
/// Base AISLADA migrada hasta la migración inmediatamente anterior, con las filas sembradas por SQL crudo.
/// </summary>
[Collection("Ways.IntegrationTests secuencial")]
public class QuitarMedioDeLaCabeceraDelPagoMigracionTests(WaysApiFixture fixture) : IClassFixture<WaysApiFixture>
{
    private const string MigracionAnterior = "20261028185025_ItemsDePago";

    private sealed record Sembrado(int IdPagoConItemDadoDeBaja, int IdPagoSinItemsVivos, int IdMedioX, int IdMedioY);

    [Fact]
    public async Task UpEliminaLaColumnaConSuFkYSuIndiceYDownLaRecreaConElMedioDelItemVivoDeMenorId()
    {
        await using var entorno = await EntornoAislado.CrearAsync(fixture);

        await using var owner = new NpgsqlConnection(entorno.CadenaOwner);
        await owner.OpenAsync();
        var sembrado = await SembrarAsync(owner);

        await entorno.MigrarAsync(null);

        Assert.Equal(0L, await EscalarAsync<long>(
            owner,
            "SELECT count(*) FROM information_schema.columns WHERE table_name = 'pagos' AND column_name = 'id_medio_pago'"));
        Assert.Equal(0L, await EscalarAsync<long>(
            owner, "SELECT count(*) FROM pg_constraint WHERE conname = 'fk_pagos_medio_pago'"));
        Assert.Equal(0L, await EscalarAsync<long>(
            owner, "SELECT count(*) FROM pg_indexes WHERE indexname = 'ix_pagos_medio_pago'"));
        Assert.Equal(5L, await EscalarAsync<long>(owner, "SELECT count(*) FROM items_pago"));

        await entorno.MigrarAsync(MigracionAnterior);

        Assert.Equal(
            sembrado.IdMedioY,
            await EscalarAsync<int>(owner, "SELECT id_medio_pago FROM pagos WHERE id_pago = $1", sembrado.IdPagoConItemDadoDeBaja));
        Assert.Equal(
            sembrado.IdMedioX,
            await EscalarAsync<int>(owner, "SELECT id_medio_pago FROM pagos WHERE id_pago = $1", sembrado.IdPagoSinItemsVivos));
        Assert.Equal("NO", await EscalarAsync<string>(
            owner,
            "SELECT is_nullable FROM information_schema.columns WHERE table_name = 'pagos' AND column_name = 'id_medio_pago'"));
        Assert.Equal(1L, await EscalarAsync<long>(
            owner, "SELECT count(*) FROM pg_constraint WHERE conname = 'fk_pagos_medio_pago'"));
        Assert.Equal(1L, await EscalarAsync<long>(
            owner, "SELECT count(*) FROM pg_indexes WHERE indexname = 'ix_pagos_medio_pago'"));
    }

    /// <summary>
    /// LA CLÁUSULA (rls-migration-backfills): el <c>SET LOCAL app.acceso = 'plataforma'</c> dentro del mismo bloque que rellena
    /// <c>pagos.id_medio_pago</c> en <c>Down</c>. Las tablas corren bajo FORCE ROW LEVEL SECURITY y el rol de aplicación no
    /// tiene BYPASSRLS: sin el GUC el UPDATE ve cero filas y reporta éxito. Se corre la constante REAL de la migración sobre
    /// <c>ways_app</c> con y sin su primera línea, contra el esquema posterior a <c>Up</c> con la columna recreada vacía.
    /// </summary>
    [Fact]
    public async Task ElBackfillDeLaBajaSoloAlcanzaLasFilasConElGucDePlataformaSobreWaysApp()
    {
        await using var entorno = await EntornoAislado.CrearAsync(fixture);

        Sembrado sembrado;
        await using (var owner = new NpgsqlConnection(entorno.CadenaOwner))
        {
            await owner.OpenAsync();
            sembrado = await SembrarAsync(owner);
            await entorno.MigrarAsync(null);

            await using var preparacion = owner.CreateCommand();
            preparacion.CommandText =
                """
                ALTER TABLE pagos ADD COLUMN id_medio_pago integer NULL;
                GRANT USAGE ON SCHEMA public TO ways_app;
                GRANT SELECT, INSERT, UPDATE, DELETE ON ALL TABLES IN SCHEMA public TO ways_app;
                """;
            await preparacion.ExecuteNonQueryAsync();
        }

        var bloque = QuitarMedioDeLaCabeceraDelPago.SqlDelRespaldoDeLaBaja;
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

        // (b) CON el bloque tal cual: alcanza los dos pagos.
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
            Assert.Equal(
                sembrado.IdMedioY,
                await EscalarAsync<int>(owner, "SELECT id_medio_pago FROM pagos WHERE id_pago = $1", sembrado.IdPagoConItemDadoDeBaja));
            Assert.Equal(
                sembrado.IdMedioX,
                await EscalarAsync<int>(owner, "SELECT id_medio_pago FROM pagos WHERE id_pago = $1", sembrado.IdPagoSinItemsVivos));
        }

        // (c) Idempotencia: otra corrida ya no encuentra nada que tocar.
        await using (var app = new NpgsqlConnection(entorno.CadenaApp))
        {
            await app.OpenAsync();
            await using var comando = app.CreateCommand();
            comando.CommandText = bloque;
            Assert.Equal(0, await comando.ExecuteNonQueryAsync());
        }
    }

    /// <summary>Un tenant con dos medios (X e Y) y dos pagos de tesorería con sus ítems. El primero tiene un ítem de menor id
    /// dado de baja (medio X) y dos vivos (Y primero, X después): el medio de compatibilidad es Y. El segundo, dado de baja,
    /// no tiene ítems vivos: queda el medio de su ítem de menor id, X.</summary>
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

        var idMedioX = await SembrarMedioAsync(owner, idTenant, "Medio X", 1);
        var idMedioY = await SembrarMedioAsync(owner, idTenant, "Medio Y", 2);

        var idPagoVivo = await SembrarPagoAsync(owner, idTenant, idEmpresa, idUsuario, idMedioX, 100m, baja: false);
        await SembrarItemsAsync(owner, idTenant, idPagoVivo, (idMedioX, 30m, true), (idMedioY, 60m, false), (idMedioX, 40m, false));

        var idPagoDeBaja = await SembrarPagoAsync(owner, idTenant, idEmpresa, idUsuario, idMedioY, 50m, baja: true);
        await SembrarItemsAsync(owner, idTenant, idPagoDeBaja, (idMedioX, 20m, true), (idMedioY, 30m, true));

        return new Sembrado(idPagoVivo, idPagoDeBaja, idMedioX, idMedioY);
    }

    private static Task<int> SembrarMedioAsync(NpgsqlConnection owner, int idTenant, string nombre, int orden) =>
        EscalarAsync<int>(
            owner,
            "INSERT INTO medios_pago (id_tenant, nombre, orden, comportamiento, admite_vuelto, requiere_referencia, " +
            "activo, created_at, updated_at) " +
            "VALUES ($1, $2, $3, 'efectivo'::comportamiento_medio_pago, false, false, true, now(), now()) " +
            "RETURNING id_medio_pago",
            idTenant, nombre, orden);

    /// <summary>La cabecera del esquema anterior todavía exige <c>id_medio_pago</c>; se siembra con un medio distinto del que
    /// la migración tiene que elegir, para que el resultado salga de los ítems y no de la columna vieja.</summary>
    private static Task<int> SembrarPagoAsync(
        NpgsqlConnection owner, int idTenant, int idEmpresa, int idUsuario, int idMedioDeLaCabecera, decimal importe, bool baja) =>
        EscalarAsync<int>(
            owner,
            "INSERT INTO pagos (id_tenant, fecha, id_empresa, id_empleado, categoria, origen_fondos, concepto, " +
            "id_medio_pago, importe, created_at, updated_at, deleted_at) " +
            "VALUES ($1, now(), $2, $3, 'otros'::categoria_pago, 'tesoreria'::origen_fondos_pago, 'Pago histórico', " +
            "$4, $5, now(), now(), CASE WHEN $6 THEN now() END) " +
            "RETURNING id_pago",
            idTenant, idEmpresa, idUsuario, idMedioDeLaCabecera, importe, baja);

    /// <summary>Los ítems de un pago van en UNA transacción: el constraint trigger diferido compara la suma de los vivos con el
    /// importe de la cabecera recién al comitear.</summary>
    private static async Task SembrarItemsAsync(
        NpgsqlConnection owner, int idTenant, int idPago, params (int IdMedioPago, decimal Importe, bool Baja)[] items)
    {
        await using var tx = await owner.BeginTransactionAsync();
        foreach (var item in items)
        {
            await using var comando = owner.CreateCommand();
            comando.Transaction = tx;
            comando.CommandText =
                "INSERT INTO items_pago (id_tenant, id_pago, id_medio_pago, importe, created_at, updated_at, deleted_at) " +
                "VALUES ($1, $2, $3, $4, now(), now(), CASE WHEN $5 THEN now() END)";
            foreach (var valor in new object[] { idTenant, idPago, item.IdMedioPago, item.Importe, item.Baja })
            {
                comando.Parameters.Add(new NpgsqlParameter { Value = valor });
            }

            await comando.ExecuteNonQueryAsync();
        }

        await tx.CommitAsync();
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
            var nombreBase = $"ways_quitar_medio_pago_{Guid.NewGuid():N}";
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
                })
                .ConfigureWarnings(w => w.Ignore(RelationalEventId.PendingModelChangesWarning))
                .Options;
    }
}
