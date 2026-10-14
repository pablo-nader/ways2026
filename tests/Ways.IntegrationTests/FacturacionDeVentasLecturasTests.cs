using System.Net;
using System.Text.Json;
using Ways.Application.Fiscal;
using Ways.Application.Ventas;

namespace Ways.IntegrationTests;

/// <summary>
/// Facturación por medio de pago, T6a: las dos lecturas de ventas informan el estado fiscal. <c>GET /api/ventas/{id}</c>
/// devuelve el mismo <c>Fiscal</c> que la emisión (con la factura si está aprobada, y el error reducido a su código) y
/// <c>GET /api/ventas/por-turno/{idTurno}</c> agrega solo el estado a cada fila. Ninguna de las dos decide, emite ni llama a ARCA.
///
/// Mutation-proof-tests: las cláusulas bajo prueba son que una venta sin fila de decisión y sin marca de decisión sea
/// <c>sin_decidir</c> (y que un espejo, que nunca se decide, no lo sea), que la lectura por id informe el código del error y no el
/// texto anotado, y que el listado del turno tome el estado de la fila de decisión de cada venta.
/// </summary>
public partial class FacturacionDeVentasEmisionTests
{
    private static async Task<ComprobanteEmitido> LeerVentaPorIdAsync(HttpClient cliente, int id)
    {
        var respuesta = await cliente.GetAsync($"/api/ventas/{id}");
        var cuerpo = await respuesta.Content.ReadAsStringAsync();
        Assert.True(respuesta.StatusCode == HttpStatusCode.OK, cuerpo);
        return JsonSerializer.Deserialize<ComprobanteEmitido>(cuerpo, OpcionesJson)!;
    }

    private static async Task<List<VentaDeTurnoListado>> LeerVentasDelTurnoAsync(HttpClient cliente, int idTurno)
    {
        var respuesta = await cliente.GetAsync($"/api/ventas/por-turno/{idTurno}");
        var cuerpo = await respuesta.Content.ReadAsStringAsync();
        Assert.True(respuesta.StatusCode == HttpStatusCode.OK, cuerpo);
        return JsonSerializer.Deserialize<List<VentaDeTurnoListado>>(cuerpo, OpcionesJson)!;
    }

    [Fact]
    public async Task LaLecturaPorIdDeUnaVentaAprobadaInformaLaFacturaConLosMismosDatosQueLaEmisionSinLlamarAArca()
    {
        var t = await PrepararCobroAsync("lecturaaprob");
        var emitido = await VenderEnLineaAsync(t.Cajero, VentaDeCobro(t));
        Assert.Equal(EstadosFiscalesDeVenta.Aprobado, emitido.Fiscal!.Estado);
        t.C.Wsfe.Reiniciar();

        var leido = await LeerVentaPorIdAsync(t.Cajero, emitido.Id);

        Assert.Equal(EstadosFiscalesDeVenta.Aprobado, leido.Fiscal!.Estado);
        Assert.Equal(emitido.Fiscal.IdFacturacion, leido.Fiscal.IdFacturacion);
        Assert.Equal(emitido.Fiscal.Motivo, leido.Fiscal.Motivo);
        Assert.NotNull(leido.Fiscal.Motivo);
        Assert.Equal("70123456789012", leido.Fiscal.Comprobante!.Cae);
        Assert.Equal(
            JsonSerializer.Serialize(emitido.Fiscal.Comprobante, OpcionesJson),
            JsonSerializer.Serialize(leido.Fiscal.Comprobante, OpcionesJson));
        Assert.Equal(emitido.Total, leido.Total);
        Assert.Empty(t.C.Wsfe.Operaciones);
        Assert.Equal(0, t.C.Wsaa.Solicitudes);
        Assert.Single(await LeerEspejosAsync(t.C));
    }

