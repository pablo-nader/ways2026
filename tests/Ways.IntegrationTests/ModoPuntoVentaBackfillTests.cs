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
/// stage-desktop-pos (rls-migration-backfills): los DOS bloques <c>Sql()</c> de
/// <c>ModoPuntoVentaYDispositivoActivoUnico</c> — el backfill de <c>puntos_venta.modo</c> y el
/// dedup de dispositivos activos duplicados — sobre <c>ways_app</c> (NOSUPERUSER NOBYPASSRLS),
/// mutation-proven (sin el <c>SET LOCAL app.acceso = 'plataforma'</c>, cero filas). El SQL está
/// DUPLICADO a propósito respecto de la migración (mismo criterio que <c>CostoCongeladoTests</c>):
/// una migración es un snapshot congelado, no debe depender de una constante compartida.
///
/// Corre sobre una base AISLADA, migrada solo hasta <see cref="MigracionAnterior"/> (la
/// inmediatamente anterior): a diferencia del backfill de costo (stage 9), acá la columna nueva
/// termina <c>NOT NULL</c> y el índice nuevo es único — pasado ese punto, ni "modo IS NULL" ni "dos
/// dispositivos activos del mismo punto de venta" son alcanzables en la base compartida ya
/// migrada a HEAD, así que la única forma de probar el mecanismo es reproducir el estado
/// pre-migración a mano (mismo criterio que la prueba "ingenua" de <c>CostoCongeladoTests</c>,
/// pero acá SÍ se ejercita <c>ways_app</c> porque el estado pre-migración no depende de RLS para
/// existir).
///
/// judgment-day ronda 1 (hallazgo CRITICAL 3, mutation-proof-tests): el dedup ordena por
/// <c>created_at DESC, id_dispositivo DESC</c> — <c>ultimo_uso_at</c> quedó afuera a propósito
/// (ver el doc-comment de la migración). El grupo Escritorio prueba la primera dimensión
/// INVIRTIENDO <c>ultimo_uso_at</c> respecto de <c>created_at</c> entre los dos duplicados: si un
/// mutante reintrodujera <c>ultimo_uso_at</c> en el <c>ORDER BY</c>, el sobreviviente cambiaría y
/// el test lo detecta. El grupo Empate prueba la segunda dimensión con dos duplicados que
/// COMPARTEN el mismo <c>created_at</c> exacto — ahí <c>id_dispositivo DESC</c> es la ÚNICA señal
/// que decide, así que invertir ese ASC/DESC también hace fallar el test.
/// </summary>
[Collection("Ways.IntegrationTests secuencial")]
public class ModoPuntoVentaBackfillTests(WaysApiFixture fixture) : IClassFixture<WaysApiFixture>
{
    private const string MigracionAnterior = "20260919185344_TurnosCajaMedioPagoEfectivo";

    /// <summary>El mismo statement que el paso 2 de <c>ModoPuntoVentaYDispositivoActivoUnico.Up</c>.</summary>
    private const string SqlBackfillModo =
        """
        UPDATE puntos_venta p
           SET modo = CASE
                        WHEN EXISTS (
                            SELECT 1
                              FROM dispositivos d
                             WHERE d.id_punto_venta = p.id_punto_venta
                               AND d.id_tenant = p.id_tenant
                               AND d.deleted_at IS NULL
                        )
                        THEN 'escritorio'
                        ELSE 'web'
                      END::modo_punto_venta
         WHERE p.modo IS NULL;
        """;

    /// <summary>El mismo statement que el paso 3 (dedup) de <c>ModoPuntoVentaYDispositivoActivoUnico.Up</c>
    /// — sin <c>ultimo_uso_at</c> en el <c>ORDER BY</c> (judgment-day ronda 1, hallazgo CRITICAL 3):
    /// lo escribe únicamente un login de dispositivo, así que con sesiones de 365 días puede
    /// quedar viejísimo en la caja realmente en uso y revocar la que sigue vendiendo a favor de la
    /// que recién se repareó.</summary>
    private const string SqlDedupDispositivos =
        """
        WITH duplicados AS (
            SELECT id_dispositivo,
                   row_number() OVER (
                       PARTITION BY id_tenant, id_punto_venta
                       ORDER BY created_at DESC, id_dispositivo DESC
                   ) AS orden
              FROM dispositivos
             WHERE deleted_at IS NULL
        )
        UPDATE dispositivos d
           SET deleted_at = now(), updated_at = now()
          FROM duplicados
         WHERE d.id_dispositivo = duplicados.id_dispositivo
           AND duplicados.orden > 1;
        """;

