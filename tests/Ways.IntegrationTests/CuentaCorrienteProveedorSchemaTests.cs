using Npgsql;
using Ways.Domain.Catalogos;
using Ways.Domain.Organizacion;
using Ways.Domain.Proveedores;
using Ways.Infrastructure.Multitenancy;

namespace Ways.IntegrationTests;

/// <summary>
/// stage-15-cc-proveedores-ledger, Slice 1 (tasks 1.22-1.24, mutation targets #9-#11,
/// db-error-backstops skill): RLS + tenant isolation, la CHECK de apertura y los backstops de FK
/// exentos de <c>movimientos_cuenta_corriente_proveedor</c> — todos sobre la base COMPARTIDA de
/// <see cref="WaysApiFixture"/> (a diferencia de <c>CuentaCorrienteProveedorBackfillTests</c>,
/// estas pruebas no dependen del momento exacto del backfill: la tabla ya existe con RLS y CHECK
/// activos apenas el contenedor migra).
/// </summary>
[Collection("Ways.IntegrationTests secuencial")]
public class CuentaCorrienteProveedorSchemaTests(WaysApiFixture fixture) : IClassFixture<WaysApiFixture>
{
    private sealed record Escenario(int IdTenant, int IdProveedor, int IdPuntoVenta);

    private async Task<Escenario> SembrarEscenarioAsync(string nombre)
    {
        using var _ = fixture.CreateClient(); // arranca el host: siembra roles/alicuotas/tipos de comprobante

        await using var db = fixture.CrearContextoDeAplicacion(TenantActualFijo.Plataforma);
        var ahora = DateTimeOffset.UtcNow;

        var tenant = new Tenant { Nombre = nombre, Estado = EstadoTenant.Activo, CreatedAt = ahora, UpdatedAt = ahora };
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

        var condicionFiscal = new CondicionFiscal { Codigo = $"{nombre}-CF", Nombre = nombre, CreatedAt = ahora, UpdatedAt = ahora };
        db.CondicionesFiscales.Add(condicionFiscal);
        await db.SaveChangesAsync();

        var proveedor = new Proveedor
        {
            IdTenant = tenant.Id, RazonSocial = nombre, IdCondicionFiscal = condicionFiscal.Id,
            CreatedAt = ahora, UpdatedAt = ahora
        };
        db.Proveedores.Add(proveedor);
        await db.SaveChangesAsync();

        return new Escenario(tenant.Id, proveedor.Id, puntoVenta.Id);
    }

    // ---------------------------------------------------------------------------------------
    // RLS (task 1.22, mutation targets #9 y #11)
    // ---------------------------------------------------------------------------------------

    /// <summary>Target #9/#11 discriminante: una fila 'apertura' válida (misma forma que la que
    /// escribe la migración) insertada bajo el tenant A no debe verse desde el tenant B.</summary>
    [Fact]
    public async Task UnaSesionDeOtroTenantNoVeLosMovimientosPorSelect()
    {
        var a = await SembrarEscenarioAsync(nameof(UnaSesionDeOtroTenantNoVeLosMovimientosPorSelect) + "-A");
        var b = await SembrarEscenarioAsync(nameof(UnaSesionDeOtroTenantNoVeLosMovimientosPorSelect) + "-B");

        await using (var cruda = await fixture.AbrirConexionCrudaAsync("tenant", a.IdTenant))
        {
            await using var insertar = cruda.CreateCommand();
            insertar.CommandText =
                "INSERT INTO movimientos_cuenta_corriente_proveedor " +
                "(id_tenant, id_proveedor, fecha, id_punto_venta, id_empleado, tipo, id_comprobante_compra, id_gasto, importe, saldo_resultante, detalle) " +
                "VALUES ($1, $2, now(), NULL, NULL, 'apertura', NULL, NULL, 500, 500, 'seed de prueba')";
            insertar.Parameters.Add(new NpgsqlParameter { Value = a.IdTenant });
            insertar.Parameters.Add(new NpgsqlParameter { Value = a.IdProveedor });
            await insertar.ExecuteNonQueryAsync();
        }

        await using var comoB = await fixture.AbrirConexionCrudaAsync("tenant", b.IdTenant);
        await using var comando = comoB.CreateCommand();
        comando.CommandText = "SELECT count(*) FROM movimientos_cuenta_corriente_proveedor WHERE id_proveedor = $1";
        comando.Parameters.Add(new NpgsqlParameter { Value = a.IdProveedor });

        var total = (long)(await comando.ExecuteScalarAsync())!;
        Assert.Equal(0, total);
    }

