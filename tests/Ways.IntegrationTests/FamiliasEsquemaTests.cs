using System.Data.Common;
using Microsoft.EntityFrameworkCore;
using Npgsql;
using Ways.Application.Abstracciones;
using Ways.Domain.Articulos;
using Ways.Domain.Catalogos;
using Ways.Domain.Organizacion;
using Ways.Infrastructure.Multitenancy;

namespace Ways.IntegrationTests;

/// <summary>
/// Esquema de las familias de artículos (migración <c>FamiliasDeArticulos</c>) contra Postgres real:
/// la forma de <c>familias</c> y de <c>articulos.id_familia</c>, el aislamiento por tenant (RLS sobre
/// <c>ways_app</c>, <c>NOSUPERUSER NOBYPASSRLS</c>), la unicidad parcial del nombre con su carrera
/// real y la FK compuesta que impide una familia de otro tenant. Todo en SQL crudo salvo la prueba
/// del filtro de EF: la capa 2 (RLS) no depende del ORM.
///
/// <para>La carrera de <c>ux_familias_nombre</c> es una carrera A NIVEL DE BASE (dos INSERT
/// concurrentes con el mismo nombre): todavía no existe ningún endpoint que cree familias, así que
/// el "un 201 y un 409" por HTTP del skill <c>db-error-backstops</c> llega con el primer escritor.
/// Lo que sí queda probado acá es que el índice es el árbitro ante la concurrencia y que dispara
/// <c>23505</c> sobre <c>ux_familias_nombre</c>, que <see cref="ManejadorDeErroresFamiliasTests"/>
/// traduce a 409 <c>familia_nombre_duplicado</c>.</para>
/// </summary>
[Collection("Ways.IntegrationTests secuencial")]
public class FamiliasEsquemaTests(WaysApiFixture fixture) : IClassFixture<WaysApiFixture>
{
    private sealed record Escenario(
        int IdTenantA, int IdTenantB, int IdArticuloA, int IdFamiliaA, int IdFamiliaB);

    /// <summary>Dos tenants, un artículo del A y una familia en cada uno.</summary>
    private async Task<Escenario> SembrarAsync(string nombre)
    {
        using var _ = fixture.CreateClient(); // arranca el host (siembra alicuotas_iva)

        await using var db = fixture.CrearContextoDeAplicacion(TenantActualFijo.Plataforma);
        var ahora = DateTimeOffset.UtcNow;

        var tenantA = new Tenant { Nombre = $"{nombre}-A", Estado = EstadoTenant.Activo, CreatedAt = ahora, UpdatedAt = ahora };
        var tenantB = new Tenant { Nombre = $"{nombre}-B", Estado = EstadoTenant.Activo, CreatedAt = ahora, UpdatedAt = ahora };
        db.Tenants.AddRange(tenantA, tenantB);
        await db.SaveChangesAsync();

        var area = new Area { IdTenant = tenantA.Id, Nombre = nombre, Orden = 1, CreatedAt = ahora, UpdatedAt = ahora };
        db.Areas.Add(area);
        await db.SaveChangesAsync();

        var idAlicuotaIva = await db.AlicuotasIva.Select(a => a.Id).FirstAsync();

        var articulo = new Articulo
        {
            IdTenant = tenantA.Id,
            CodigoInterno = $"{nombre}-cod",
            Nombre = nombre,
            IdArea = area.Id,
            IdAlicuotaIva = idAlicuotaIva,
            UnidadVenta = UnidadVenta.Unidad,
            EsProducto = true,
            CreatedAt = ahora,
            UpdatedAt = ahora
        };
        var familiaA = new Familia { IdTenant = tenantA.Id, Nombre = $"{nombre} familia", CreatedAt = ahora, UpdatedAt = ahora };
        var familiaB = new Familia { IdTenant = tenantB.Id, Nombre = $"{nombre} familia", CreatedAt = ahora, UpdatedAt = ahora };
        db.Articulos.Add(articulo);
        db.Familias.AddRange(familiaA, familiaB);
        await db.SaveChangesAsync();

        return new Escenario(tenantA.Id, tenantB.Id, articulo.Id, familiaA.Id, familiaB.Id);
    }

