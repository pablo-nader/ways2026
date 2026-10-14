using System.Globalization;
using System.Net;
using System.Text.Json;
using System.Text.RegularExpressions;
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

    /// <summary>Cláusula bajo prueba: <c>IgnoreQueryFilters(["BajaLogica"])</c> en las lecturas de catálogo de la factura. Se
    /// estampa <c>deleted_at</c> sobre el cliente referenciado (la baja por la API la rechaza la guarda de referencias).</summary>
    [Fact]
    public async Task LaLecturaPorIdDeUnaVentaAprobadaSigueInformandoLaFacturaAunqueSuClienteSeDioDeBaja()
    {
        var t = await PrepararCobroAsync("lecturaclientebaja");
        var venta = await SembrarVentaDecididaAsync(t.C, t.C.IdClienteRi, sembrarAcumulados: false);
        t.C.Wsfe.Solicitar = Responde(FecaeAprobado());
        Assert.Equal(1, (await ProcesarLoteAsync(t.Cajero, t.C.IdPuntoVenta)).Emitidas);
        var espejo = Assert.Single(await LeerEspejosAsync(t.C));
        Assert.Equal(t.C.IdClienteRi, espejo.IdCliente);
        await EjecutarComoOwnerAsync("UPDATE clientes SET deleted_at = now() WHERE id_cliente = $1", t.C.IdClienteRi);
        t.C.Wsfe.Reiniciar();

        var leido = await LeerVentaPorIdAsync(t.Cajero, venta.IdVenta);

        Assert.Equal(EstadosFiscalesDeVenta.Aprobado, leido.Fiscal!.Estado);
        Assert.Equal(venta.IdFacturacion, leido.Fiscal.IdFacturacion);
        Assert.Equal("70123456789012", leido.Fiscal.Comprobante!.Cae);
        Assert.Equal(espejo.Id, leido.Fiscal.Comprobante.IdComprobante);
        Assert.Empty(t.C.Wsfe.Operaciones);
    }

    /// <summary>Cláusula bajo prueba: el <c>catch</c> de <c>LeerEstadoDeVentaAsync</c> que informa el estado de la decisión sin
    /// la factura cuando no se pueden proyectar sus datos (acá, un CUIT de empresa no numérico).</summary>
    [Fact]
    public async Task LaLecturaPorIdDeUnaVentaAprobadaInformaElEstadoSinLaFacturaSiSusDatosNoSePuedenProyectar()
    {
        var t = await PrepararCobroAsync("lecturacuitroto");
        var emitido = await VenderEnLineaAsync(t.Cajero, VentaDeCobro(t));
        await EjecutarComoOwnerAsync("UPDATE empresas SET cuit = 'no-numerico' WHERE id_empresa = $1", t.C.IdEmpresa);
        t.C.Wsfe.Reiniciar();

        var leido = await LeerVentaPorIdAsync(t.Cajero, emitido.Id);

        Assert.Equal(emitido.Total, leido.Total);
        Assert.Equal(EstadosFiscalesDeVenta.Aprobado, leido.Fiscal!.Estado);
        Assert.Equal(emitido.Fiscal!.IdFacturacion, leido.Fiscal.IdFacturacion);
        Assert.Null(leido.Fiscal.Comprobante);
        Assert.Empty(t.C.Wsfe.Operaciones);
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

    // ---- T7b: datos legales de la factura impresa ----------------------------------------------------------

    private static List<(short Id, decimal Base, decimal Importe)> AlicuotasDelCuerpo(string cuerpo) =>
    [
        .. Regex.Matches(
                cuerpo,
                @"<(?:\w+:)?AlicIva>\s*<(?:\w+:)?Id>(\d+)</(?:\w+:)?Id>\s*<(?:\w+:)?BaseImp>([\d.]+)</(?:\w+:)?BaseImp>\s*<(?:\w+:)?Importe>([\d.]+)</",
                RegexOptions.None, TimeSpan.FromSeconds(5))
            .Select(m => (short.Parse(m.Groups[1].Value, CultureInfo.InvariantCulture),
                decimal.Parse(m.Groups[2].Value, CultureInfo.InvariantCulture),
                decimal.Parse(m.Groups[3].Value, CultureInfo.InvariantCulture)))
    ];

    private async Task CargarDatosLegalesDeLaEmpresaAsync(Ctx c)
    {
        await EjecutarComoOwnerAsync(
            "UPDATE empresas SET razon_social = 'Emisor Legal SA', nombre_fantasia = 'Legal Mercado', " +
            "ingresos_brutos = 'CM 901-555', inicio_actividades = '2012-04-02' WHERE id_empresa = $1", c.IdEmpresa);
        await EjecutarComoOwnerAsync(
            "UPDATE puntos_venta SET domicilio = 'Av. Siempre Viva 742' WHERE id_punto_venta = $1", c.IdPuntoVenta);
    }

    private async Task<string> NombreDeCondicionAsync(Ctx c, string codigo) => Assert.Single(await ConsultarAsync(
        c, "SELECT nombre FROM condiciones_fiscales WHERE codigo = $1", l => l.GetString(0), codigo));

    /// <summary>Cláusulas bajo prueba: el emisor sale de empresa y punto de venta (con el nombre de la condición), el receptor
    /// con el documento tal como viajó a ARCA y los totales se recomponen del snapshot de las líneas del espejo: las dos
    /// alícuotas coinciden con las que el espía de WSFE recibió.</summary>
    [Fact]
    public async Task LaLecturaPorIdDeUnaVentaAprobadaTraeEmisorReceptorYTotalesIgualesAlPedidoQueViajoAArca()
    {
        var t = await PrepararCobroAsync("lecturalegal");
        await CargarDatosLegalesDeLaEmpresaAsync(t.C);
        await EjecutarComoOwnerAsync(
            "UPDATE clientes SET apellido = 'Pérez', domicilio = 'Calle Falsa 123' WHERE id_cliente = $1", t.C.IdClienteRi);
        var venta = await SembrarVentaDecididaAsync(t.C, t.C.IdClienteRi, sembrarAcumulados: false);
        t.C.Wsfe.Solicitar = Responde(FecaeAprobado());
        Assert.Equal(1, (await ProcesarLoteAsync(t.Cajero, t.C.IdPuntoVenta)).Emitidas);
        var cuerpo = Assert.Single(t.C.Wsfe.CuerposDeSolicitud);
        t.C.Wsfe.Reiniciar();

        var factura = (await LeerVentaPorIdAsync(t.Cajero, venta.IdVenta)).Fiscal!.Comprobante!;

        Assert.Equal(
            new EmisorDeFacturaFiscal(
                "Emisor Legal SA", "Legal Mercado", "20111111112", await NombreDeCondicionAsync(t.C, "RI"), "CM 901-555",
                new DateOnly(2012, 4, 2), "Av. Siempre Viva 742"),
            factura.Emisor);
        var receptor = factura.Receptor;
        Assert.Equal(
            ("CUIT", (short)80, 20222222223L, await NombreDeCondicionAsync(t.C, "RI"), "Calle Falsa 123"),
            (receptor.TipoDocumento, receptor.CodigoTipoDocumento, receptor.NumeroDocumento, receptor.CondicionFiscal, receptor.Domicilio));
        Assert.EndsWith(" Pérez", receptor.Nombre, StringComparison.Ordinal);
        Assert.Equal(short.Parse(CampoDelCuerpo(cuerpo, "DocTipo"), CultureInfo.InvariantCulture), receptor.CodigoTipoDocumento);
        Assert.Equal(long.Parse(CampoDelCuerpo(cuerpo, "DocNro"), CultureInfo.InvariantCulture), receptor.NumeroDocumento);

        var totales = factura.Totales;
        var enviadas = AlicuotasDelCuerpo(cuerpo);
        Assert.Equal(2, enviadas.Count);
        Assert.Equal(
            [(21m, 100m, 21m), (10.5m, 99.55m, 10.45m)],
            totales.Alicuotas.Select(a => (a.Porcentaje, a.Neto, a.Iva)));
        Assert.Equal(enviadas.Select(a => (a.Base, a.Importe)), totales.Alicuotas.Select(a => (a.Neto, a.Iva)));
        Assert.Equal(
            (decimal.Parse(CampoDelCuerpo(cuerpo, "ImpNeto"), CultureInfo.InvariantCulture),
                decimal.Parse(CampoDelCuerpo(cuerpo, "ImpIVA"), CultureInfo.InvariantCulture),
                decimal.Parse(CampoDelCuerpo(cuerpo, "ImpOpEx"), CultureInfo.InvariantCulture),
                decimal.Parse(CampoDelCuerpo(cuerpo, "ImpTotConc"), CultureInfo.InvariantCulture),
                decimal.Parse(CampoDelCuerpo(cuerpo, "ImpTrib"), CultureInfo.InvariantCulture),
                decimal.Parse(CampoDelCuerpo(cuerpo, "ImpTotal"), CultureInfo.InvariantCulture)),
            (totales.ImpNeto, totales.ImpIva, totales.ImpOpEx, totales.ImpTotConc, totales.ImpTrib, totales.ImpTotal));
        Assert.Equal(231m, totales.ImpTotal);
        Assert.Empty(t.C.Wsfe.Operaciones);
    }

    /// <summary>Cláusula bajo prueba: el nombre a imprimir es la razón social si hay y, si no, nombre y apellido.</summary>
    [Fact]
    public async Task ElReceptorImprimeLaRazonSocialSiLaTieneYSiNoNombreYApellido()
    {
        var t = await PrepararCobroAsync("lecturanombre");
        await EjecutarComoOwnerAsync(
            "UPDATE clientes SET nombre = 'Juana', apellido = 'Gómez' WHERE id_cliente = $1", t.C.IdClienteRi);
        var venta = await SembrarVentaDecididaAsync(t.C, t.C.IdClienteRi, sembrarAcumulados: false);
        t.C.Wsfe.Solicitar = Responde(FecaeAprobado());
        Assert.Equal(1, (await ProcesarLoteAsync(t.Cajero, t.C.IdPuntoVenta)).Emitidas);

        var sinRazonSocial = (await LeerVentaPorIdAsync(t.Cajero, venta.IdVenta)).Fiscal!.Comprobante!.Receptor;
        await EjecutarComoOwnerAsync(
            "UPDATE clientes SET razon_social = 'Gómez Hnos. SRL' WHERE id_cliente = $1", t.C.IdClienteRi);
        var conRazonSocial = (await LeerVentaPorIdAsync(t.Cajero, venta.IdVenta)).Fiscal!.Comprobante!.Receptor;

        Assert.Equal("Juana Gómez", sinRazonSocial.Nombre);
        Assert.Equal("Gómez Hnos. SRL", conRazonSocial.Nombre);
    }

    [Fact]
    public async Task ElReceptorConsumidorFinalSeInformaSinIdentificarConDocumentoCero()
    {
        var t = await PrepararCobroAsync("lecturacf");
        var venta = await SembrarVentaDecididaAsync(t.C, t.C.IdClienteCf, sembrarAcumulados: false);
        t.C.Wsfe.Solicitar = Responde(FecaeAprobado());
        Assert.Equal(1, (await ProcesarLoteAsync(t.Cajero, t.C.IdPuntoVenta)).Emitidas);

        var receptor = (await LeerVentaPorIdAsync(t.Cajero, venta.IdVenta)).Fiscal!.Comprobante!.Receptor;

        Assert.Equal(
            ("Sin identificar", (short)99, 0L, await NombreDeCondicionAsync(t.C, "CF")),
            (receptor.TipoDocumento, receptor.CodigoTipoDocumento, receptor.NumeroDocumento, receptor.CondicionFiscal));
    }

    /// <summary>Con una línea exenta el desglose por alícuota solo trae la gravada y el importe exento va a ImpOpEx, igual que lo
    /// que viajó a ARCA.</summary>
    [Fact]
    public async Task UnaLineaExentaVaAImpOpExYNoApareceEnLasAlicuotasDeLaFactura()
    {
        var t = await PrepararCobroAsync("lecturaexento");
        var venta = await SembrarVentaDecididaAsync(t.C, t.C.IdClienteCf, sembrarAcumulados: false);
        await EjecutarComoOwnerAsync(
            "UPDATE items_comprobante_venta SET porcentaje_iva = 0, id_alicuota_iva = " +
            "(SELECT id_alicuota_iva FROM alicuotas_iva WHERE nombre = 'Exento') " +
            "WHERE id_comprobante_venta = $1 AND orden = 2", venta.IdVenta);
        t.C.Wsfe.Solicitar = Responde(FecaeAprobado());
        Assert.Equal(1, (await ProcesarLoteAsync(t.Cajero, t.C.IdPuntoVenta)).Emitidas);
        var cuerpo = Assert.Single(t.C.Wsfe.CuerposDeSolicitud);

        var totales = (await LeerVentaPorIdAsync(t.Cajero, venta.IdVenta)).Fiscal!.Comprobante!.Totales;

        Assert.Equal("110.00", CampoDelCuerpo(cuerpo, "ImpOpEx"));
        Assert.Equal((100m, 21m, 110m, 0m, 231m), (totales.ImpNeto, totales.ImpIva, totales.ImpOpEx, totales.ImpTotConc, totales.ImpTotal));
        var gravada = Assert.Single(totales.Alicuotas);
        Assert.Equal((21m, 100m, 21m), (gravada.Porcentaje, gravada.Neto, gravada.Iva));
    }

    /// <summary>Cláusula bajo prueba: los totales salen del snapshot de las líneas del espejo, no del catálogo ni del precio actual.
    /// Cambiar el porcentaje de la alícuota del catálogo no mueve lo informado.</summary>
    [Fact]
    public async Task LosTotalesDeLaFacturaSalenDelSnapshotDeLasLineasAunqueElCatalogoDeAlicuotasCambie()
    {
        var t = await PrepararCobroAsync("lecturasnapshot");
        var venta = await SembrarVentaDecididaAsync(t.C, t.C.IdClienteCf, sembrarAcumulados: false);
        t.C.Wsfe.Solicitar = Responde(FecaeAprobado());
        Assert.Equal(1, (await ProcesarLoteAsync(t.Cajero, t.C.IdPuntoVenta)).Emitidas);
        var antes = (await LeerVentaPorIdAsync(t.Cajero, venta.IdVenta)).Fiscal!.Comprobante!.Totales;
        await EjecutarComoOwnerAsync("UPDATE alicuotas_iva SET porcentaje = 27 WHERE codigo_afip = 5");

        try
        {
            var despues = (await LeerVentaPorIdAsync(t.Cajero, venta.IdVenta)).Fiscal!.Comprobante!.Totales;

            Assert.Equal(JsonSerializer.Serialize(antes, OpcionesJson), JsonSerializer.Serialize(despues, OpcionesJson));
            Assert.Equal(21m, despues.Alicuotas[0].Porcentaje);
        }
        finally
        {
            await EjecutarComoOwnerAsync("UPDATE alicuotas_iva SET porcentaje = 21 WHERE codigo_afip = 5");
        }
    }

    [Fact]
    public async Task LaFacturaSigueInformandoSusTotalesAunqueLaAlicuotaDeLaLineaSeDioDeBaja()
    {
        var t = await PrepararCobroAsync("lecturaalicuotabaja");
        var venta = await SembrarVentaDecididaAsync(t.C, t.C.IdClienteCf, sembrarAcumulados: false);
        t.C.Wsfe.Solicitar = Responde(FecaeAprobado());
        Assert.Equal(1, (await ProcesarLoteAsync(t.Cajero, t.C.IdPuntoVenta)).Emitidas);
        await EjecutarComoOwnerAsync("UPDATE alicuotas_iva SET deleted_at = now() WHERE codigo_afip = 4");

        try
        {
            var totales = (await LeerVentaPorIdAsync(t.Cajero, venta.IdVenta)).Fiscal!.Comprobante?.Totales;

            Assert.NotNull(totales);
            Assert.Equal(2, totales.Alicuotas.Count);
        }
        finally
        {
            await EjecutarComoOwnerAsync("UPDATE alicuotas_iva SET deleted_at = NULL WHERE codigo_afip = 4");
        }
    }
}
