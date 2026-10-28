using Npgsql;
using Ways.Domain.Catalogos;
using Ways.Domain.Organizacion;
using Ways.Domain.Usuarios;
using Ways.Infrastructure.Multitenancy;
using Ways.Infrastructure.Persistencia.Migraciones;

namespace Ways.IntegrationTests;

/// <summary>
/// E2-S1 (líneas por medio de pago), <c>db-error-backstops</c> y <c>rls-migration-backfills</c>: el esquema de
/// <c>items_pago</c> probado con SQL crudo, sin pasar por <c>ServicioDePagos</c>. Cubre las dos CHECKs, las FKs
/// compuestas, el constraint trigger diferido de la suma contra la cabecera y el backfill de la migración
/// <c>ItemsDePago</c> bajo <c>ways_app</c> (NOSUPERUSER NOBYPASSRLS), nunca bajo el dueño.
///
/// Honesto sobre alcanzabilidad: el servicio escribe siempre un único ítem igual a la cabecera, así que ninguna
/// de estas ramas es alcanzable por un cliente HTTP; prueban el backstop de esquema.
/// </summary>
[Collection("Ways.IntegrationTests secuencial")]
public class ItemsPagoEsquemaTests(WaysApiFixture fixture) : IClassFixture<WaysApiFixture>
{
    private const string Trigger = "tg_items_pago_suma_igual_importe";

    private sealed record Prerequisitos(int IdTenant, int IdEmpresa, int IdEmpleado, int IdMedioPago);