    private static async Task<List<string[]>> LeerAsync(NpgsqlConnection conexion, string sql, params object[] parametros)
    {
        await using var comando = conexion.CreateCommand();
        comando.CommandText = sql;
        foreach (var parametro in parametros)
        {
            comando.Parameters.Add(new NpgsqlParameter { Value = parametro });
        }

        await using var lector = await comando.ExecuteReaderAsync();
        var filas = new List<string[]>();
        while (await lector.ReadAsync())
        {
            filas.Add(Enumerable.Range(0, lector.FieldCount)
                .Select(i => lector.IsDBNull(i) ? "<null>" : Convert.ToString(lector.GetValue(i))!)
                .ToArray());
        }

        return filas;
    }

    private static async Task<string> EscalarAsync(NpgsqlConnection conexion, string sql, params object[] parametros)
    {
        var filas = await LeerAsync(conexion, sql, parametros);
        return Assert.Single(filas)[0];
    }

    private static async Task InsertarFamiliaAsync(
        DbConnection conexion, DbTransaction? transaccion, int idTenant, string nombre)
    {
        await using var comando = conexion.CreateCommand();
        comando.Transaction = transaccion;
        comando.CommandText =
            "INSERT INTO familias (id_tenant, nombre, created_at, updated_at) VALUES ($1, $2, now(), now())";
        comando.Parameters.Add(new NpgsqlParameter { Value = idTenant });
        comando.Parameters.Add(new NpgsqlParameter { Value = nombre });
        await comando.ExecuteNonQueryAsync();
    }

    private static async Task<int> BackendPidAsync(NpgsqlConnection conexion) =>
        (int)(await new NpgsqlCommand("SELECT pg_backend_pid()", conexion).ExecuteScalarAsync())!;

    private static async Task<bool> EsperarBackendBloqueadoAsync(NpgsqlConnection conexionPoll, int pid)
    {
        var limite = DateTime.UtcNow.AddSeconds(10);
        while (DateTime.UtcNow < limite)
        {
            await using var comando = new NpgsqlCommand(
                "SELECT wait_event_type FROM pg_stat_activity WHERE pid = $1", conexionPoll);
            comando.Parameters.Add(new NpgsqlParameter { Value = pid });

            if ((await comando.ExecuteScalarAsync()) as string == "Lock")
            {
                return true;
            }

            await Task.Delay(25);
        }

        return false;
    }

    // ---- forma de las tablas -----------------------------------------------------------------

    [Fact]
    public async Task LaTablaFamiliasTieneLasColumnasDelModeloAprobado()
    {
        using var _ = fixture.CreateClient();
        await using var cruda = await fixture.AbrirConexionCrudaAsync("plataforma", null);

        var filas = await LeerAsync(
            cruda,
            "SELECT column_name, udt_name, is_nullable, coalesce(column_default, '<null>'), is_identity, " +
            "coalesce(identity_generation, '<null>') " +
            "FROM information_schema.columns WHERE table_schema = 'public' AND table_name = 'familias'");

        var porColumna = filas.ToDictionary(f => f[0]);

        Assert.Equal(
            ["activo", "created_at", "deleted_at", "id_familia", "id_tenant", "nombre", "updated_at"],
            porColumna.Keys.Order(StringComparer.Ordinal));

        // columna -> (tipo, es_nulable, default)
        Assert.Equal(["int4", "NO", "<null>", "YES", "BY DEFAULT"], porColumna["id_familia"][1..]);
        Assert.Equal(["int4", "NO", "<null>", "NO", "<null>"], porColumna["id_tenant"][1..]);
        Assert.Equal(["citext", "NO", "<null>", "NO", "<null>"], porColumna["nombre"][1..]);
        Assert.Equal(["bool", "NO", "true", "NO", "<null>"], porColumna["activo"][1..]);
        Assert.Equal(["timestamptz", "NO", "<null>", "NO", "<null>"], porColumna["created_at"][1..]);
        Assert.Equal(["timestamptz", "NO", "<null>", "NO", "<null>"], porColumna["updated_at"][1..]);
        Assert.Equal(["timestamptz", "YES", "<null>", "NO", "<null>"], porColumna["deleted_at"][1..]);
    }

