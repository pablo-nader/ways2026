using System.Runtime.CompilerServices;
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
using Ways.Domain.Gastos;
using Ways.Domain.Organizacion;
using Ways.Domain.Stock;
using Ways.Domain.Usuarios;
using Ways.Domain.Ventas;
using Ways.Infrastructure.Multitenancy;
using Ways.Infrastructure.Persistencia;

namespace Ways.IntegrationTests;

/// <summary>
/// El backfill de <c>RendicionSaldadaEnReservaNumeracion</c> (<c>rls-migration-backfills</c>): la
/// guarda de cierre dejó de excluir los bloques abandonados, así que cada bloque abandonado
/// HISTÓRICO entra en su alcance con <c>reportado_at NULL</c> — <c>SinReporte</c>, bloqueando para
/// siempre todos los cierres de su punto de venta sin que nadie pueda rendirlos. El backfill los da
/// por saldados de una vez, y solo a ellos.
///
/// Mismo patrón que <see cref="ModoPuntoVentaBackfillTests"/>: base AISLADA migrada hasta la
/// migración inmediatamente anterior, la columna nueva agregada a mano (que es literalmente el primer
/// paso de la migración real, aislado para poder ejercitar el backfill solo), y el <c>UPDATE</c>
/// corrido sobre <c>ways_app</c> (NOSUPERUSER NOBYPASSRLS) — nunca sobre el dueño, que bypassea RLS y
/// no probaría nada.
///
/// El SQL NO se copia a mano: se EXTRAE del archivo real de la migración, como hace
/// <see cref="TurnosCajaMedioPagoEfectivoMigracionTests"/> — una copia hardcodeada ejecuta el SQL
/// correcto sin importar lo que la migración diga, así que no detectaría que alguien le saque el
/// <c>SET LOCAL</c>. Acá sí lo detecta: la mitad (b) espera UNA fila afectada y sin el <c>SET
/// LOCAL</c> son cero.
/// </summary>
[Collection("Ways.IntegrationTests secuencial")]
public class RendicionSaldadaBackfillTests(WaysApiFixture fixture) : IClassFixture<WaysApiFixture>
{
    private const string MigracionAnterior = "20260926144614_RendicionDeColaDelDispositivo";
    private const string ArchivoDeLaMigracion = "20260927000933_RendicionSaldadaEnReservaNumeracion.cs";

    [Fact]
    public async Task LaColumnaExistePostMigracion()
    {
        using var _ = fixture.CreateClient();

        await using var cruda = await fixture.AbrirConexionCrudaAsync("plataforma", null);
        await using var comando = cruda.CreateCommand();
        comando.CommandText =
            "SELECT count(*) FROM information_schema.columns " +
            "WHERE table_schema = 'public' AND table_name = 'reservas_numeracion' " +
            "AND column_name = 'rendicion_saldada_at' AND is_nullable = 'YES'";

        Assert.Equal(1L, (long)(await comando.ExecuteScalarAsync())!);
    }

