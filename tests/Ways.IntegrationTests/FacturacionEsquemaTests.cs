using Microsoft.EntityFrameworkCore;
using Ways.Application.Abstracciones;
using Npgsql;
using Ways.Domain.Catalogos;
using Ways.Domain.Fiscal;
using Ways.Domain.Organizacion;
using Ways.Domain.Plantel;
using Ways.Domain.Usuarios;
using Ways.Domain.Clientes;
using Ways.Domain.Ventas;
using Ways.Infrastructure.Multitenancy;

namespace Ways.IntegrationTests;

/// <summary>
/// Facturación por medio de pago, T3: esquema del espejo fiscal (<c>id_venta_origen</c>,
/// <c>factura_forzada</c>) y de las dos tablas nuevas (<c>facturaciones_de_venta</c>,
/// <c>acumulados_de_facturacion</c>). Cada restricción se prueba con un INSERT crudo bajo el rol de
/// aplicación <c>ways_app</c> (NOBYPASSRLS) y se aserta el SQLSTATE y el nombre de la restricción; el
/// aislamiento por tenant, bajo el mismo rol. Mismo criterio que <see cref="FiscalSchemaTests"/>.
/// </summary>
[Collection("Ways.IntegrationTests secuencial")]
public partial class FacturacionEsquemaTests(WaysApiFixture fixture) : IClassFixture<WaysApiFixture>
{
    private sealed record Escenario(
        int IdTenant, int IdEmpresa, int IdPuntoVenta, int IdCliente, int IdEmpleado, int IdMedioPago,
        int IdTipoTx, int IdTipoFa);

    private static long _numeroSecuencial = 1000;

    private async Task<Escenario> SembrarEscenarioAsync(string nombre)
    {
        using var _ = fixture.CreateClient();

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

        var condicionFiscal = new CondicionFiscal { Codigo = $"{nombre}-CF", Nombre = nombre, CreatedAt = ahora, UpdatedAt = ahora };
        db.CondicionesFiscales.Add(condicionFiscal);
        await db.SaveChangesAsync();

        var listaPrecio = new ListaPrecio
        {
            IdTenant = tenant.Id, Nombre = nombre, EsDefault = false, Modo = ModoLista.Fija,
            Activo = true, CreatedAt = ahora, UpdatedAt = ahora
        };
        db.ListasPrecio.Add(listaPrecio);
        await db.SaveChangesAsync();

        var cliente = new Cliente
        {
            IdTenant = tenant.Id, Numero = 701, Nombre = nombre, IdCondicionFiscal = condicionFiscal.Id,
            IdListaPrecio = listaPrecio.Id, CreatedAt = ahora, UpdatedAt = ahora
        };
        db.Clientes.Add(cliente);

        var empleado = new Usuario
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
        db.Usuarios.Add(empleado);

        var medioPago = new MedioPago
        {
            IdTenant = tenant.Id, Nombre = "Transferencia", Orden = 1, Comportamiento = ComportamientoMedioPago.Electronico,
            AdmiteVuelto = false, RequiereReferencia = false, Activo = true, CreatedAt = ahora, UpdatedAt = ahora
        };
        db.MediosPago.Add(medioPago);
        await db.SaveChangesAsync();

        var idTipoTx = await db.TiposComprobante.Where(t => t.Codigo == "TX").Select(t => t.Id).SingleAsync();
        var idTipoFa = await db.TiposComprobante.Where(t => t.Codigo == "FA").Select(t => t.Id).SingleAsync();

        return new Escenario(
            tenant.Id, empresa.Id, puntoVenta.Id, cliente.Id, empleado.Id, medioPago.Id, idTipoTx, idTipoFa);
    }

    // ---- helpers de INSERT crudo ---------------------------------------------------------------------