    [Fact]
    public async Task ArticulosGanaIdFamiliaNulableSinDefaultYSinBackfill()
    {
        var s = await SembrarAsync(nameof(ArticulosGanaIdFamiliaNulableSinDefaultYSinBackfill));
        await using var cruda = await fixture.AbrirConexionCrudaAsync("plataforma", null);

        var columna = Assert.Single(await LeerAsync(
            cruda,
            "SELECT udt_name, is_nullable, coalesce(column_default, '<null>') FROM information_schema.columns " +
            "WHERE table_schema = 'public' AND table_name = 'articulos' AND column_name = 'id_familia'"));
        Assert.Equal(["int4", "YES", "<null>"], columna);

        Assert.Equal(
            "<null>",
            await EscalarAsync(cruda, "SELECT coalesce(id_familia::text, '<null>') FROM articulos WHERE id_articulo = $1", s.IdArticuloA));
    }

    [Fact]
    public async Task LasConstraintsEIndicesDeFamiliasTienenLosNombresYLaFormaAprobados()
    {
        using var _ = fixture.CreateClient();
        await using var cruda = await fixture.AbrirConexionCrudaAsync("plataforma", null);

        var constraints = (await LeerAsync(
                cruda,
                "SELECT conname, pg_get_constraintdef(oid) FROM pg_constraint " +
                "WHERE conrelid IN ('familias'::regclass, 'articulos'::regclass) " +
                "AND conname IN ('pk_familias', 'ak_familias_id_familia_id_tenant', 'fk_familias_tenant', 'fk_articulos_familia')"))
            .ToDictionary(f => f[0], f => f[1]);

        Assert.Equal("PRIMARY KEY (id_familia)", constraints["pk_familias"]);
        Assert.Equal("UNIQUE (id_familia, id_tenant)", constraints["ak_familias_id_familia_id_tenant"]);
        Assert.Equal("FOREIGN KEY (id_tenant) REFERENCES tenants(id_tenant) ON DELETE RESTRICT", constraints["fk_familias_tenant"]);
        Assert.Equal(
            "FOREIGN KEY (id_familia, id_tenant) REFERENCES familias(id_familia, id_tenant) ON DELETE RESTRICT",
            constraints["fk_articulos_familia"]);

        var indices = (await LeerAsync(
                cruda,
                "SELECT indexname, indexdef FROM pg_indexes WHERE schemaname = 'public' " +
                "AND indexname IN ('ux_familias_nombre', 'ix_articulos_familia')"))
            .ToDictionary(f => f[0], f => f[1]);

        Assert.Equal(
            "CREATE UNIQUE INDEX ux_familias_nombre ON public.familias USING btree (id_tenant, nombre) WHERE (deleted_at IS NULL)",
            indices["ux_familias_nombre"]);
        Assert.Equal(
            "CREATE INDEX ix_articulos_familia ON public.articulos USING btree (id_familia, id_tenant) WHERE (id_familia IS NOT NULL)",
            indices["ix_articulos_familia"]);
    }