    [Fact]
    public async Task LaLecturaPorIdInformaSoloElCodigoDelErrorAnotadoEnUnaVentaRechazadaYEnUnaPendiente()
    {
        var t = await PrepararCobroAsync("lecturacodigo", conTicket: false);
        var rechazada = await VenderEnLineaAsync(t.Cajero, VentaDeCobro(t));
        var pendiente = await VenderEnLineaAsync(t.Cajero, VentaDeCobro(t));
        await EjecutarComoOwnerAsync(
            "UPDATE facturaciones_de_venta SET estado = 'rechazada', ultimo_error = $2 WHERE id_facturacion_venta = $1",
            rechazada.Fiscal!.IdFacturacion!.Value, "10015: Rechazado por el receptor.; 10016: otro texto de ARCA");
        await EjecutarComoOwnerAsync(
            "UPDATE facturaciones_de_venta SET ultimo_error = $2 WHERE id_facturacion_venta = $1",
            pendiente.Fiscal!.IdFacturacion!.Value, "HttpRequestException: Connection refused (10.0.0.5:443) clave=hunter2");

        var leidaRechazada = await LeerVentaPorIdAsync(t.Cajero, rechazada.Id);
        var leidaPendiente = await LeerVentaPorIdAsync(t.Cajero, pendiente.Id);

        Assert.Equal((EstadosFiscalesDeVenta.Rechazado, "10015"), (leidaRechazada.Fiscal!.Estado, leidaRechazada.Fiscal.Error));
        Assert.Null(leidaRechazada.Fiscal.Comprobante);
        Assert.Equal(
            (EstadosFiscalesDeVenta.Pendiente, EmisionEnLineaAlCobrar.ErrorInesperado),
            (leidaPendiente.Fiscal!.Estado, leidaPendiente.Fiscal.Error));
        Assert.Null(leidaPendiente.Fiscal.Comprobante);
        Assert.Empty(t.C.Wsfe.Operaciones);

        // Lo anotado no se toca: solo cambia lo que la lectura informa.
        Assert.StartsWith(
            "HttpRequestException: ", (await LeerDecisionAsync(t.C, pendiente.Fiscal.IdFacturacion!.Value)).UltimoError,
            StringComparison.Ordinal);
    }

    [Fact]
    public async Task LaLecturaPorIdDeUnaVentaPendienteSinErrorNoInformaErrorNiFactura()
    {
        var t = await PrepararCobroAsync("lecturapend", conTicket: false);
        var emitido = await VenderEnLineaAsync(t.Cajero, VentaDeCobro(t));

        var leido = await LeerVentaPorIdAsync(t.Cajero, emitido.Id);

        Assert.Equal(EstadosFiscalesDeVenta.Pendiente, leido.Fiscal!.Estado);
        Assert.Equal(emitido.Fiscal!.IdFacturacion, leido.Fiscal.IdFacturacion);
        Assert.Null(leido.Fiscal.Error);
        Assert.Null(leido.Fiscal.Comprobante);
    }

    [Fact]
    public async Task LaLecturaPorIdDeUnaVentaCuyaDecisionSeCanceloInformaCancelado()
    {
        var t = await PrepararCobroAsync("lecturacancel", conTicket: false);
        var emitido = await VenderEnLineaAsync(t.Cajero, VentaDeCobro(t));
        var anulacion = await t.C.Admin.PostAsync($"/api/ventas/{emitido.Id}/anulacion", null);
        Assert.Equal(HttpStatusCode.OK, anulacion.StatusCode);
        var lote = await ProcesarLoteAsync(t.Cajero, t.C.IdPuntoVenta);
        Assert.Equal(1, lote.Canceladas);

        var leido = await LeerVentaPorIdAsync(t.Cajero, emitido.Id);

        Assert.Equal(EstadosFiscalesDeVenta.Cancelado, leido.Fiscal!.Estado);
        Assert.Null(leido.Fiscal.Comprobante);
    }

    [Fact]
    public async Task LaLecturaPorIdDeUnaVentaDecididaSinFacturarEsNoCorrespondeYLaDeUnaSinMarcaDeDecisionEsSinDecidir()
    {
        var t = await PrepararCobroAsync("lecturasinfila");
        var decidida = await VenderEnLineaAsync(t.Cajero, VentaDeCobro(t, forzar: false));
        var sinMarca = await VenderEnLineaAsync(t.Cajero, VentaDeCobro(t, forzar: false));
        await EjecutarComoOwnerAsync(
            "UPDATE comprobantes_venta SET facturacion_decidida_en = NULL WHERE id_comprobante_venta = $1", sinMarca.Id);

        var leidaDecidida = await LeerVentaPorIdAsync(t.Cajero, decidida.Id);
        var leidaSinMarca = await LeerVentaPorIdAsync(t.Cajero, sinMarca.Id);

        Assert.Equal(EstadoFiscalDeVenta.NoCorresponde, leidaDecidida.Fiscal);
        Assert.Equal(EstadoFiscalDeVenta.SinDecidir, leidaSinMarca.Fiscal);
        Assert.Empty(t.C.Wsfe.Operaciones);
    }