    /// <summary>Inserta un comprobante. <paramref name="fiscal"/> lo deja aprobado con CAE;
    /// <paramref name="idVentaOrigen"/> lo vuelve espejo de esa venta.</summary>
    private static async Task<int> InsertarComprobanteAsync(
        NpgsqlConnection cruda, Escenario e, int idTipo, bool fiscal, int? idVentaOrigen = null, int? idTenant = null,
        int? idTurnoCaja = null)
    {
        await using var comando = cruda.CreateCommand();
        comando.CommandText =
            "INSERT INTO comprobantes_venta (id_tenant, id_tipo_comprobante, numero, fecha, id_punto_venta, " +
            " id_empleado, id_cliente, subtotal, descuento_total, total, estado, cae, cae_vencimiento, " +
            " resultado_fiscal, id_venta_origen, id_turno_caja, created_at, updated_at) " +
            "VALUES ($1, $2, $3, now(), $4, $5, $6, 10, 0, 10, 'emitido'::estado_comprobante, $7, $8, " +
            " $9::resultado_fiscal, $10, $11, now(), now()) RETURNING id_comprobante_venta";
        comando.Parameters.Add(new NpgsqlParameter { Value = idTenant ?? e.IdTenant });
        comando.Parameters.Add(new NpgsqlParameter { Value = idTipo });
        comando.Parameters.Add(new NpgsqlParameter { Value = Interlocked.Increment(ref _numeroSecuencial) });
        comando.Parameters.Add(new NpgsqlParameter { Value = e.IdPuntoVenta });
        comando.Parameters.Add(new NpgsqlParameter { Value = e.IdEmpleado });
        comando.Parameters.Add(new NpgsqlParameter { Value = e.IdCliente });
        comando.Parameters.Add(new NpgsqlParameter { Value = fiscal ? "12345678901234" : DBNull.Value });
        comando.Parameters.Add(new NpgsqlParameter { Value = fiscal ? new DateOnly(2026, 5, 20) : DBNull.Value });
        comando.Parameters.Add(new NpgsqlParameter { Value = fiscal ? "aprobado" : DBNull.Value });
        comando.Parameters.Add(new NpgsqlParameter { Value = (object?)idVentaOrigen ?? DBNull.Value });
        comando.Parameters.Add(new NpgsqlParameter { Value = (object?)idTurnoCaja ?? DBNull.Value });
        return (int)(await comando.ExecuteScalarAsync())!;
    }

    private static async Task<int> InsertarFacturacionAsync(
        NpgsqlConnection cruda, Escenario e, int idVenta, string motivo = "forzada", string estado = "pendiente",
        string aportes = "[]", DateOnly? periodo = null, int? idFiscal = null, int intentos = 0, int? idTenant = null)
    {
        await using var comando = cruda.CreateCommand();
        comando.CommandText =
            "INSERT INTO facturaciones_de_venta (id_tenant, id_empresa, id_comprobante_venta, motivo, estado, " +
            " aportes, periodo, id_comprobante_fiscal, intentos, created_at, updated_at) " +
            "VALUES ($1, $2, $3, $4::motivo_facturacion, $5::estado_facturacion_venta, $6::jsonb, $7, $8, $9, " +
            " now(), now()) RETURNING id_facturacion_venta";
        comando.Parameters.Add(new NpgsqlParameter { Value = idTenant ?? e.IdTenant });
        comando.Parameters.Add(new NpgsqlParameter { Value = e.IdEmpresa });
        comando.Parameters.Add(new NpgsqlParameter { Value = idVenta });
        comando.Parameters.Add(new NpgsqlParameter { Value = motivo });
        comando.Parameters.Add(new NpgsqlParameter { Value = estado });
        comando.Parameters.Add(new NpgsqlParameter { Value = aportes });
        comando.Parameters.Add(new NpgsqlParameter { Value = periodo ?? new DateOnly(2026, 5, 1) });
        comando.Parameters.Add(new NpgsqlParameter { Value = (object?)idFiscal ?? DBNull.Value });
        comando.Parameters.Add(new NpgsqlParameter { Value = intentos });
        return (int)(await comando.ExecuteScalarAsync())!;
    }

    private static async Task InsertarAcumuladoAsync(
        NpgsqlConnection cruda, Escenario e, DateOnly? periodo = null, decimal saldo = 0m, int? idTenant = null,
        int? idMedioPago = null)
    {
        await using var comando = cruda.CreateCommand();
        comando.CommandText =
            "INSERT INTO acumulados_de_facturacion (id_empresa, id_medio_pago, periodo, id_tenant, saldo_a_facturar) " +
            "VALUES ($1, $2, $3, $4, $5)";
        comando.Parameters.Add(new NpgsqlParameter { Value = e.IdEmpresa });
        comando.Parameters.Add(new NpgsqlParameter { Value = idMedioPago ?? e.IdMedioPago });
        comando.Parameters.Add(new NpgsqlParameter { Value = periodo ?? new DateOnly(2026, 5, 1) });
        comando.Parameters.Add(new NpgsqlParameter { Value = idTenant ?? e.IdTenant });
        comando.Parameters.Add(new NpgsqlParameter { Value = saldo });
        await comando.ExecuteNonQueryAsync();
    }