    /// <summary>Target #9/#11: un INSERT con <c>id_tenant</c> ajeno al de la sesión es refusado
    /// por <c>WITH CHECK</c> antes de que la fila exista.</summary>
    [Fact]
    public async Task UnInsertConIdTenantAjenoSeRechaza()
    {
        var a = await SembrarEscenarioAsync(nameof(UnInsertConIdTenantAjenoSeRechaza) + "-A");
        var b = await SembrarEscenarioAsync(nameof(UnInsertConIdTenantAjenoSeRechaza) + "-B");

        await using var comoA = await fixture.AbrirConexionCrudaAsync("tenant", a.IdTenant);
        await using var comando = comoA.CreateCommand();
        comando.CommandText =
            "INSERT INTO movimientos_cuenta_corriente_proveedor " +
            "(id_tenant, id_proveedor, fecha, id_punto_venta, id_empleado, tipo, id_comprobante_compra, id_gasto, importe, saldo_resultante, detalle) " +
            "VALUES ($1, $2, now(), NULL, NULL, 'apertura', NULL, NULL, 1, 1, 'intruso')";
        comando.Parameters.Add(new NpgsqlParameter { Value = b.IdTenant }); // ajeno a la sesión (tenant A)
        comando.Parameters.Add(new NpgsqlParameter { Value = a.IdProveedor });

        var excepcion = await Assert.ThrowsAsync<PostgresException>(() => comando.ExecuteNonQueryAsync());
        Assert.Equal("42501", excepcion.SqlState);
    }

    // ---------------------------------------------------------------------------------------
    // CHECK de apertura (task 1.23, mutation target #10)
    // ---------------------------------------------------------------------------------------

    /// <summary>Target #10: <c>tipo = 'apertura'</c> CON punto de venta viola la CHECK.</summary>
    [Fact]
    public async Task UnInsertDeAperturaConPuntoDeVentaViolaLaCheck()
    {
        var e = await SembrarEscenarioAsync(nameof(UnInsertDeAperturaConPuntoDeVentaViolaLaCheck));

        await using var cruda = await fixture.AbrirConexionCrudaAsync("tenant", e.IdTenant);
        await using var comando = cruda.CreateCommand();
        comando.CommandText =
            "INSERT INTO movimientos_cuenta_corriente_proveedor " +
            "(id_tenant, id_proveedor, fecha, id_punto_venta, id_empleado, tipo, id_comprobante_compra, id_gasto, importe, saldo_resultante, detalle) " +
            "VALUES ($1, $2, now(), $3, NULL, 'apertura', NULL, NULL, 1, 1, 'invalido')";
        comando.Parameters.Add(new NpgsqlParameter { Value = e.IdTenant });
        comando.Parameters.Add(new NpgsqlParameter { Value = e.IdProveedor });
        comando.Parameters.Add(new NpgsqlParameter { Value = e.IdPuntoVenta });

        var excepcion = await Assert.ThrowsAsync<PostgresException>(() => comando.ExecuteNonQueryAsync());
        Assert.Equal("23514", excepcion.SqlState);
        Assert.Equal("ck_movimientos_cuenta_corriente_proveedor_apertura", excepcion.ConstraintName);
    }