    /// <summary>
    /// LA CLÁUSULA: el <c>SET LOCAL app.acceso = 'plataforma'</c> DENTRO del mismo bloque
    /// <c>Sql()</c>. <c>reservas_numeracion</c> corre bajo FORCE ROW LEVEL SECURITY y el camino real
    /// de <c>dotnet ef database update</c> (<c>WaysDbContextFactory</c>) no registra
    /// <c>InterceptorDeContextoDeTenant</c>, así que sin el GUC el <c>UPDATE</c> ve cero filas y
    /// reporta éxito igual — el bloque abandonado quedaría sin saldar y su punto de venta no volvería
    /// a cerrar nunca.
    ///
    /// Y el ALCANCE: el <c>WHERE abandonada_at IS NOT NULL</c> salda los abandonados y NADA MÁS. El
    /// bloque VIVO del mismo dispositivo tiene que quedar en <c>NULL</c> — saldarlo lo volvería ciego
    /// para la guarda desde el día uno, que es justo lo contrario de lo que esta migración existe para
    /// arreglar. Los dos valores por fila son el discriminante.
    /// </summary>
    [Fact]
    public async Task ElBackfillSaldaSoloLosBloquesAbandonadosYSoloBajoElSetLocalDePlataforma()
    {
        var nombreBase = $"ways_rendicion_saldada_{Guid.NewGuid():N}";
        var cadenaAdmin = new NpgsqlConnectionStringBuilder(fixture.OwnerConnectionString)
        {
            Database = "postgres"
        }.ConnectionString;
        var cadenaOwner = new NpgsqlConnectionStringBuilder(fixture.OwnerConnectionString)
        {
            Database = nombreBase
        }.ConnectionString;
        var cadenaApp = new NpgsqlConnectionStringBuilder(fixture.AppConnectionString)
        {
            Database = nombreBase
        }.ConnectionString;

        await using (var admin = new NpgsqlConnection(cadenaAdmin))
        {
            await admin.OpenAsync();
            await using var crear = admin.CreateCommand();
            crear.CommandText = $"CREATE DATABASE \"{nombreBase}\"";
            await crear.ExecuteNonQueryAsync();
        }

        try
        {
            await using (var migrando = new WaysDbContext(ConstruirOpciones(cadenaOwner), TenantActualFijo.Plataforma))
            {
                var migrador = migrando.Database.GetInfrastructure().GetRequiredService<IMigrator>();
                await migrador.MigrateAsync(MigracionAnterior);
            }

            // ways_app existe a nivel cluster (WaysApiFixture lo crea una vez) pero los GRANTs son
            // por base — una base nueva no los hereda, igual que el camino real de deploy.
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

            long idReservaAbandonada, idReservaViva;
            await using (var owner = new NpgsqlConnection(cadenaOwner))
            {
                await owner.OpenAsync();
                (idReservaAbandonada, idReservaViva) = await SembrarDosBloquesAsync(owner);

                // Primer paso de la migración real, aislado: la columna nullable a mano.
                await using var columna = owner.CreateCommand();
                columna.CommandText = "ALTER TABLE reservas_numeracion ADD COLUMN rendicion_saldada_at timestamptz;";
                await columna.ExecuteNonQueryAsync();
            }

            var bloque = LeerBloqueDeBackfillDesdeElArchivoDeLaMigracion();

            // (a) SIN el SET LOCAL: ways_app sin GUC ⇒ app_es_plataforma() = false y
            // app_tenant_actual() = NULL, así que RLS no le deja ver ninguna fila de ningún tenant —
            // cero filas afectadas, y ningún error.
            await using (var cruda = new NpgsqlConnection(cadenaApp))
            {
                await cruda.OpenAsync();
                await using var comando = cruda.CreateCommand();
                comando.CommandText = SinElSetLocal(bloque);
                Assert.Equal(0, await comando.ExecuteNonQueryAsync());
            }

            // (b) CON el bloque TAL CUAL lo trae la migración: alcanza el bloque abandonado, y solo
            // ese (el vivo no entra en el WHERE).
            await using (var cruda = new NpgsqlConnection(cadenaApp))
            {
                await cruda.OpenAsync();
                await using var comando = cruda.CreateCommand();
                comando.CommandText = bloque;
                Assert.Equal(1, await comando.ExecuteNonQueryAsync());
            }

            await using (var owner = new NpgsqlConnection(cadenaOwner))
            {
                await owner.OpenAsync();
                Assert.True(
                    await EstaSaldadaAsync(owner, idReservaAbandonada),
                    "El bloque ABANDONADO tenía que quedar saldado.");
                Assert.False(
                    await EstaSaldadaAsync(owner, idReservaViva),
                    "El bloque VIVO NO tenía que quedar saldado.");
                Assert.Equal(
                    await LeerUpdatedAtAsync(owner, idReservaAbandonada),
                    await LeerRendicionSaldadaAtAsync(owner, idReservaAbandonada));
            }

            // (c) Idempotencia: el mismo bloque, otra vez, ya no encuentra nada que tocar.
            await using (var cruda = new NpgsqlConnection(cadenaApp))
            {
                await cruda.OpenAsync();
                await using var comando = cruda.CreateCommand();
                comando.CommandText = bloque;
                Assert.Equal(0, await comando.ExecuteNonQueryAsync());
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

    /// <summary>Un bloque ABANDONADO que nunca rindió (el histórico que el backfill existe para
    /// saldar) y uno VIVO del mismo dispositivo y la misma serie, que tiene que quedar intacto.
    /// <c>ux_reservas_numeracion_dispositivo_activo</c> admite un solo vivo por clave, así que esta
    /// pareja es exactamente el estado que deja una reposición de bloque.</summary>
    private static async Task<(long Abandonada, long Viva)> SembrarDosBloquesAsync(NpgsqlConnection owner)
    {
        async Task<T> EscalarAsync<T>(string sql, params object?[] parametros)
        {
            await using var comando = owner.CreateCommand();
            comando.CommandText = sql;
            foreach (var valor in parametros)
            {
                comando.Parameters.Add(new NpgsqlParameter { Value = valor ?? DBNull.Value });
            }

            return (T)(await comando.ExecuteScalarAsync())!;
        }

        // `roles` es [global] y no se siembra sola en una base propia (mismo trap documentado en
        // CostoCongeladoTests): hace falta a mano para fk_usuarios_rol.
        await EscalarAsync<int>(
            "INSERT INTO roles (id_rol, nombre, created_at, updated_at) " +
            "VALUES ($1, 'admin', now(), now()) RETURNING id_rol",
            (int)RolConocido.Admin);

        var idTenant = await EscalarAsync<int>(
            "INSERT INTO tenants (nombre, estado, created_at, updated_at) " +
            "VALUES ('Tenant histórico', 'activo'::estado_tenant, now(), now()) RETURNING id_tenant");
        var idEmpresa = await EscalarAsync<int>(
            "INSERT INTO empresas (id_tenant, razon_social, created_at, updated_at) " +
            "VALUES ($1, 'Empresa histórica', now(), now()) RETURNING id_empresa",
            idTenant);
        var idPuntoVenta = await EscalarAsync<int>(
            "INSERT INTO puntos_venta (id_tenant, id_empresa, nombre, modo, created_at, updated_at) " +
            "VALUES ($1, $2, 'PV histórico', 'escritorio'::modo_punto_venta, now(), now()) RETURNING id_punto_venta",
            idTenant, idEmpresa);
        var idUsuario = await EscalarAsync<int>(
            "INSERT INTO usuarios (id_tenant, usuario, mail, id_rol, password_hash, password_algoritmo, " +
            "password_actualizado_el, created_at, updated_at) " +
            "VALUES ($1, 'admin', 'historico@ways.test', $2, 'hash', 'test', now(), now(), now()) RETURNING id_usuario",
            idTenant, (int)RolConocido.Admin);
        var idDispositivo = await EscalarAsync<int>(
            "INSERT INTO dispositivos (id_tenant, id_punto_venta, nombre, token_hash, id_usuario_alta, " +
            "created_at, updated_at) " +
            "VALUES ($1, $2, 'Caja histórica', $3, $4, now(), now()) RETURNING id_dispositivo",
            idTenant, idPuntoVenta, new string('a', 64), idUsuario);

        // Sin `rendicion_saldada_at` en la lista de columnas: todavía no existe en este punto de la
        // historia (misma trampa que documenta TurnosCajaMedioPagoEfectivoMigracionTests).
        var abandonada = await EscalarAsync<long>(
            "INSERT INTO reservas_numeracion (id_tenant, id_punto_venta, tipo_comprobante, id_dispositivo, " +
            "desde, hasta, abandonada_at, created_at, updated_at) " +
            "VALUES ($1, $2, 'TX', $3, 1, 10, now(), now(), now()) RETURNING id_reserva_numeracion",
            idTenant, idPuntoVenta, idDispositivo);
        var viva = await EscalarAsync<long>(
            "INSERT INTO reservas_numeracion (id_tenant, id_punto_venta, tipo_comprobante, id_dispositivo, " +
            "desde, hasta, created_at, updated_at) " +
            "VALUES ($1, $2, 'TX', $3, 11, 20, now(), now()) RETURNING id_reserva_numeracion",
            idTenant, idPuntoVenta, idDispositivo);

        return (abandonada, viva);
    }

    private static async Task<bool> EstaSaldadaAsync(NpgsqlConnection owner, long idReserva)
    {
        await using var comando = owner.CreateCommand();
        comando.CommandText =
            "SELECT rendicion_saldada_at IS NOT NULL FROM reservas_numeracion WHERE id_reserva_numeracion = $1";
        comando.Parameters.Add(new NpgsqlParameter { Value = idReserva });
        return (bool)(await comando.ExecuteScalarAsync())!;
    }

    private static async Task<DateTime> LeerUpdatedAtAsync(NpgsqlConnection owner, long idReserva) =>
        await LeerTimestampAsync(owner, idReserva, "updated_at");

    private static async Task<DateTime> LeerRendicionSaldadaAtAsync(NpgsqlConnection owner, long idReserva) =>
        await LeerTimestampAsync(owner, idReserva, "rendicion_saldada_at");

    private static async Task<DateTime> LeerTimestampAsync(NpgsqlConnection owner, long idReserva, string columna)
    {
        await using var comando = owner.CreateCommand();
        comando.CommandText = $"SELECT {columna} FROM reservas_numeracion WHERE id_reserva_numeracion = $1";
        comando.Parameters.Add(new NpgsqlParameter { Value = idReserva });
        return (DateTime)(await comando.ExecuteScalarAsync())!;
    }

    /// <summary>El ÚNICO bloque <c>"""..."""</c> del archivo real de la migración — nunca una copia a
    /// mano: una copia ejecuta el SQL correcto sin importar lo que la migración diga.</summary>
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

        Assert.Contains("UPDATE reservas_numeracion", bloque, StringComparison.Ordinal);
        return bloque;
    }

    /// <summary>El MISMO bloque sin su <c>SET LOCAL</c>, para la mitad (a): así las dos mitades salen
    /// de la misma fuente y la diferencia entre ellas es exactamente la línea bajo prueba.</summary>
    private static string SinElSetLocal(string bloque) =>
        string.Join(
            '\n',
            bloque.Split('\n').Where(l => !l.TrimStart().StartsWith("SET LOCAL", StringComparison.Ordinal)));

    private static string RutaDeEsteArchivo([CallerFilePath] string ruta = "") => ruta;

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
                npgsql.MapEnum<CategoriaGasto>("categoria_gasto");
                npgsql.MapEnum<EstadoCompra>("estado_compra");
                npgsql.MapEnum<TipoMovimientoCcProveedor>("tipo_movimiento_cc_proveedor");
                npgsql.MapEnum<EstadoOrdenCompra>("estado_orden_compra");
                npgsql.MapEnum<EstadoPresupuesto>("estado_presupuesto");
                npgsql.MapEnum<EstadoRemito>("estado_remito");
                npgsql.MapEnum<ResultadoFiscal>("resultado_fiscal");
                npgsql.MapEnum<AmbienteFiscal>("ambiente_fiscal");
                npgsql.MapEnum<ModoPuntoVenta>("modo_punto_venta");
            })
            // El modelo HEAD ya incluye rendicion_saldada_at y esta base se queda en la migración
            // anterior a propósito.
            .ConfigureWarnings(w => w.Ignore(RelationalEventId.PendingModelChangesWarning))
            .Options;
}
