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
using Ways.Domain.Gastos;
using Ways.Domain.Organizacion;
using Ways.Domain.Stock;
using Ways.Domain.Usuarios;
using Ways.Domain.Ventas;
using Ways.Infrastructure.Multitenancy;
using Ways.Infrastructure.Persistencia;

namespace Ways.IntegrationTests;

/// <summary>
/// GastosOrigenFondosYTesoreriaPorEmpresa (rls-migration-backfills): TRES de los cuatro bloques
/// <c>Sql()</c> de la migración — <c>gastos.id_empresa</c>, <c>movimientos_tesoreria.id_empresa</c>
/// y el re-encadenado de <c>inicio</c>/<c>final</c> por <c>(id_tenant, id_empresa)</c> — sobre
/// <c>ways_app</c> (NOSUPERUSER NOBYPASSRLS), mutation-proven (sin <c>SET LOCAL app.acceso =
/// 'plataforma'</c>, cero filas afectadas). El cuarto bloque (<c>gastos.origen_fondos = 'caja_turno'</c>)
/// no tiene test propio: es un UPDATE incondicional de una sola columna con el mismo SET LOCAL,
/// sin ninguna lógica de merge/partición que mutar. Mismo criterio que
/// <c>ModoPuntoVentaBackfillTests</c>: SQL DUPLICADO a propósito respecto de la migración (una
/// migración es un snapshot congelado).
///
/// Corre sobre una base AISLADA, migrada solo hasta <see cref="MigracionAnterior"/>, y reproduce a
/// mano el schema pre-migración (columnas nuevas agregadas sueltas, sin el resto de la migración)
/// — mismo criterio que <c>ModoPuntoVentaBackfillTests</c>: pasado ese punto en la base compartida
/// ya migrada a HEAD, ni "id_empresa = 0" ni una cadena sin re-anclar son alcanzables.
///
/// El fixture de la cadena (mutation-proof-tests regla 15/12(c)): DOS puntos de venta (PV1/PV2) de
/// la MISMA empresa X con filas INTERCALADAS por fecha — si el backfill mezclara por
/// <c>id_punto_venta</c>/orden de <c>id</c> en vez de <c>fecha</c> global de la empresa, el merge
/// saldría distinto — más un tercer punto de venta de una empresa Y del MISMO tenant, con una fila
/// cuya fecha cae EN MEDIO de las de la empresa X: si la partición fuera solo por <c>id_tenant</c>
/// (sin <c>id_empresa</c>), esta fila se mezclaría en la cadena de la empresa X y el total cambiaría.
/// </summary>
[Collection("Ways.IntegrationTests secuencial")]
public class GastosOrigenFondosYTesoreriaPorEmpresaBackfillTests(WaysApiFixture fixture) : IClassFixture<WaysApiFixture>
{
    private const string MigracionAnterior = "20260927000933_RendicionSaldadaEnReservaNumeracion";

    /// <summary>El mismo statement que el backfill 2/4 de <c>GastosOrigenFondosYTesoreriaPorEmpresa.Up</c>.</summary>
    private const string SqlBackfillIdEmpresaGastos =
        """
        UPDATE gastos g
           SET id_empresa = pv.id_empresa
          FROM puntos_venta pv
         WHERE pv.id_punto_venta = g.id_punto_venta
           AND pv.id_tenant = g.id_tenant
           AND g.id_empresa = 0;
        """;

    /// <summary>El mismo statement que el backfill 3/4.</summary>
    private const string SqlBackfillIdEmpresaMovimientos =
        """
        UPDATE movimientos_tesoreria m
           SET id_empresa = pv.id_empresa
          FROM puntos_venta pv
         WHERE pv.id_punto_venta = m.id_punto_venta
           AND pv.id_tenant = m.id_tenant
           AND m.id_empresa = 0;
        """;

    /// <summary>El mismo statement que el backfill 4/4 (re-encadenado por empresa).</summary>
    private const string SqlRecomputarCadena =
        """
        WITH cadena AS (
            SELECT
                id_movimiento,
                SUM(ingreso - egreso) OVER (
                    PARTITION BY id_tenant, id_empresa
                    ORDER BY fecha, id_movimiento
                    ROWS BETWEEN UNBOUNDED PRECEDING AND CURRENT ROW
                ) AS final_acumulado
            FROM movimientos_tesoreria
        )
        UPDATE movimientos_tesoreria m
           SET inicio = c.final_acumulado - (m.ingreso - m.egreso),
               final  = c.final_acumulado
          FROM cadena c
         WHERE c.id_movimiento = m.id_movimiento;
        """;