    private static async Task<PostgresException> ViolacionAsync(Func<Task> accion) =>
        await Assert.ThrowsAsync<PostgresException>(accion);

    private static void AssertViolacion(PostgresException excepcion, string sqlState, string restriccion)
    {
        Assert.Equal(sqlState, excepcion.SqlState);
        Assert.Equal(restriccion, excepcion.ConstraintName);
    }

    private static async Task<T> ScalarAsync<T>(NpgsqlConnection cruda, string sql, params object[] parametros)
    {
        await using var comando = cruda.CreateCommand();
        comando.CommandText = sql;
        foreach (var p in parametros)
        {
            comando.Parameters.Add(new NpgsqlParameter { Value = p });
        }

        return (T)(await comando.ExecuteScalarAsync())!;
    }

    // ---- comprobantes_venta: espejo fiscal -----------------------------------------------------------

    [Fact]
    public async Task UnSegundoEspejoParaLaMismaVentaViolaLaUnicidadParcial()
    {
        var e = await SembrarEscenarioAsync(nameof(UnSegundoEspejoParaLaMismaVentaViolaLaUnicidadParcial));
        await using var cruda = await fixture.AbrirConexionCrudaAsync("tenant", e.IdTenant);
        var venta = await InsertarComprobanteAsync(cruda, e, e.IdTipoTx, fiscal: false);
        await InsertarComprobanteAsync(cruda, e, e.IdTipoFa, fiscal: true, idVentaOrigen: venta);

        var excepcion = await ViolacionAsync(
            () => InsertarComprobanteAsync(cruda, e, e.IdTipoFa, fiscal: true, idVentaOrigen: venta));

        AssertViolacion(excepcion, "23505", "ux_comprobantes_venta_venta_origen");
    }

    [Fact]
    public async Task LasVentasSinEspejoNoCompitenPorLaUnicidadParcial()
    {
        var e = await SembrarEscenarioAsync(nameof(LasVentasSinEspejoNoCompitenPorLaUnicidadParcial));
        await using var cruda = await fixture.AbrirConexionCrudaAsync("tenant", e.IdTenant);

        await InsertarComprobanteAsync(cruda, e, e.IdTipoTx, fiscal: false);
        await InsertarComprobanteAsync(cruda, e, e.IdTipoTx, fiscal: false);
        await InsertarComprobanteAsync(cruda, e, e.IdTipoFa, fiscal: true);

        Assert.Equal(
            3L,
            await ScalarAsync<long>(
                cruda, "SELECT count(*) FROM comprobantes_venta WHERE id_tenant = $1 AND id_venta_origen IS NULL", e.IdTenant));
    }

    [Fact]
    public async Task UnEspejoSinResultadoFiscalViolaElCheckDeEspejoFiscal()
    {
        var e = await SembrarEscenarioAsync(nameof(UnEspejoSinResultadoFiscalViolaElCheckDeEspejoFiscal));
        await using var cruda = await fixture.AbrirConexionCrudaAsync("tenant", e.IdTenant);
        var venta = await InsertarComprobanteAsync(cruda, e, e.IdTipoTx, fiscal: false);

        var excepcion = await ViolacionAsync(
            () => InsertarComprobanteAsync(cruda, e, e.IdTipoFa, fiscal: false, idVentaOrigen: venta));

        AssertViolacion(excepcion, "23514", "ck_comprobantes_venta_espejo_fiscal");
    }

    [Fact]
    public async Task UnEspejoConTurnoDeCajaViolaElCheckDeEspejoSinTurno()
    {
        var e = await SembrarEscenarioAsync(nameof(UnEspejoConTurnoDeCajaViolaElCheckDeEspejoSinTurno));
        await using var cruda = await fixture.AbrirConexionCrudaAsync("tenant", e.IdTenant);
        var venta = await InsertarComprobanteAsync(cruda, e, e.IdTipoTx, fiscal: false);

        // El CHECK se evalúa antes que la FK del turno, así que alcanza con un id cualquiera.
        var excepcion = await ViolacionAsync(
            () => InsertarComprobanteAsync(cruda, e, e.IdTipoFa, fiscal: true, idVentaOrigen: venta, idTurnoCaja: 999_999_999));

        AssertViolacion(excepcion, "23514", "ck_comprobantes_venta_espejo_sin_turno");
    }