    /// <summary>El índice parcial de <c>articulos.id_familia</c> es utilizable por una consulta con
    /// <c>id_familia = x</c> (la igualdad implica <c>id_familia IS NOT NULL</c>, el predicado del
    /// índice): sin esa implicación el filtro parcial dejaría el índice sin uso. La consulta no
    /// filtra por <c>id_tenant</c> a propósito: con una tabla chica un índice encabezado por
    /// <c>id_tenant</c> empata en costo con este y el planificador elige entre los dos por
    /// desempate; sin esa columna ningún otro índice de <c>articulos</c> tiene una condición que
    /// aplicar. Se descarta el scan secuencial con <c>enable_seqscan = off</c> porque con una tabla
    /// casi vacía el planificador lo prefiere aunque el índice sirva.</summary>
    [Fact]
    public async Task ElIndiceParcialDeIdFamiliaEsUtilizablePorUnaConsultaConIdFamilia()
    {
        var s = await SembrarAsync(nameof(ElIndiceParcialDeIdFamiliaEsUtilizablePorUnaConsultaConIdFamilia));
        await using var cruda = await fixture.AbrirConexionCrudaAsync("tenant", s.IdTenantA);
        await using var transaccion = await cruda.BeginTransactionAsync();

        await using (var forzar = new NpgsqlCommand("SET LOCAL enable_seqscan = off", cruda, transaccion))
        {
            await forzar.ExecuteNonQueryAsync();
        }

        // Id interpolado (entero propio de la prueba): EXPLAIN no necesita parámetros enlazados.
        await using var explicar = new NpgsqlCommand(
            $"EXPLAIN SELECT id_articulo FROM articulos WHERE id_familia = {s.IdFamiliaA}",
            cruda,
            transaccion);

        var plan = new List<string>();
        await using (var lector = await explicar.ExecuteReaderAsync())
        {
            while (await lector.ReadAsync())
            {
                plan.Add(lector.GetString(0));
            }
        }

        Assert.True(
            plan.Any(linea => linea.Contains("ix_articulos_familia", StringComparison.Ordinal)),
            $"El plan no usa ix_articulos_familia: {string.Join(" | ", plan)}");
    }

    // ---- RLS ----------------------------------------------------------------------------------

    [Fact]
    public async Task FamiliasTieneRlsHabilitadaYForzadaConLaPolicyEstandar()
    {
        using var _ = fixture.CreateClient();
        await using var cruda = await fixture.AbrirConexionCrudaAsync("plataforma", null);

        var estado = Assert.Single(await LeerAsync(
            cruda, "SELECT relrowsecurity::text, relforcerowsecurity::text FROM pg_class WHERE relname = 'familias'"));
        Assert.Equal(["true", "true"], estado);

        var politica = Assert.Single(await LeerAsync(
            cruda, "SELECT policyname, cmd, qual, with_check FROM pg_policies WHERE tablename = 'familias'"));
        Assert.Equal("familias_tenant", politica[0]);
        Assert.Equal("ALL", politica[1]);

        // USING y WITH CHECK con el mismo predicado estándar (HabilitarRlsDeTenant).
        foreach (var predicado in new[] { politica[2], politica[3] })
        {
            Assert.Contains("app_es_plataforma()", predicado, StringComparison.Ordinal);
            Assert.Contains("id_tenant = app_tenant_actual()", predicado, StringComparison.Ordinal);
        }
    }

    [Fact]
    public async Task UnaSesionDeOtroTenantNoVeLaFamiliaPorSelect()
    {
        var s = await SembrarAsync(nameof(UnaSesionDeOtroTenantNoVeLaFamiliaPorSelect));

        await using var delDueno = await fixture.AbrirConexionCrudaAsync("tenant", s.IdTenantA);
        Assert.Equal("1", await EscalarAsync(delDueno, "SELECT count(*) FROM familias WHERE id_familia = $1", s.IdFamiliaA));

        await using var ajena = await fixture.AbrirConexionCrudaAsync("tenant", s.IdTenantB);
        Assert.Equal("0", await EscalarAsync(ajena, "SELECT count(*) FROM familias WHERE id_familia = $1", s.IdFamiliaA));
    }