    [Fact]
    public async Task LaLecturaPorIdDeLaFacturaEspejoNoCorrespondeAunqueNoTengaMarcaDeDecision()
    {
        var t = await PrepararCobroAsync("lecturaespejo");
        var emitido = await VenderEnLineaAsync(t.Cajero, VentaDeCobro(t));
        var espejo = Assert.Single(await LeerEspejosAsync(t.C));
        Assert.Equal(emitido.Id, espejo.IdVentaOrigen);

        var leido = await LeerVentaPorIdAsync(t.Cajero, espejo.Id);

        Assert.Equal(EstadoFiscalDeVenta.NoCorresponde, leido.Fiscal);
    }

    [Fact]
    public async Task LaLecturaPorIdDeUnaVentaDeOtroTenantEsUn404YNoFiltraElEstadoFiscal()
    {
        var t = await PrepararCobroAsync("lecturatenanta");
        var emitido = await VenderEnLineaAsync(t.Cajero, VentaDeCobro(t));
        var otro = await PrepararCobroAsync("lecturatenantb");

        var respuesta = await otro.Cajero.GetAsync($"/api/ventas/{emitido.Id}");

        Assert.Equal(HttpStatusCode.NotFound, respuesta.StatusCode);
        Assert.DoesNotContain("70123456789012", await respuesta.Content.ReadAsStringAsync(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task ElListadoDelTurnoInformaElEstadoFiscalDeCadaVentaSegunSuDecision()
    {
        var t = await PrepararCobroAsync("lecturaturno");
        var aprobada = await VenderEnLineaAsync(t.Cajero, VentaDeCobro(t));

        t.C.Wsfe.Solicitar = Responde(FecaeRechazado());
        var rechazada = await VenderEnLineaAsync(t.Cajero, VentaDeCobro(t));

        t.C.Wsfe.Solicitar = RespondeHttp500();
        var pendiente = await VenderEnLineaAsync(t.Cajero, VentaDeCobro(t));

        var noCorresponde = await VenderEnLineaAsync(t.Cajero, VentaDeCobro(t, forzar: false));
        var sinDecidir = await VenderEnLineaAsync(t.Cajero, VentaDeCobro(t, forzar: false));
        await EjecutarComoOwnerAsync(
            "UPDATE comprobantes_venta SET facturacion_decidida_en = NULL WHERE id_comprobante_venta = $1", sinDecidir.Id);
        t.C.Wsfe.Reiniciar();

        var filas = await LeerVentasDelTurnoAsync(t.Cajero, t.C.IdTurno);

        var esperados = new Dictionary<int, string>
        {
            [aprobada.Id] = EstadosFiscalesDeVenta.Aprobado,
            [rechazada.Id] = EstadosFiscalesDeVenta.Rechazado,
            [pendiente.Id] = EstadosFiscalesDeVenta.Pendiente,
            [noCorresponde.Id] = EstadosFiscalesDeVenta.NoCorresponde,
            [sinDecidir.Id] = EstadosFiscalesDeVenta.SinDecidir
        };
        // Las facturas espejo no son ventas del turno: el listado trae las cinco ventas y nada más.
        Assert.Equal(esperados.Keys.Order(), filas.Select(f => f.Id).Order());
        Assert.All(filas, f => Assert.Equal(esperados[f.Id], f.EstadoFiscal));
        Assert.Empty(t.C.Wsfe.Operaciones);
    }

    [Fact]
    public async Task ElListadoDelTurnoInformaCanceladoCuandoLaDecisionSeCancelo()
    {
        var t = await PrepararCobroAsync("lecturaturnocancel", conTicket: false);
        var emitido = await VenderEnLineaAsync(t.Cajero, VentaDeCobro(t));
        var anulacion = await t.C.Admin.PostAsync($"/api/ventas/{emitido.Id}/anulacion", null);
        Assert.Equal(HttpStatusCode.OK, anulacion.StatusCode);
        Assert.Equal(1, (await ProcesarLoteAsync(t.Cajero, t.C.IdPuntoVenta)).Canceladas);

        var fila = Assert.Single(await LeerVentasDelTurnoAsync(t.Cajero, t.C.IdTurno));

        Assert.Equal(emitido.Id, fila.Id);
        Assert.Equal(EstadosFiscalesDeVenta.Cancelado, fila.EstadoFiscal);
    }

    [Fact]
    public async Task ElListadoDelTurnoNoMuestraLasVentasNiLosEstadosFiscalesDeOtroTenant()
    {
        var t = await PrepararCobroAsync("lecturaturnoa");
        await VenderEnLineaAsync(t.Cajero, VentaDeCobro(t));
        var otro = await PrepararCobroAsync("lecturaturnob");

        Assert.Empty(await LeerVentasDelTurnoAsync(otro.Cajero, t.C.IdTurno));
    }
}