    [Fact]
    public async Task UnEspejoDeUnaVentaInexistenteViolaLaFkDeVentaOrigen()
    {
        var e = await SembrarEscenarioAsync(nameof(UnEspejoDeUnaVentaInexistenteViolaLaFkDeVentaOrigen));
        await using var cruda = await fixture.AbrirConexionCrudaAsync("tenant", e.IdTenant);

        var excepcion = await ViolacionAsync(
            () => InsertarComprobanteAsync(cruda, e, e.IdTipoFa, fiscal: true, idVentaOrigen: 999_999_999));

        AssertViolacion(excepcion, "23503", "fk_comprobantes_venta_venta_origen");
    }

    [Fact]
    public async Task UnEspejoNoPuedeApuntarALaVentaDeOtroTenant()
    {
        var a = await SembrarEscenarioAsync(nameof(UnEspejoNoPuedeApuntarALaVentaDeOtroTenant) + "-A");
        var b = await SembrarEscenarioAsync(nameof(UnEspejoNoPuedeApuntarALaVentaDeOtroTenant) + "-B");

        int ventaDeB;
        await using (var comoB = await fixture.AbrirConexionCrudaAsync("tenant", b.IdTenant))
        {
            ventaDeB = await InsertarComprobanteAsync(comoB, b, b.IdTipoTx, fiscal: false);
        }

        await using var comoA = await fixture.AbrirConexionCrudaAsync("tenant", a.IdTenant);
        var excepcion = await ViolacionAsync(
            () => InsertarComprobanteAsync(comoA, a, a.IdTipoFa, fiscal: true, idVentaOrigen: ventaDeB));

        AssertViolacion(excepcion, "23503", "fk_comprobantes_venta_venta_origen");
    }

    [Fact]
    public async Task LaVentaSeCreaSinOrigenYSinFacturaForzadaPorDefecto()
    {
        var e = await SembrarEscenarioAsync(nameof(LaVentaSeCreaSinOrigenYSinFacturaForzadaPorDefecto));
        await using var cruda = await fixture.AbrirConexionCrudaAsync("tenant", e.IdTenant);
        var venta = await InsertarComprobanteAsync(cruda, e, e.IdTipoTx, fiscal: false);

        Assert.False(await ScalarAsync<bool>(
            cruda, "SELECT factura_forzada FROM comprobantes_venta WHERE id_comprobante_venta = $1", venta));
        Assert.True(await ScalarAsync<bool>(
            cruda, "SELECT id_venta_origen IS NULL FROM comprobantes_venta WHERE id_comprobante_venta = $1", venta));
    }

    // ---- facturaciones_de_venta ----------------------------------------------------------------------

    [Fact]
    public async Task UnaVentaTieneUnaSolaDecisionDeFacturacion()
    {
        var e = await SembrarEscenarioAsync(nameof(UnaVentaTieneUnaSolaDecisionDeFacturacion));
        await using var cruda = await fixture.AbrirConexionCrudaAsync("tenant", e.IdTenant);
        var venta = await InsertarComprobanteAsync(cruda, e, e.IdTipoTx, fiscal: false);
        await InsertarFacturacionAsync(cruda, e, venta);

        var excepcion = await ViolacionAsync(() => InsertarFacturacionAsync(cruda, e, venta, motivo: "cupo_alcanzado"));

        AssertViolacion(excepcion, "23505", "ux_facturaciones_de_venta_comprobante");
    }

    [Fact]
    public async Task UnEspejoPerteneceAUnaSolaDecision()
    {
        var e = await SembrarEscenarioAsync(nameof(UnEspejoPerteneceAUnaSolaDecision));
        await using var cruda = await fixture.AbrirConexionCrudaAsync("tenant", e.IdTenant);
        var ventaUno = await InsertarComprobanteAsync(cruda, e, e.IdTipoTx, fiscal: false);
        var ventaDos = await InsertarComprobanteAsync(cruda, e, e.IdTipoTx, fiscal: false);
        var espejo = await InsertarComprobanteAsync(cruda, e, e.IdTipoFa, fiscal: true, idVentaOrigen: ventaUno);
        await InsertarFacturacionAsync(cruda, e, ventaUno, estado: "emitida", idFiscal: espejo);

        var excepcion = await ViolacionAsync(
            () => InsertarFacturacionAsync(cruda, e, ventaDos, estado: "emitida", idFiscal: espejo));

        AssertViolacion(excepcion, "23505", "ux_facturaciones_de_venta_comprobante_fiscal");
    }

