using Npgsql;

namespace Ways.IntegrationTests;

/// <summary>
/// Facturación por medio de pago, T8-S1: el esquema de la nota de crédito fiscal. Una nota de crédito es la segunda fila de
/// la misma venta, en las dos tablas: la decisión (<c>facturaciones_de_venta.id_facturacion_origen</c> apunta a la decisión de la
/// factura) y el espejo (<c>comprobantes_venta</c> con <c>id_venta_origen</c> = la venta e <c>id_comprobante_asociado</c> = el
/// espejo de la factura). Cada restricción se prueba con un INSERT crudo bajo <c>ways_app</c> y se aserta el SQLSTATE y el nombre de
/// la restricción; las dos unicidades nuevas, además, contra una carrera real de dos conexiones.
/// </summary>
public partial class FacturacionEsquemaTests
{
    private const string UnicidadDeNotaDeCreditoDeDecision = "ux_facturaciones_de_venta_origen";

    private const string UnicidadDeNotaDeCreditoDeEspejo = "ux_comprobantes_venta_nota_credito_espejo";

    private static async Task<int> InsertarDecisionDeNotaDeCreditoAsync(
        NpgsqlConnection cruda, Escenario e, int idVenta, int idFacturacionOrigen, string aportes = "[]")
    {
        await using var comando = cruda.CreateCommand();
        comando.CommandText =
            "INSERT INTO facturaciones_de_venta (id_tenant, id_empresa, id_comprobante_venta, motivo, estado, " +
            " aportes, periodo, id_facturacion_origen, intentos, created_at, updated_at) " +
            "VALUES ($1, $2, $3, 'forzada'::motivo_facturacion, 'pendiente'::estado_facturacion_venta, $4::jsonb, " +
            " '2026-05-01', $5, 0, now(), now()) RETURNING id_facturacion_venta";
        comando.Parameters.Add(new NpgsqlParameter { Value = e.IdTenant });
        comando.Parameters.Add(new NpgsqlParameter { Value = e.IdEmpresa });
        comando.Parameters.Add(new NpgsqlParameter { Value = idVenta });
        comando.Parameters.Add(new NpgsqlParameter { Value = aportes });
        comando.Parameters.Add(new NpgsqlParameter { Value = idFacturacionOrigen });
        return (int)(await comando.ExecuteScalarAsync())!;
    }

    /// <summary>Inserta el espejo de una nota de crédito: espejo de <paramref name="idVenta"/> asociado a la factura espejo.</summary>
    private static async Task<int> InsertarEspejoDeNotaDeCreditoAsync(
        NpgsqlConnection cruda, Escenario e, int idVenta, int idFacturaEspejo)
    {
        await using var comando = cruda.CreateCommand();
        comando.CommandText =
            "INSERT INTO comprobantes_venta (id_tenant, id_tipo_comprobante, numero, fecha, id_punto_venta, " +
            " id_empleado, id_cliente, subtotal, descuento_total, total, estado, cae, cae_vencimiento, " +
            " resultado_fiscal, id_venta_origen, id_comprobante_asociado, created_at, updated_at) " +
            "VALUES ($1, $2, $3, now(), $4, $5, $6, 10, 0, 10, 'emitido'::estado_comprobante, '12345678901234', " +
            " '2026-05-20', 'aprobado'::resultado_fiscal, $7, $8, now(), now()) RETURNING id_comprobante_venta";
        comando.Parameters.Add(new NpgsqlParameter { Value = e.IdTenant });
        comando.Parameters.Add(new NpgsqlParameter { Value = e.IdTipoFa });
        comando.Parameters.Add(new NpgsqlParameter { Value = Interlocked.Increment(ref _numeroSecuencial) });
        comando.Parameters.Add(new NpgsqlParameter { Value = e.IdPuntoVenta });
        comando.Parameters.Add(new NpgsqlParameter { Value = e.IdEmpleado });
        comando.Parameters.Add(new NpgsqlParameter { Value = e.IdCliente });
        comando.Parameters.Add(new NpgsqlParameter { Value = idVenta });
        comando.Parameters.Add(new NpgsqlParameter { Value = idFacturaEspejo });
        return (int)(await comando.ExecuteScalarAsync())!;
    }

    /// <summary>Dos inserciones simultáneas de la misma clave, cada una en su conexión: exactamente una gana y la otra recibe
    /// <c>23505</c> sobre <paramref name="restriccion"/>.</summary>
    private async Task AssertCarreraUnicaAsync(Escenario e, Func<NpgsqlConnection, Task> insertar, string restriccion)
    {
        await using var a = await fixture.AbrirConexionCrudaAsync("tenant", e.IdTenant);
        await using var b = await fixture.AbrirConexionCrudaAsync("tenant", e.IdTenant);
        var compuerta = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        async Task<PostgresException?> IntentarAsync(NpgsqlConnection conexion)
        {
            await compuerta.Task;
            try
            {
                await insertar(conexion);
                return null;
            }
            catch (PostgresException ex)
            {
                return ex;
            }
        }

        var tareas = new[] { IntentarAsync(a), IntentarAsync(b) };
        compuerta.SetResult();
        var resultados = await Task.WhenAll(tareas);

        Assert.Single(resultados, r => r is null);
        var perdedora = Assert.Single(resultados, r => r is not null)!;
        AssertViolacion(perdedora, "23505", restriccion);
    }