    [Fact]
    public async Task UnaSesionDeOtroTenantNoPuedeActualizarNiBorrarLaFamilia()
    {
        var s = await SembrarAsync(nameof(UnaSesionDeOtroTenantNoPuedeActualizarNiBorrarLaFamilia));
        await using var ajena = await fixture.AbrirConexionCrudaAsync("tenant", s.IdTenantB);

        await using var actualizar = ajena.CreateCommand();
        actualizar.CommandText = "UPDATE familias SET nombre = 'tocada por intruso' WHERE id_familia = $1";
        actualizar.Parameters.Add(new NpgsqlParameter { Value = s.IdFamiliaA });
        Assert.Equal(0, await actualizar.ExecuteNonQueryAsync());

        await using var borrar = ajena.CreateCommand();
        borrar.CommandText = "DELETE FROM familias WHERE id_familia = $1";
        borrar.Parameters.Add(new NpgsqlParameter { Value = s.IdFamiliaA });
        Assert.Equal(0, await borrar.ExecuteNonQueryAsync());

        await using var delDueno = await fixture.AbrirConexionCrudaAsync("tenant", s.IdTenantA);
        Assert.Equal(
            $"{nameof(UnaSesionDeOtroTenantNoPuedeActualizarNiBorrarLaFamilia)} familia",
            await EscalarAsync(delDueno, "SELECT nombre FROM familias WHERE id_familia = $1", s.IdFamiliaA));
    }

    [Fact]
    public async Task UnInsertConIdTenantAjenoSeRechazaConSqlState42501()
    {
        var s = await SembrarAsync(nameof(UnInsertConIdTenantAjenoSeRechazaConSqlState42501));
        await using var delTenantA = await fixture.AbrirConexionCrudaAsync("tenant", s.IdTenantA);

        var excepcion = await Assert.ThrowsAsync<PostgresException>(
            () => InsertarFamiliaAsync(delTenantA, null, s.IdTenantB, "intrusa"));

        Assert.Equal("42501", excepcion.SqlState);
    }

    [Fact]
    public async Task ElFiltroDeEfNuncaDevuelveFamiliasDeOtroTenant()
    {
        var s = await SembrarAsync(nameof(ElFiltroDeEfNuncaDevuelveFamiliasDeOtroTenant));

        await using var sesionA = fixture.CrearContextoDeAplicacion(new TenantActualFijo(ModoDeAcceso.Tenant, s.IdTenantA));
        Assert.Equal([s.IdFamiliaA], await sesionA.Familias.Select(f => f.Id).ToListAsync());

        await using var sesionB = fixture.CrearContextoDeAplicacion(new TenantActualFijo(ModoDeAcceso.Tenant, s.IdTenantB));
        Assert.Equal([s.IdFamiliaB], await sesionB.Familias.Select(f => f.Id).ToListAsync());
    }

    // ---- unicidad parcial del nombre (ux_familias_nombre) ------------------------------------

    [Fact]
    public async Task UnNombreDuplicadoEnElMismoTenantViolaLaUnicidadConSqlState23505()
    {
        var s = await SembrarAsync(nameof(UnNombreDuplicadoEnElMismoTenantViolaLaUnicidadConSqlState23505));
        await using var cruda = await fixture.AbrirConexionCrudaAsync("tenant", s.IdTenantA);

        // Mismo nombre que la familia sembrada, con otra capitalización: la columna es citext.
        var excepcion = await Assert.ThrowsAsync<PostgresException>(
            () => InsertarFamiliaAsync(
                cruda, null, s.IdTenantA,
                $"{nameof(UnNombreDuplicadoEnElMismoTenantViolaLaUnicidadConSqlState23505)} FAMILIA".ToUpperInvariant()));

        Assert.Equal("23505", excepcion.SqlState);
        Assert.Equal("ux_familias_nombre", excepcion.ConstraintName);
    }

    /// <summary>La unicidad es POR tenant: la prueba de arriba falla con el nombre repetido dentro del
    /// tenant A, y acá el mismo nombre se crea en el tenant B sin conflicto.</summary>
    [Fact]
    public async Task ElMismoNombreEnOtroTenantEsValido()
    {
        var s = await SembrarAsync(nameof(ElMismoNombreEnOtroTenantEsValido));
        const string nombre = "Nombre compartido entre tenants";

        await using var delTenantA = await fixture.AbrirConexionCrudaAsync("tenant", s.IdTenantA);
        await InsertarFamiliaAsync(delTenantA, null, s.IdTenantA, nombre);

        await using var delTenantB = await fixture.AbrirConexionCrudaAsync("tenant", s.IdTenantB);
        await InsertarFamiliaAsync(delTenantB, null, s.IdTenantB, nombre);

        await using var cruda = await fixture.AbrirConexionCrudaAsync("plataforma", null);
        Assert.Equal(
            "2",
            await EscalarAsync(
                cruda,
                "SELECT count(*) FROM familias WHERE nombre = $1 AND id_tenant IN ($2, $3)",
                nombre,
                s.IdTenantA,
                s.IdTenantB));
    }