    [Theory]
    [InlineData("periodo", "ck_facturaciones_de_venta_periodo_dia_uno")]
    [InlineData("aportes", "ck_facturaciones_de_venta_aportes_arreglo")]
    [InlineData("intentos", "ck_facturaciones_de_venta_intentos_no_negativo")]
    [InlineData("motivo", "ck_facturaciones_de_venta_motivo_requerido")]
    [InlineData("emitida", "ck_facturaciones_de_venta_emitida_con_comprobante")]
    public async Task UnaDecisionIncoherenteViolaSuCheck(string campo, string restriccion)
    {
        var e = await SembrarEscenarioAsync(nameof(UnaDecisionIncoherenteViolaSuCheck) + campo);
        await using var cruda = await fixture.AbrirConexionCrudaAsync("tenant", e.IdTenant);
        var venta = await InsertarComprobanteAsync(cruda, e, e.IdTipoTx, fiscal: false);

        var excepcion = await ViolacionAsync(() => campo switch
        {
            "periodo" => InsertarFacturacionAsync(cruda, e, venta, periodo: new DateOnly(2026, 5, 2)),
            "aportes" => InsertarFacturacionAsync(cruda, e, venta, aportes: "{}"),
            "intentos" => InsertarFacturacionAsync(cruda, e, venta, intentos: -1),
            "motivo" => InsertarFacturacionAsync(cruda, e, venta, motivo: "no_requerida"),
            _ => InsertarFacturacionAsync(cruda, e, venta, estado: "emitida", idFiscal: null)
        });

        AssertViolacion(excepcion, "23514", restriccion);
    }

    [Fact]
    public async Task UnaDecisionCoherenteNoViolaNingunCheck()
    {
        var e = await SembrarEscenarioAsync(nameof(UnaDecisionCoherenteNoViolaNingunCheck));
        await using var cruda = await fixture.AbrirConexionCrudaAsync("tenant", e.IdTenant);
        var venta = await InsertarComprobanteAsync(cruda, e, e.IdTipoTx, fiscal: false);
        var espejo = await InsertarComprobanteAsync(cruda, e, e.IdTipoFa, fiscal: true, idVentaOrigen: venta);

        await InsertarFacturacionAsync(
            cruda, e, venta, motivo: "medio_exige_factura", estado: "emitida",
            aportes: "[{\"idMedioPago\":1,\"aporte\":-300.000000}]", idFiscal: espejo, intentos: 2);

        Assert.Equal(
            "emitida",
            await ScalarAsync<string>(
                cruda, "SELECT estado::text FROM facturaciones_de_venta WHERE id_comprobante_venta = $1", venta));
    }

    [Fact]
    public async Task UnaDecisionDeUnaVentaInexistenteViolaLaFk()
    {
        var e = await SembrarEscenarioAsync(nameof(UnaDecisionDeUnaVentaInexistenteViolaLaFk));
        await using var cruda = await fixture.AbrirConexionCrudaAsync("tenant", e.IdTenant);

        var excepcion = await ViolacionAsync(() => InsertarFacturacionAsync(cruda, e, 999_999_999));

        AssertViolacion(excepcion, "23503", "fk_facturaciones_de_venta_comprobante");
    }

    [Fact]
    public async Task UnaSesionDeOtroTenantNoVeLasDecisionesDeFacturacion()
    {
        var a = await SembrarEscenarioAsync(nameof(UnaSesionDeOtroTenantNoVeLasDecisionesDeFacturacion) + "-A");
        var b = await SembrarEscenarioAsync(nameof(UnaSesionDeOtroTenantNoVeLasDecisionesDeFacturacion) + "-B");

        await using (var comoA = await fixture.AbrirConexionCrudaAsync("tenant", a.IdTenant))
        {
            var venta = await InsertarComprobanteAsync(comoA, a, a.IdTipoTx, fiscal: false);
            await InsertarFacturacionAsync(comoA, a, venta);
        }

        await using var comoB = await fixture.AbrirConexionCrudaAsync("tenant", b.IdTenant);
        Assert.Equal(
            0L, await ScalarAsync<long>(comoB, "SELECT count(*) FROM facturaciones_de_venta WHERE id_tenant = $1", a.IdTenant));
    }

