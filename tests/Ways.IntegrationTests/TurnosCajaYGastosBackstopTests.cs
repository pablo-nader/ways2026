using Npgsql;
using Ways.Domain.Caja;
using Ways.Domain.Catalogos;
using Ways.Domain.Organizacion;
using Ways.Domain.Usuarios;
using Ways.Infrastructure.Multitenancy;

namespace Ways.IntegrationTests;

/// <summary>
/// stage-6-turnos-caja, Slice 1 (task 1.9, db-error-backstops, design: Backstop Map):
/// raw-SQL INSERTs que bypasean por completo <c>ReglaDeTurnos</c>/<c>ReglaDeMovimientosDeCaja</c>
/// (Slice 2)/<c>ServicioDeGastos</c> (Slice 3)/<c>ServicioDeTurnos.CerrarAsync</c> (Slice 4) — no
/// existen todavía — para probar las seis CHECKs de esquema nuevas y los dos índices únicos,
/// mismo patrón que <c>VentasStockBackstopTests</c>/<c>OfertasCheckBackstopTests</c>.
///
/// Honesto sobre alcanzabilidad (design: Backstop Map): bajo operación normal (Slice 2 en
/// adelante) ninguna de estas ramas es alcanzable por un cliente HTTP — prueban la traducción de
/// esquema, no un camino de cliente real. <c>ux_arqueos_turno_medio</c> queda exenta de prueba de
/// carrera de forma permanente (el cierre deriva el set de filas dentro de su propio lock
/// exclusivo, Slice 4); <c>ux_turnos_caja_abierto</c> solo queda exenta EN ESTA SLICE — la prueba
/// de carrera real (rendezvous, dos aperturas concurrentes) es tarea de Slice 2 (task 2.8), una
/// vez que exista <c>ServicioDeTurnos.AbrirAsync</c>.
/// </summary>
[Collection("Ways.IntegrationTests secuencial")]
public class TurnosCajaYGastosBackstopTests(WaysApiFixture fixture) : IClassFixture<WaysApiFixture>
{
    private sealed record Prerequisitos(int IdTenant, int IdEmpresa, int IdPuntoVenta, int IdEmpleado, int IdMedioPago);

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

        var puntoVenta = new PuntoVenta
        {
            IdTenant = tenant.Id, IdEmpresa = empresa.Id, Nombre = nombre, CreatedAt = ahora, UpdatedAt = ahora
        };
        db.PuntosVenta.Add(puntoVenta);
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