    /// <summary>La unicidad es PARCIAL (<c>deleted_at IS NULL</c>): el nombre de una familia dada de
    /// baja se puede reutilizar, y la nueva familia viva vuelve a estar protegida.</summary>
    [Fact]
    public async Task ElNombreDeUnaFamiliaDadaDeBajaSePuedeReutilizar()
    {
        var s = await SembrarAsync(nameof(ElNombreDeUnaFamiliaDadaDeBajaSePuedeReutilizar));
        var nombre = $"{nameof(ElNombreDeUnaFamiliaDadaDeBajaSePuedeReutilizar)} familia";
        await using var cruda = await fixture.AbrirConexionCrudaAsync("tenant", s.IdTenantA);

        await using (var baja = cruda.CreateCommand())
        {
            baja.CommandText = "UPDATE familias SET deleted_at = now() WHERE id_familia = $1";
            baja.Parameters.Add(new NpgsqlParameter { Value = s.IdFamiliaA });
            Assert.Equal(1, await baja.ExecuteNonQueryAsync());
        }

        await InsertarFamiliaAsync(cruda, null, s.IdTenantA, nombre);

        var excepcion = await Assert.ThrowsAsync<PostgresException>(
            () => InsertarFamiliaAsync(cruda, null, s.IdTenantA, nombre));
        Assert.Equal("23505", excepcion.SqlState);
        Assert.Equal("ux_familias_nombre", excepcion.ConstraintName);
    }

    /// <summary>Carrera real a nivel de base: la ganadora inserta sin comitear, la perdedora queda
    /// esperando la misma clave (observado en <c>pg_stat_activity</c>, sin dormir a ciegas) y, recién
    /// cuando la ganadora comitea, falla con <c>23505</c> sobre <c>ux_familias_nombre</c>. Queda
    /// exactamente una fila viva con ese nombre.</summary>
    [Fact]
    public async Task DosAltasConcurrentesDelMismoNombreDejanUnaFilaYUnaViolacion23505()
    {
        var s = await SembrarAsync(nameof(DosAltasConcurrentesDelMismoNombreDejanUnaFilaYUnaViolacion23505));
        const string nombre = "Familia en carrera";

        await using var conexionGanadora = await fixture.AbrirConexionCrudaAsync("tenant", s.IdTenantA);
        await using var transaccionGanadora = await conexionGanadora.BeginTransactionAsync();
        await InsertarFamiliaAsync(conexionGanadora, transaccionGanadora, s.IdTenantA, nombre);

        await using var conexionPerdedora = await fixture.AbrirConexionCrudaAsync("tenant", s.IdTenantA);
        var pidPerdedora = await BackendPidAsync(conexionPerdedora);
        var altaPerdedora = InsertarFamiliaAsync(conexionPerdedora, null, s.IdTenantA, nombre);

        await using var conexionPoll = await fixture.AbrirConexionCrudaAsync("plataforma", null);
        Assert.True(
            await EsperarBackendBloqueadoAsync(conexionPoll, pidPerdedora),
            "La segunda alta nunca se observó esperando la primera: la prueba no está probando la carrera.");

        await transaccionGanadora.CommitAsync();

        var violacion = await Assert.ThrowsAsync<PostgresException>(() => altaPerdedora);
        Assert.Equal("23505", violacion.SqlState);
        Assert.Equal("ux_familias_nombre", violacion.ConstraintName);

        Assert.Equal(
            "1",
            await EscalarAsync(
                conexionPoll,
                "SELECT count(*) FROM familias WHERE id_tenant = $1 AND nombre = $2 AND deleted_at IS NULL",
                s.IdTenantA,
                nombre));
    }