    /// <summary>Target #10: <c>tipo = 'compra'</c> SIN punto de venta ni empleado viola la
    /// CHECK, la dirección opuesta del mismo par de columnas.</summary>
    [Fact]
    public async Task UnInsertDeCompraSinPuntoDeVentaNiEmpleadoViolaLaCheck()
    {
        var e = await SembrarEscenarioAsync(nameof(UnInsertDeCompraSinPuntoDeVentaNiEmpleadoViolaLaCheck));

        await using var cruda = await fixture.AbrirConexionCrudaAsync("tenant", e.IdTenant);
        await using var comando = cruda.CreateCommand();
        comando.CommandText =
            "INSERT INTO movimientos_cuenta_corriente_proveedor " +
            "(id_tenant, id_proveedor, fecha, id_punto_venta, id_empleado, tipo, id_comprobante_compra, id_gasto, importe, saldo_resultante, detalle) " +
            "VALUES ($1, $2, now(), NULL, NULL, 'compra', NULL, NULL, 1, 1, NULL)";
        comando.Parameters.Add(new NpgsqlParameter { Value = e.IdTenant });
        comando.Parameters.Add(new NpgsqlParameter { Value = e.IdProveedor });

        var excepcion = await Assert.ThrowsAsync<PostgresException>(() => comando.ExecuteNonQueryAsync());
        Assert.Equal("23514", excepcion.SqlState);
        Assert.Equal("ck_movimientos_cuenta_corriente_proveedor_apertura", excepcion.ConstraintName);
    }

    // ---------------------------------------------------------------------------------------
    // db-error-backstops — exenciones (task 1.24, proposal §E)
    // ---------------------------------------------------------------------------------------

    /// <summary>Exención documentada: <c>fk_..._tenant</c> (session-derived). Bajo modo
    /// plataforma (WITH CHECK no bloquea) un <c>id_tenant</c> inexistente sigue rechazado por
    /// FK — raw insert probando SQLSTATE <c>23503</c>. NOTA: como <c>id_proveedor</c> es
    /// NOT NULL y su FK es compuesta (<c>id_proveedor, id_tenant</c>), cualquier
    /// <c>id_tenant</c> espurio también rompe simultáneamente <c>fk_..._proveedor</c> — Postgres
    /// reporta UNA sola constraint violada (la primera en el orden de declaración de la tabla,
    /// empíricamente <c>fk_..._proveedor</c> acá), así que aislar el nombre exacto de
    /// <c>fk_..._tenant</c> es estructuralmente imposible en esta tabla de clave compuesta —
    /// se afirma el SQLSTATE genérico y el prefijo <c>fk_</c>, que es lo que
    /// <c>ManejadorDeErrores.cs</c> de verdad clasifica.</summary>
    [Fact]
    public async Task UnIdTenantInexistenteViolaAlgunaFkGenerica23503()
    {
        var e = await SembrarEscenarioAsync(nameof(UnIdTenantInexistenteViolaAlgunaFkGenerica23503));

        await using var comoPlataforma = await fixture.AbrirConexionCrudaAsync("plataforma", null);
        await using var comando = comoPlataforma.CreateCommand();
        comando.CommandText =
            "INSERT INTO movimientos_cuenta_corriente_proveedor " +
            "(id_tenant, id_proveedor, fecha, id_punto_venta, id_empleado, tipo, id_comprobante_compra, id_gasto, importe, saldo_resultante, detalle) " +
            "VALUES ($1, $2, now(), NULL, NULL, 'apertura', NULL, NULL, 1, 1, NULL)";
        comando.Parameters.Add(new NpgsqlParameter { Value = 999_999_999 }); // id_tenant inexistente
        comando.Parameters.Add(new NpgsqlParameter { Value = e.IdProveedor });

        var excepcion = await Assert.ThrowsAsync<PostgresException>(() => comando.ExecuteNonQueryAsync());
        Assert.Equal("23503", excepcion.SqlState);
        Assert.StartsWith("fk_movimientos_cuenta_corriente_proveedor_", excepcion.ConstraintName);
    }