    [Fact]
    public async Task ElBackfillDeModoYElDedupDeDispositivosSoloAlcanzanFilasEnModoPlataformaYSonIdempotentes()
    {
        var nombreBase = $"ways_modo_pv_{Guid.NewGuid():N}";
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
            // columna `modo`, sin el índice único de dispositivos — el estado exacto en el que mi
            // migración arranca. `ModoPuntoVenta` no se mapea a propósito: ese tipo Postgres
            // todavía no existe en este punto de la historia.
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
                    // ModoPuntoVenta NO se mapea: el tipo modo_punto_venta todavía no existe en
                    // MigracionAnterior, que es exactamente el estado que este test necesita.
                })
                .ConfigureWarnings(w => w.Ignore(RelationalEventId.PendingModelChangesWarning))
                .Options;

            await using (var migrando = new WaysDbContext(opciones, TenantActualFijo.Plataforma))
            {
                var migrador = migrando.Database.GetInfrastructure().GetRequiredService<IMigrator>();
                await migrador.MigrateAsync(MigracionAnterior);
            }

            // ways_app ya existe a nivel cluster (lo crea WaysApiFixture.InitializeAsync una sola
            // vez, sobre SU base) pero los GRANTs son por base — una base nueva no los hereda,
            // igual que el camino real de deploy (WaysDbContextFactory) tampoco corre el
            // interceptor de tenant.
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

            int idTenantEscritorio, idPuntoVentaEscritorio, idDispositivoViejo, idDispositivoNuevo;
            int idTenantWeb, idPuntoVentaWeb;
            int idTenantEmpate, idPuntoVentaEmpate, idDispositivoEmpateBajo, idDispositivoEmpateAlto;

            // Semilla a mano, como dueño (bypassa RLS, sin necesidad de ningún GUC) — schema
            // todavía sin `modo` ni el índice único.
            //
            // Tenant Escritorio: un punto de venta con DOS dispositivos activos duplicados (el
            // hallazgo real de la base de desarrollo) — acá `ultimo_uso_at` se INVIERTE a
            // propósito respecto de `created_at` (judgment-day ronda 1, hallazgo CRITICAL 3): "Caja
            // vieja" (pareja más antigua) tiene el login MÁS RECIENTE, "Caja nueva" (pareja más
            // reciente) tiene el login MÁS VIEJO — el escenario real de una PC repareada que el
            // cajero todavía no volvió a loguear. Si el dedup mirara `ultimo_uso_at` (el bug
            // corregido), sobreviviría "Caja vieja"; con `created_at DESC` sobrevive "Caja nueva",
            // que es la pareja vigente.
            //
            // Tenant Web: un punto de venta sin ningún dispositivo.
            //
            // Tenant Empate: un punto de venta con DOS dispositivos activos que comparten el MISMO
            // `created_at` exacto — el único desempate posible es `id_dispositivo DESC`, así que
            // este grupo es el que prueba esa segunda dimensión del ORDER BY en aislamiento.
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

                var ahora = DateTimeOffset.UtcNow;

                // `roles` es [global] y no se siembra sola en una base propia (mismo trap
                // documentado en CostoCongeladoTests) — hace falta a mano para que
                // fk_usuarios_rol no reviente al insertar el usuario de alta de cada dispositivo.
                await using (var comandoRol = owner.CreateCommand())
                {
                    comandoRol.CommandText =
                        "INSERT INTO roles (id_rol, nombre, created_at, updated_at) VALUES ($1, 'admin', now(), now())";
                    comandoRol.Parameters.Add(new NpgsqlParameter { Value = (int)RolConocido.Admin });
                    await comandoRol.ExecuteNonQueryAsync();
                }

                idTenantEscritorio = await EscalarAsync(
                    "INSERT INTO tenants (nombre, estado, created_at, updated_at) " +
                    "VALUES ($1, 'activo'::estado_tenant, now(), now()) RETURNING id_tenant",
                    "Tenant Escritorio");
                var idEmpresaEscritorio = await EscalarAsync(
                    "INSERT INTO empresas (id_tenant, razon_social, created_at, updated_at) " +
                    "VALUES ($1, $2, now(), now()) RETURNING id_empresa",
                    idTenantEscritorio, "Empresa Escritorio");
                idPuntoVentaEscritorio = await EscalarAsync(
                    "INSERT INTO puntos_venta (id_tenant, id_empresa, nombre, created_at, updated_at) " +
                    "VALUES ($1, $2, $3, now(), now()) RETURNING id_punto_venta",
                    idTenantEscritorio, idEmpresaEscritorio, "PV Escritorio");
                var idUsuarioEscritorio = await EscalarAsync(
                    "INSERT INTO usuarios (id_tenant, usuario, mail, id_rol, password_hash, password_algoritmo, " +
                    "password_actualizado_el, created_at, updated_at) " +
                    "VALUES ($1, 'admin', $2, $3, 'hash', 'test', now(), now(), now()) RETURNING id_usuario",
                    idTenantEscritorio, "escritorio-admin@ways.test", (int)RolConocido.Admin);

                // ultimo_uso_at INVERTIDO respecto de created_at (judgment-day ronda 1, hallazgo
                // CRITICAL 3): "Caja vieja" es la pareja más ANTIGUA pero con el login MÁS
                // RECIENTE; "Caja nueva" es la pareja más RECIENTE pero con el login MÁS VIEJO. Si
                // el dedup mirara ultimo_uso_at, sobreviviría "Caja vieja" — con created_at DESC
                // sobrevive "Caja nueva".
                idDispositivoViejo = await EscalarAsync(
                    "INSERT INTO dispositivos (id_tenant, id_punto_venta, nombre, token_hash, id_usuario_alta, " +
                    "ultimo_uso_at, created_at, updated_at) " +
                    "VALUES ($1, $2, 'Caja vieja', $3, $4, $5, $6, $6) RETURNING id_dispositivo",
                    idTenantEscritorio, idPuntoVentaEscritorio, new string('a', 64), idUsuarioEscritorio,
                    ahora.AddHours(-1), ahora.AddDays(-2));
                idDispositivoNuevo = await EscalarAsync(
                    "INSERT INTO dispositivos (id_tenant, id_punto_venta, nombre, token_hash, id_usuario_alta, " +
                    "ultimo_uso_at, created_at, updated_at) " +
                    "VALUES ($1, $2, 'Caja nueva', $3, $4, $5, $6, $6) RETURNING id_dispositivo",
                    idTenantEscritorio, idPuntoVentaEscritorio, new string('b', 64), idUsuarioEscritorio,
                    ahora.AddDays(-2), ahora.AddHours(-1));

                idTenantWeb = await EscalarAsync(
                    "INSERT INTO tenants (nombre, estado, created_at, updated_at) " +
                    "VALUES ($1, 'activo'::estado_tenant, now(), now()) RETURNING id_tenant",
                    "Tenant Web");
                var idEmpresaWeb = await EscalarAsync(
                    "INSERT INTO empresas (id_tenant, razon_social, created_at, updated_at) " +
                    "VALUES ($1, $2, now(), now()) RETURNING id_empresa",
                    idTenantWeb, "Empresa Web");
                idPuntoVentaWeb = await EscalarAsync(
                    "INSERT INTO puntos_venta (id_tenant, id_empresa, nombre, created_at, updated_at) " +
                    "VALUES ($1, $2, $3, now(), now()) RETURNING id_punto_venta",
                    idTenantWeb, idEmpresaWeb, "PV Web");

                // Tenant Empate: DOS dispositivos activos con el MISMO created_at exacto — el
                // único desempate posible es id_dispositivo DESC. ultimo_uso_at en NULL en los dos
                // (irrelevante para este grupo: ya no entra en el ORDER BY).
                idTenantEmpate = await EscalarAsync(
                    "INSERT INTO tenants (nombre, estado, created_at, updated_at) " +
                    "VALUES ($1, 'activo'::estado_tenant, now(), now()) RETURNING id_tenant",
                    "Tenant Empate");
                var idEmpresaEmpate = await EscalarAsync(
                    "INSERT INTO empresas (id_tenant, razon_social, created_at, updated_at) " +
                    "VALUES ($1, $2, now(), now()) RETURNING id_empresa",
                    idTenantEmpate, "Empresa Empate");
                idPuntoVentaEmpate = await EscalarAsync(
                    "INSERT INTO puntos_venta (id_tenant, id_empresa, nombre, created_at, updated_at) " +
                    "VALUES ($1, $2, $3, now(), now()) RETURNING id_punto_venta",
                    idTenantEmpate, idEmpresaEmpate, "PV Empate");
                var idUsuarioEmpate = await EscalarAsync(
                    "INSERT INTO usuarios (id_tenant, usuario, mail, id_rol, password_hash, password_algoritmo, " +
                    "password_actualizado_el, created_at, updated_at) " +
                    "VALUES ($1, 'admin', $2, $3, 'hash', 'test', now(), now(), now()) RETURNING id_usuario",
                    idTenantEmpate, "empate-admin@ways.test", (int)RolConocido.Admin);

                var creadoEmpate = ahora.AddDays(-3);
                idDispositivoEmpateBajo = await EscalarAsync(
                    "INSERT INTO dispositivos (id_tenant, id_punto_venta, nombre, token_hash, id_usuario_alta, " +
                    "ultimo_uso_at, created_at, updated_at) " +
                    "VALUES ($1, $2, 'Caja empate 1', $3, $4, NULL, $5, $5) RETURNING id_dispositivo",
                    idTenantEmpate, idPuntoVentaEmpate, new string('c', 64), idUsuarioEmpate, creadoEmpate);
                idDispositivoEmpateAlto = await EscalarAsync(
                    "INSERT INTO dispositivos (id_tenant, id_punto_venta, nombre, token_hash, id_usuario_alta, " +
                    "ultimo_uso_at, created_at, updated_at) " +
                    "VALUES ($1, $2, 'Caja empate 2', $3, $4, NULL, $5, $5) RETURNING id_dispositivo",
                    idTenantEmpate, idPuntoVentaEmpate, new string('d', 64), idUsuarioEmpate, creadoEmpate);

                // `modo` nullable a mano: es exactamente el primer paso de la migración real
                // (AddColumn nullable), aislado del resto para poder ejercitar el backfill solo.
                await using var tipo = owner.CreateCommand();
                tipo.CommandText = "CREATE TYPE modo_punto_venta AS ENUM ('escritorio', 'web');";
                await tipo.ExecuteNonQueryAsync();

                await using var columna = owner.CreateCommand();
                columna.CommandText = "ALTER TABLE puntos_venta ADD COLUMN modo modo_punto_venta;";
                await columna.ExecuteNonQueryAsync();
            }

            var cadenaApp = new NpgsqlConnectionStringBuilder(fixture.AppConnectionString) { Database = nombreBase }.ConnectionString;

            // (a) SIN el SET LOCAL: ways_app sin GUC -> app_es_plataforma() = false,
            // app_tenant_actual() = NULL -> RLS no deja ver ninguna fila de ningún tenant; los dos
            // UPDATE afectan CERO filas y no revientan (mismo mecanismo que CostoCongeladoTests).
            await using (var cruda = new NpgsqlConnection(cadenaApp))
            {
                await cruda.OpenAsync();

                await using var comandoModo = cruda.CreateCommand();
                comandoModo.CommandText = SqlBackfillModo;
                Assert.Equal(0, await comandoModo.ExecuteNonQueryAsync());

                await using var comandoDedup = cruda.CreateCommand();
                comandoDedup.CommandText = SqlDedupDispositivos;
                Assert.Equal(0, await comandoDedup.ExecuteNonQueryAsync());
            }

            // (b) CON el SET LOCAL, mismo bloque que la migración: alcanza los TRES tenants
            // (Escritorio, Web, Empate) de una sola pasada.
            await using (var cruda = new NpgsqlConnection(cadenaApp))
            {
                await cruda.OpenAsync();

                await using var comandoModo = cruda.CreateCommand();
                comandoModo.CommandText = "SET LOCAL app.acceso = 'plataforma';\n" + SqlBackfillModo;
                Assert.Equal(3, await comandoModo.ExecuteNonQueryAsync());

                await using var comandoDedup = cruda.CreateCommand();
                comandoDedup.CommandText = "SET LOCAL app.acceso = 'plataforma';\n" + SqlDedupDispositivos;
                // Un duplicado revocado por partición: Escritorio (created_at DESC) + Empate
                // (id_dispositivo DESC de desempate) = 2.
                Assert.Equal(2, await comandoDedup.ExecuteNonQueryAsync());
            }

            await using (var verificacion = new NpgsqlConnection(cadenaOwner))
            {
                await verificacion.OpenAsync();

                async Task<string> LeerModoAsync(int idPuntoVenta)
                {
                    await using var comando = verificacion.CreateCommand();
                    comando.CommandText = "SELECT modo::text FROM puntos_venta WHERE id_punto_venta = $1";
                    comando.Parameters.Add(new NpgsqlParameter { Value = idPuntoVenta });
                    return (string)(await comando.ExecuteScalarAsync())!;
                }

                Assert.Equal("escritorio", await LeerModoAsync(idPuntoVentaEscritorio));
                Assert.Equal("web", await LeerModoAsync(idPuntoVentaWeb));
                Assert.Equal("escritorio", await LeerModoAsync(idPuntoVentaEmpate));

                async Task<bool> EstaActivoAsync(int idDispositivo)
                {
                    await using var comando = verificacion.CreateCommand();
                    comando.CommandText = "SELECT deleted_at IS NULL FROM dispositivos WHERE id_dispositivo = $1";
                    comando.Parameters.Add(new NpgsqlParameter { Value = idDispositivo });
                    return (bool)(await comando.ExecuteScalarAsync())!;
                }

                // Dimensión 1 (created_at DESC, ultimo_uso_at IGNORADO): sobrevive "Caja nueva"
                // (pareja más reciente) aunque "Caja vieja" tenga el login más reciente — si un
                // mutante reintrodujera ultimo_uso_at en el ORDER BY, este assert se invertiría.
                Assert.False(await EstaActivoAsync(idDispositivoViejo));
                Assert.True(await EstaActivoAsync(idDispositivoNuevo));

                // Dimensión 2 (id_dispositivo DESC de desempate, mismo created_at exacto):
                // sobrevive el de MAYOR id — si el ASC/DESC de este desempate se invirtiera, este
                // assert se invertiría también.
                Assert.False(await EstaActivoAsync(idDispositivoEmpateBajo));
                Assert.True(await EstaActivoAsync(idDispositivoEmpateAlto));
            }

            // (c) Idempotencia: reejecutar con el mismo SET LOCAL ya no encuentra nada que tocar.
            await using (var cruda = new NpgsqlConnection(cadenaApp))
            {
                await cruda.OpenAsync();

                await using var comandoModo = cruda.CreateCommand();
                comandoModo.CommandText = "SET LOCAL app.acceso = 'plataforma';\n" + SqlBackfillModo;
                Assert.Equal(0, await comandoModo.ExecuteNonQueryAsync());

                await using var comandoDedup = cruda.CreateCommand();
                comandoDedup.CommandText = "SET LOCAL app.acceso = 'plataforma';\n" + SqlDedupDispositivos;
                Assert.Equal(0, await comandoDedup.ExecuteNonQueryAsync());
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
