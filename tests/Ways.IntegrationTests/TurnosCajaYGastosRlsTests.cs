using Microsoft.EntityFrameworkCore;
using Npgsql;
using Ways.Application.Abstracciones;
using Ways.Domain.Caja;
using Ways.Domain.Catalogos;
using Ways.Domain.Gastos;
using Ways.Domain.Organizacion;
using Ways.Domain.Usuarios;
using Ways.Infrastructure.Multitenancy;

namespace Ways.IntegrationTests;

/// <summary>
/// stage-6-turnos-caja, Slice 1 (task 1.8, spec: turnos-de-caja/movimientos-de-caja/
/// arqueo-de-cierre/tesoreria/gastos — aislamiento de tenant implícito): mismo patrón que
/// <c>VentasStockYCuentaCorrienteRlsTests</c> — SQL crudo, independiente de EF, 0 filas para
/// SELECT/UPDATE cross-tenant, 42501 para el INSERT que viola <c>WITH CHECK</c>, más un proof a
/// nivel EF (LINQ) por tabla. Cubre las cinco tablas nuevas de esta etapa.
/// </summary>
[Collection("Ways.IntegrationTests secuencial")]
public class TurnosCajaYGastosRlsTests(WaysApiFixture fixture) : IClassFixture<WaysApiFixture>
{
    public static TheoryData<string, string> TablasDeTenant => new()
    {
        { "turnos_caja", "id_turno_caja" },
        { "movimientos_caja", "id_movimiento" },
        { "arqueos_turno", "id_arqueo" },
        { "movimientos_tesoreria", "id_movimiento" },
        { "gastos", "id_gasto" }
    };

    private sealed record Escenario(
        int IdTenant, int IdPuntoVenta, int IdEmpleado, int IdMedioPago, int IdMedioPagoAlterno,
        int IdTurnoCaja, int IdMovimientoCaja, int IdArqueoTurno, int IdMovimientoTesoreria, int IdGasto);

    /// <summary>Arma la cadena completa de prerequisitos y una fila en cada una de las cinco
    /// tablas nuevas, todas del mismo tenant A — comparte el escenario entero para no repetir
    /// cinco tablas de seed por test. <see cref="Escenario.IdMedioPagoAlterno"/> existe solo
    /// para que el INSERT ajeno de <c>arqueos_turno</c> no choque con
    /// <c>ux_arqueos_turno_medio</c> contra la fila ya sembrada.</summary>
    private async Task<Escenario> SembrarEscenarioAsync(string nombre)
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

        var medioPagoAlterno = new MedioPago
        {
            IdTenant = tenant.Id,
            Nombre = nombre + "-tarjeta",
            Orden = 2,
            Comportamiento = ComportamientoMedioPago.Electronico,
            AdmiteVuelto = false,
            RequiereReferencia = true,
            CreatedAt = ahora,
            UpdatedAt = ahora
        };
        db.MediosPago.Add(medioPagoAlterno);
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

        var turno = new TurnoCaja
        {
            IdTenant = tenant.Id,
            IdPuntoVenta = puntoVenta.Id,
            IdEmpleadoApertura = usuario.Id,
            FechaApertura = ahora,
            FondoInicial = 500m,
            Estado = EstadoTurno.Abierto,
            CreatedAt = ahora,
            UpdatedAt = ahora
        };
        db.TurnosCaja.Add(turno);
        await db.SaveChangesAsync();

        var movimientoCaja = new MovimientoCaja
        {
            IdTenant = tenant.Id,
            IdTurnoCaja = turno.Id,
            Tipo = TipoMovimientoCaja.Retiro,
            Importe = 100m,
            Motivo = "retiro de prueba de RLS",
            IdEmpleado = usuario.Id,
            CreadoEl = ahora
        };
        db.MovimientosCaja.Add(movimientoCaja);
        await db.SaveChangesAsync();

        var arqueoTurno = new ArqueoTurno
        {
            IdTenant = tenant.Id,
            IdTurnoCaja = turno.Id,
            IdMedioPago = medioPago.Id,
            ImporteEsperado = 400m,
            ImporteDeclarado = 400m
        };
        db.ArqueosTurno.Add(arqueoTurno);
        await db.SaveChangesAsync();

        var movimientoTesoreria = new MovimientoTesoreria
        {
            IdTenant = tenant.Id,
            IdPuntoVenta = puntoVenta.Id,
            Fecha = ahora,
            Tipo = TipoMovimientoTesoreria.RetiroCaja,
            IdTurnoCaja = turno.Id,
            Concepto = "cierre de prueba",
            Inicio = 0m,
            Ingreso = 100m,
            Egreso = 0m,
            Final = 100m,
            IdEmpleado = usuario.Id
        };
        db.MovimientosTesoreria.Add(movimientoTesoreria);
        await db.SaveChangesAsync();