    // ---- FK compuesta articulos -> familias ---------------------------------------------------

    [Fact]
    public async Task UnArticuloPuedeEntrarYSalirDeUnaFamiliaDeSuTenant()
    {
        var s = await SembrarAsync(nameof(UnArticuloPuedeEntrarYSalirDeUnaFamiliaDeSuTenant));
        await using var cruda = await fixture.AbrirConexionCrudaAsync("tenant", s.IdTenantA);

        await using (var entrar = cruda.CreateCommand())
        {
            entrar.CommandText = "UPDATE articulos SET id_familia = $1 WHERE id_articulo = $2";
            entrar.Parameters.Add(new NpgsqlParameter { Value = s.IdFamiliaA });
            entrar.Parameters.Add(new NpgsqlParameter { Value = s.IdArticuloA });
            Assert.Equal(1, await entrar.ExecuteNonQueryAsync());
        }

        Assert.Equal(
            s.IdFamiliaA.ToString(),
            await EscalarAsync(cruda, "SELECT id_familia FROM articulos WHERE id_articulo = $1", s.IdArticuloA));

        await using var salir = cruda.CreateCommand();
        salir.CommandText = "UPDATE articulos SET id_familia = NULL WHERE id_articulo = $1";
        salir.Parameters.Add(new NpgsqlParameter { Value = s.IdArticuloA });
        Assert.Equal(1, await salir.ExecuteNonQueryAsync());
    }

    /// <summary>La FK es COMPUESTA <c>(id_familia, id_tenant)</c>: el artículo del tenant A no puede
    /// apuntar a la familia del tenant B aunque el id exista. La RLS no interviene (el chequeo de una
    /// FK no pasa por las policies), así que el que rechaza es el esquema.</summary>
    [Fact]
    public async Task UnArticuloNoPuedeEntrarEnLaFamiliaDeOtroTenant()
    {
        var s = await SembrarAsync(nameof(UnArticuloNoPuedeEntrarEnLaFamiliaDeOtroTenant));
        await using var cruda = await fixture.AbrirConexionCrudaAsync("tenant", s.IdTenantA);

        await using var entrar = cruda.CreateCommand();
        entrar.CommandText = "UPDATE articulos SET id_familia = $1 WHERE id_articulo = $2";
        entrar.Parameters.Add(new NpgsqlParameter { Value = s.IdFamiliaB });
        entrar.Parameters.Add(new NpgsqlParameter { Value = s.IdArticuloA });

        var excepcion = await Assert.ThrowsAsync<PostgresException>(() => entrar.ExecuteNonQueryAsync());

        Assert.Equal("23503", excepcion.SqlState);
        Assert.Equal("fk_articulos_familia", excepcion.ConstraintName);
    }

    [Fact]
    public async Task UnaFamiliaConMiembrosNoSePuedeBorrarFisicamente()
    {
        var s = await SembrarAsync(nameof(UnaFamiliaConMiembrosNoSePuedeBorrarFisicamente));
        await using var cruda = await fixture.AbrirConexionCrudaAsync("tenant", s.IdTenantA);

        await using (var entrar = cruda.CreateCommand())
        {
            entrar.CommandText = "UPDATE articulos SET id_familia = $1 WHERE id_articulo = $2";
            entrar.Parameters.Add(new NpgsqlParameter { Value = s.IdFamiliaA });
            entrar.Parameters.Add(new NpgsqlParameter { Value = s.IdArticuloA });
            await entrar.ExecuteNonQueryAsync();
        }

        await using var borrar = cruda.CreateCommand();
        borrar.CommandText = "DELETE FROM familias WHERE id_familia = $1";
        borrar.Parameters.Add(new NpgsqlParameter { Value = s.IdFamiliaA });

        var excepcion = await Assert.ThrowsAsync<PostgresException>(() => borrar.ExecuteNonQueryAsync());

        Assert.Equal("23503", excepcion.SqlState);
        Assert.Equal("fk_articulos_familia", excepcion.ConstraintName);
    }
}
