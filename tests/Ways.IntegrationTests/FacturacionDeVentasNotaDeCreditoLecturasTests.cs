using Ways.Application.Fiscal;
using Ways.Application.Ventas;

namespace Ways.IntegrationTests;

/// <summary>
/// Facturación por medio de pago, T8-S1: con las filas de una nota de crédito ya sembradas (decisión y espejo, enlazados a la
/// factura), los lectores que suponían una sola decisión y un solo espejo por venta siguen informando la FACTURA y no se rompen.
/// Ningún camino de la aplicación crea todavía esas filas: se siembran por SQL. Desde T8b el lote también procesa la decisión de
/// la nota (ver FacturacionDeVentasNotaDeCreditoEmisionTests).
///
/// Mutation-proof-tests: las cláusulas bajo prueba son el filtro de tipo (<c>IdFacturacionOrigen == null</c>) del listado del
/// turno y el <c>id_facturacion_origen IS NULL</c> de la lectura por id.
/// </summary>
public partial class FacturacionDeVentasEmisionTests
{
    private sealed record NotaDeCreditoSembrada(int IdDecision, int IdEspejo);

    /// <summary>Siembra, sobre una venta, la decisión de la nota de crédito (pendiente, sin aportes) y, con
    /// <paramref name="idEspejoFactura"/>, su espejo (aprobado, asociado al espejo de la factura). Después reescribe la decisión de
    /// la factura: su versión nueva queda físicamente DESPUÉS de la de la nota de crédito, así que un lector sin filtro de tipo que
    /// toma "la primera fila de la venta" se queda con la nota de crédito.</summary>
    private async Task<NotaDeCreditoSembrada> SembrarNotaDeCreditoAsync(
        Ctx c, int idVenta, int idDecisionFactura, int? idEspejoFactura)
    {
        var espejo = idEspejoFactura is not { } idEspejo ? 0 : await ScalarOwnerAsync<int>(
            "INSERT INTO comprobantes_venta (id_tenant, id_tipo_comprobante, numero, fecha, id_punto_venta, id_empleado, " +
            " id_cliente, subtotal, descuento_total, total, estado, cae, cae_vencimiento, resultado_fiscal, id_venta_origen, " +
            " id_comprobante_asociado, created_at, updated_at) " +
            "SELECT id_tenant, id_tipo_comprobante, numero + 500000, fecha, id_punto_venta, id_empleado, id_cliente, subtotal, " +
            " descuento_total, total, estado, cae, cae_vencimiento, resultado_fiscal, id_venta_origen, $2, now(), now() " +
            "FROM comprobantes_venta WHERE id_comprobante_venta = $1 RETURNING id_comprobante_venta",
            idEspejo, idEspejo);
        var decision = await ScalarOwnerAsync<int>(
            "INSERT INTO facturaciones_de_venta (id_tenant, id_empresa, id_comprobante_venta, motivo, estado, aportes, " +
            " periodo, id_facturacion_origen, intentos, created_at, updated_at) " +
            "SELECT id_tenant, id_empresa, id_comprobante_venta, motivo, 'pendiente'::estado_facturacion_venta, '[]'::jsonb, " +
            " periodo, id_facturacion_venta, 0, now(), now() " +
            "FROM facturaciones_de_venta WHERE id_facturacion_venta = $1 RETURNING id_facturacion_venta",
            idDecisionFactura);
        await EjecutarComoOwnerAsync(
            "UPDATE facturaciones_de_venta SET updated_at = now() WHERE id_facturacion_venta = $1", idDecisionFactura);
        return new NotaDeCreditoSembrada(decision, espejo);
    }

    private async Task<T> ScalarOwnerAsync<T>(string sql, params object?[] parametros)
    {
        await using var conexion = await AbrirOwnerAsync();
        await using var comando = conexion.CreateCommand();
        comando.CommandText = sql;
        foreach (var parametro in parametros)
        {
            comando.Parameters.Add(new Npgsql.NpgsqlParameter { Value = parametro ?? DBNull.Value });
        }

        return (T)(await comando.ExecuteScalarAsync())!;
    }