    private async Task<Prerequisitos> SembrarPrerequisitosAsync(string nombre)
    {
        using var _ = fixture.CreateClient(); // arranca el host (siembra roles, alícuotas, tipos de comprobante)

        await using var db = fixture.CrearContextoDeAplicacion(TenantActualFijo.Plataforma);
        var ahora = DateTimeOffset.UtcNow;

        var tenant = new Tenant { Nombre = nombre, Estado = EstadoTenant.Activo, CreatedAt = ahora, UpdatedAt = ahora };
        db.Tenants.Add(tenant);
        await db.SaveChangesAsync();

        var empresa = new Empresa { IdTenant = tenant.Id, RazonSocial = nombre, CreatedAt = ahora, UpdatedAt = ahora };
        db.Empresas.Add(empresa);
        await db.SaveChangesAsync();

        var medioPago = new MedioPago
        {
            IdTenant = tenant.Id,
            Nombre = nombre + "-efectivo",
            Orden = 1,
            Comportamiento = ComportamientoMedioPago.Efectivo,
            AdmiteVuelto = true,
            RequiereReferencia = false,
            CreatedAt = ahora,
            UpdatedAt = ahora
        };
        db.MediosPago.Add(medioPago);
        await db.SaveChangesAsync();

        var usuario = new Usuario
        {
            IdTenant = tenant.Id,
            NombreUsuario = "vendedor",
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

        return new Prerequisitos(tenant.Id, empresa.Id, usuario.Id, medioPago.Id);
    }

    private static NpgsqlParameter P(object? valor) => new() { Value = valor ?? DBNull.Value };

    private static async Task<int> EjecutarAsync(NpgsqlConnection conexion, NpgsqlTransaction? tx, string sql, params object?[] valores)
    {
        await using var comando = conexion.CreateCommand();
        comando.Transaction = tx;
        comando.CommandText = sql;
        foreach (var valor in valores)
        {
            comando.Parameters.Add(P(valor));
        }

        return await comando.ExecuteNonQueryAsync();
    }

    private static async Task<T> EscalarAsync<T>(NpgsqlConnection conexion, string sql, params object?[] valores)
    {
        await using var comando = conexion.CreateCommand();
        comando.CommandText = sql;
        foreach (var valor in valores)
        {
            comando.Parameters.Add(P(valor));
        }

        return (T)(await comando.ExecuteScalarAsync())!;
    }

    /// <summary>Un pago de tesorería sin ítems (el estado previo al backfill), insertado crudo.</summary>
    private static Task<int> InsertarPagoAsync(
        NpgsqlConnection conexion, Prerequisitos p, decimal importe, DateTimeOffset? baja = null) =>
        EscalarAsync<int>(
            conexion,
            "INSERT INTO pagos (id_tenant, fecha, id_empresa, id_punto_venta, id_turno_caja, id_empleado, " +
            "categoria, origen_fondos, concepto, id_medio_pago, importe, created_at, updated_at, deleted_at) " +
            "VALUES ($1, now(), $2, NULL, NULL, $3, 'otros', 'tesoreria', 'pago de prueba', $4, $5, now(), now(), $6) " +
            "RETURNING id_pago",
            p.IdTenant, p.IdEmpresa, p.IdEmpleado, p.IdMedioPago, importe, baja);

    private static Task<int> InsertarItemAsync(
        NpgsqlConnection conexion, NpgsqlTransaction? tx, Prerequisitos p, int idPago, decimal importe,
        string? referencia = null, DateTimeOffset? baja = null, int? idMedioPago = null) =>
        EjecutarAsync(
            conexion, tx,
            "INSERT INTO items_pago (id_tenant, id_pago, id_medio_pago, importe, referencia, created_at, updated_at, deleted_at) " +
            "VALUES ($1, $2, $3, $4, $5, now(), now(), $6)",
            p.IdTenant, idPago, idMedioPago ?? p.IdMedioPago, importe, referencia, baja);

    private static async Task<PostgresException> LaTransaccionFallaAlComitearAsync(
        NpgsqlConnection conexion, Func<NpgsqlTransaction, Task> escribir)
    {
        await using var tx = await conexion.BeginTransactionAsync();
        await escribir(tx);
        return await Assert.ThrowsAsync<PostgresException>(() => tx.CommitAsync());
    }

    private static async Task LaTransaccionComiteaAsync(NpgsqlConnection conexion, Func<NpgsqlTransaction, Task> escribir)
    {
        await using var tx = await conexion.BeginTransactionAsync();
        await escribir(tx);
        await tx.CommitAsync();
    }

    // ---- CHECKs --------------------------------------------------------------------------------

    [Fact]
    public async Task UnItemConImporteCeroViolaLaCheckDeImportePositivo()
    {
        var p = await SembrarPrerequisitosAsync(nameof(UnItemConImporteCeroViolaLaCheckDeImportePositivo));
        await using var cruda = await fixture.AbrirConexionCrudaAsync("tenant", p.IdTenant);
        var idPago = await InsertarPagoAsync(cruda, p, 100m);

        var excepcion = await Assert.ThrowsAsync<PostgresException>(() => InsertarItemAsync(cruda, null, p, idPago, 0m));

        Assert.Equal("23514", excepcion.SqlState);
        Assert.Equal("ck_items_pago_importe_positivo", excepcion.ConstraintName);
    }

    [Fact]
    public async Task UnItemConReferenciaDeMasDeCienCaracteresViolaLaCheckDeLongitud()
    {
        var p = await SembrarPrerequisitosAsync(nameof(UnItemConReferenciaDeMasDeCienCaracteresViolaLaCheckDeLongitud));
        await using var cruda = await fixture.AbrirConexionCrudaAsync("tenant", p.IdTenant);
        var idPago = await InsertarPagoAsync(cruda, p, 100m);

        var excepcion = await Assert.ThrowsAsync<PostgresException>(
            () => InsertarItemAsync(cruda, null, p, idPago, 100m, referencia: new string('r', 101)));

        Assert.Equal("23514", excepcion.SqlState);
        Assert.Equal("ck_items_pago_referencia_longitud", excepcion.ConstraintName);
    }

    [Fact]
    public async Task UnItemConReferenciaDeExactamenteCienCaracteresEsAceptado()
    {
        var p = await SembrarPrerequisitosAsync(nameof(UnItemConReferenciaDeExactamenteCienCaracteresEsAceptado));
        await using var cruda = await fixture.AbrirConexionCrudaAsync("tenant", p.IdTenant);
        var idPago = await InsertarPagoAsync(cruda, p, 100m);

        await LaTransaccionComiteaAsync(
            cruda, tx => InsertarItemAsync(cruda, tx, p, idPago, 100m, referencia: new string('r', 100)));
    }

    // ---- FKs -----------------------------------------------------------------------------------

    [Fact]
    public async Task UnItemDeUnPagoInexistenteViolaLaFkDelPago()
    {
        var p = await SembrarPrerequisitosAsync(nameof(UnItemDeUnPagoInexistenteViolaLaFkDelPago));
        await using var cruda = await fixture.AbrirConexionCrudaAsync("tenant", p.IdTenant);

        var excepcion = await Assert.ThrowsAsync<PostgresException>(() => InsertarItemAsync(cruda, null, p, 999999, 100m));

        Assert.Equal("23503", excepcion.SqlState);
        Assert.Equal("fk_items_pago_pago", excepcion.ConstraintName);
    }

    [Fact]
    public async Task UnItemConUnMedioDeOtroTenantViolaLaFkDelMedio()
    {
        var p = await SembrarPrerequisitosAsync(nameof(UnItemConUnMedioDeOtroTenantViolaLaFkDelMedio));
        var ajeno = await SembrarPrerequisitosAsync(nameof(UnItemConUnMedioDeOtroTenantViolaLaFkDelMedio) + "Ajeno");
        await using var cruda = await fixture.AbrirConexionCrudaAsync("tenant", p.IdTenant);
        var idPago = await InsertarPagoAsync(cruda, p, 100m);

        var excepcion = await Assert.ThrowsAsync<PostgresException>(
            () => InsertarItemAsync(cruda, null, p, idPago, 100m, idMedioPago: ajeno.IdMedioPago));

        Assert.Equal("23503", excepcion.SqlState);
        Assert.Equal("fk_items_pago_medio_pago", excepcion.ConstraintName);
    }

    // ---- constraint trigger de la suma ---------------------------------------------------------

    /// <summary>LA CLÁUSULA: un ítem cuya suma no coincide con la cabecera falla AL COMITEAR (el trigger es
    /// diferido) con 23514 y el nombre del trigger como constraint, que es lo que traduce
    /// <c>ManejadorDeErrores</c>.</summary>
    [Fact]
    public async Task UnItemQueNoSumaElImporteDelPagoFallaAlComitearConElNombreDelTrigger()
    {
        var p = await SembrarPrerequisitosAsync(nameof(UnItemQueNoSumaElImporteDelPagoFallaAlComitearConElNombreDelTrigger));
        await using var cruda = await fixture.AbrirConexionCrudaAsync("tenant", p.IdTenant);
        var idPago = await InsertarPagoAsync(cruda, p, 100m);

        var excepcion = await LaTransaccionFallaAlComitearAsync(
            cruda, tx => InsertarItemAsync(cruda, tx, p, idPago, 60m));

        Assert.Equal("23514", excepcion.SqlState);
        Assert.Equal(Trigger, excepcion.ConstraintName);
        Assert.Equal(0L, await EscalarAsync<long>(cruda, "SELECT count(*) FROM items_pago WHERE id_pago = $1", idPago));
    }

    [Fact]
    public async Task VariosItemsQueSumanElImporteDelPagoComitean()
    {
        var p = await SembrarPrerequisitosAsync(nameof(VariosItemsQueSumanElImporteDelPagoComitean));
        await using var cruda = await fixture.AbrirConexionCrudaAsync("tenant", p.IdTenant);
        var idPago = await InsertarPagoAsync(cruda, p, 100m);

        // El orden dentro de la transacción no importa: tras el primer ítem la suma todavía no cierra.
        await LaTransaccionComiteaAsync(cruda, async tx =>
        {
            await InsertarItemAsync(cruda, tx, p, idPago, 60m);
            await InsertarItemAsync(cruda, tx, p, idPago, 40m, referencia: "cupón 1");
        });

        Assert.Equal(2L, await EscalarAsync<long>(cruda, "SELECT count(*) FROM items_pago WHERE id_pago = $1", idPago));
    }

    [Fact]
    public async Task CambiarElImporteDeLaCabeceraSinMoverSusItemsFallaAlComitear()
    {
        var p = await SembrarPrerequisitosAsync(nameof(CambiarElImporteDeLaCabeceraSinMoverSusItemsFallaAlComitear));
        await using var cruda = await fixture.AbrirConexionCrudaAsync("tenant", p.IdTenant);
        var idPago = await InsertarPagoAsync(cruda, p, 100m);
        await LaTransaccionComiteaAsync(cruda, tx => InsertarItemAsync(cruda, tx, p, idPago, 100m));

        var excepcion = await LaTransaccionFallaAlComitearAsync(
            cruda, tx => EjecutarAsync(cruda, tx, "UPDATE pagos SET importe = 150 WHERE id_pago = $1", idPago));

        Assert.Equal("23514", excepcion.SqlState);
        Assert.Equal(Trigger, excepcion.ConstraintName);
        Assert.Equal(100m, await EscalarAsync<decimal>(cruda, "SELECT importe FROM pagos WHERE id_pago = $1", idPago));
    }

    [Fact]
    public async Task CambiarLaCabeceraYSuItemEnLaMismaTransaccionComitea()
    {
        var p = await SembrarPrerequisitosAsync(nameof(CambiarLaCabeceraYSuItemEnLaMismaTransaccionComitea));
        await using var cruda = await fixture.AbrirConexionCrudaAsync("tenant", p.IdTenant);
        var idPago = await InsertarPagoAsync(cruda, p, 100m);
        await LaTransaccionComiteaAsync(cruda, tx => InsertarItemAsync(cruda, tx, p, idPago, 100m));

        await LaTransaccionComiteaAsync(cruda, async tx =>
        {
            await EjecutarAsync(cruda, tx, "UPDATE items_pago SET importe = 150 WHERE id_pago = $1", idPago);
            await EjecutarAsync(cruda, tx, "UPDATE pagos SET importe = 150 WHERE id_pago = $1", idPago);
        });

        Assert.Equal(150m, await EscalarAsync<decimal>(cruda, "SELECT sum(importe) FROM items_pago WHERE id_pago = $1", idPago));
    }

    [Fact]
    public async Task BorrarUnItemDeUnPagoConDosFallaAlComitear()
    {
        var p = await SembrarPrerequisitosAsync(nameof(BorrarUnItemDeUnPagoConDosFallaAlComitear));
        await using var cruda = await fixture.AbrirConexionCrudaAsync("tenant", p.IdTenant);
        var idPago = await InsertarPagoAsync(cruda, p, 100m);
        await LaTransaccionComiteaAsync(cruda, async tx =>
        {
            await InsertarItemAsync(cruda, tx, p, idPago, 60m);
            await InsertarItemAsync(cruda, tx, p, idPago, 40m);
        });

        var excepcion = await LaTransaccionFallaAlComitearAsync(
            cruda, tx => EjecutarAsync(cruda, tx, "DELETE FROM items_pago WHERE id_pago = $1 AND importe = 40", idPago));

        Assert.Equal("23514", excepcion.SqlState);
        Assert.Equal(Trigger, excepcion.ConstraintName);
    }

    [Fact]
    public async Task MoverUnItemAOtroPagoControlaLosDosPagos()
    {
        var p = await SembrarPrerequisitosAsync(nameof(MoverUnItemAOtroPagoControlaLosDosPagos));
        await using var cruda = await fixture.AbrirConexionCrudaAsync("tenant", p.IdTenant);
        var origen = await InsertarPagoAsync(cruda, p, 100m);
        var destino = await InsertarPagoAsync(cruda, p, 100m);
        await LaTransaccionComiteaAsync(cruda, async tx =>
        {
            await InsertarItemAsync(cruda, tx, p, origen, 100m);
            await InsertarItemAsync(cruda, tx, p, destino, 100m);
        });

        // El destino pasaría a sumar 200: aunque el origen quede sin ítems (y por eso sin control), el destino rompe.
        var excepcion = await LaTransaccionFallaAlComitearAsync(
            cruda, tx => EjecutarAsync(cruda, tx, "UPDATE items_pago SET id_pago = $1 WHERE id_pago = $2", destino, origen));

        Assert.Equal("23514", excepcion.SqlState);
        Assert.Equal(Trigger, excepcion.ConstraintName);
    }

    [Fact]
    public async Task LosItemsDadosDeBajaNoCuentanEnLaSumaNiHacenFallarAlPago()
    {
        var p = await SembrarPrerequisitosAsync(nameof(LosItemsDadosDeBajaNoCuentanEnLaSumaNiHacenFallarAlPago));
        await using var cruda = await fixture.AbrirConexionCrudaAsync("tenant", p.IdTenant);
        var idPago = await InsertarPagoAsync(cruda, p, 100m);

        await LaTransaccionComiteaAsync(cruda, async tx =>
        {
            await InsertarItemAsync(cruda, tx, p, idPago, 100m);
            await InsertarItemAsync(cruda, tx, p, idPago, 50m, baja: DateTimeOffset.UtcNow);
        });

        Assert.Equal(2L, await EscalarAsync<long>(cruda, "SELECT count(*) FROM items_pago WHERE id_pago = $1", idPago));
    }

    [Fact]
    public async Task UnPagoSinItemsVivosCambiaDeImporteLibrementeYUnPagoDadoDeBajaConservaSuInvariante()
    {
        var p = await SembrarPrerequisitosAsync(
            nameof(UnPagoSinItemsVivosCambiaDeImporteLibrementeYUnPagoDadoDeBajaConservaSuInvariante));
        await using var cruda = await fixture.AbrirConexionCrudaAsync("tenant", p.IdTenant);

        var sinItems = await InsertarPagoAsync(cruda, p, 100m);
        await LaTransaccionComiteaAsync(
            cruda, tx => EjecutarAsync(cruda, tx, "UPDATE pagos SET importe = 999 WHERE id_pago = $1", sinItems));

        // La baja lógica de la cabecera no toca los ítems: la suma sigue valiendo.
        var dadoDeBaja = await InsertarPagoAsync(cruda, p, 100m);
        await LaTransaccionComiteaAsync(cruda, tx => InsertarItemAsync(cruda, tx, p, dadoDeBaja, 100m));
        await LaTransaccionComiteaAsync(
            cruda, tx => EjecutarAsync(cruda, tx, "UPDATE pagos SET deleted_at = now() WHERE id_pago = $1", dadoDeBaja));
        var excepcion = await LaTransaccionFallaAlComitearAsync(
            cruda, tx => EjecutarAsync(cruda, tx, "UPDATE pagos SET importe = 5 WHERE id_pago = $1", dadoDeBaja));

        Assert.Equal(Trigger, excepcion.ConstraintName);
    }

    // ---- backfill ------------------------------------------------------------------------------

    /// <summary>LA CLÁUSULA del backfill: el <c>SET LOCAL app.acceso = 'plataforma'</c> DENTRO del mismo bloque.
    /// <c>pagos</c> e <c>items_pago</c> corren bajo FORCE ROW LEVEL SECURITY y <c>ways_app</c> no tiene BYPASSRLS:
    /// sin el GUC el <c>INSERT ... SELECT</c> no ve ninguna fila y reporta éxito. Se ejecuta la constante REAL de la
    /// migración (<see cref="ItemsDePago.SqlDelBackfill"/>), no una copia, primero sin la línea del GUC (cero filas)
    /// y después tal cual (un ítem por pago sin ítems, también el dado de baja, copiando su <c>deleted_at</c>).</summary>
    [Fact]
    public async Task ElBackfillCreaUnItemPorPagoIncluidosLosDeBajaSoloBajoElSetLocalDePlataforma()
    {
        var p = await SembrarPrerequisitosAsync(nameof(ElBackfillCreaUnItemPorPagoIncluidosLosDeBajaSoloBajoElSetLocalDePlataforma));
        var baja = DateTimeOffset.UtcNow.AddDays(-1);

        int vivo, dadoDeBaja, conItem;
        await using (var cruda = await fixture.AbrirConexionCrudaAsync("tenant", p.IdTenant))
        {
            vivo = await InsertarPagoAsync(cruda, p, 250m);
            dadoDeBaja = await InsertarPagoAsync(cruda, p, 80m, baja);
            conItem = await InsertarPagoAsync(cruda, p, 100m);
            await LaTransaccionComiteaAsync(cruda, tx => InsertarItemAsync(cruda, tx, p, conItem, 100m, referencia: "previo"));
        }

        var sql = ItemsDePago.SqlDelBackfill;
        Assert.Contains("SET LOCAL app.acceso = 'plataforma'", sql, StringComparison.Ordinal);
        var sinElSetLocal = string.Join(
            '\n', sql.Split('\n').Where(l => !l.TrimStart().StartsWith("SET LOCAL", StringComparison.Ordinal)));

        // (a) ways_app sin GUC: RLS no le deja ver ningún pago, cero filas y ningún error.
        await using (var app = new NpgsqlConnection(fixture.AppConnectionString))
        {
            await app.OpenAsync();
            Assert.Equal(0, await EjecutarAsync(app, null, sinElSetLocal));
        }

        // (b) el bloque tal cual lo trae la migración.
        await using (var app = new NpgsqlConnection(fixture.AppConnectionString))
        {
            await app.OpenAsync();
            Assert.True(await EjecutarAsync(app, null, sql) >= 2, "El backfill tenía que crear al menos los dos ítems faltantes.");
        }

        await using (var propietario = new NpgsqlConnection(fixture.OwnerConnectionString))
        {
            await propietario.OpenAsync();

            Assert.Equal(1L, await EscalarAsync<long>(
                propietario,
                "SELECT count(*) FROM items_pago i JOIN pagos g ON g.id_pago = i.id_pago AND g.id_tenant = i.id_tenant " +
                "WHERE i.id_pago = $1 AND i.id_medio_pago = g.id_medio_pago AND i.importe = g.importe " +
                "AND i.referencia IS NULL AND i.deleted_at IS NULL AND i.created_at = g.created_at AND i.updated_at = g.updated_at",
                vivo));
            Assert.Equal(1L, await EscalarAsync<long>(
                propietario,
                "SELECT count(*) FROM items_pago i JOIN pagos g ON g.id_pago = i.id_pago AND g.id_tenant = i.id_tenant " +
                "WHERE i.id_pago = $1 AND i.importe = 80 AND i.deleted_at = g.deleted_at AND i.deleted_at IS NOT NULL",
                dadoDeBaja));

            // El pago que ya tenía ítem no se duplica ni se pisa.
            Assert.Equal("previo", await EscalarAsync<string>(propietario, "SELECT referencia FROM items_pago WHERE id_pago = $1", conItem));
            Assert.Equal(1L, await EscalarAsync<long>(propietario, "SELECT count(*) FROM items_pago WHERE id_pago = $1", conItem));
        }

        // (c) idempotente: una segunda corrida no encuentra pagos sin ítems.
        await using (var app = new NpgsqlConnection(fixture.AppConnectionString))
        {
            await app.OpenAsync();
            Assert.Equal(0, await EjecutarAsync(app, null, sql));
        }
    }
}