    [Fact]
    public async Task ElBackfillDeEmpresaYElReencadenadoDeTesoreriaSoloAlcanzanFilasEnModoPlataformaYMergeanPorEmpresa()
    {
        var nombreBase = $"ways_gastos_tesoreria_{Guid.NewGuid():N}";
        var cadenaAdmin = new NpgsqlConnectionStringBuilder(fixture.OwnerConnectionString) { Database = "postgres" }.ConnectionString;
        var cadenaOwner = new NpgsqlConnectionStringBuilder(fixture.OwnerConnectionString) { Database = nombreBase }.ConnectionString;

        await using (var admin = new NpgsqlConnection(cadenaAdmin))
        {
            await admin.OpenAsync();
            await using var crear = admin.CreateCommand();
            crear.CommandText = $"CREATE DATABASE \"{nombreBase}\"";
            await crear.ExecuteNonQueryAsync();
        }

        try
        {
            // Migra la base nueva SOLO hasta la migración inmediatamente anterior a la mía: sin
            // id_empresa/id_gasto en movimientos_tesoreria, sin id_empresa/origen_fondos en gastos,
            // id_turno_caja de gastos todavía NOT NULL. OrigenFondosGasto no se mapea a propósito:
            // el tipo origen_fondos_gasto todavía no existe en este punto de la historia.
            var opciones = new DbContextOptionsBuilder<WaysDbContext>()
                .UseNpgsql(cadenaOwner, npgsql =>
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
                    npgsql.MapEnum<CategoriaGasto>("categoria_gasto");
                    npgsql.MapEnum<EstadoCompra>("estado_compra");
                    npgsql.MapEnum<TipoMovimientoCcProveedor>("tipo_movimiento_cc_proveedor");
                    npgsql.MapEnum<EstadoOrdenCompra>("estado_orden_compra");
                    npgsql.MapEnum<EstadoPresupuesto>("estado_presupuesto");
                    npgsql.MapEnum<EstadoRemito>("estado_remito");
                    npgsql.MapEnum<Ways.Domain.Fiscal.ResultadoFiscal>("resultado_fiscal");
                    npgsql.MapEnum<Ways.Domain.Fiscal.AmbienteFiscal>("ambiente_fiscal");
                    npgsql.MapEnum<ModoPuntoVenta>("modo_punto_venta");
                    // OrigenFondosGasto NO se mapea: origen_fondos_gasto todavía no existe.
                })
                .ConfigureWarnings(w => w.Ignore(RelationalEventId.PendingModelChangesWarning))
                .Options;

            await using (var migrando = new WaysDbContext(opciones, TenantActualFijo.Plataforma))
            {
                var migrador = migrando.Database.GetInfrastructure().GetRequiredService<IMigrator>();
                await migrador.MigrateAsync(MigracionAnterior);
            }

            await using (var owner = new NpgsqlConnection(cadenaOwner))
            {
                await owner.OpenAsync();
                await using var comando = owner.CreateCommand();
                comando.CommandText =
                    """
                    GRANT USAGE ON SCHEMA public TO ways_app;
                    GRANT SELECT, INSERT, UPDATE, DELETE ON ALL TABLES IN SCHEMA public TO ways_app;
                    """;
                await comando.ExecuteNonQueryAsync();
            }

            int idTenant;
            int idPuntoVenta1, idPuntoVenta2, idPuntoVenta3;
            int idGasto;
            int idMovPv1Antes, idMovPv2Medio, idMovPv1Despues, idMovEmpresaY;
            var t1 = new DateTimeOffset(2026, 9, 1, 10, 0, 0, TimeSpan.Zero);
            var t2 = new DateTimeOffset(2026, 9, 1, 11, 0, 0, TimeSpan.Zero);
            var t3 = new DateTimeOffset(2026, 9, 1, 12, 0, 0, TimeSpan.Zero);
            var tEmpresaY = new DateTimeOffset(2026, 9, 1, 10, 30, 0, TimeSpan.Zero); // entre t1 y t2

            await using (var owner = new NpgsqlConnection(cadenaOwner))
            {
                await owner.OpenAsync();

                async Task<int> EscalarAsync(string sql, params object?[] parametros)
                {
                    await using var comando = owner.CreateCommand();
                    comando.CommandText = sql;
                    foreach (var valor in parametros)
                    {
                        comando.Parameters.Add(new NpgsqlParameter { Value = valor ?? DBNull.Value });
                    }

                    return (int)(await comando.ExecuteScalarAsync())!;
                }

                async Task EjecutarAsync(string sql, params object?[] parametros)
                {
                    await using var comando = owner.CreateCommand();
                    comando.CommandText = sql;
                    foreach (var valor in parametros)
                    {
                        comando.Parameters.Add(new NpgsqlParameter { Value = valor ?? DBNull.Value });
                    }

                    await comando.ExecuteNonQueryAsync();
                }

                await using (var comandoRol = owner.CreateCommand())
                {
                    comandoRol.CommandText =
                        "INSERT INTO roles (id_rol, nombre, created_at, updated_at) VALUES ($1, 'admin', now(), now())";
                    comandoRol.Parameters.Add(new NpgsqlParameter { Value = (int)RolConocido.Admin });
                    await comandoRol.ExecuteNonQueryAsync();
                }

                idTenant = await EscalarAsync(
                    "INSERT INTO tenants (nombre, estado, created_at, updated_at) " +
                    "VALUES ($1, 'activo'::estado_tenant, now(), now()) RETURNING id_tenant",
                    "Tenant Backfill Tesorería");

                var idEmpresaX = await EscalarAsync(
                    "INSERT INTO empresas (id_tenant, razon_social, created_at, updated_at) " +
                    "VALUES ($1, $2, now(), now()) RETURNING id_empresa",
                    idTenant, "Empresa X");
                var idEmpresaY = await EscalarAsync(
                    "INSERT INTO empresas (id_tenant, razon_social, created_at, updated_at) " +
                    "VALUES ($1, $2, now(), now()) RETURNING id_empresa",
                    idTenant, "Empresa Y");

                idPuntoVenta1 = await EscalarAsync(
                    "INSERT INTO puntos_venta (id_tenant, id_empresa, nombre, modo, created_at, updated_at) " +
                    "VALUES ($1, $2, $3, 'web'::modo_punto_venta, now(), now()) RETURNING id_punto_venta",
                    idTenant, idEmpresaX, "PV1 Empresa X");
                idPuntoVenta2 = await EscalarAsync(
                    "INSERT INTO puntos_venta (id_tenant, id_empresa, nombre, modo, created_at, updated_at) " +
                    "VALUES ($1, $2, $3, 'web'::modo_punto_venta, now(), now()) RETURNING id_punto_venta",
                    idTenant, idEmpresaX, "PV2 Empresa X");
                idPuntoVenta3 = await EscalarAsync(
                    "INSERT INTO puntos_venta (id_tenant, id_empresa, nombre, modo, created_at, updated_at) " +
                    "VALUES ($1, $2, $3, 'web'::modo_punto_venta, now(), now()) RETURNING id_punto_venta",
                    idTenant, idEmpresaY, "PV3 Empresa Y");

                var idUsuario = await EscalarAsync(
                    "INSERT INTO usuarios (id_tenant, usuario, mail, id_rol, password_hash, password_algoritmo, " +
                    "password_actualizado_el, created_at, updated_at) " +
                    "VALUES ($1, 'admin', $2, $3, 'hash', 'test', now(), now(), now()) RETURNING id_usuario",
                    idTenant, "backfill-tesoreria-admin@ways.test", (int)RolConocido.Admin);

                var idMedioPago = await EscalarAsync(
                    "INSERT INTO medios_pago (id_tenant, nombre, orden, comportamiento, admite_vuelto, " +
                    "requiere_referencia, activo, created_at, updated_at) " +
                    "VALUES ($1, 'Efectivo', 1, 'efectivo'::comportamiento_medio_pago, true, false, true, now(), now()) " +
                    "RETURNING id_medio_pago",
                    idTenant);

                var idTurno1 = await EscalarAsync(
                    "INSERT INTO turnos_caja (id_tenant, id_punto_venta, id_empleado_apertura, fecha_apertura, " +
                    "fondo_inicial, estado, created_at, updated_at) " +
                    "VALUES ($1, $2, $3, $4, 0, 'abierto'::estado_turno, now(), now()) RETURNING id_turno_caja",
                    idTenant, idPuntoVenta1, idUsuario, t1);

                // gastos: una sola fila sobre PV1 — prueba el backfill 2/4 (id_empresa desde
                // puntos_venta), id_turno_caja pre-migración todavía NOT NULL.
                idGasto = await EscalarAsync(
                    "INSERT INTO gastos (id_tenant, fecha, id_punto_venta, id_turno_caja, id_empleado, categoria, " +
                    "concepto, id_medio_pago, importe, created_at, updated_at) " +
                    "VALUES ($1, $2, $3, $4, $5, 'otros'::categoria_gasto, 'Gasto de prueba', $6, 100, now(), now()) " +
                    "RETURNING id_gasto",
                    idTenant, t1, idPuntoVenta1, idTurno1, idUsuario, idMedioPago);

                // movimientos_tesoreria: cadena vieja por PUNTO DE VENTA (cada PV arranca en 0) —
                // exactamente lo que produciría ServicioDeTurnos ANTES de este PR. Intercaladas por
                // fecha entre PV1/PV2 (empresa X) y con una fila de PV3 (empresa Y) en el medio.
                idMovPv1Antes = await EscalarAsync(
                    "INSERT INTO movimientos_tesoreria (id_tenant, id_punto_venta, fecha, tipo, concepto, " +
                    "inicio, ingreso, egreso, final, id_empleado) " +
                    "VALUES ($1, $2, $3, 'retiro_caja'::tipo_movimiento_tesoreria, 'Cierre PV1 #1', 0, 100, 0, 100, $4) " +
                    "RETURNING id_movimiento",
                    idTenant, idPuntoVenta1, t1, idUsuario);

                idMovEmpresaY = await EscalarAsync(
                    "INSERT INTO movimientos_tesoreria (id_tenant, id_punto_venta, fecha, tipo, concepto, " +
                    "inicio, ingreso, egreso, final, id_empleado) " +
                    "VALUES ($1, $2, $3, 'retiro_caja'::tipo_movimiento_tesoreria, 'Cierre PV3 #1', 0, 999, 0, 999, $4) " +
                    "RETURNING id_movimiento",
                    idTenant, idPuntoVenta3, tEmpresaY, idUsuario);

                idMovPv2Medio = await EscalarAsync(
                    "INSERT INTO movimientos_tesoreria (id_tenant, id_punto_venta, fecha, tipo, concepto, " +
                    "inicio, ingreso, egreso, final, id_empleado) " +
                    "VALUES ($1, $2, $3, 'retiro_caja'::tipo_movimiento_tesoreria, 'Cierre PV2 #1', 0, 50, 20, 30, $4) " +
                    "RETURNING id_movimiento",
                    idTenant, idPuntoVenta2, t2, idUsuario);

                idMovPv1Despues = await EscalarAsync(
                    "INSERT INTO movimientos_tesoreria (id_tenant, id_punto_venta, fecha, tipo, concepto, " +
                    "inicio, ingreso, egreso, final, id_empleado) " +
                    "VALUES ($1, $2, $3, 'retiro_caja'::tipo_movimiento_tesoreria, 'Cierre PV1 #2', 100, 0, 40, 60, $4) " +
                    "RETURNING id_movimiento",
                    idTenant, idPuntoVenta1, t3, idUsuario);

                // Reproduce a mano el estado exacto en el que arranca mi migración: las columnas
                // nuevas agregadas sueltas (AddColumn), sin backfill todavía.
                await EjecutarAsync("CREATE TYPE origen_fondos_gasto AS ENUM ('caja_turno', 'tesoreria');");
                await EjecutarAsync("ALTER TABLE gastos ALTER COLUMN id_turno_caja DROP NOT NULL;");
                await EjecutarAsync("ALTER TABLE gastos ADD COLUMN id_empresa integer NOT NULL DEFAULT 0;");
                await EjecutarAsync(
                    "ALTER TABLE gastos ADD COLUMN origen_fondos origen_fondos_gasto NOT NULL DEFAULT 'caja_turno';");
                await EjecutarAsync(
                    "ALTER TABLE movimientos_tesoreria ADD COLUMN id_empresa integer NOT NULL DEFAULT 0;");
                await EjecutarAsync(
                    "ALTER TABLE movimientos_tesoreria ADD COLUMN id_gasto integer NULL;");
            }

            var cadenaApp = new NpgsqlConnectionStringBuilder(fixture.AppConnectionString) { Database = nombreBase }.ConnectionString;

            // (a) SIN el SET LOCAL: ways_app sin GUC -> RLS no deja ver ninguna fila de ningún
            // tenant — los tres statements afectan CERO filas y no revientan.
            await using (var cruda = new NpgsqlConnection(cadenaApp))
            {
                await cruda.OpenAsync();

                await using var comandoGastos = cruda.CreateCommand();
                comandoGastos.CommandText = SqlBackfillIdEmpresaGastos;
                Assert.Equal(0, await comandoGastos.ExecuteNonQueryAsync());

                await using var comandoMovimientos = cruda.CreateCommand();
                comandoMovimientos.CommandText = SqlBackfillIdEmpresaMovimientos;
                Assert.Equal(0, await comandoMovimientos.ExecuteNonQueryAsync());

                await using var comandoCadena = cruda.CreateCommand();
                comandoCadena.CommandText = SqlRecomputarCadena;
                Assert.Equal(0, await comandoCadena.ExecuteNonQueryAsync());
            }

            // (b) CON el SET LOCAL, mismo bloque que la migración.
            await using (var cruda = new NpgsqlConnection(cadenaApp))
            {
                await cruda.OpenAsync();

                await using var comandoGastos = cruda.CreateCommand();
                comandoGastos.CommandText = "SET LOCAL app.acceso = 'plataforma';\n" + SqlBackfillIdEmpresaGastos;
                Assert.Equal(1, await comandoGastos.ExecuteNonQueryAsync());

                await using var comandoMovimientos = cruda.CreateCommand();
                comandoMovimientos.CommandText = "SET LOCAL app.acceso = 'plataforma';\n" + SqlBackfillIdEmpresaMovimientos;
                Assert.Equal(4, await comandoMovimientos.ExecuteNonQueryAsync());

                await using var comandoCadena = cruda.CreateCommand();
                comandoCadena.CommandText = "SET LOCAL app.acceso = 'plataforma';\n" + SqlRecomputarCadena;
                Assert.Equal(4, await comandoCadena.ExecuteNonQueryAsync());
            }

            await using (var verificacion = new NpgsqlConnection(cadenaOwner))
            {
                await verificacion.OpenAsync();

                async Task<(int IdEmpresa, decimal Inicio, decimal Final)> LeerMovimientoAsync(int idMovimiento)
                {
                    await using var comando = verificacion.CreateCommand();
                    comando.CommandText =
                        "SELECT id_empresa, inicio, final FROM movimientos_tesoreria WHERE id_movimiento = $1";
                    comando.Parameters.Add(new NpgsqlParameter { Value = idMovimiento });
                    await using var lector = await comando.ExecuteReaderAsync();
                    await lector.ReadAsync();
                    return (lector.GetInt32(0), lector.GetDecimal(1), lector.GetDecimal(2));
                }

                await using (var comandoGasto = verificacion.CreateCommand())
                {
                    comandoGasto.CommandText = "SELECT id_empresa FROM gastos WHERE id_gasto = $1";
                    comandoGasto.Parameters.Add(new NpgsqlParameter { Value = idGasto });
                    var idEmpresaDelGasto = (int)(await comandoGasto.ExecuteScalarAsync())!;

                    await using var comandoPv = verificacion.CreateCommand();
                    comandoPv.CommandText = "SELECT id_empresa FROM puntos_venta WHERE id_punto_venta = $1";
                    comandoPv.Parameters.Add(new NpgsqlParameter { Value = idPuntoVenta1 });
                    var idEmpresaEsperada = (int)(await comandoPv.ExecuteScalarAsync())!;

                    Assert.Equal(idEmpresaEsperada, idEmpresaDelGasto);
                }

                // Cadena merged de la empresa X, ordenada por fecha (t1 < t2 < t3), IGNORANDO la
                // fila de PV3/empresa Y que cae cronológicamente en el medio (t1 < tEmpresaY < t2):
                // si la partición fuera solo por id_tenant, esta fila se colaría entre PV1#1 y
                // PV2#1 y los totales de abajo no cerrarían.
                var pv1Antes = await LeerMovimientoAsync(idMovPv1Antes);
                Assert.Equal(0m, pv1Antes.Inicio);
                Assert.Equal(100m, pv1Antes.Final);

                var pv2Medio = await LeerMovimientoAsync(idMovPv2Medio);
                Assert.Equal(100m, pv2Medio.Inicio); // encadena desde el final de PV1#1 — YA NO desde 0
                Assert.Equal(130m, pv2Medio.Final);

                var pv1Despues = await LeerMovimientoAsync(idMovPv1Despues);
                Assert.Equal(130m, pv1Despues.Inicio); // encadena desde PV2#1, no desde el propio PV1#1
                Assert.Equal(90m, pv1Despues.Final);

                // Empresa Y: cadena propia, intacta, arranca en 0 pese a caer cronológicamente
                // adentro del rango de la empresa X.
                var movEmpresaY = await LeerMovimientoAsync(idMovEmpresaY);
                Assert.Equal(0m, movEmpresaY.Inicio);
                Assert.Equal(999m, movEmpresaY.Final);

                // id_empresa de las cuatro filas coincide con el de su punto de venta de origen.
                await using var comandoEmpresaX = verificacion.CreateCommand();
                comandoEmpresaX.CommandText = "SELECT id_empresa FROM puntos_venta WHERE id_punto_venta = $1";
                comandoEmpresaX.Parameters.Add(new NpgsqlParameter { Value = idPuntoVenta1 });
                var idEmpresaXEsperada = (int)(await comandoEmpresaX.ExecuteScalarAsync())!;

                Assert.Equal(idEmpresaXEsperada, pv1Antes.IdEmpresa);
                Assert.Equal(idEmpresaXEsperada, pv2Medio.IdEmpresa);
                Assert.Equal(idEmpresaXEsperada, pv1Despues.IdEmpresa);
                Assert.NotEqual(idEmpresaXEsperada, movEmpresaY.IdEmpresa);
            }

            // (c) Idempotencia: los backfills de id_empresa ya no encuentran filas en 0; el
            // re-encadenado, al ser un recompute puro (no un guard de exclusión), vuelve a afectar
            // las cuatro filas pero converge a los MISMOS valores — ver el comentario de la
            // migración sobre por qué esto SÍ cuenta como idempotente.
            await using (var cruda = new NpgsqlConnection(cadenaApp))
            {
                await cruda.OpenAsync();

                await using var comandoGastos = cruda.CreateCommand();
                comandoGastos.CommandText = "SET LOCAL app.acceso = 'plataforma';\n" + SqlBackfillIdEmpresaGastos;
                Assert.Equal(0, await comandoGastos.ExecuteNonQueryAsync());

                await using var comandoMovimientos = cruda.CreateCommand();
                comandoMovimientos.CommandText = "SET LOCAL app.acceso = 'plataforma';\n" + SqlBackfillIdEmpresaMovimientos;
                Assert.Equal(0, await comandoMovimientos.ExecuteNonQueryAsync());

                await using var comandoCadena = cruda.CreateCommand();
                comandoCadena.CommandText = "SET LOCAL app.acceso = 'plataforma';\n" + SqlRecomputarCadena;
                Assert.Equal(4, await comandoCadena.ExecuteNonQueryAsync());
            }

            await using (var verificacion = new NpgsqlConnection(cadenaOwner))
            {
                await verificacion.OpenAsync();
                await using var comando = verificacion.CreateCommand();
                comando.CommandText = "SELECT inicio, final FROM movimientos_tesoreria WHERE id_movimiento = $1";
                comando.Parameters.Add(new NpgsqlParameter { Value = idMovPv1Despues });
                await using var lector = await comando.ExecuteReaderAsync();
                await lector.ReadAsync();
                Assert.Equal(130m, lector.GetDecimal(0));
                Assert.Equal(90m, lector.GetDecimal(1));
            }
        }
        finally
        {
            await using var admin = new NpgsqlConnection(cadenaAdmin);
            await admin.OpenAsync();
            await using var dropear = admin.CreateCommand();
            dropear.CommandText = $"DROP DATABASE IF EXISTS \"{nombreBase}\" WITH (FORCE)";
            await dropear.ExecuteNonQueryAsync();
        }
    }
}