    /// <summary>Exención documentada: <c>fk_..._empleado</c> (server-derived,
    /// <c>usuarios</c> soft-deleted así que nunca se remueve físicamente) — raw insert contra un
    /// <c>id_empleado</c> inexistente, SQLSTATE <c>23503</c>.</summary>
    [Fact]
    public async Task UnIdEmpleadoInexistenteViolaLaFkGenerica23503()
    {
        var e = await SembrarEscenarioAsync(nameof(UnIdEmpleadoInexistenteViolaLaFkGenerica23503));

        await using var cruda = await fixture.AbrirConexionCrudaAsync("tenant", e.IdTenant);
        await using var comando = cruda.CreateCommand();
        comando.CommandText =
            "INSERT INTO movimientos_cuenta_corriente_proveedor " +
            "(id_tenant, id_proveedor, fecha, id_punto_venta, id_empleado, tipo, id_comprobante_compra, id_gasto, importe, saldo_resultante, detalle) " +
            "VALUES ($1, $2, now(), $3, $4, 'ajuste', NULL, NULL, 1, 1, 'ajuste de prueba')";
        comando.Parameters.Add(new NpgsqlParameter { Value = e.IdTenant });
        comando.Parameters.Add(new NpgsqlParameter { Value = e.IdProveedor });
        comando.Parameters.Add(new NpgsqlParameter { Value = e.IdPuntoVenta });
        comando.Parameters.Add(new NpgsqlParameter { Value = 999_999_999 }); // id_empleado inexistente

        var excepcion = await Assert.ThrowsAsync<PostgresException>(() => comando.ExecuteNonQueryAsync());
        Assert.Equal("23503", excepcion.SqlState);
        Assert.Equal("fk_movimientos_cuenta_corriente_proveedor_empleado", excepcion.ConstraintName);
    }

    /// <summary>Exención documentada: <c>fk_..._gasto</c> (el id del gasto que la misma
    /// transacción acaba de insertar, todavía sin call site hasta slice 2/3) — raw insert contra
    /// un <c>id_gasto</c> inexistente, SQLSTATE <c>23503</c>.</summary>
    [Fact]
    public async Task UnIdGastoInexistenteViolaLaFkGenerica23503()
    {
        var e = await SembrarEscenarioAsync(nameof(UnIdGastoInexistenteViolaLaFkGenerica23503));

        await using var cruda = await fixture.AbrirConexionCrudaAsync("tenant", e.IdTenant);
        await using var comando = cruda.CreateCommand();
        comando.CommandText =
            "INSERT INTO movimientos_cuenta_corriente_proveedor " +
            "(id_tenant, id_proveedor, fecha, id_punto_venta, id_empleado, tipo, id_comprobante_compra, id_gasto, importe, saldo_resultante, detalle) " +
            "VALUES ($1, $2, now(), NULL, NULL, 'apertura', NULL, $3, 1, 1, NULL)";
        comando.Parameters.Add(new NpgsqlParameter { Value = e.IdTenant });
        comando.Parameters.Add(new NpgsqlParameter { Value = e.IdProveedor });
        comando.Parameters.Add(new NpgsqlParameter { Value = 999_999_999 }); // id_gasto inexistente

        var excepcion = await Assert.ThrowsAsync<PostgresException>(() => comando.ExecuteNonQueryAsync());
        Assert.Equal("23503", excepcion.SqlState);
        Assert.Equal("fk_movimientos_cuenta_corriente_proveedor_gasto", excepcion.ConstraintName);
    }