        return new Prerequisitos(tenant.Id, empresa.Id, puntoVenta.Id, usuario.Id, medioPago.Id);
    }

    /// <summary>Abre un turno vía EF (sesión de plataforma, sin pasar por el backstop bajo
    /// prueba) — sirve de FK target para los tests de <c>movimientos_caja</c>/
    /// <c>arqueos_turno</c>/<c>movimientos_tesoreria</c>/<c>gastos</c>.</summary>
    private async Task<int> AbrirTurnoAsync(Prerequisitos p)
    {
        await using var db = fixture.CrearContextoDeAplicacion(TenantActualFijo.Plataforma);
        var ahora = DateTimeOffset.UtcNow;

        var turno = new TurnoCaja
        {
            IdTenant = p.IdTenant,
            IdPuntoVenta = p.IdPuntoVenta,
            IdEmpleadoApertura = p.IdEmpleado,
            FechaApertura = ahora,
            FondoInicial = 500m,
            Estado = EstadoTurno.Abierto,
            CreatedAt = ahora,
            UpdatedAt = ahora
        };
        db.TurnosCaja.Add(turno);
        await db.SaveChangesAsync();

        return turno.Id;
    }

    // ---- ck_turnos_caja_fondo_inicial_no_negativo -------------------------------------------

    [Fact]
    public async Task UnTurnoConFondoInicialNegativoViolaLaCheckDeFondoNoNegativo()
    {
        var p = await SembrarPrerequisitosAsync(nameof(UnTurnoConFondoInicialNegativoViolaLaCheckDeFondoNoNegativo));

        await using var cruda = await fixture.AbrirConexionCrudaAsync("tenant", p.IdTenant);
        await using var comando = cruda.CreateCommand();
        comando.CommandText =
            "INSERT INTO turnos_caja (id_tenant, id_punto_venta, id_empleado_apertura, fecha_apertura, " +
            "fondo_inicial, estado, created_at, updated_at) VALUES ($1, $2, $3, now(), -100, 'abierto', now(), now())";
        comando.Parameters.Add(new NpgsqlParameter { Value = p.IdTenant });
        comando.Parameters.Add(new NpgsqlParameter { Value = p.IdPuntoVenta });
        comando.Parameters.Add(new NpgsqlParameter { Value = p.IdEmpleado });

        var excepcion = await Assert.ThrowsAsync<PostgresException>(() => comando.ExecuteNonQueryAsync());
        Assert.Equal("23514", excepcion.SqlState);
        Assert.Equal("ck_turnos_caja_fondo_inicial_no_negativo", excepcion.ConstraintName);
    }

    // ---- ck_turnos_caja_cierre_consistente ---------------------------------------------------

    [Fact]
    public async Task UnTurnoAbiertoConFechaDeCierreViolaLaCheckDeCierreConsistente()
    {
        var p = await SembrarPrerequisitosAsync(nameof(UnTurnoAbiertoConFechaDeCierreViolaLaCheckDeCierreConsistente));

        await using var cruda = await fixture.AbrirConexionCrudaAsync("tenant", p.IdTenant);
        await using var comando = cruda.CreateCommand();
        comando.CommandText =
            "INSERT INTO turnos_caja (id_tenant, id_punto_venta, id_empleado_apertura, fecha_apertura, " +
            "fecha_cierre, fondo_inicial, estado, created_at, updated_at) " +
            "VALUES ($1, $2, $3, now(), now(), 0, 'abierto', now(), now())";
        comando.Parameters.Add(new NpgsqlParameter { Value = p.IdTenant });
        comando.Parameters.Add(new NpgsqlParameter { Value = p.IdPuntoVenta });
        comando.Parameters.Add(new NpgsqlParameter { Value = p.IdEmpleado });

        var excepcion = await Assert.ThrowsAsync<PostgresException>(() => comando.ExecuteNonQueryAsync());
        Assert.Equal("23514", excepcion.SqlState);
        Assert.Equal("ck_turnos_caja_cierre_consistente", excepcion.ConstraintName);
    }

    // ---- ck_turnos_caja_medio_efectivo_solo_cerrado (judgment-day JD-E5a-2) -------------------

    /// <summary>Defensa en profundidad de <c>ServicioDeTurnos.InsertarArqueosYTesoreriaAsync</c>,
    /// que SIEMPRE fija <c>id_medio_pago_efectivo</c> DESPUÉS del UPDATE guardado que transiciona
    /// a <c>cerrado</c> — inalcanzable por operación normal, solo por un INSERT/UPDATE crudo que
    /// intente pinear el ancla en un turno todavía abierto.</summary>
    [Fact]
    public async Task UnTurnoAbiertoConMedioPagoEfectivoFijadoViolaLaCheckDeMedioEfectivoSoloCerrado()
    {
        var p = await SembrarPrerequisitosAsync(nameof(UnTurnoAbiertoConMedioPagoEfectivoFijadoViolaLaCheckDeMedioEfectivoSoloCerrado));

        await using var cruda = await fixture.AbrirConexionCrudaAsync("tenant", p.IdTenant);
        await using var comando = cruda.CreateCommand();
        comando.CommandText =
            "INSERT INTO turnos_caja (id_tenant, id_punto_venta, id_empleado_apertura, fecha_apertura, " +
            "fondo_inicial, estado, id_medio_pago_efectivo, created_at, updated_at) " +
            "VALUES ($1, $2, $3, now(), 0, 'abierto', $4, now(), now())";
        comando.Parameters.Add(new NpgsqlParameter { Value = p.IdTenant });
        comando.Parameters.Add(new NpgsqlParameter { Value = p.IdPuntoVenta });
        comando.Parameters.Add(new NpgsqlParameter { Value = p.IdEmpleado });
        comando.Parameters.Add(new NpgsqlParameter { Value = p.IdMedioPago });

        var excepcion = await Assert.ThrowsAsync<PostgresException>(() => comando.ExecuteNonQueryAsync());
        Assert.Equal("23514", excepcion.SqlState);
        Assert.Equal("ck_turnos_caja_medio_efectivo_solo_cerrado", excepcion.ConstraintName);
    }

    // ---- ck_movimientos_caja_importe ---------------------------------------------------------

    [Fact]
    public async Task UnRetiroConImporteCeroViolaLaCheckDeImporte()
    {
        var p = await SembrarPrerequisitosAsync(nameof(UnRetiroConImporteCeroViolaLaCheckDeImporte));
        var idTurno = await AbrirTurnoAsync(p);

        await using var cruda = await fixture.AbrirConexionCrudaAsync("tenant", p.IdTenant);
        await using var comando = cruda.CreateCommand();
        comando.CommandText =
            "INSERT INTO movimientos_caja (id_tenant, id_turno_caja, tipo, importe, motivo, id_empleado, creado_el) " +
            "VALUES ($1, $2, 'retiro', 0, 'motivo válido de prueba', $3, now())";
        comando.Parameters.Add(new NpgsqlParameter { Value = p.IdTenant });
        comando.Parameters.Add(new NpgsqlParameter { Value = idTurno });
        comando.Parameters.Add(new NpgsqlParameter { Value = p.IdEmpleado });

        var excepcion = await Assert.ThrowsAsync<PostgresException>(() => comando.ExecuteNonQueryAsync());
        Assert.Equal("23514", excepcion.SqlState);
        Assert.Equal("ck_movimientos_caja_importe", excepcion.ConstraintName);
    }

    // ---- ck_movimientos_caja_motivo_minimo -----------------------------------------------

    [Fact]
    public async Task UnRetiroConMotivoCortoViolaLaCheckDeMotivoMinimo()
    {
        var p = await SembrarPrerequisitosAsync(nameof(UnRetiroConMotivoCortoViolaLaCheckDeMotivoMinimo));
        var idTurno = await AbrirTurnoAsync(p);

        await using var cruda = await fixture.AbrirConexionCrudaAsync("tenant", p.IdTenant);
        await using var comando = cruda.CreateCommand();
        comando.CommandText =
            "INSERT INTO movimientos_caja (id_tenant, id_turno_caja, tipo, importe, motivo, id_empleado, creado_el) " +
            "VALUES ($1, $2, 'retiro', 50, 'ab', $3, now())";
        comando.Parameters.Add(new NpgsqlParameter { Value = p.IdTenant });
        comando.Parameters.Add(new NpgsqlParameter { Value = idTurno });
        comando.Parameters.Add(new NpgsqlParameter { Value = p.IdEmpleado });

        var excepcion = await Assert.ThrowsAsync<PostgresException>(() => comando.ExecuteNonQueryAsync());
        Assert.Equal("23514", excepcion.SqlState);
        Assert.Equal("ck_movimientos_caja_motivo_minimo", excepcion.ConstraintName);
    }

    // ---- ck_gastos_importe_positivo ----------------------------------------------------------

    [Fact]
    public async Task UnGastoConImporteCeroViolaLaCheckDeImportePositivo()
    {
        var p = await SembrarPrerequisitosAsync(nameof(UnGastoConImporteCeroViolaLaCheckDeImportePositivo));
        var idTurno = await AbrirTurnoAsync(p);

        await using var cruda = await fixture.AbrirConexionCrudaAsync("tenant", p.IdTenant);
        await using var comando = cruda.CreateCommand();
        comando.CommandText =
            "INSERT INTO gastos (id_tenant, fecha, id_punto_venta, id_empresa, id_turno_caja, id_empleado, " +
            "categoria, origen_fondos, concepto, id_medio_pago, importe, created_at, updated_at) " +
            "VALUES ($1, now(), $2, $3, $4, $5, 'otros', 'caja_turno', 'gasto de prueba', $6, 0, now(), now())";
        comando.Parameters.Add(new NpgsqlParameter { Value = p.IdTenant });
        comando.Parameters.Add(new NpgsqlParameter { Value = p.IdPuntoVenta });
        comando.Parameters.Add(new NpgsqlParameter { Value = p.IdEmpresa });
        comando.Parameters.Add(new NpgsqlParameter { Value = idTurno });
        comando.Parameters.Add(new NpgsqlParameter { Value = p.IdEmpleado });
        comando.Parameters.Add(new NpgsqlParameter { Value = p.IdMedioPago });

        var excepcion = await Assert.ThrowsAsync<PostgresException>(() => comando.ExecuteNonQueryAsync());
        Assert.Equal("23514", excepcion.SqlState);
        Assert.Equal("ck_gastos_importe_positivo", excepcion.ConstraintName);
    }

    // ---- ck_movimientos_tesoreria_cadena -----------------------------------------------------

    [Fact]
    public async Task UnMovimientoDeTesoreriaConCadenaInconsistenteViolaLaCheckDeCadena()
    {
        var p = await SembrarPrerequisitosAsync(nameof(UnMovimientoDeTesoreriaConCadenaInconsistenteViolaLaCheckDeCadena));
        var idTurno = await AbrirTurnoAsync(p);

        await using var cruda = await fixture.AbrirConexionCrudaAsync("tenant", p.IdTenant);
        await using var comando = cruda.CreateCommand();
        comando.CommandText =
            "INSERT INTO movimientos_tesoreria (id_tenant, id_empresa, id_punto_venta, fecha, tipo, " +
            "id_turno_caja, concepto, inicio, ingreso, egreso, final, id_empleado) " +
            "VALUES ($1, $2, $3, now(), 'retiro_caja', $4, 'cadena inconsistente', 0, 10, 0, 999, $5)";
        comando.Parameters.Add(new NpgsqlParameter { Value = p.IdTenant });
        comando.Parameters.Add(new NpgsqlParameter { Value = p.IdEmpresa });
        comando.Parameters.Add(new NpgsqlParameter { Value = p.IdPuntoVenta });
        comando.Parameters.Add(new NpgsqlParameter { Value = idTurno });
        comando.Parameters.Add(new NpgsqlParameter { Value = p.IdEmpleado });

        var excepcion = await Assert.ThrowsAsync<PostgresException>(() => comando.ExecuteNonQueryAsync());
        Assert.Equal("23514", excepcion.SqlState);
        Assert.Equal("ck_movimientos_tesoreria_cadena", excepcion.ConstraintName);
    }

    // ---- ux_turnos_caja_abierto (proof 23505 -- la carrera real es Slice 2, task 2.8) --------

    [Fact]
    public async Task DosTurnosAbiertosEnElMismoPuntoDeVentaViolanLaUnicidad()
    {
        var p = await SembrarPrerequisitosAsync(nameof(DosTurnosAbiertosEnElMismoPuntoDeVentaViolanLaUnicidad));

        await using var cruda = await fixture.AbrirConexionCrudaAsync("tenant", p.IdTenant);

        async Task InsertarAsync()
        {
            await using var comando = cruda.CreateCommand();
            comando.CommandText =
                "INSERT INTO turnos_caja (id_tenant, id_punto_venta, id_empleado_apertura, fecha_apertura, " +
                "fondo_inicial, estado, created_at, updated_at) VALUES ($1, $2, $3, now(), 0, 'abierto', now(), now())";
            comando.Parameters.Add(new NpgsqlParameter { Value = p.IdTenant });
            comando.Parameters.Add(new NpgsqlParameter { Value = p.IdPuntoVenta });
            comando.Parameters.Add(new NpgsqlParameter { Value = p.IdEmpleado });
            await comando.ExecuteNonQueryAsync();
        }

        await InsertarAsync();

        var excepcion = await Assert.ThrowsAsync<PostgresException>(InsertarAsync);
        Assert.Equal("23505", excepcion.SqlState);
        Assert.Equal("ux_turnos_caja_abierto", excepcion.ConstraintName);
    }

    // ---- ux_arqueos_turno_medio (exención documentada de prueba de carrera) -----------------

    [Fact]
    public async Task DosArqueosDelMismoTurnoYMedioViolanLaUnicidad()
    {
        var p = await SembrarPrerequisitosAsync(nameof(DosArqueosDelMismoTurnoYMedioViolanLaUnicidad));
        var idTurno = await AbrirTurnoAsync(p);

        await using var cruda = await fixture.AbrirConexionCrudaAsync("tenant", p.IdTenant);

        async Task InsertarAsync()
        {
            await using var comando = cruda.CreateCommand();
            comando.CommandText =
                "INSERT INTO arqueos_turno (id_tenant, id_turno_caja, id_medio_pago, importe_esperado, " +
                "importe_declarado) VALUES ($1, $2, $3, 100, 100)";
            comando.Parameters.Add(new NpgsqlParameter { Value = p.IdTenant });
            comando.Parameters.Add(new NpgsqlParameter { Value = idTurno });
            comando.Parameters.Add(new NpgsqlParameter { Value = p.IdMedioPago });
            await comando.ExecuteNonQueryAsync();
        }

        await InsertarAsync();

        var excepcion = await Assert.ThrowsAsync<PostgresException>(InsertarAsync);
        Assert.Equal("23505", excepcion.SqlState);
        Assert.Equal("ux_arqueos_turno_medio", excepcion.ConstraintName);
    }

    // ---- GastosOrigenFondosYTesoreriaPorEmpresa: ck_gastos_caja_turno_requiere_turno --------

    /// <summary>Defensa en profundidad de esquema: <c>ServicioDeGastos.RegistrarAsync</c> siempre
    /// resuelve un turno abierto ANTES de insertar (nunca deja <c>id_turno_caja</c> nulo con
    /// <c>origen_fondos = caja_turno</c>) — esta CHECK es el backstop de una escritura
    /// cruda/fuera de banda que lo bypasee.</summary>
    [Fact]
    public async Task UnGastoDeCajaTurnoSinTurnoViolaLaCheckDeCajaTurnoRequiereTurno()
    {
        var p = await SembrarPrerequisitosAsync(nameof(UnGastoDeCajaTurnoSinTurnoViolaLaCheckDeCajaTurnoRequiereTurno));

        await using var cruda = await fixture.AbrirConexionCrudaAsync("tenant", p.IdTenant);
        await using var comando = cruda.CreateCommand();
        comando.CommandText =
            "INSERT INTO gastos (id_tenant, fecha, id_punto_venta, id_empresa, id_turno_caja, id_empleado, " +
            "categoria, origen_fondos, concepto, id_medio_pago, importe, created_at, updated_at) " +
            "VALUES ($1, now(), $2, $3, NULL, $4, 'otros', 'caja_turno', 'gasto de prueba', $5, 100, now(), now())";
        comando.Parameters.Add(new NpgsqlParameter { Value = p.IdTenant });
        comando.Parameters.Add(new NpgsqlParameter { Value = p.IdPuntoVenta });
        comando.Parameters.Add(new NpgsqlParameter { Value = p.IdEmpresa });
        comando.Parameters.Add(new NpgsqlParameter { Value = p.IdEmpleado });
        comando.Parameters.Add(new NpgsqlParameter { Value = p.IdMedioPago });

        var excepcion = await Assert.ThrowsAsync<PostgresException>(() => comando.ExecuteNonQueryAsync());
        Assert.Equal("23514", excepcion.SqlState);
        Assert.Equal("ck_gastos_caja_turno_requiere_turno", excepcion.ConstraintName);
    }

    // ---- GastosOrigenFondosYTesoreriaPorEmpresa: ux_movimientos_tesoreria_id_gasto ----------

    /// <summary>Inalcanzable hoy (ningún escritor puebla <c>IdGasto</c> todavía) — backstop de
    /// esquema puro para la etapa futura que sí lo escriba: un gasto no puede originar dos
    /// movimientos de tesorería.</summary>
    [Fact]
    public async Task DosMovimientosDeTesoreriaConElMismoGastoViolanLaUnicidad()
    {
        var p = await SembrarPrerequisitosAsync(nameof(DosMovimientosDeTesoreriaConElMismoGastoViolanLaUnicidad));
        var idTurno = await AbrirTurnoAsync(p);

        await using var cruda = await fixture.AbrirConexionCrudaAsync("tenant", p.IdTenant);

        int idGasto;
        await using (var comandoGasto = cruda.CreateCommand())
        {
            comandoGasto.CommandText =
                "INSERT INTO gastos (id_tenant, fecha, id_punto_venta, id_empresa, id_turno_caja, id_empleado, " +
                "categoria, origen_fondos, concepto, id_medio_pago, importe, created_at, updated_at) " +
                "VALUES ($1, now(), $2, $3, $4, $5, 'otros', 'caja_turno', 'gasto de prueba', $6, 100, now(), now()) " +
                "RETURNING id_gasto";
            comandoGasto.Parameters.Add(new NpgsqlParameter { Value = p.IdTenant });
            comandoGasto.Parameters.Add(new NpgsqlParameter { Value = p.IdPuntoVenta });
            comandoGasto.Parameters.Add(new NpgsqlParameter { Value = p.IdEmpresa });
            comandoGasto.Parameters.Add(new NpgsqlParameter { Value = idTurno });
            comandoGasto.Parameters.Add(new NpgsqlParameter { Value = p.IdEmpleado });
            comandoGasto.Parameters.Add(new NpgsqlParameter { Value = p.IdMedioPago });
            idGasto = (int)(await comandoGasto.ExecuteScalarAsync())!;
        }

        async Task InsertarMovimientoAsync()
        {
            await using var comando = cruda.CreateCommand();
            comando.CommandText =
                "INSERT INTO movimientos_tesoreria (id_tenant, id_empresa, id_punto_venta, fecha, tipo, " +
                "id_turno_caja, id_gasto, concepto, inicio, ingreso, egreso, final, id_empleado) " +
                "VALUES ($1, $2, $3, now(), 'retiro_caja', $4, $5, 'movimiento de gasto', 0, 100, 0, 100, $6)";
            comando.Parameters.Add(new NpgsqlParameter { Value = p.IdTenant });
            comando.Parameters.Add(new NpgsqlParameter { Value = p.IdEmpresa });
            comando.Parameters.Add(new NpgsqlParameter { Value = p.IdPuntoVenta });
            comando.Parameters.Add(new NpgsqlParameter { Value = idTurno });
            comando.Parameters.Add(new NpgsqlParameter { Value = idGasto });
            comando.Parameters.Add(new NpgsqlParameter { Value = p.IdEmpleado });
            await comando.ExecuteNonQueryAsync();
        }

        await InsertarMovimientoAsync();

        var excepcion = await Assert.ThrowsAsync<PostgresException>(InsertarMovimientoAsync);
        Assert.Equal("23505", excepcion.SqlState);
        Assert.Equal("ux_movimientos_tesoreria_id_gasto", excepcion.ConstraintName);
    }

    // ---- GastosOrigenFondosYTesoreriaPorEmpresa: fk_movimientos_tesoreria_gasto ------------

    /// <summary>Un <c>id_gasto</c> que no existe (o que es de otro tenant, invisible bajo RLS) es
    /// una referencia inválida — 23503, capturado por el backstop genérico de <c>fk_*</c> en
    /// <c>ManejadorDeErrores</c>.</summary>
    [Fact]
    public async Task UnMovimientoDeTesoreriaConIdGastoInexistenteViolaLaFk()
    {
        var p = await SembrarPrerequisitosAsync(nameof(UnMovimientoDeTesoreriaConIdGastoInexistenteViolaLaFk));
        var idTurno = await AbrirTurnoAsync(p);

        await using var cruda = await fixture.AbrirConexionCrudaAsync("tenant", p.IdTenant);
        await using var comando = cruda.CreateCommand();
        comando.CommandText =
            "INSERT INTO movimientos_tesoreria (id_tenant, id_empresa, id_punto_venta, fecha, tipo, " +
            "id_turno_caja, id_gasto, concepto, inicio, ingreso, egreso, final, id_empleado) " +
            "VALUES ($1, $2, $3, now(), 'retiro_caja', $4, 999999, 'movimiento de gasto inexistente', " +
            "0, 100, 0, 100, $5)";
        comando.Parameters.Add(new NpgsqlParameter { Value = p.IdTenant });
        comando.Parameters.Add(new NpgsqlParameter { Value = p.IdEmpresa });
        comando.Parameters.Add(new NpgsqlParameter { Value = p.IdPuntoVenta });
        comando.Parameters.Add(new NpgsqlParameter { Value = idTurno });
        comando.Parameters.Add(new NpgsqlParameter { Value = p.IdEmpleado });

        var excepcion = await Assert.ThrowsAsync<PostgresException>(() => comando.ExecuteNonQueryAsync());
        Assert.Equal("23503", excepcion.SqlState);
        Assert.Equal("fk_movimientos_tesoreria_gasto", excepcion.ConstraintName);
    }

    // ---- GastosOrigenFondosYTesoreriaPorEmpresa: id_punto_venta NULLable (design aprobado) ---

    /// <summary>Prueba de ACEPTACIÓN, no de backstop: un gasto de origen <c>tesoreria</c> (etapa
    /// futura, hoy inalcanzable desde <c>ServicioDeGastos</c>) no nace de ningún punto de venta
    /// puntual ni de ningún turno — <c>id_punto_venta</c> y <c>id_turno_caja</c> nulos a la vez son
    /// un estado válido del esquema, no una violación de <c>ck_gastos_caja_turno_requiere_turno</c>
    /// (que solo exige turno para <c>origen_fondos = caja_turno</c>).</summary>
    [Fact]
    public async Task UnGastoDeOrigenTesoreriaConPuntoDeVentaYTurnoNulosEsAceptadoPorElEsquema()
    {
        var p = await SembrarPrerequisitosAsync(
            nameof(UnGastoDeOrigenTesoreriaConPuntoDeVentaYTurnoNulosEsAceptadoPorElEsquema));

        await using var cruda = await fixture.AbrirConexionCrudaAsync("tenant", p.IdTenant);
        await using var comando = cruda.CreateCommand();
        comando.CommandText =
            "INSERT INTO gastos (id_tenant, fecha, id_empresa, id_punto_venta, id_turno_caja, id_empleado, " +
            "categoria, origen_fondos, concepto, id_medio_pago, importe, created_at, updated_at) " +
            "VALUES ($1, now(), $2, NULL, NULL, $3, 'otros', 'tesoreria', 'gasto de tesorería directo', " +
            "$4, 100, now(), now()) RETURNING id_gasto";
        comando.Parameters.Add(new NpgsqlParameter { Value = p.IdTenant });
        comando.Parameters.Add(new NpgsqlParameter { Value = p.IdEmpresa });
        comando.Parameters.Add(new NpgsqlParameter { Value = p.IdEmpleado });
        comando.Parameters.Add(new NpgsqlParameter { Value = p.IdMedioPago });

        var idGasto = (int)(await comando.ExecuteScalarAsync())!;
        Assert.True(idGasto > 0);
    }

    /// <summary>Prueba de ACEPTACIÓN, no de backstop: un movimiento de tesorería que no se origina
    /// en el cierre de un punto de venta puntual (etapa futura — un gasto de origen
    /// <c>tesoreria</c> que escribe directo al libro) acepta <c>id_punto_venta</c> nulo — la
    /// cadena inicio→final ya no depende del punto de venta (design aprobado), solo de
    /// <c>id_empresa</c>.</summary>
    [Fact]
    public async Task UnMovimientoDeTesoreriaConPuntoDeVentaNuloEsAceptadoPorElEsquema()
    {
        var p = await SembrarPrerequisitosAsync(nameof(UnMovimientoDeTesoreriaConPuntoDeVentaNuloEsAceptadoPorElEsquema));

        await using var cruda = await fixture.AbrirConexionCrudaAsync("tenant", p.IdTenant);
        await using var comando = cruda.CreateCommand();
        comando.CommandText =
            "INSERT INTO movimientos_tesoreria (id_tenant, id_empresa, id_punto_venta, fecha, tipo, " +
            "id_turno_caja, concepto, inicio, ingreso, egreso, final, id_empleado) " +
            "VALUES ($1, $2, NULL, now(), 'deposito', NULL, 'depósito directo sin punto de venta', " +
            "0, 100, 0, 100, $3) RETURNING id_movimiento";
        comando.Parameters.Add(new NpgsqlParameter { Value = p.IdTenant });
        comando.Parameters.Add(new NpgsqlParameter { Value = p.IdEmpresa });
        comando.Parameters.Add(new NpgsqlParameter { Value = p.IdEmpleado });

        var idMovimiento = (int)(await comando.ExecuteScalarAsync())!;
        Assert.True(idMovimiento > 0);
    }
}
