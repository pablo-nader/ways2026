using System.Data;
using Microsoft.EntityFrameworkCore;
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
/// judgment-day JD-E5a-2 (DB CHANGE GATE aprobado): migración <c>TurnosCajaMedioPagoEfectivo</c>.
/// Los cuatro primeros tests son el mismo patrón LIVIANO que <see cref="LotesMigracionTests"/> —
/// <see cref="WaysApiFixture"/> ya migró TODO antes de que exista ningún cliente HTTP, así que
/// basta con afirmar el post-estado (columna/índice/CHECK/FK). El backfill en sí necesita datos
/// PREVIOS a la migración, así que usa la misma base dedicada + migrar-hasta-N-1 que
/// <see cref="CuentaCorrienteProveedorBackfillTests"/> (que documenta la trampa: INSERT crudo con
/// las columnas de ANTES de la etapa, nunca vía EF con el modelo HEAD, que incluiría
/// <c>id_medio_pago_efectivo</c> y rompería contra el esquema viejo con <c>42703</c>).
/// </summary>
[Collection("Ways.IntegrationTests secuencial")]
public class TurnosCajaMedioPagoEfectivoMigracionTests(WaysApiFixture fixture) : IClassFixture<WaysApiFixture>
{
    private const string MigracionAnterior = "20260917025328_QuitarVueltoMaximo";

    /// <summary>La migración bajo prueba en este archivo — se pasa como target EXPLÍCITO a
    /// <c>MigrateAsync</c> en vez de dejarlo sin argumento (stage-desktop-pos: agregó
    /// <c>ModoPuntoVentaYDispositivoActivoUnico</c> arriba de esta, así que "sin target" pasó a
    /// significar "aplicá esa también" — <c>ConstruirOpciones</c> no mapea <c>modo_punto_venta</c>
    /// a propósito, y aplicar una migración que la necesita revienta con
    /// <c>PendingModelChangesWarning</c>). Pinear el nombre aísla este test de cualquier migración
    /// futura, para siempre.</summary>
    private const string MigracionBajoPrueba = "20260919185344_TurnosCajaMedioPagoEfectivo";

    // ---- la migración aplica limpiamente: columna/índice/CHECK/FK existen post-Up() -----------

    private async Task<NpgsqlConnection> AbrirAsync()
    {
        using var _ = fixture.CreateClient(); // fuerza el arranque del host (migración ya corrida)
        return await fixture.AbrirConexionCrudaAsync("plataforma", null);
    }

    [Fact]
    public async Task LaColumnaExistePostMigracion()
    {
        await using var cruda = await AbrirAsync();
        await using var comando = cruda.CreateCommand();
        comando.CommandText =
            "SELECT count(*) FROM information_schema.columns " +
            "WHERE table_schema = 'public' AND table_name = 'turnos_caja' AND column_name = 'id_medio_pago_efectivo'";
        Assert.Equal(1L, (long)(await comando.ExecuteScalarAsync())!);
    }

    [Fact]
    public async Task ElIndiceExistePostMigracion()
    {
        await using var cruda = await AbrirAsync();
        await using var comando = cruda.CreateCommand();
        comando.CommandText =
            "SELECT count(*) FROM pg_indexes WHERE schemaname = 'public' " +
            "AND tablename = 'turnos_caja' AND indexname = 'ix_turnos_caja_medio_pago_efectivo'";
        Assert.Equal(1L, (long)(await comando.ExecuteScalarAsync())!);
    }

    [Fact]
    public async Task LaCheckExistePostMigracion()
    {
        await using var cruda = await AbrirAsync();
        await using var comando = cruda.CreateCommand();
        comando.CommandText =
            "SELECT count(*) FROM pg_constraint WHERE conname = 'ck_turnos_caja_medio_efectivo_solo_cerrado' " +
            "AND contype = 'c'";
        Assert.Equal(1L, (long)(await comando.ExecuteScalarAsync())!);
    }