    [Fact]
    public async Task LaLecturaPorIdSigueInformandoLaFacturaConUnaNotaDeCreditoSembrada()
    {
        var t = await PrepararCobroAsync("ncleeporid");
        var emitido = await VenderEnLineaAsync(t.Cajero, VentaDeCobro(t));
        var espejoFactura = Assert.Single(await LeerEspejosAsync(t.C));
        var nota = await SembrarNotaDeCreditoAsync(t.C, emitido.Id, emitido.Fiscal!.IdFacturacion!.Value, espejoFactura.Id);
        t.C.Wsfe.Reiniciar();

        var leido = await LeerVentaPorIdAsync(t.Cajero, emitido.Id);

        Assert.Equal(EstadosFiscalesDeVenta.Aprobado, leido.Fiscal!.Estado);
        Assert.Equal(emitido.Fiscal.IdFacturacion, leido.Fiscal.IdFacturacion);
        Assert.NotEqual(nota.IdDecision, leido.Fiscal.IdFacturacion);
        Assert.Equal(espejoFactura.Id, leido.Fiscal.Comprobante!.IdComprobante);
        Assert.Empty(t.C.Wsfe.Operaciones);
    }

    [Fact]
    public async Task ElListadoDelTurnoNoSeRompeNiCambiaDeEstadoConUnaNotaDeCreditoSembrada()
    {
        var t = await PrepararCobroAsync("ncleeturno");
        var emitido = await VenderEnLineaAsync(t.Cajero, VentaDeCobro(t));
        var espejoFactura = Assert.Single(await LeerEspejosAsync(t.C));
        await SembrarNotaDeCreditoAsync(t.C, emitido.Id, emitido.Fiscal!.IdFacturacion!.Value, espejoFactura.Id);

        var fila = Assert.Single(await LeerVentasDelTurnoAsync(t.Cajero, t.C.IdTurno));

        Assert.Equal(emitido.Id, fila.Id);
        Assert.Equal(EstadosFiscalesDeVenta.Aprobado, fila.EstadoFiscal);
    }

    /// <summary>Desde T8b el lote procesa las dos clases de decisión. La factura todavía no tiene espejo y la nota de crédito de la
    /// misma venta no corresponde (la venta no está anulada): el lote emite la factura y deja la nota esperando con su código, en la
    /// cuenta de pendientes y sin tocar ARCA ni tomar número.</summary>
    [Fact]
    public async Task ElLoteEmiteLaFacturaPendienteYDejaEnEsperaLaNotaDeUnaVentaQueNoEstaAnulada()
    {
        var t = await PrepararCobroAsync("ncfiltrocandidatas", conTicket: true);
        var venta = await SembrarVentaDecididaAsync(t.C, t.C.IdClienteRi, sembrarAcumulados: false);
        var nota = await SembrarNotaDeCreditoAsync(t.C, venta.IdVenta, venta.IdFacturacion, idEspejoFactura: null);
        t.C.Wsfe.Solicitar = Responde(FecaeAprobado());

        var lote = await ProcesarLoteAsync(t.Cajero, t.C.IdPuntoVenta);

        Assert.Equal((1, 0, 0, 1), (lote.Emitidas, lote.Rechazadas, lote.Canceladas, lote.PendientesRestantes));
        Assert.Equal([venta.IdFacturacion, nota.IdDecision], lote.Resultados.Select(r => r.IdFacturacion));
        Assert.Equal(1, t.C.Wsfe.Contar("FECAESolicitar"));
        var decision = await LeerDecisionAsync(t.C, nota.IdDecision);
        Assert.Equal((1, "pendiente"), (decision.Intentos, decision.Estado));
        Assert.StartsWith(ServicioDeFacturacionDeVentas.NotaDeCreditoVentaNoAnulada + ":", decision.UltimoError);
        Assert.Null(decision.IdComprobanteFiscal);
    }
}