    [Fact]
    public async Task UnInsertConIdTenantAjenoEnFacturacionesDeVentaSeRechaza()
    {
        var a = await SembrarEscenarioAsync(nameof(UnInsertConIdTenantAjenoEnFacturacionesDeVentaSeRechaza) + "-A");
        var b = await SembrarEscenarioAsync(nameof(UnInsertConIdTenantAjenoEnFacturacionesDeVentaSeRechaza) + "-B");

        await using var comoA = await fixture.AbrirConexionCrudaAsync("tenant", a.IdTenant);
        var venta = await InsertarComprobanteAsync(comoA, a, a.IdTipoTx, fiscal: false);

        var excepcion = await ViolacionAsync(() => InsertarFacturacionAsync(comoA, b, venta, idTenant: b.IdTenant));

        Assert.Equal("42501", excepcion.SqlState);
    }

    // ---- acumulados_de_facturacion -------------------------------------------------------------------

    [Fact]
    public async Task UnPeriodoQueNoEsElPrimerDiaDelMesViolaElCheck()
    {
        var e = await SembrarEscenarioAsync(nameof(UnPeriodoQueNoEsElPrimerDiaDelMesViolaElCheck));
        await using var cruda = await fixture.AbrirConexionCrudaAsync("tenant", e.IdTenant);

        var excepcion = await ViolacionAsync(() => InsertarAcumuladoAsync(cruda, e, periodo: new DateOnly(2026, 5, 15)));

        AssertViolacion(excepcion, "23514", "ck_acumulados_de_facturacion_periodo_dia_uno");
    }

    [Fact]
    public async Task ElAcumuladoEsUnoPorEmpresaMedioDePagoYMes()
    {
        var e = await SembrarEscenarioAsync(nameof(ElAcumuladoEsUnoPorEmpresaMedioDePagoYMes));
        await using var cruda = await fixture.AbrirConexionCrudaAsync("tenant", e.IdTenant);
        await InsertarAcumuladoAsync(cruda, e, periodo: new DateOnly(2026, 5, 1));
        await InsertarAcumuladoAsync(cruda, e, periodo: new DateOnly(2026, 6, 1));

        var excepcion = await ViolacionAsync(() => InsertarAcumuladoAsync(cruda, e, periodo: new DateOnly(2026, 5, 1)));

        AssertViolacion(excepcion, "23505", "pk_acumulados_de_facturacion");
    }

    [Fact]
    public async Task ElSaldoConservaSeisDecimalesExactos()
    {
        var e = await SembrarEscenarioAsync(nameof(ElSaldoConservaSeisDecimalesExactos));
        await using var cruda = await fixture.AbrirConexionCrudaAsync("tenant", e.IdTenant);
        // 99999999,99 por 99,99 por ciento dividido 100 ya son seis decimales; el último no se redondea.
        const decimal aporte = 99_999_999.99m * 99.99m / 100m;
        await InsertarAcumuladoAsync(cruda, e, saldo: aporte);

        var guardado = await ScalarAsync<decimal>(
            cruda, "SELECT saldo_a_facturar FROM acumulados_de_facturacion WHERE id_empresa = $1", e.IdEmpresa);

        Assert.Equal(aporte, guardado);
        Assert.Equal(6, (decimal.GetBits(aporte)[3] >> 16) & 0xFF);
    }

    [Fact]
    public async Task LosTotalesDelAcumuladoArrancanEnCero()
    {
        var e = await SembrarEscenarioAsync(nameof(LosTotalesDelAcumuladoArrancanEnCero));
        await using var cruda = await fixture.AbrirConexionCrudaAsync("tenant", e.IdTenant);
        await InsertarAcumuladoAsync(cruda, e);

        Assert.Equal(
            0m,
            await ScalarAsync<decimal>(
                cruda,
                "SELECT total_vendido + total_facturado + saldo_a_facturar FROM acumulados_de_facturacion WHERE id_empresa = $1",
                e.IdEmpresa));
    }