    [Fact]
    public async Task LaFacturaYLaNotaDeCreditoCoexistenEnLaMismaVentaEnLasDosTablas()
    {
        var e = await SembrarEscenarioAsync(nameof(LaFacturaYLaNotaDeCreditoCoexistenEnLaMismaVentaEnLasDosTablas));
        await using var cruda = await fixture.AbrirConexionCrudaAsync("tenant", e.IdTenant);
        var venta = await InsertarComprobanteAsync(cruda, e, e.IdTipoTx, fiscal: false);
        var factura = await InsertarComprobanteAsync(cruda, e, e.IdTipoFa, fiscal: true, idVentaOrigen: venta);
        var decisionFactura = await InsertarFacturacionAsync(cruda, e, venta, estado: "emitida", idFiscal: factura);

        var notaDeCredito = await InsertarEspejoDeNotaDeCreditoAsync(cruda, e, venta, factura);
        var decisionNota = await InsertarDecisionDeNotaDeCreditoAsync(cruda, e, venta, decisionFactura);

        Assert.Equal(2, await ScalarAsync<long>(
            cruda, "SELECT count(*) FROM comprobantes_venta WHERE id_venta_origen = $1", venta));
        Assert.Equal(2, await ScalarAsync<long>(
            cruda, "SELECT count(*) FROM facturaciones_de_venta WHERE id_comprobante_venta = $1", venta));
        Assert.Equal(factura, await ScalarAsync<int>(
            cruda, "SELECT id_comprobante_asociado FROM comprobantes_venta WHERE id_comprobante_venta = $1", notaDeCredito));
        Assert.Equal(decisionFactura, await ScalarAsync<int>(
            cruda, "SELECT id_facturacion_origen FROM facturaciones_de_venta WHERE id_facturacion_venta = $1", decisionNota));
    }

    [Fact]
    public async Task UnaSegundaNotaDeCreditoParaLaMismaFacturaViolaLaUnicidadDelEspejo()
    {
        var e = await SembrarEscenarioAsync(nameof(UnaSegundaNotaDeCreditoParaLaMismaFacturaViolaLaUnicidadDelEspejo));
        await using var cruda = await fixture.AbrirConexionCrudaAsync("tenant", e.IdTenant);
        var venta = await InsertarComprobanteAsync(cruda, e, e.IdTipoTx, fiscal: false);
        var factura = await InsertarComprobanteAsync(cruda, e, e.IdTipoFa, fiscal: true, idVentaOrigen: venta);
        await InsertarEspejoDeNotaDeCreditoAsync(cruda, e, venta, factura);

        var excepcion = await ViolacionAsync(() => InsertarEspejoDeNotaDeCreditoAsync(cruda, e, venta, factura));

        AssertViolacion(excepcion, "23505", UnicidadDeNotaDeCreditoDeEspejo);
    }

    [Fact]
    public async Task UnaSegundaDecisionDeNotaDeCreditoParaLaMismaDecisionViolaLaUnicidadDelOrigen()
    {
        var e = await SembrarEscenarioAsync(nameof(UnaSegundaDecisionDeNotaDeCreditoParaLaMismaDecisionViolaLaUnicidadDelOrigen));
        await using var cruda = await fixture.AbrirConexionCrudaAsync("tenant", e.IdTenant);
        var venta = await InsertarComprobanteAsync(cruda, e, e.IdTipoTx, fiscal: false);
        var decisionFactura = await InsertarFacturacionAsync(cruda, e, venta);
        await InsertarDecisionDeNotaDeCreditoAsync(cruda, e, venta, decisionFactura);

        var excepcion = await ViolacionAsync(() => InsertarDecisionDeNotaDeCreditoAsync(cruda, e, venta, decisionFactura));

        AssertViolacion(excepcion, "23505", UnicidadDeNotaDeCreditoDeDecision);
    }

    [Fact]
    public async Task LaFilaDeLaFacturaSigueSiendoUnicaPorVentaAunqueExistaLaDeLaNotaDeCredito()
    {
        var e = await SembrarEscenarioAsync(nameof(LaFilaDeLaFacturaSigueSiendoUnicaPorVentaAunqueExistaLaDeLaNotaDeCredito));
        await using var cruda = await fixture.AbrirConexionCrudaAsync("tenant", e.IdTenant);
        var venta = await InsertarComprobanteAsync(cruda, e, e.IdTipoTx, fiscal: false);
        var factura = await InsertarComprobanteAsync(cruda, e, e.IdTipoFa, fiscal: true, idVentaOrigen: venta);
        var decisionFactura = await InsertarFacturacionAsync(cruda, e, venta);
        await InsertarEspejoDeNotaDeCreditoAsync(cruda, e, venta, factura);
        await InsertarDecisionDeNotaDeCreditoAsync(cruda, e, venta, decisionFactura);

        var segundaFactura = await ViolacionAsync(
            () => InsertarComprobanteAsync(cruda, e, e.IdTipoFa, fiscal: true, idVentaOrigen: venta));
        var segundaDecision = await ViolacionAsync(() => InsertarFacturacionAsync(cruda, e, venta));

        AssertViolacion(segundaFactura, "23505", "ux_comprobantes_venta_venta_origen");
        AssertViolacion(segundaDecision, "23505", "ux_facturaciones_de_venta_comprobante");
    }