    /// <summary><c>ManejadorDeErrores.cs</c> stays unmodified (tasks.md decisión 9): el mapping
    /// genérico <c>fk_</c>/<c>23503</c> → <c>400 referencia_invalida</c> ya cubre esta etapa —
    /// smoke test de que la clasificación por prefijo sigue reconociendo el nombre de esta FK
    /// nueva sin ningún caso hardcodeado agregado.</summary>
    [Fact]
    public void LosNombresDeFkNuevosEmpiezanConElPrefijoGenericoFk()
    {
        string[] nombres =
        [
            "fk_movimientos_cuenta_corriente_proveedor_tenant",
            "fk_movimientos_cuenta_corriente_proveedor_proveedor",
            "fk_movimientos_cuenta_corriente_proveedor_punto_venta",
            "fk_movimientos_cuenta_corriente_proveedor_empleado",
            "fk_movimientos_cuenta_corriente_proveedor_comprobante_compra",
            "fk_movimientos_cuenta_corriente_proveedor_gasto"
        ];

        Assert.All(nombres, n => Assert.StartsWith("fk_", n));
    }

    /// <summary>Gate guard VINCULANTE (task 1.36, state.yaml db_gate_approval): el conteo total
    /// de índices nuevos tiene que ser EXACTAMENTE 7 — 6 nombrados a mano sobre la tabla nueva
    /// (gate §B) + 1 implícito de la clave alterna nueva de <c>gastos</c> (gate §D). Cualquier
    /// índice extra que <c>ForeignKeyIndexConvention</c> agregue sin que este contrato lo
    /// nombre reabre el gate (precedente: enmienda 1 de la etapa 14).</summary>
    [Fact]
    public async Task ElConteoTotalDeIndicesNuevosEsExactamenteSiete()
    {
        using var _ = fixture.CreateClient();

        await using var cruda = new Npgsql.NpgsqlConnection(fixture.OwnerConnectionString);
        await cruda.OpenAsync();

        await using var comandoTabla = cruda.CreateCommand();
        comandoTabla.CommandText =
            "SELECT indexname FROM pg_indexes WHERE tablename = 'movimientos_cuenta_corriente_proveedor' ORDER BY indexname";
        var indicesTabla = new List<string>();
        await using (var lector = await comandoTabla.ExecuteReaderAsync())
        {
            while (await lector.ReadAsync())
            {
                indicesTabla.Add(lector.GetString(0));
            }
        }

        // 6 índices nombrados a mano + 1 índice implícito de la PK (pg_indexes cuenta también
        // el índice que respalda la PRIMARY KEY, que NO es parte del conteo "7" del gate — ese
        // conteo es de índices NUEVOS de soporte/negocio, la PK es la identidad de la fila).
        var indicesDeSoporte = indicesTabla.Where(n => n != "pk_movimientos_cuenta_corriente_proveedor").ToList();
        Assert.Equal(6, indicesDeSoporte.Count);
        Assert.Equal(
            new[]
            {
                "ix_movimientos_cuenta_corriente_proveedor_comprobante_compra",
                "ix_movimientos_cuenta_corriente_proveedor_empleado",
                "ix_movimientos_cuenta_corriente_proveedor_gasto",
                "ix_movimientos_cuenta_corriente_proveedor_proveedor_fecha",
                "ix_movimientos_cuenta_corriente_proveedor_punto_venta",
                "ix_movimientos_cuenta_corriente_proveedor_tenant"
            },
            indicesDeSoporte.OrderBy(n => n));

        await using var comandoGastos = cruda.CreateCommand();
        comandoGastos.CommandText =
            "SELECT indexname FROM pg_indexes WHERE tablename = 'gastos' AND indexname LIKE '%id_gasto_id_tenant%'";
        var indiceImplicitoGastos = (string?)await comandoGastos.ExecuteScalarAsync();
        Assert.NotNull(indiceImplicitoGastos);

        // El total del gate: 6 (tabla nueva) + 1 (implícito de gastos) = 7.
        Assert.Equal(7, indicesDeSoporte.Count + 1);
    }
}