    [Fact]
    public async Task LaFkExistePostMigracion()
    {
        await using var cruda = await AbrirAsync();
        await using var comando = cruda.CreateCommand();
        comando.CommandText =
            "SELECT count(*) FROM pg_constraint WHERE conname = 'fk_turnos_caja_medio_pago_efectivo' " +
            "AND contype = 'f'";
        Assert.Equal(1L, (long)(await comando.ExecuteScalarAsync())!);
    }

    // ---- backfill: base dedicada, migrar hasta N-1, sembrar, migrar la nueva -------------------

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
            })
            .Options;

    private sealed record Tenant1(int IdTenant, int IdPuntoVenta, int IdEmpleado);

    /// <summary>Siembra tenant/empresa/rol/usuario vía EF (ninguna de esas tablas cambió en esta
    /// migración) — mismo criterio que <c>SembrarEntornoAsync</c> de
    /// <see cref="CuentaCorrienteProveedorBackfillTests"/>. <c>puntos_venta</c> es la EXCEPCIÓN
    /// (stage-desktop-pos): esquema todavía en <see cref="MigracionBajoPrueba"/> acá, ANTES de
    /// <c>modo</c> — SQL crudo con la lista de columnas de antes de esa columna, nunca vía EF
    /// (que, con el modelo HEAD, incluiría la columna nueva en el INSERT y rompería contra el
    /// esquema viejo con 42703 — misma trampa documentada en <c>CostoCongeladoTests</c>).</summary>
    private static async Task<Tenant1> SembrarEntornoAsync(WaysDbContext db, string cadenaConexion, string nombre)
    {
        var ahora = DateTimeOffset.UtcNow;

        var tenant = new Ways.Domain.Organizacion.Tenant
        {
            Nombre = nombre, Estado = EstadoTenant.Activo, CreatedAt = ahora, UpdatedAt = ahora
        };
        db.Tenants.Add(tenant);
        await db.SaveChangesAsync();

        var empresa = new Empresa { IdTenant = tenant.Id, RazonSocial = nombre, CreatedAt = ahora, UpdatedAt = ahora };
        db.Empresas.Add(empresa);
        await db.SaveChangesAsync();

        int idPuntoVenta;
        await using (var cruda = new NpgsqlConnection(cadenaConexion))
        {
            await cruda.OpenAsync();
            await using var comando = cruda.CreateCommand();
            comando.CommandText =
                "INSERT INTO puntos_venta (id_tenant, id_empresa, nombre, created_at, updated_at) " +
                "VALUES ($1, $2, $3, now(), now()) RETURNING id_punto_venta";
            comando.Parameters.Add(new NpgsqlParameter { Value = tenant.Id });
            comando.Parameters.Add(new NpgsqlParameter { Value = empresa.Id });
            comando.Parameters.Add(new NpgsqlParameter { Value = nombre });
            idPuntoVenta = (int)(await comando.ExecuteScalarAsync())!;
        }

        if (!await db.Roles.AnyAsync(r => r.Id == (int)RolConocido.Vendedor))
        {
            db.Roles.Add(new Rol { Id = (int)RolConocido.Vendedor, Nombre = "Vendedor", CreatedAt = ahora, UpdatedAt = ahora });
            await db.SaveChangesAsync();
        }

        var usuario = new Usuario
        {
            IdTenant = tenant.Id,
            NombreUsuario = $"{nombre.ToLowerInvariant()}-empleado",
            Mail = $"{nombre.ToLowerInvariant()}@ways.test",
            RolId = (int)RolConocido.Vendedor,
            PasswordHash = "hash-de-prueba",
            PasswordAlgoritmo = "test",
            PasswordActualizadoEl = ahora,
            CreatedAt = ahora,
            UpdatedAt = ahora
        };
        db.Usuarios.Add(usuario);
        await db.SaveChangesAsync();

        return new Tenant1(tenant.Id, idPuntoVenta, usuario.Id);
    }

    private static async Task<int> SembrarMedioPagoAsync(WaysDbContext db, int idTenant, string nombre, ComportamientoMedioPago comportamiento)
    {
        var ahora = DateTimeOffset.UtcNow;
        var medio = new MedioPago
        {
            IdTenant = idTenant, Nombre = nombre, Orden = 1, Comportamiento = comportamiento,
            AdmiteVuelto = false, RequiereReferencia = false, Activo = true, CreatedAt = ahora, UpdatedAt = ahora
        };
        db.MediosPago.Add(medio);
        await db.SaveChangesAsync();
        return medio.Id;
    }

    /// <summary><c>turnos_caja</c> SÍ cambió en esta migración — INSERT crudo con la lista de
    /// columnas de ANTES (sin <c>id_medio_pago_efectivo</c>), nunca vía EF (que con el modelo HEAD
    /// incluiría la columna nueva y rompería con <c>42703</c> contra el esquema viejo).</summary>
    private static async Task<int> SembrarTurnoPreMigracionAsync(
        NpgsqlConnection cruda, int idTenant, int idPuntoVenta, int idEmpleado, decimal fondoInicial, bool cerrado)
    {
        await using var comando = cruda.CreateCommand();
        comando.CommandText = cerrado
            ? "INSERT INTO turnos_caja (id_tenant, id_punto_venta, id_empleado_apertura, id_empleado_cierre, " +
              "fecha_apertura, fecha_cierre, fondo_inicial, estado, created_at, updated_at) " +
              "VALUES ($1, $2, $3, $3, now() - interval '1 hour', now(), $4, 'cerrado', now(), now()) " +
              "RETURNING id_turno_caja"
            : "INSERT INTO turnos_caja (id_tenant, id_punto_venta, id_empleado_apertura, " +
              "fecha_apertura, fondo_inicial, estado, created_at, updated_at) " +
              "VALUES ($1, $2, $3, now(), $4, 'abierto', now(), now()) " +
              "RETURNING id_turno_caja";
        comando.Parameters.Add(new NpgsqlParameter { Value = idTenant });
        comando.Parameters.Add(new NpgsqlParameter { Value = idPuntoVenta });
        comando.Parameters.Add(new NpgsqlParameter { Value = idEmpleado });
        comando.Parameters.Add(new NpgsqlParameter { Value = fondoInicial });
        return (int)(await comando.ExecuteScalarAsync())!;
    }

    private static async Task<int?> LeerAnclaAsync(NpgsqlConnection cruda, int idTurno)
    {
        await using var comando = cruda.CreateCommand();
        comando.CommandText = "SELECT id_medio_pago_efectivo FROM turnos_caja WHERE id_turno_caja = $1";
        comando.Parameters.Add(new NpgsqlParameter { Value = idTurno });
        var resultado = await comando.ExecuteScalarAsync();
        return resultado is null or DBNull ? null : (int)resultado;
    }

    private static async Task EliminarBaseAsync(string cadenaAdmin, string nombreBase)
    {
        await using var admin = new NpgsqlConnection(cadenaAdmin);
        await admin.OpenAsync();
        await using var dropear = admin.CreateCommand();
        dropear.CommandText = $"DROP DATABASE IF EXISTS \"{nombreBase}\" WITH (FORCE)";
        await dropear.ExecuteNonQueryAsync();
    }

    /// <summary>Los tres discriminantes que el modelo aprobado pide: exactamente un medio
    /// efectivo + turno cerrado ⇒ backfilled; dos medios efectivo (catálogo mal configurado) +
    /// turno cerrado ⇒ NULL (fail-closed, nunca adivinar); exactamente un medio efectivo + turno
    /// ABIERTO ⇒ NULL siempre (la CHECK ni lo permitiría).</summary>
    [Fact]
    public async Task ElBackfillAsignaElAnclaSoloATurnosCerradosConExactamenteUnMedioEfectivo()
    {
        var nombreBase = $"ways_jde5a2_{Guid.NewGuid():N}";
        var cadenaAdmin = new NpgsqlConnectionStringBuilder(fixture.OwnerConnectionString) { Database = "postgres" }.ConnectionString;
        var cadenaNueva = new NpgsqlConnectionStringBuilder(fixture.OwnerConnectionString) { Database = nombreBase }.ConnectionString;

        await using (var admin = new NpgsqlConnection(cadenaAdmin))
        {
            await admin.OpenAsync();
            await using var crear = admin.CreateCommand();
            crear.CommandText = $"CREATE DATABASE \"{nombreBase}\"";
            await crear.ExecuteNonQueryAsync();
        }

        try
        {
            var opciones = ConstruirOpciones(cadenaNueva);

            await using (var db = new WaysDbContext(opciones, TenantActualFijo.Plataforma))
            {
                var migrador = db.Database.GetInfrastructure().GetRequiredService<IMigrator>();
                await migrador.MigrateAsync(MigracionAnterior);
            }

            await using var conexionCruda = new NpgsqlConnection(cadenaNueva);
            await conexionCruda.OpenAsync();

            int idTurnoUnicoEfectivoCerrado, idTurnoUnicoEfectivoAbierto, idTurnoDosEfectivosCerrado;
            int idMedioEfectivoUnico;

            await using (var db2 = new WaysDbContext(opciones, TenantActualFijo.Plataforma))
            {
                // Tenant 1: exactamente un medio efectivo — un turno cerrado (backfill esperado)
                // y un turno abierto (nunca tocado).
                var t1 = await SembrarEntornoAsync(db2, cadenaNueva, "t1-unico");
                idMedioEfectivoUnico = await SembrarMedioPagoAsync(db2, t1.IdTenant, "Efectivo", ComportamientoMedioPago.Efectivo);
                await SembrarMedioPagoAsync(db2, t1.IdTenant, "Transferencia", ComportamientoMedioPago.Electronico);
                idTurnoUnicoEfectivoCerrado = await SembrarTurnoPreMigracionAsync(
                    conexionCruda, t1.IdTenant, t1.IdPuntoVenta, t1.IdEmpleado, 500m, cerrado: true);
                idTurnoUnicoEfectivoAbierto = await SembrarTurnoPreMigracionAsync(
                    conexionCruda, t1.IdTenant, t1.IdPuntoVenta, t1.IdEmpleado, 500m, cerrado: false);

                // Tenant 2: DOS medios efectivo (catálogo mal configurado) — un turno cerrado que
                // tiene que quedar NULL, nunca adivinar cuál de los dos.
                var t2 = await SembrarEntornoAsync(db2, cadenaNueva, "t2-ambiguo");
                await SembrarMedioPagoAsync(db2, t2.IdTenant, "Efectivo A", ComportamientoMedioPago.Efectivo);
                await SembrarMedioPagoAsync(db2, t2.IdTenant, "Efectivo B", ComportamientoMedioPago.Efectivo);
                idTurnoDosEfectivosCerrado = await SembrarTurnoPreMigracionAsync(
                    conexionCruda, t2.IdTenant, t2.IdPuntoVenta, t2.IdEmpleado, 0m, cerrado: true);
            }

            await using (var db = new WaysDbContext(opciones, TenantActualFijo.Plataforma))
            {
                var migrador = db.Database.GetInfrastructure().GetRequiredService<IMigrator>();
                await migrador.MigrateAsync(MigracionBajoPrueba);
            }

            Assert.Equal(idMedioEfectivoUnico, await LeerAnclaAsync(conexionCruda, idTurnoUnicoEfectivoCerrado));
            Assert.Null(await LeerAnclaAsync(conexionCruda, idTurnoUnicoEfectivoAbierto));
            Assert.Null(await LeerAnclaAsync(conexionCruda, idTurnoDosEfectivosCerrado));
        }
        finally
        {
            await EliminarBaseAsync(cadenaAdmin, nombreBase);
        }
    }

    // ---- judgment-day ronda 2 (JD-E5a-2 escalado): el backfill SOLO alcanza todos los tenants
    // bajo RLS real gracias al SET LOCAL app.acceso = 'plataforma' dentro del mismo bloque Sql() ----

    private const string RolApp = "ways_app";
    private const string PasswordApp = "ways_app_password";

    /// <summary>Extrae el ÚNICO bloque <c>"""..."""</c> del <c>Sql()</c> de backfill DIRECTO del
    /// archivo `.cs` de la migración — nunca una copia escrita a mano en el test (misma lección
    /// que <c>CuentaCorrienteProveedorBackfillTests.LeerStatementsDelBackfillDesdeElArchivoDeLaMigracion</c>:
    /// una copia hardcodeada ejecuta el SQL CORRECTO sin importar lo que la migración de verdad
    /// diga, y no detecta una mutación real del archivo).</summary>
    private static string LeerBloqueDeBackfillDesdeElArchivoDeLaMigracion()
    {
        var rutaMigracion = Path.Combine(
            Path.GetDirectoryName(RutaDeEsteArchivo())!,
            "..", "..", "src", "Ways.Infrastructure", "Persistencia", "Migraciones",
            "20260919185344_TurnosCajaMedioPagoEfectivo.cs");

        Assert.True(File.Exists(rutaMigracion), $"No se encontró la migración en {rutaMigracion}");

        var fuente = File.ReadAllText(rutaMigracion);
        const string delimitador = "\"\"\"";

        var inicio = fuente.IndexOf(delimitador, StringComparison.Ordinal);
        var finApertura = inicio + delimitador.Length;
        var fin = fuente.IndexOf(delimitador, finApertura, StringComparison.Ordinal);
        var bloque = fuente[finApertura..fin].Trim();

        Assert.Contains("UPDATE turnos_caja", bloque);
        return bloque;
    }

    private static string RutaDeEsteArchivo([System.Runtime.CompilerServices.CallerFilePath] string ruta = "") => ruta;

    /// <summary>
    /// LA CLÁUSULA (judgment-day ronda 2, JD-E5a-2 escalado): <c>turnos_caja</c> corre bajo FORCE
    /// ROW LEVEL SECURITY (misma migración <c>TurnosCaja</c> que la creó) y el camino real de
    /// <c>dotnet ef database update</c> (<c>WaysDbContextFactory</c>) NO registra
    /// <c>InterceptorDeContextoDeTenant</c> — así que, sin un <c>SET LOCAL app.acceso =
    /// 'plataforma'</c> DENTRO del mismo bloque <c>Sql()</c>, el backfill corre con los GUCs de
    /// RLS vacíos y actualiza CERO filas, reportando éxito igual.
    ///
    /// Reproduce ese camino de verdad: una conexión autenticada como <c>ways_app</c> (rol
    /// <c>NOSUPERUSER NOBYPASSRLS</c>, el mismo que <see cref="WaysApiFixture"/> crea) sin
    /// ningún GUC seteado desde afuera, ejecutando el bloque de backfill EXTRAÍDO del archivo
    /// real de la migración — nunca la conexión <c>ways_owner</c> (superuser, bypassea RLS
    /// siempre) que el resto de esta clase usa para migrar/sembrar: esa conexión no prueba nada
    /// de RLS, es la misma "carryover weakness" que
    /// <c>CuentaCorrienteProveedorBackfillTests.ElTextoFuenteDeLaMigracionOrdenaRlsDespuesDelBackfillTarget11</c>
    /// ya documenta.
    ///
    /// <c>ways_app</c> no es DUEÑO de las tablas en esta base dedicada (a diferencia de
    /// Producción, ADR-5, donde el rol de aplicación sí lo es) — se le otorgan a mano los mismos
    /// GRANTs de datos que <c>WaysApiFixture.CrearRolDeAplicacionAsync</c> ya le da sobre la base
    /// principal de la fixture; no necesita privilegios de DDL porque el backfill es un UPDATE.
    ///
    /// Mutation-proof-tests: se corrió la mutación de verdad — sacar la línea <c>SET LOCAL
    /// app.acceso = 'plataforma';</c> del archivo real de la migración hace fallar esta prueba
    /// (0 filas afectadas en vez de 1, el turno queda en <c>NULL</c>); revertido después de
    /// confirmar el fallo, la suite vuelve a quedar verde.
    /// </summary>
    [Fact]
    public async Task ElBackfillAlcanzaElTenantBajoRlsRealSoloGraciasAlSetLocalDePlataforma()
    {
        var nombreBase = $"ways_jde5a2rls_{Guid.NewGuid():N}";
        var cadenaAdmin = new NpgsqlConnectionStringBuilder(fixture.OwnerConnectionString) { Database = "postgres" }.ConnectionString;
        var cadenaOwner = new NpgsqlConnectionStringBuilder(fixture.OwnerConnectionString) { Database = nombreBase }.ConnectionString;
        var cadenaWaysApp = new NpgsqlConnectionStringBuilder(fixture.OwnerConnectionString)
        {
            Database = nombreBase, Username = RolApp, Password = PasswordApp
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
            var opciones = ConstruirOpciones(cadenaOwner);

            // Migra hasta MigracionBajoPrueba como owner (superuser en el testcontainer) — arma el
            // esquema de esa migración (columna/índice/CHECK/FK incluidos), pineado como target
            // EXPLÍCITO por el mismo motivo documentado en la constante (una migración posterior,
            // como ModoPuntoVentaYDispositivoActivoUnico, no debe colarse acá). El backfill que
            // corre ACÁ es bajo superuser y no prueba nada de RLS por sí solo; lo que prueba RLS de
            // verdad es reejecutar el mismo bloque más abajo, bajo ways_app, sobre una fila
            // sembrada DESPUÉS de este paso (así que sigue en NULL cuando llega ahí).
            await using (var db = new WaysDbContext(opciones, TenantActualFijo.Plataforma))
            {
                var migrador = db.Database.GetInfrastructure().GetRequiredService<IMigrator>();
                await migrador.MigrateAsync(MigracionBajoPrueba);
            }

            await using (var owner = new NpgsqlConnection(cadenaOwner))
            {
                await owner.OpenAsync();
                await using var comando = owner.CreateCommand();
                comando.CommandText =
                    $"""
                    GRANT USAGE, CREATE ON SCHEMA public TO {RolApp};
                    GRANT SELECT, INSERT, UPDATE, DELETE ON ALL TABLES IN SCHEMA public TO {RolApp};
                    GRANT USAGE, SELECT ON ALL SEQUENCES IN SCHEMA public TO {RolApp};
                    """;
                await comando.ExecuteNonQueryAsync();
            }

            int idTenant, idPuntoVenta, idEmpleado, idMedioEfectivo;
            await using (var db2 = new WaysDbContext(opciones, TenantActualFijo.Plataforma))
            {
                var t = await SembrarEntornoAsync(db2, cadenaOwner, "rls");
                idTenant = t.IdTenant;
                idPuntoVenta = t.IdPuntoVenta;
                idEmpleado = t.IdEmpleado;
                idMedioEfectivo = await SembrarMedioPagoAsync(db2, idTenant, "Efectivo", ComportamientoMedioPago.Efectivo);
            }

            int idTurnoCerrado;
            await using (var owner = new NpgsqlConnection(cadenaOwner))
            {
                await owner.OpenAsync();
                // Sembrada DESPUÉS de la corrida-como-owner de arriba — arranca en NULL, que es
                // justo el punto de partida que esta prueba necesita.
                idTurnoCerrado = await SembrarTurnoPreMigracionAsync(owner, idTenant, idPuntoVenta, idEmpleado, 0m, cerrado: true);
                Assert.Null(await LeerAnclaAsync(owner, idTurnoCerrado));
            }

            var bloqueDeBackfill = LeerBloqueDeBackfillDesdeElArchivoDeLaMigracion();

            // LA CLÁUSULA: reejecuta el backfill EXACTO (leído del archivo real, con su propio
            // SET LOCAL como primera sentencia del mismo batch) sobre una conexión ways_app
            // fresca — sin interceptor de tenant, sin ningún GUC seteado desde afuera.
            await using (var comoWaysApp = new NpgsqlConnection(cadenaWaysApp))
            {
                await comoWaysApp.OpenAsync();
                await using var comando = comoWaysApp.CreateCommand();
                comando.CommandText = bloqueDeBackfill;
                await comando.ExecuteNonQueryAsync();
            }

            await using (var owner = new NpgsqlConnection(cadenaOwner))
            {
                await owner.OpenAsync();
                Assert.Equal(idMedioEfectivo, await LeerAnclaAsync(owner, idTurnoCerrado));
            }
        }
        finally
        {
            await EliminarBaseAsync(cadenaAdmin, nombreBase);
        }
    }
}