    [Fact]
    public async Task UnAcumuladoConUnMedioDePagoDeOtroTenantViolaLaFk()
    {
        var a = await SembrarEscenarioAsync(nameof(UnAcumuladoConUnMedioDePagoDeOtroTenantViolaLaFk) + "-A");
        var b = await SembrarEscenarioAsync(nameof(UnAcumuladoConUnMedioDePagoDeOtroTenantViolaLaFk) + "-B");
        await using var comoA = await fixture.AbrirConexionCrudaAsync("tenant", a.IdTenant);

        var excepcion = await ViolacionAsync(() => InsertarAcumuladoAsync(comoA, a, idMedioPago: b.IdMedioPago));

        AssertViolacion(excepcion, "23503", "fk_acumulados_de_facturacion_medio_pago");
    }

    [Fact]
    public async Task UnaSesionDeOtroTenantNoVeLosAcumulados()
    {
        var a = await SembrarEscenarioAsync(nameof(UnaSesionDeOtroTenantNoVeLosAcumulados) + "-A");
        var b = await SembrarEscenarioAsync(nameof(UnaSesionDeOtroTenantNoVeLosAcumulados) + "-B");

        await using (var comoA = await fixture.AbrirConexionCrudaAsync("tenant", a.IdTenant))
        {
            await InsertarAcumuladoAsync(comoA, a);
        }

        await using var comoB = await fixture.AbrirConexionCrudaAsync("tenant", b.IdTenant);
        Assert.Equal(
            0L, await ScalarAsync<long>(comoB, "SELECT count(*) FROM acumulados_de_facturacion WHERE id_tenant = $1", a.IdTenant));
    }

    [Fact]
    public async Task UnInsertConIdTenantAjenoEnAcumuladosSeRechaza()
    {
        var a = await SembrarEscenarioAsync(nameof(UnInsertConIdTenantAjenoEnAcumuladosSeRechaza) + "-A");
        var b = await SembrarEscenarioAsync(nameof(UnInsertConIdTenantAjenoEnAcumuladosSeRechaza) + "-B");
        await using var comoA = await fixture.AbrirConexionCrudaAsync("tenant", a.IdTenant);

        var excepcion = await ViolacionAsync(() => InsertarAcumuladoAsync(comoA, b, idTenant: b.IdTenant));

        Assert.Equal("42501", excepcion.SqlState);
    }

    [Fact]
    public async Task ElFiltroDeEfNuncaDevuelveAcumuladosDeOtroTenant()
    {
        var a = await SembrarEscenarioAsync(nameof(ElFiltroDeEfNuncaDevuelveAcumuladosDeOtroTenant) + "-A");
        var b = await SembrarEscenarioAsync(nameof(ElFiltroDeEfNuncaDevuelveAcumuladosDeOtroTenant) + "-B");

        await using (var comoA = await fixture.AbrirConexionCrudaAsync("tenant", a.IdTenant))
        {
            await InsertarAcumuladoAsync(comoA, a);
        }

        await using var sesionB = fixture.CrearContextoDeAplicacion(new TenantActualFijo(ModoDeAcceso.Tenant, b.IdTenant));
        var visibles = await sesionB.AcumuladosDeFacturacion.Where(x => x.IdEmpresa == a.IdEmpresa).ToListAsync();

        Assert.Empty(visibles);
    }

    [Fact]
    public async Task UnSaveChangesSobreUnAcumuladoTira()
    {
        var e = await SembrarEscenarioAsync(nameof(UnSaveChangesSobreUnAcumuladoTira));

        await using var db = fixture.CrearContextoDeAplicacion(new TenantActualFijo(ModoDeAcceso.Tenant, e.IdTenant));
        db.AcumuladosDeFacturacion.Add(new AcumuladoDeFacturacion
        {
            IdEmpresa = e.IdEmpresa, IdMedioPago = e.IdMedioPago, Periodo = new DateOnly(2026, 5, 1), IdTenant = e.IdTenant
        });

        await Assert.ThrowsAsync<InvalidOperationException>(() => db.SaveChangesAsync());
    }

    // ---- definición del esquema ----------------------------------------------------------------------