    [Fact]
    public async Task UnaDecisionDeNotaDeCreditoNoPuedeLlevarAportes()
    {
        var e = await SembrarEscenarioAsync(nameof(UnaDecisionDeNotaDeCreditoNoPuedeLlevarAportes));
        await using var cruda = await fixture.AbrirConexionCrudaAsync("tenant", e.IdTenant);
        var venta = await InsertarComprobanteAsync(cruda, e, e.IdTipoTx, fiscal: false);
        var decisionFactura = await InsertarFacturacionAsync(cruda, e, venta);

        var excepcion = await ViolacionAsync(() => InsertarDecisionDeNotaDeCreditoAsync(
            cruda, e, venta, decisionFactura, aportes: $"[{{\"idMedioPago\": {e.IdMedioPago}, \"aporte\": 5.0}}]"));

        AssertViolacion(excepcion, "23514", "ck_facturaciones_de_venta_nc_sin_aportes");
    }

    [Fact]
    public async Task UnaDecisionNoPuedeSerSuPropioOrigen()
    {
        var e = await SembrarEscenarioAsync(nameof(UnaDecisionNoPuedeSerSuPropioOrigen));
        await using var cruda = await fixture.AbrirConexionCrudaAsync("tenant", e.IdTenant);
        var venta = await InsertarComprobanteAsync(cruda, e, e.IdTipoTx, fiscal: false);
        var decision = await InsertarFacturacionAsync(cruda, e, venta);

        await using var comando = cruda.CreateCommand();
        comando.CommandText =
            "UPDATE facturaciones_de_venta SET id_facturacion_origen = id_facturacion_venta WHERE id_facturacion_venta = $1";
        comando.Parameters.Add(new NpgsqlParameter { Value = decision });
        var excepcion = await ViolacionAsync(() => comando.ExecuteNonQueryAsync());

        AssertViolacion(excepcion, "23514", "ck_facturaciones_de_venta_origen_distinto");
    }

    [Fact]
    public async Task ElOrigenDeUnaDecisionDeNotaDeCreditoDebeExistir()
    {
        var e = await SembrarEscenarioAsync(nameof(ElOrigenDeUnaDecisionDeNotaDeCreditoDebeExistir));
        await using var cruda = await fixture.AbrirConexionCrudaAsync("tenant", e.IdTenant);
        var venta = await InsertarComprobanteAsync(cruda, e, e.IdTipoTx, fiscal: false);

        var excepcion = await ViolacionAsync(() => InsertarDecisionDeNotaDeCreditoAsync(cruda, e, venta, 2_000_000_000));

        AssertViolacion(excepcion, "23503", "fk_facturaciones_de_venta_origen");
    }

    [Fact]
    public async Task DosNotasDeCreditoSimultaneasDelEspejoDejanExactamenteUnaYLaOtraRecibe23505()
    {
        var e = await SembrarEscenarioAsync(nameof(DosNotasDeCreditoSimultaneasDelEspejoDejanExactamenteUnaYLaOtraRecibe23505));
        int venta;
        int factura;
        await using (var cruda = await fixture.AbrirConexionCrudaAsync("tenant", e.IdTenant))
        {
            venta = await InsertarComprobanteAsync(cruda, e, e.IdTipoTx, fiscal: false);
            factura = await InsertarComprobanteAsync(cruda, e, e.IdTipoFa, fiscal: true, idVentaOrigen: venta);
        }

        await AssertCarreraUnicaAsync(
            e, conexion => InsertarEspejoDeNotaDeCreditoAsync(conexion, e, venta, factura), UnicidadDeNotaDeCreditoDeEspejo);
    }

    [Fact]
    public async Task DosDecisionesDeNotaDeCreditoSimultaneasDejanExactamenteUnaYLaOtraRecibe23505()
    {
        var e = await SembrarEscenarioAsync(nameof(DosDecisionesDeNotaDeCreditoSimultaneasDejanExactamenteUnaYLaOtraRecibe23505));
        int venta;
        int decisionFactura;
        await using (var cruda = await fixture.AbrirConexionCrudaAsync("tenant", e.IdTenant))
        {
            venta = await InsertarComprobanteAsync(cruda, e, e.IdTipoTx, fiscal: false);
            decisionFactura = await InsertarFacturacionAsync(cruda, e, venta);
        }

        await AssertCarreraUnicaAsync(
            e, conexion => InsertarDecisionDeNotaDeCreditoAsync(conexion, e, venta, decisionFactura),
            UnicidadDeNotaDeCreditoDeDecision);
    }
}