        var gasto = new Gasto
        {
            IdTenant = tenant.Id,
            Fecha = ahora,
            IdPuntoVenta = puntoVenta.Id,
            IdTurnoCaja = turno.Id,
            IdEmpleado = usuario.Id,
            Categoria = CategoriaGasto.Otros,
            Concepto = "gasto de prueba de RLS",
            IdMedioPago = medioPago.Id,
            Importe = 50m,
            CreatedAt = ahora,
            UpdatedAt = ahora
        };
        db.Gastos.Add(gasto);
        await db.SaveChangesAsync();

        return new Escenario(
            tenant.Id, puntoVenta.Id, usuario.Id, medioPago.Id, medioPagoAlterno.Id,
            turno.Id, movimientoCaja.Id, arqueoTurno.Id, movimientoTesoreria.Id, gasto.Id);
    }

    private static int IdDeFila(Escenario escenario, string tabla) => tabla switch
    {
        "turnos_caja" => escenario.IdTurnoCaja,
        "movimientos_caja" => escenario.IdMovimientoCaja,
        "arqueos_turno" => escenario.IdArqueoTurno,
        "movimientos_tesoreria" => escenario.IdMovimientoTesoreria,
        "gastos" => escenario.IdGasto,
        _ => throw new ArgumentOutOfRangeException(nameof(tabla), tabla, "Tabla no cubierta por este helper.")
    };

    [Theory]
    [MemberData(nameof(TablasDeTenant))]
    public async Task UnaSesionDeOtroTenantNoVeLaFilaPorSelect(string tabla, string columnaId)
    {
        var escenario = await SembrarEscenarioAsync(nameof(UnaSesionDeOtroTenantNoVeLaFilaPorSelect) + tabla);
        var idFila = IdDeFila(escenario, tabla);

        await using var db = fixture.CrearContextoDeAplicacion(TenantActualFijo.Plataforma);
        var tenantB = new Tenant
        {
            Nombre = nameof(UnaSesionDeOtroTenantNoVeLaFilaPorSelect) + tabla + "-B",
            Estado = EstadoTenant.Activo,
            CreatedAt = DateTimeOffset.UtcNow,
            UpdatedAt = DateTimeOffset.UtcNow
        };
        db.Tenants.Add(tenantB);
        await db.SaveChangesAsync();

        await using var cruda = await fixture.AbrirConexionCrudaAsync("tenant", tenantB.Id);

        await using var comando = cruda.CreateCommand();
        comando.CommandText = $"SELECT count(*) FROM {tabla} WHERE {columnaId} = $1";
        comando.Parameters.Add(new NpgsqlParameter { Value = idFila });

        var total = (long)(await comando.ExecuteScalarAsync())!;

        Assert.Equal(0, total);
    }

    [Theory]
    [MemberData(nameof(TablasDeTenant))]
    public async Task UnaSesionDeOtroTenantNoPuedeActualizarLaFila(string tabla, string columnaId)
    {
        var escenario = await SembrarEscenarioAsync(nameof(UnaSesionDeOtroTenantNoPuedeActualizarLaFila) + tabla);
        var idFila = IdDeFila(escenario, tabla);

        await using var db = fixture.CrearContextoDeAplicacion(TenantActualFijo.Plataforma);
        var tenantB = new Tenant
        {
            Nombre = nameof(UnaSesionDeOtroTenantNoPuedeActualizarLaFila) + tabla + "-B",
            Estado = EstadoTenant.Activo,
            CreatedAt = DateTimeOffset.UtcNow,
            UpdatedAt = DateTimeOffset.UtcNow
        };
        db.Tenants.Add(tenantB);
        await db.SaveChangesAsync();

        await using var cruda = await fixture.AbrirConexionCrudaAsync("tenant", tenantB.Id);

        // turnos_caja/gastos tienen updated_at (EntidadTenant); los tres ledgers append-only no
        // — da lo mismo qué columna se toque, lo que se prueba es que USING oculta la fila antes
        // de que el UPDATE la alcance.
        var (columna, valor) = tabla switch
        {
            "turnos_caja" => ("updated_at", "now()"),
            "movimientos_caja" => ("motivo", "'motivo actualizado por prueba'"),
            "arqueos_turno" => ("importe_declarado", "999"),
            "movimientos_tesoreria" => ("concepto", "'concepto actualizado'"),
            "gastos" => ("updated_at", "now()"),
            _ => throw new ArgumentOutOfRangeException(nameof(tabla), tabla, "Tabla no cubierta por este helper.")
        };

        await using var comando = cruda.CreateCommand();
        comando.CommandText = $"UPDATE {tabla} SET {columna} = {valor} WHERE {columnaId} = $1";
        comando.Parameters.Add(new NpgsqlParameter { Value = idFila });

        var filas = await comando.ExecuteNonQueryAsync();
        Assert.Equal(0, filas);
    }

    /// <summary>Proof a nivel EF (LINQ) de que el filtro de tenant también bloquea a las
    /// entidades que pasan por el ORM — <c>movimientos_caja</c>/<c>arqueos_turno</c>/
    /// <c>movimientos_tesoreria</c> quedan cubiertas acá también aunque usen el filtro manual
    /// (no heredan <c>EntidadTenant</c>, ver los comentarios de
    /// <c>WaysDbContext.AplicarFiltroDeTenantEnMovimientoCaja</c>/<c>...EnArqueoTurno</c>/
    /// <c>...EnMovimientoTesoreria</c>).</summary>
    [Theory]
    [MemberData(nameof(TablasDeTenant))]
    public async Task ElFiltroDeEfNuncaDevuelveFilasDeOtroTenant(string tabla, string columnaId)
    {
        _ = columnaId;
        var escenario = await SembrarEscenarioAsync(nameof(ElFiltroDeEfNuncaDevuelveFilasDeOtroTenant) + tabla);

        await using var db = fixture.CrearContextoDeAplicacion(TenantActualFijo.Plataforma);
        var tenantB = new Tenant
        {
            Nombre = nameof(ElFiltroDeEfNuncaDevuelveFilasDeOtroTenant) + tabla + "-B",
            Estado = EstadoTenant.Activo,
            CreatedAt = DateTimeOffset.UtcNow,
            UpdatedAt = DateTimeOffset.UtcNow
        };
        db.Tenants.Add(tenantB);
        await db.SaveChangesAsync();

        await using var sesionB = fixture.CrearContextoDeAplicacion(new TenantActualFijo(ModoDeAcceso.Tenant, tenantB.Id));

        var visible = tabla switch
        {
            "turnos_caja" => await sesionB.TurnosCaja.AnyAsync(t => t.Id == escenario.IdTurnoCaja),
            "movimientos_caja" => await sesionB.MovimientosCaja.AnyAsync(m => m.Id == escenario.IdMovimientoCaja),
            "arqueos_turno" => await sesionB.ArqueosTurno.AnyAsync(a => a.Id == escenario.IdArqueoTurno),
            "movimientos_tesoreria" => await sesionB.MovimientosTesoreria.AnyAsync(m => m.Id == escenario.IdMovimientoTesoreria),
            "gastos" => await sesionB.Gastos.AnyAsync(g => g.Id == escenario.IdGasto),
            _ => throw new ArgumentOutOfRangeException(nameof(tabla), tabla, "Tabla no cubierta por este helper.")
        };

        Assert.False(visible);
    }

    [Theory]
    [MemberData(nameof(TablasDeTenant))]
    public async Task UnInsertConIdTenantAjenoSeRechaza(string tabla, string columnaId)
    {
        _ = columnaId;
        var escenario = await SembrarEscenarioAsync(nameof(UnInsertConIdTenantAjenoSeRechaza) + tabla);

        await using var db = fixture.CrearContextoDeAplicacion(TenantActualFijo.Plataforma);
        var tenantB = new Tenant
        {
            Nombre = nameof(UnInsertConIdTenantAjenoSeRechaza) + tabla + "-B",
            Estado = EstadoTenant.Activo,
            CreatedAt = DateTimeOffset.UtcNow,
            UpdatedAt = DateTimeOffset.UtcNow
        };
        db.Tenants.Add(tenantB);
        await db.SaveChangesAsync();

        // Sesión del tenant A intentando insertar una fila con id_tenant del tenant B (ajeno) —
        // WITH CHECK tiene que rechazarla antes de que cualquier FK/CHECK se evalúe.
        await using var cruda = await fixture.AbrirConexionCrudaAsync("tenant", escenario.IdTenant);

        var ahora = DateTimeOffset.UtcNow;

        (string Sql, Action<NpgsqlCommand> Bind) insert = tabla switch
        {
            // estado='cerrado' a propósito (no 'abierto'): esquiva ux_turnos_caja_abierto —
            // ya existe un turno abierto para este punto de venta en el escenario sembrado, y
            // lo que se prueba acá es el 42501 de WITH CHECK, no la unicidad.
            "turnos_caja" => (
                "INSERT INTO turnos_caja (id_tenant, id_punto_venta, id_empleado_apertura, " +
                "id_empleado_cierre, fecha_apertura, fecha_cierre, fondo_inicial, estado, " +
                "created_at, updated_at) VALUES ($1, $2, $3, $3, $4, $4, 0, 'cerrado', $4, $4)",
                c =>
                {
                    c.Parameters.Add(new NpgsqlParameter { Value = tenantB.Id });
                    c.Parameters.Add(new NpgsqlParameter { Value = escenario.IdPuntoVenta });
                    c.Parameters.Add(new NpgsqlParameter { Value = escenario.IdEmpleado });
                    c.Parameters.Add(new NpgsqlParameter { Value = ahora });
                }),
            "movimientos_caja" => (
                "INSERT INTO movimientos_caja (id_tenant, id_turno_caja, tipo, importe, motivo, " +
                "id_empleado, creado_el) VALUES ($1, $2, 'retiro', 10, 'motivo de intruso válido', $3, $4)",
                c =>
                {
                    c.Parameters.Add(new NpgsqlParameter { Value = tenantB.Id });
                    c.Parameters.Add(new NpgsqlParameter { Value = escenario.IdTurnoCaja });
                    c.Parameters.Add(new NpgsqlParameter { Value = escenario.IdEmpleado });
                    c.Parameters.Add(new NpgsqlParameter { Value = ahora });
                }),
            // id_medio_pago ALTERNO a propósito: esquiva ux_arqueos_turno_medio contra la fila
            // ya sembrada con el medio principal — lo que se prueba acá es el 42501.
            "arqueos_turno" => (
                "INSERT INTO arqueos_turno (id_tenant, id_turno_caja, id_medio_pago, " +
                "importe_esperado, importe_declarado) VALUES ($1, $2, $3, 10, 10)",
                c =>
                {
                    c.Parameters.Add(new NpgsqlParameter { Value = tenantB.Id });
                    c.Parameters.Add(new NpgsqlParameter { Value = escenario.IdTurnoCaja });
                    c.Parameters.Add(new NpgsqlParameter { Value = escenario.IdMedioPagoAlterno });
                }),
            "movimientos_tesoreria" => (
                "INSERT INTO movimientos_tesoreria (id_tenant, id_punto_venta, fecha, tipo, " +
                "id_turno_caja, concepto, inicio, ingreso, egreso, final, id_empleado) " +
                "VALUES ($1, $2, $3, 'retiro_caja', $4, 'intruso', 0, 10, 0, 10, $5)",
                c =>
                {
                    c.Parameters.Add(new NpgsqlParameter { Value = tenantB.Id });
                    c.Parameters.Add(new NpgsqlParameter { Value = escenario.IdPuntoVenta });
                    c.Parameters.Add(new NpgsqlParameter { Value = ahora });
                    c.Parameters.Add(new NpgsqlParameter { Value = escenario.IdTurnoCaja });
                    c.Parameters.Add(new NpgsqlParameter { Value = escenario.IdEmpleado });
                }),
            "gastos" => (
                "INSERT INTO gastos (id_tenant, fecha, id_punto_venta, id_turno_caja, id_empleado, " +
                "categoria, concepto, id_medio_pago, importe, created_at, updated_at) " +
                "VALUES ($1, $2, $3, $4, $5, 'otros', 'gasto intruso', $6, 10, $2, $2)",
                c =>
                {
                    c.Parameters.Add(new NpgsqlParameter { Value = tenantB.Id });
                    c.Parameters.Add(new NpgsqlParameter { Value = ahora });
                    c.Parameters.Add(new NpgsqlParameter { Value = escenario.IdPuntoVenta });
                    c.Parameters.Add(new NpgsqlParameter { Value = escenario.IdTurnoCaja });
                    c.Parameters.Add(new NpgsqlParameter { Value = escenario.IdEmpleado });
                    c.Parameters.Add(new NpgsqlParameter { Value = escenario.IdMedioPago });
                }),
            _ => throw new ArgumentOutOfRangeException(nameof(tabla), tabla, "Tabla no cubierta por este helper.")
        };

        await using var comando = cruda.CreateCommand();
        comando.CommandText = insert.Sql;
        insert.Bind(comando);

        // 42501 = insufficient_privilege (violación de WITH CHECK) -- se dispara antes de
        // cualquier FK/CHECK, sin importar que el resto de columnas referencien filas válidas
        // del tenant A (la sesión sigue siendo la del tenant A, así que solo id_tenant desentona).
        var excepcion = await Assert.ThrowsAsync<PostgresException>(() => comando.ExecuteNonQueryAsync());
        Assert.Equal("42501", excepcion.SqlState);
    }
}