    [Theory]
    [InlineData("facturaciones_de_venta")]
    [InlineData("acumulados_de_facturacion")]
    public async Task LasTablasNuevasTienenRlsHabilitadaYForzada(string tabla)
    {
        using var _ = fixture.CreateClient();
        await using var cruda = new NpgsqlConnection(fixture.OwnerConnectionString);
        await cruda.OpenAsync();

        Assert.True(await ScalarAsync<bool>(
            cruda, "SELECT relrowsecurity AND relforcerowsecurity FROM pg_class WHERE relname = $1", tabla));
        Assert.Equal(
            1L, await ScalarAsync<long>(cruda, "SELECT count(*) FROM pg_policies WHERE tablename = $1", tabla));
    }

    [Theory]
    [InlineData("estado_facturacion_venta", "pendiente,emitida,rechazada,cancelada")]
    [InlineData("motivo_facturacion", "no_requerida,forzada,medio_exige_factura,cupo_alcanzado")]
    public async Task ElOrdenDelEnumNativoEsElDelCicloDeVida(string tipo, string orden)
    {
        using var _ = fixture.CreateClient();
        await using var cruda = new NpgsqlConnection(fixture.OwnerConnectionString);
        await cruda.OpenAsync();

        var etiquetas = await ScalarAsync<string>(
            cruda,
            "SELECT string_agg(e.enumlabel, ',' ORDER BY e.enumsortorder) FROM pg_enum e " +
            "JOIN pg_type t ON t.oid = e.enumtypid WHERE t.typname = $1",
            tipo);

        Assert.Equal(orden, etiquetas);
    }

    [Theory]
    [InlineData("comprobantes_venta", "ux_comprobantes_venta_venta_origen", "UNIQUE INDEX", "btree (id_venta_origen, id_tenant)", "WHERE ((id_venta_origen IS NOT NULL) AND (id_comprobante_asociado IS NULL))")]
    [InlineData("comprobantes_venta", "ux_comprobantes_venta_nota_credito_espejo", "UNIQUE INDEX", "btree (id_comprobante_asociado, id_tenant)", "WHERE ((id_venta_origen IS NOT NULL) AND (id_comprobante_asociado IS NOT NULL))")]
    [InlineData("comprobantes_venta", "ix_comprobantes_venta_asociado", "INDEX", "btree (id_comprobante_asociado, id_tenant)", "")]
    [InlineData("facturaciones_de_venta", "ux_facturaciones_de_venta_comprobante", "UNIQUE INDEX", "btree (id_comprobante_venta, id_tenant)", "WHERE (id_facturacion_origen IS NULL)")]
    [InlineData("facturaciones_de_venta", "ux_facturaciones_de_venta_origen", "UNIQUE INDEX", "btree (id_facturacion_origen, id_tenant)", "WHERE (id_facturacion_origen IS NOT NULL)")]
    [InlineData("facturaciones_de_venta", "ux_facturaciones_de_venta_comprobante_fiscal", "UNIQUE INDEX", "btree (id_comprobante_fiscal, id_tenant)", "WHERE (id_comprobante_fiscal IS NOT NULL)")]
    [InlineData("facturaciones_de_venta", "ix_facturaciones_de_venta_pendientes", "INDEX", "btree (id_tenant, proximo_intento_en)", "WHERE (estado = 'pendiente'::estado_facturacion_venta)")]
    public async Task LosIndicesNuevosTienenLaDefinicionEsperada(
        string tabla, string indice, string tipo, string columnas, string filtro)
    {
        using var _ = fixture.CreateClient();
        await using var cruda = new NpgsqlConnection(fixture.OwnerConnectionString);
        await cruda.OpenAsync();

        var definicion = await ScalarAsync<string>(
            cruda, "SELECT indexdef FROM pg_indexes WHERE tablename = $1 AND indexname = $2", tabla, indice);

        Assert.Contains(tipo, definicion);
        Assert.Contains(columnas, definicion);
        if (filtro.Length > 0)
        {
            Assert.Contains(filtro, definicion);
        }
    }

    [Fact]
    public async Task ElSaldoEsNumeric18Con6Decimales()
    {
        using var _ = fixture.CreateClient();
        await using var cruda = new NpgsqlConnection(fixture.OwnerConnectionString);
        await cruda.OpenAsync();

        Assert.Equal(
            "18,6",
            await ScalarAsync<string>(
                cruda,
                "SELECT numeric_precision || ',' || numeric_scale FROM information_schema.columns " +
                "WHERE table_name = 'acumulados_de_facturacion' AND column_name = 'saldo_a_facturar'"));
    }
}
