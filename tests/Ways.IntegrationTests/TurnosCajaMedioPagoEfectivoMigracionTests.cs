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

    /// <summary>Siembra tenant/empresa/punto de venta/rol/usuario vía EF (ninguna de esas tablas
    /// cambió en esta migración) — mismo criterio que <c>SembrarEntornoAsync</c> de
    /// <see cref="CuentaCorrienteProveedorBackfillTests"/>.</summary>
    private static async Task<Tenant1> SembrarEntornoAsync(WaysDbContext db, string nombre)
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

        var puntoVenta = new PuntoVenta
        {
            IdTenant = tenant.Id, IdEmpresa = empresa.Id, Nombre = nombre, CreatedAt = ahora, UpdatedAt = ahora
        };
        db.PuntosVenta.Add(puntoVenta);
        await db.SaveChangesAsync();

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

        return new Tenant1(tenant.Id, puntoVenta.Id, usuario.Id);
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
                var t1 = await SembrarEntornoAsync(db2, "t1-unico");
                idMedioEfectivoUnico = await SembrarMedioPagoAsync(db2, t1.IdTenant, "Efectivo", ComportamientoMedioPago.Efectivo);
                await SembrarMedioPagoAsync(db2, t1.IdTenant, "Transferencia", ComportamientoMedioPago.Electronico);
                idTurnoUnicoEfectivoCerrado = await SembrarTurnoPreMigracionAsync(
                    conexionCruda, t1.IdTenant, t1.IdPuntoVenta, t1.IdEmpleado, 500m, cerrado: true);
                idTurnoUnicoEfectivoAbierto = await SembrarTurnoPreMigracionAsync(
                    conexionCruda, t1.IdTenant, t1.IdPuntoVenta, t1.IdEmpleado, 500m, cerrado: false);

                // Tenant 2: DOS medios efectivo (catálogo mal configurado) — un turno cerrado que
                // tiene que quedar NULL, nunca adivinar cuál de los dos.
                var t2 = await SembrarEntornoAsync(db2, "t2-ambiguo");
                await SembrarMedioPagoAsync(db2, t2.IdTenant, "Efectivo A", ComportamientoMedioPago.Efectivo);
                await SembrarMedioPagoAsync(db2, t2.IdTenant, "Efectivo B", ComportamientoMedioPago.Efectivo);
                idTurnoDosEfectivosCerrado = await SembrarTurnoPreMigracionAsync(
                    conexionCruda, t2.IdTenant, t2.IdPuntoVenta, t2.IdEmpleado, 0m, cerrado: true);
            }

            await using (var db = new WaysDbContext(opciones, TenantActualFijo.Plataforma))
            {
                var migrador = db.Database.GetInfrastructure().GetRequiredService<IMigrator>();
                await migrador.MigrateAsync(); // aplica TurnosCajaMedioPagoEfectivo, la única pendiente
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
}
