using System.Net;
using System.Text.Json;
using Ways.Application.MercadoPago;
using static Ways.IntegrationTests.ApoyoDeMercadoPago;

namespace Ways.IntegrationTests;

/// <summary>
/// El cobro con la terminal Point (E4) contra Postgres real y el manejador falso de Mercado Pago de
/// <see cref="MercadoPagoCobrosTests"/>: la orden <c>type: "point"</c> con la terminal de la caja, el 409 de terminal ocupada, el mapeo
/// de los estados de una orden Point (en la terminal, mirar la terminal, rechazada, procesada), la cancelación que solo vale mientras la
/// orden está <c>created</c> y el cobro <c>fallido</c> que libera el número para otro intento.
/// </summary>
public partial class MercadoPagoCobrosTests
{
    private const string TerminalDePrueba = "PAX_A910__SMARTPOS1234567890";

    private async Task AsignarTerminalAsync(Escenario e, string? idTerminal)
    {
        await EjecutarAsync(
            idTerminal is null
                ? $"UPDATE cajas_mercadopago SET id_terminal_point = NULL, fecha_terminal = NULL WHERE id_punto_venta = {e.IdPuntoVenta}"
                : $"UPDATE cajas_mercadopago SET id_terminal_point = '{idTerminal}', fecha_terminal = now() WHERE id_punto_venta = {e.IdPuntoVenta}");
    }

    private async Task<(Escenario E, int IdMedioPoint)> PrepararPointAsync(string nombre, bool conTerminal = true)
    {
        var e = await PrepararAsync(nombre);
        var idMedioPoint = await CrearMedioAsync(e.Ctx, "Point Mercado Pago", "Electronico", "mercadopago_point");
        if (conTerminal)
        {
            await AsignarTerminalAsync(e, TerminalDePrueba);
        }

        return (e, idMedioPoint);
    }

    private static async Task<CobroDeMercadoPago> CrearPointOkAsync(Escenario e, int idMedioPoint, long numero, decimal importe) =>
        await LeerAsync<CobroDeMercadoPago>(await CrearAsync(e, numero, importe, idMedioPoint));

    private static string IdDeLaOrden(Escenario e) => e.Ordenes.OrdenPorClave.Values.Single();

    // Clausula: la orden Point lleva type point, sin total_amount, la terminal de la caja, print_on_terminal no_ticket, la vigencia y el pago;
    // no lleva ni caja QR ni items; la fila guarda tipo point, la terminal pedida y ningun qr_data.
    [Fact]
    public async Task CrearUnCobroPointMandaLaOrdenConLaTerminalDeLaCajaYGuardaLaFilaSinQr()
    {
        var (e, medio) = await PrepararPointAsync("mp-point-alta");

        var cobro = await CrearPointOkAsync(e, medio, 6001, 250.5m);

        Assert.Equal("pendiente", cobro.Estado);
        Assert.Equal("point", cobro.Tipo);
        Assert.Equal(TerminalDePrueba, cobro.IdTerminal);
        Assert.Null(cobro.QrData);
        Assert.False(cobro.VerificandoPago);
        Assert.False(cobro.PagoInformadoSinDatos);
        Assert.False(cobro.EnTerminal);
        Assert.False(cobro.RevisarTerminal);

        var pedido = Assert.Single(Creaciones(e));
        using var cuerpo = JsonDocument.Parse(pedido.Cuerpo);
        var raiz = cuerpo.RootElement;
        Assert.Equal("point", raiz.GetProperty("type").GetString());
        Assert.Equal($"W{e.IdTenant}P{e.IdPuntoVenta}N6001I1", raiz.GetProperty("external_reference").GetString());
        Assert.False(raiz.TryGetProperty("total_amount", out _));
        Assert.Equal("PT10M", raiz.GetProperty("expiration_time").GetString());
        var point = raiz.GetProperty("config").GetProperty("point");
        Assert.Equal(TerminalDePrueba, point.GetProperty("terminal_id").GetString());
        Assert.Equal("no_ticket", point.GetProperty("print_on_terminal").GetString());
        Assert.False(raiz.GetProperty("config").TryGetProperty("qr", out _));
        Assert.False(raiz.TryGetProperty("items", out _));
        var pago = Assert.Single(raiz.GetProperty("transactions").GetProperty("payments").EnumerateArray());
        Assert.Equal("250.50", pago.GetProperty("amount").GetString());
        Assert.StartsWith("ways-cobro-", pedido.ClaveDeIdempotencia);

        var fila = Assert.Single(await FilasAsync(e.IdPuntoVenta));
        Assert.Equal("point", fila["tipo"]);
        Assert.Equal(TerminalDePrueba, fila["id_terminal_point"]);
        Assert.Null(fila["qr_data"]);
        Assert.Null(fila["situacion_terminal"]);
        Assert.Equal(e.ExternalIdCaja, fila["external_pos_id"]);
    }

    // Clausula: el cobro QR no lleva terminal ni cambia por la existencia del Point.
    [Fact]
    public async Task UnCobroQrDeUnaCajaConTerminalSigueSiendoQrSinTerminal()
    {
        var (e, _) = await PrepararPointAsync("mp-point-qr-intacto");

        var cobro = await CrearOkAsync(e, 6002, 100m);

        Assert.Equal("qr", cobro.Tipo);
        Assert.Null(cobro.IdTerminal);
        Assert.NotNull(cobro.QrData);
        using var cuerpo = JsonDocument.Parse(Assert.Single(Creaciones(e)).Cuerpo);
        Assert.Equal("qr", cuerpo.RootElement.GetProperty("type").GetString());
        Assert.Null(Assert.Single(await FilasAsync(e.IdPuntoVenta))["id_terminal_point"]);
    }

    // Clausula: un medio Point exige que la caja tenga terminal asignada; sin ella 409 y no se reserva ni se llama a Mercado Pago.
    [Fact]
    public async Task UnMedioPointSinTerminalEnLaCajaEsUn409SinCrearNada()
    {
        var (e, medio) = await PrepararPointAsync("mp-point-sin-terminal", conTerminal: false);

        await AssertCodigoAsync(await CrearAsync(e, 6003, 100m, medio), HttpStatusCode.Conflict, "mercadopago_caja_sin_terminal");

        Assert.Empty(Creaciones(e));
        Assert.Empty(await FilasAsync(e.IdPuntoVenta));
    }

    // Clausula: el 409 already_queued_order_for_terminal de Mercado Pago rechaza la creacion: 409 mercadopago_terminal_ocupada, la fila
    // queda cancelada (sin orden) y libera el numero; liberada la terminal, un crear nuevo abre otro intento y otra orden.
    [Fact]
    public async Task UnaTerminalConOtraOrdenEsperandoEsUn409TerminalOcupadaCancelaLaFilaYElSiguienteCrearAbreOtroIntento()
    {
        var (e, medio) = await PrepararPointAsync("mp-point-ocupada");
        var ocupada = true;
        e.Ordenes.Antes = pedido => ocupada && pedido.Metodo == HttpMethod.Post && pedido.Uri.AbsolutePath == "/v1/orders"
            ? Json(HttpStatusCode.Conflict, """{"errors":[{"code":"already_queued_order_for_terminal","message":"terminal ocupada"}]}""")
            : null;

        await AssertCodigoAsync(await CrearAsync(e, 6004, 100m, medio), HttpStatusCode.Conflict, "mercadopago_terminal_ocupada");
        var rechazada = Assert.Single(await FilasAsync(e.IdPuntoVenta));
        Assert.Equal("cancelado", rechazada["estado"]);
        Assert.Null(rechazada["id_orden_mercadopago"]);
        Assert.Null(rechazada["situacion_terminal"]);

        ocupada = false;
        var cobro = await CrearPointOkAsync(e, medio, 6004, 100m);

        Assert.Equal("pendiente", cobro.Estado);
        var filas = await FilasAsync(e.IdPuntoVenta);
        Assert.Equal(["cancelado", "pendiente"], filas.Select(f => (string)f["estado"]!));
        Assert.Equal([1, 2], filas.Select(f => (int)f["intento"]!));
        Assert.Equal(2, Creaciones(e).Select(p => p.ClaveDeIdempotencia).Distinct().Count());
    }

    // Clausula: la fila cancelada por terminal ocupada se devuelve tal cual al consultarla y no abre ordenes.
    [Fact]
    public async Task UnCobroCanceladoPorTerminalOcupadaSeDevuelveTalCualAlConsultarloYNoAbreOrdenes()
    {
        var (e, medio) = await PrepararPointAsync("mp-point-ocupada-consulta");
        e.Ordenes.Antes = pedido => pedido.Metodo == HttpMethod.Post && pedido.Uri.AbsolutePath == "/v1/orders"
            ? Json(HttpStatusCode.Conflict, """{"errors":[{"code":"already_queued_order_for_terminal","message":"terminal ocupada"}]}""")
            : null;
        await CrearAsync(e, 6008, 100m, medio);
        var id = (int)Assert.Single(await FilasAsync(e.IdPuntoVenta))["id_cobro_mercadopago"]!;

        var consultado = await LeerAsync<CobroDeMercadoPago>(await ConsultarAsync(e, id));

        Assert.Equal("cancelado", consultado.Estado);
        Assert.Single(Creaciones(e));
    }

    // Clausula: un 409 con otro codigo (o un QR) no es terminal ocupada.
    [Fact]
    public async Task UnConflictoDeMercadoPagoConOtroCodigoNoEsTerminalOcupada()
    {
        var (e, medio) = await PrepararPointAsync("mp-point-409-otro");
        e.Ordenes.Antes = pedido => pedido.Metodo == HttpMethod.Post && pedido.Uri.AbsolutePath == "/v1/orders"
            ? Json(HttpStatusCode.Conflict, """{"errors":[{"code":"otro_conflicto","message":"x"}]}""")
            : null;

        var respuesta = await CrearAsync(e, 6005, 100m, medio);

        Assert.Equal(HttpStatusCode.BadGateway, respuesta.StatusCode);
    }

    // Clausula: una orden Point no necesita type_response; una respuesta sin id sigue siendo 502.
    [Fact]
    public async Task UnaOrdenPointSinTypeResponseEsValidaYSinIdEsUn502()
    {
        var (e, medio) = await PrepararPointAsync("mp-point-sin-qr");
        e.Ordenes.SinQr = true;

        var cobro = await CrearPointOkAsync(e, medio, 6006, 100m);
        Assert.Equal("pendiente", cobro.Estado);
        Assert.False(cobro.VerificandoPago);

        e.Ordenes.Antes = pedido => pedido.Metodo == HttpMethod.Post && pedido.Uri.AbsolutePath == "/v1/orders"
            ? Json(HttpStatusCode.Created, """{"status": "created"}""")
            : null;
        var sinId = await CrearAsync(e, 6007, 100m, medio);
        Assert.Equal(HttpStatusCode.BadGateway, sinId.StatusCode);
    }

    // Clausula: at_terminal -> pendiente con enTerminal; action_required -> pendiente con revisarTerminal; created los baja.
    [Theory]
    [InlineData("created", false, false, null)]
    [InlineData("at_terminal", true, false, "en_terminal")]
    [InlineData("action_required", false, true, "revisar_terminal")]
    public async Task LaOrdenPointPendienteInformaDondeEstaEnLaTerminal(string estadoDeMercadoPago, bool enTerminal, bool revisar, string? situacion)
    {
        var (e, medio) = await PrepararPointAsync("mp-point-situacion-" + estadoDeMercadoPago.Replace("_", string.Empty));
        var cobro = await CrearPointOkAsync(e, medio, 6010, 100m);
        e.Ordenes.Estados[IdDeLaOrden(e)] = estadoDeMercadoPago;

        var consultado = await LeerAsync<CobroDeMercadoPago>(await ConsultarAsync(e, cobro.IdCobro));

        Assert.Equal("pendiente", consultado.Estado);
        Assert.Equal(enTerminal, consultado.EnTerminal);
        Assert.Equal(revisar, consultado.RevisarTerminal);
        Assert.False(consultado.VerificandoPago);
        Assert.Null(consultado.QrData);
        Assert.Equal(situacion, Assert.Single(await FilasAsync(e.IdPuntoVenta))["situacion_terminal"]);
    }

    // Clausula: la situacion en la terminal se limpia al volver a created y no sobrevive a un estado final.
    [Theory]
    [InlineData("canceled", "cancelado")]
    [InlineData("expired", "expirado")]
    [InlineData("refunded", "devuelto")]
    [InlineData("failed", "fallido")]
    [InlineData("processed", "aprobado")]
    public async Task UnEstadoFinalDeLaOrdenPointCierraElCobroYLimpiaLaSituacionDeLaTerminal(string estadoDeMercadoPago, string estadoEsperado)
    {
        var (e, medio) = await PrepararPointAsync("mp-point-final-" + estadoDeMercadoPago);
        var cobro = await CrearPointOkAsync(e, medio, 6011, 100m);
        var idOrden = IdDeLaOrden(e);
        e.Ordenes.Estados[idOrden] = "at_terminal";
        Assert.True((await LeerAsync<CobroDeMercadoPago>(await ConsultarAsync(e, cobro.IdCobro))).EnTerminal);

        e.Reloj.Ahora = e.Reloj.Ahora.AddSeconds(3);
        e.Ordenes.Estados[idOrden] = estadoDeMercadoPago;
        var consultado = await LeerAsync<CobroDeMercadoPago>(await ConsultarAsync(e, cobro.IdCobro));

        Assert.Equal(estadoEsperado, consultado.Estado);
        Assert.False(consultado.EnTerminal);
        Assert.False(consultado.RevisarTerminal);
        Assert.Null(consultado.QrData);
        var fila = Assert.Single(await FilasAsync(e.IdPuntoVenta));
        Assert.Equal(estadoEsperado, fila["estado"]);
        Assert.Null(fila["situacion_terminal"]);
    }

    // Clausula: un cobro Point procesado se aprueba con las reglas de E3 (importe e id legibles) y no es "verificando" por no tener QR.
    [Fact]
    public async Task UnCobroPointProcesadoSeApruebaConElPagoYSinQr()
    {
        var (e, medio) = await PrepararPointAsync("mp-point-aprobado");
        var cobro = await CrearPointOkAsync(e, medio, 6012, 300m);
        e.Ordenes.Estados[IdDeLaOrden(e)] = "processed";

        var aprobado = await LeerAsync<CobroDeMercadoPago>(await ConsultarAsync(e, cobro.IdCobro));

        Assert.Equal("aprobado", aprobado.Estado);
        Assert.Equal(300m, aprobado.ImportePagado);
        Assert.False(aprobado.ImporteDistinto);
        var fila = Assert.Single(await FilasAsync(e.IdPuntoVenta));
        Assert.Equal($"PAY{IdDeLaOrden(e)}", fila["id_pago_mercadopago"]);
    }

    // Clausula: failed es una regla de Point; una orden QR en failed sigue como estaba.
    [Fact]
    public async Task UnaOrdenFailedDeUnCobroQrNoLoCierra()
    {
        var e = await PrepararAsync("mp-point-failed-qr");
        var cobro = await CrearOkAsync(e, 6013, 100m);
        e.Ordenes.Estados[IdDeLaOrden(e)] = "failed";

        var consultado = await LeerAsync<CobroDeMercadoPago>(await ConsultarAsync(e, cobro.IdCobro));

        Assert.Equal("pendiente", consultado.Estado);
    }

    // Clausula: fallido no es vivo: libera el numero, el mismo numero abre otro intento (otra referencia y otra clave) y el medio se edita.
    [Fact]
    public async Task UnCobroPointFallidoLiberaElNumeroYElSiguienteIntentoCreaOtraOrden()
    {
        var (e, medio) = await PrepararPointAsync("mp-point-fallido-reintento");
        var primero = await CrearPointOkAsync(e, medio, 6014, 100m);
        e.Ordenes.Estados[IdDeLaOrden(e)] = "failed";
        Assert.Equal("fallido", (await LeerAsync<CobroDeMercadoPago>(await ConsultarAsync(e, primero.IdCobro))).Estado);

        var segundo = await CrearPointOkAsync(e, medio, 6014, 100m);

        Assert.NotEqual(primero.IdCobro, segundo.IdCobro);
        Assert.Equal("pendiente", segundo.Estado);
        var filas = await FilasAsync(e.IdPuntoVenta);
        Assert.Equal(["fallido", "pendiente"], filas.Select(f => (string)f["estado"]!));
        Assert.Equal([1, 2], filas.Select(f => (int)f["intento"]!));
        Assert.Equal($"W{e.IdTenant}P{e.IdPuntoVenta}N6014I2", filas[1]["external_reference"]);
        Assert.Equal(2, Creaciones(e).Select(p => p.ClaveDeIdempotencia).Distinct().Count());
    }

    // Clausula: un cobro fallido se sigue leyendo (con el limite de una consulta cada 2 s) pero cancelarlo no llama a Mercado Pago.
    [Fact]
    public async Task ConsultarUnCobroFallidoRespetaElLimiteDeConsultasYCancelarloNoLlamaAMercadoPago()
    {
        var (e, medio) = await PrepararPointAsync("mp-point-fallido-final");
        var cobro = await CrearPointOkAsync(e, medio, 6015, 100m);
        e.Ordenes.Estados[IdDeLaOrden(e)] = "failed";
        await ConsultarAsync(e, cobro.IdCobro);
        var lecturas = Lecturas(e).Count;

        var consultado = await LeerAsync<CobroDeMercadoPago>(await ConsultarAsync(e, cobro.IdCobro));
        var cancelado = await LeerAsync<CobroDeMercadoPago>(await CancelarAsync(e, cobro.IdCobro));

        Assert.Equal("fallido", consultado.Estado);
        Assert.Equal("fallido", cancelado.Estado);
        Assert.Equal(lecturas, Lecturas(e).Count);
        Assert.Empty(Cancelaciones(e));
    }

    private async Task<(Escenario E, int IdMedio, CobroDeMercadoPago Fallido, string IdOrden)> PrepararFallidoAsync(string nombre, long numero)
    {
        var (e, medio) = await PrepararPointAsync(nombre);
        var cobro = await CrearPointOkAsync(e, medio, numero, 100m);
        var idOrden = IdDeLaOrden(e);
        e.Ordenes.Estados[idOrden] = "failed";
        var fallido = await LeerAsync<CobroDeMercadoPago>(await ConsultarAsync(e, cobro.IdCobro));
        Assert.Equal("fallido", fallido.Estado);
        return (e, medio, fallido, idOrden);
    }

    // Clausula: failed no cierra la vigilancia. Si Mercado Pago informa despues un pago legible, el cobro fallido (cuyo numero pudo
    // reutilizarse) pasa a sin_venta con fecha_apartado, nunca a aprobado, y aparece en el listado de la conciliacion.
    [Fact]
    public async Task UnPointFallidoSeSigueLeyendoYUnPagoPosteriorLoDejaSinVentaYListadoParaLaConciliacion()
    {
        var (e, medio, fallido, idOrden) = await PrepararFallidoAsync("mp-point-fallido-pago-tardio", 6030);
        var reintento = await CrearPointOkAsync(e, medio, 6030, 100m);
        e.Ordenes.Estados[idOrden] = "processed";
        e.Reloj.Ahora = e.Reloj.Ahora.AddSeconds(3);

        var tardio = await LeerAsync<CobroDeMercadoPago>(await ConsultarAsync(e, fallido.IdCobro));

        Assert.Equal("sin_venta", tardio.Estado);
        Assert.Equal(100m, tardio.ImportePagado);
        var filas = await FilasAsync(e.IdPuntoVenta);
        var fila = filas.Single(f => Convert.ToInt32(f["id_cobro_mercadopago"]) == fallido.IdCobro);
        Assert.NotNull(fila["fecha_apartado"]);
        Assert.Equal($"PAY{idOrden}", fila["id_pago_mercadopago"]);
        Assert.Equal("pendiente", filas.Single(f => Convert.ToInt32(f["id_cobro_mercadopago"]) == reintento.IdCobro)["estado"]);
        var lista = await LeerAsync<List<CobroSinVentaListado>>(await e.Ctx.Admin.GetAsync($"{RutaSinVenta}?idEmpresa={e.Ctx.IdEmpresa}"));
        Assert.Equal(fallido.IdCobro, Assert.Single(lista).IdCobro);
    }

    // Clausula: lo mismo por el camino del webhook (el POS ya no consulta un cobro fallido).
    [Fact]
    public async Task UnAvisoFirmadoDeUnPointFallidoConPagoPosteriorLoDejaSinVenta()
    {
        var (e, _, fallido, idOrden) = await PrepararFallidoAsync("mp-point-fallido-aviso", 6031);
        e.Ordenes.Estados[idOrden] = "processed";
        e.Reloj.Ahora = e.Reloj.Ahora.AddSeconds(3);

        var respuesta = await AvisarAsync(ClienteDelWebhook(e), idOrden, FirmaValida(e, idOrden));

        Assert.Equal(HttpStatusCode.OK, respuesta.StatusCode);
        var fila = Assert.Single(await FilasAsync(e.IdPuntoVenta));
        Assert.Equal("sin_venta", fila["estado"]);
        Assert.NotNull(fila["fecha_apartado"]);
        var lista = await LeerAsync<List<CobroSinVentaListado>>(await e.Ctx.Admin.GetAsync($"{RutaSinVenta}?idEmpresa={e.Ctx.IdEmpresa}"));
        Assert.Equal(fallido.IdCobro, Assert.Single(lista).IdCobro);
    }

    // Clausula: un fallido que Mercado Pago despues informa canceled o expired se cierra como cancelado o expirado (deja de vigilarse).
    [Theory]
    [InlineData("canceled", "cancelado")]
    [InlineData("expired", "expirado")]
    public async Task UnPointFallidoQueMercadoPagoCierraDespuesQuedaCanceladoOExpirado(string estadoDeMercadoPago, string esperado)
    {
        var (e, _, fallido, idOrden) = await PrepararFallidoAsync("mp-point-fallido-cierra-" + estadoDeMercadoPago, 6032);
        e.Ordenes.Estados[idOrden] = estadoDeMercadoPago;
        e.Reloj.Ahora = e.Reloj.Ahora.AddSeconds(3);

        var consultado = await LeerAsync<CobroDeMercadoPago>(await ConsultarAsync(e, fallido.IdCobro));

        Assert.Equal(esperado, consultado.Estado);
    }

    // Clausula: pasado el plazo de la orden mas el margen de abandono el fallido ya no se lee: sigue fallido aunque Mercado Pago diga otra cosa.
    [Fact]
    public async Task UnPointFallidoYaNoSeLeePasadoElPlazoDeLaOrdenMasElMargen()
    {
        var (e, _, fallido, idOrden) = await PrepararFallidoAsync("mp-point-fallido-fuera-de-plazo", 6033);
        var lecturas = Lecturas(e).Count;
        e.Ordenes.Estados[idOrden] = "processed";
        e.Reloj.Ahora = e.Reloj.Ahora.AddMinutes(16);

        var consultado = await LeerAsync<CobroDeMercadoPago>(await ConsultarAsync(e, fallido.IdCobro));

        Assert.Equal("fallido", consultado.Estado);
        Assert.Equal(lecturas, Lecturas(e).Count);
    }

    private const string TerminalOcupadaJson = """{"errors":[{"code":"already_queued_order_for_terminal","message":"terminal ocupada"}]}""";

    private static bool EsCreacion(PedidoRegistrado pedido) => pedido.Metodo == HttpMethod.Post && pedido.Uri.AbsolutePath == "/v1/orders";

    // Clausula: el 409 de terminal ocupada cancela la fila SOLO en el primer envio (el de la llamada que la reservo). En un reenvio de una
    // fila que quedo creando tras una respuesta perdida pudo haber una orden creada: la fila sigue creando, retiene el numero y las
    // bandas de tiempo de E3 la liberan.
    [Fact]
    public async Task UnReenvioDeUnaFilaCreandoQueRecibeTerminalOcupadaLaDejaCreandoYLasBandasDeTiempoLaLiberan()
    {
        var (e, medio) = await PrepararPointAsync("mp-point-ocupada-reenvio");
        e.Ordenes.Antes = pedido => EsCreacion(pedido)
            ? Json(HttpStatusCode.BadRequest, """{"error": "invalid_params", "message": "x"}""")
            : null;
        await CrearAsync(e, 6040, 100m, medio);
        Assert.Equal("creando", Assert.Single(await FilasAsync(e.IdPuntoVenta))["estado"]);
        var idCobro = (int)Assert.Single(await FilasAsync(e.IdPuntoVenta))["id_cobro_mercadopago"]!;
        var reservado = await ReservadoEnAsync(e);

        e.Ordenes.Antes = pedido => EsCreacion(pedido) ? Json(HttpStatusCode.Conflict, TerminalOcupadaJson) : null;
        await AssertCodigoAsync(await CrearAsync(e, 6040, 100m, medio), HttpStatusCode.Conflict, "mercadopago_terminal_ocupada");

        var fila = Assert.Single(await FilasAsync(e.IdPuntoVenta));
        Assert.Equal("creando", fila["estado"]);
        Assert.Null(fila["id_orden_mercadopago"]);

        e.Reloj.Ahora = reservado + VigenciaEsperada;
        await AssertCodigoAsync(await CrearAsync(e, 6040, 100m, medio), HttpStatusCode.Conflict, "cobro_mercadopago_en_curso");
        Assert.Equal("creando", Assert.Single(await FilasAsync(e.IdPuntoVenta))["estado"]);

        e.Reloj.Ahora = reservado + VigenciaEsperada + VigenciaEsperada + TimeSpan.FromMinutes(5);
        e.Ordenes.Antes = null;
        var nuevo = await CrearPointOkAsync(e, medio, 6040, 100m);

        Assert.NotEqual(idCobro, nuevo.IdCobro);
        Assert.Equal(["expirado", "pendiente"], (await FilasAsync(e.IdPuntoVenta)).Select(f => (string)f["estado"]!));
    }

    // Clausula: un fallido releido dentro de su ventana que recibe un error de Mercado Pago (5xx, 404 antes del plazo) devuelve el estado
    // guardado: la vigilancia es un extra y no rompe la consulta del rechazo.
    [Theory]
    [InlineData(HttpStatusCode.ServiceUnavailable)]
    [InlineData(HttpStatusCode.NotFound)]
    public async Task UnPointFallidoQueNoSePuedeRelerSeDevuelveTalCualEnConsultarApartarYElAviso(HttpStatusCode error)
    {
        var (e, _, fallido, idOrden) = await PrepararFallidoAsync("mp-point-fallido-error-" + (int)error, 6041);
        e.Ordenes.Antes = pedido => pedido.Metodo == HttpMethod.Get ? Json(error, """{"error": "x", "message": "x"}""") : null;
        var lecturas = Lecturas(e).Count;

        e.Reloj.Ahora = e.Reloj.Ahora.AddSeconds(3);
        var consultado = await LeerAsync<CobroDeMercadoPago>(await ConsultarAsync(e, fallido.IdCobro));
        Assert.Equal("fallido", consultado.Estado);
        Assert.True(Lecturas(e).Count > lecturas, "el fallido tenia que intentar releerse");

        var aviso = await AvisarAsync(ClienteDelWebhook(e), idOrden, FirmaValida(e, idOrden));
        Assert.Equal(HttpStatusCode.OK, aviso.StatusCode);

        var apartado = await LeerAsync<CobroDeMercadoPago>(await ApartarAsync(e, fallido.IdCobro));
        Assert.Equal("fallido", apartado.Estado);
        Assert.Equal("fallido", Assert.Single(await FilasAsync(e.IdPuntoVenta))["estado"]);
    }

    // Clausula: un fallido que Mercado Pago informa procesado sin un pago legible sigue vigilado pasada la ventana normal (hasta 24 h desde
    // la reserva) y aparece en cobros-sin-venta con pagoInformadoSinDatos; cuando el pago se vuelve legible pasa a sin_venta.
    [Fact]
    public async Task UnPointFallidoConPagoInformadoSinDatosSeSigueVigilandoYSeListaParaLaConciliacion()
    {
        var (e, _, fallido, idOrden) = await PrepararFallidoAsync("mp-point-fallido-sin-datos", 6042);
        e.Ordenes.Estados[idOrden] = "processed";
        e.Ordenes.SinPagos = true;
        e.Reloj.Ahora = e.Reloj.Ahora.AddSeconds(3);
        var informado = await LeerAsync<CobroDeMercadoPago>(await ConsultarAsync(e, fallido.IdCobro));
        Assert.Equal("fallido", informado.Estado);
        Assert.NotNull(Assert.Single(await FilasAsync(e.IdPuntoVenta))["fecha_procesada_mercadopago"]);

        e.Reloj.Ahora = e.Reloj.Ahora.AddMinutes(16);
        var lecturas = Lecturas(e).Count;
        await ConsultarAsync(e, fallido.IdCobro);
        Assert.True(Lecturas(e).Count > lecturas, "pasada la ventana normal, un fallido con pago informado sigue leyendose");

        var lista = await LeerAsync<List<CobroSinVentaListado>>(await e.Ctx.Admin.GetAsync($"{RutaSinVenta}?idEmpresa={e.Ctx.IdEmpresa}"));
        var listado = Assert.Single(lista);
        Assert.Equal(fallido.IdCobro, listado.IdCobro);
        Assert.True(listado.PagoInformadoSinDatos);
        Assert.Null(listado.IdPagoMercadoPago);

        e.Ordenes.SinPagos = false;
        e.Reloj.Ahora = e.Reloj.Ahora.AddMinutes(1);
        var legible = await LeerAsync<CobroDeMercadoPago>(await ConsultarAsync(e, fallido.IdCobro));
        Assert.Equal("sin_venta", legible.Estado);
        var despues = await LeerAsync<List<CobroSinVentaListado>>(await e.Ctx.Admin.GetAsync($"{RutaSinVenta}?idEmpresa={e.Ctx.IdEmpresa}"));
        Assert.False(Assert.Single(despues).PagoInformadoSinDatos);
    }

    // Clausula: la vigilancia de un fallido con pago informado sin datos tiene tope de 24 h desde la reserva.
    [Fact]
    public async Task UnPointFallidoConPagoInformadoSinDatosDejaDeVigilarsePasadas24HorasDeLaReserva()
    {
        var (e, _, fallido, idOrden) = await PrepararFallidoAsync("mp-point-fallido-sin-datos-tope", 6043);
        var reservado = await ReservadoEnAsync(e);
        e.Ordenes.Estados[idOrden] = "processed";
        e.Ordenes.SinPagos = true;
        e.Reloj.Ahora = e.Reloj.Ahora.AddSeconds(3);
        await ConsultarAsync(e, fallido.IdCobro);

        e.Reloj.Ahora = reservado + TimeSpan.FromHours(24);
        var lecturas = Lecturas(e).Count;
        var consultado = await LeerAsync<CobroDeMercadoPago>(await ConsultarAsync(e, fallido.IdCobro));

        Assert.Equal("fallido", consultado.Estado);
        Assert.Equal(lecturas, Lecturas(e).Count);
    }

    // Clausula: single-read-under-lock. Crear toma el lock de la caja antes de leer la terminal: una reasignacion que confirma mientras
    // el crear espera se ve en la orden (la orden va a la terminal vigente, no a la leida antes del lock).
    [Fact]
    public async Task CrearPointTomaElLockDeLaCajaYPideLaOrdenALaTerminalReasignadaMientrasEsperaba()
    {
        var (e, medio) = await PrepararPointAsync("mp-point-lock-caja");

        await using var conexion = await fixture.AbrirConexionCrudaAsync("plataforma", null);
        await using var transaccion = await conexion.BeginTransactionAsync();
        await using (var tomar = new Npgsql.NpgsqlCommand("SELECT pg_advisory_xact_lock($1, $2)", conexion, transaccion))
        {
            tomar.Parameters.Add(new Npgsql.NpgsqlParameter { Value = e.IdPuntoVenta });
            tomar.Parameters.Add(new Npgsql.NpgsqlParameter { Value = ServicioDeCajasMercadoPago.ClaveDeLockDeCaja });
            await tomar.ExecuteNonQueryAsync();
        }

        var crear = CrearAsync(e, 6034, 100m, medio);
        await Task.Delay(TimeSpan.FromSeconds(1));
        Assert.False(crear.IsCompleted);

        await using (var reasignar = new Npgsql.NpgsqlCommand(
            "UPDATE cajas_mercadopago SET id_terminal_point = 'PAX_A910__REASIGNADA1' WHERE id_punto_venta = $1", conexion, transaccion))
        {
            reasignar.Parameters.Add(new Npgsql.NpgsqlParameter { Value = e.IdPuntoVenta });
            await reasignar.ExecuteNonQueryAsync();
        }

        await transaccion.CommitAsync();
        var cobro = await LeerAsync<CobroDeMercadoPago>(await crear);

        Assert.Equal("PAX_A910__REASIGNADA1", cobro.IdTerminal);
        using var cuerpo = JsonDocument.Parse(Assert.Single(Creaciones(e)).Cuerpo);
        Assert.Equal("PAX_A910__REASIGNADA1", cuerpo.RootElement.GetProperty("config").GetProperty("point").GetProperty("terminal_id").GetString());
    }

    // Clausula: si Mercado Pago contesta 409 al cancelar y la lectura posterior deja el cobro pendiente y fuera de la terminal, la
    // cancelacion no se aplico: 409 mercadopago_cancelacion_no_aplicada, no un 200 silencioso; el cobro sigue pendiente.
    [Fact]
    public async Task UnCancelarQueContesta409YDejaElCobroPendienteFueraDeLaTerminalEsUn409CancelacionNoAplicada()
    {
        var (e, medio) = await PrepararPointAsync("mp-point-cancelacion-no-aplicada");
        var cobro = await CrearPointOkAsync(e, medio, 6035, 100m);
        e.Ordenes.Cancelar = _ => Json(HttpStatusCode.Conflict, """{"error": "cannot_cancel_order", "message": "nope"}""");

        await AssertCodigoAsync(await CancelarAsync(e, cobro.IdCobro), HttpStatusCode.Conflict, "mercadopago_cancelacion_no_aplicada");

        Assert.Equal("pendiente", Assert.Single(await FilasAsync(e.IdPuntoVenta))["estado"]);
    }

    // Clausula: mientras la orden esta created el cobro Point se cancela como un QR.
    [Fact]
    public async Task UnCobroPointCreatedSeCancelaEnMercadoPago()
    {
        var (e, medio) = await PrepararPointAsync("mp-point-cancelar-created");
        var cobro = await CrearPointOkAsync(e, medio, 6016, 100m);

        var cancelado = await LeerAsync<CobroDeMercadoPago>(await CancelarAsync(e, cobro.IdCobro));

        Assert.Equal("cancelado", cancelado.Estado);
        Assert.Single(Cancelaciones(e));
    }

    // Clausula: at_terminal / action_required (ya leidos) no se cancelan desde el POS: 409 sin llamar a Mercado Pago, el cobro sigue.
    [Theory]
    [InlineData("at_terminal")]
    [InlineData("action_required")]
    public async Task CancelarUnCobroPointQueYaEstaEnLaTerminalEsUn409SinLlamarAMercadoPago(string estadoDeMercadoPago)
    {
        var (e, medio) = await PrepararPointAsync("mp-point-cancelar-terminal-" + estadoDeMercadoPago.Replace("_", string.Empty));
        var cobro = await CrearPointOkAsync(e, medio, 6017, 100m);
        e.Ordenes.Estados[IdDeLaOrden(e)] = estadoDeMercadoPago;
        await ConsultarAsync(e, cobro.IdCobro);

        await AssertCodigoAsync(await CancelarAsync(e, cobro.IdCobro), HttpStatusCode.Conflict, "mercadopago_cancelar_en_la_terminal");

        Assert.Empty(Cancelaciones(e));
        Assert.Equal("pendiente", Assert.Single(await FilasAsync(e.IdPuntoVenta))["estado"]);
    }

    // Clausula: si la orden llego a la terminal despues de la ultima lectura, Mercado Pago contesta 409 al cancelar; se relee la
    // orden y el cancelar responde 409 cancelar_en_la_terminal (y el cobro queda en la terminal).
    [Fact]
    public async Task UnCancelarPointQueContesta409PorqueLaOrdenYaEstabaEnLaTerminalRespondeCancelarEnLaTerminal()
    {
        var (e, medio) = await PrepararPointAsync("mp-point-cancelar-409-terminal");
        var cobro = await CrearPointOkAsync(e, medio, 6018, 100m);
        e.Ordenes.Cancelar = id =>
        {
            e.Ordenes.Estados[id] = "at_terminal";
            return Json(HttpStatusCode.Conflict, """{"error": "cannot_cancel_order", "message": "order at terminal"}""");
        };

        await AssertCodigoAsync(await CancelarAsync(e, cobro.IdCobro), HttpStatusCode.Conflict, "mercadopago_cancelar_en_la_terminal");

        Assert.Single(Cancelaciones(e));
        Assert.Equal("en_terminal", Assert.Single(await FilasAsync(e.IdPuntoVenta))["situacion_terminal"]);
    }

    // Clausula: si el cajero cancela en la terminal, el refresco lo refleja como cancelado.
    [Fact]
    public async Task SiElCajeroCancelaEnLaTerminalElRefrescoLoDejaCancelado()
    {
        var (e, medio) = await PrepararPointAsync("mp-point-cancelado-en-terminal");
        var cobro = await CrearPointOkAsync(e, medio, 6019, 100m);
        var idOrden = IdDeLaOrden(e);
        e.Ordenes.Estados[idOrden] = "at_terminal";
        await ConsultarAsync(e, cobro.IdCobro);

        e.Reloj.Ahora = e.Reloj.Ahora.AddSeconds(3);
        e.Ordenes.Estados[idOrden] = "canceled";
        var consultado = await LeerAsync<CobroDeMercadoPago>(await ConsultarAsync(e, cobro.IdCobro));

        Assert.Equal("cancelado", consultado.Estado);
        Assert.False(consultado.EnTerminal);
    }

    // Clausula: un 404 de la orden despues del plazo expira el cobro Point y limpia su situacion (CHECK de la base).
    [Fact]
    public async Task UnoDe404DeLaOrdenPointEnLaTerminalDespuesDelPlazoLaDejaExpiradaSinSituacion()
    {
        var (e, medio) = await PrepararPointAsync("mp-point-404-expira");
        var cobro = await CrearPointOkAsync(e, medio, 6020, 100m);
        e.Ordenes.Estados[IdDeLaOrden(e)] = "at_terminal";
        await ConsultarAsync(e, cobro.IdCobro);
        e.Reloj.Ahora = e.Reloj.Ahora.AddMinutes(11);
        e.Ordenes.Antes = pedido => pedido.Metodo == HttpMethod.Get ? Json(HttpStatusCode.NotFound, """{"error":"order_not_found"}""") : null;

        var consultado = await LeerAsync<CobroDeMercadoPago>(await ConsultarAsync(e, cobro.IdCobro));

        Assert.Equal("expirado", consultado.Estado);
        Assert.Null(Assert.Single(await FilasAsync(e.IdPuntoVenta))["situacion_terminal"]);
    }

    // Clausula: la base exige terminal exactamente en los cobros point y situacion solo en un point pendiente.
    [Fact]
    public async Task LaBaseRechazaUnPointSinTerminalUnQrConTerminalYUnaSituacionFueraDeUnPointPendiente()
    {
        var (e, medio) = await PrepararPointAsync("mp-point-checks");
        var cobro = await CrearPointOkAsync(e, medio, 6021, 100m);
        var id = cobro.IdCobro;

        var sinTerminal = await Assert.ThrowsAnyAsync<Npgsql.PostgresException>(
            () => EjecutarAsync($"UPDATE cobros_mercadopago SET id_terminal_point = NULL WHERE id_cobro_mercadopago = {id}"));
        Assert.Equal(Npgsql.PostgresErrorCodes.CheckViolation, sinTerminal.SqlState);
        Assert.Equal("ck_cobros_mercadopago_terminal", sinTerminal.ConstraintName);

        var qrConTerminal = await Assert.ThrowsAnyAsync<Npgsql.PostgresException>(
            () => EjecutarAsync($"UPDATE cobros_mercadopago SET tipo = 'qr' WHERE id_cobro_mercadopago = {id}"));
        Assert.Equal("ck_cobros_mercadopago_terminal", qrConTerminal.ConstraintName);

        var situacionInvalida = await Assert.ThrowsAnyAsync<Npgsql.PostgresException>(
            () => EjecutarAsync($"UPDATE cobros_mercadopago SET situacion_terminal = 'otra' WHERE id_cobro_mercadopago = {id}"));
        Assert.Equal("ck_cobros_mercadopago_situacion_terminal", situacionInvalida.ConstraintName);

        var situacionEnCancelado = await Assert.ThrowsAnyAsync<Npgsql.PostgresException>(
            () => EjecutarAsync(
                $"UPDATE cobros_mercadopago SET estado = 'cancelado', situacion_terminal = 'en_terminal' WHERE id_cobro_mercadopago = {id}"));
        Assert.Equal("ck_cobros_mercadopago_situacion_terminal", situacionEnCancelado.ConstraintName);

        await EjecutarAsync($"UPDATE cobros_mercadopago SET estado = 'fallido' WHERE id_cobro_mercadopago = {id}");
    }

    // Clausula: reintentar una creacion reenvia la terminal ORIGINAL aunque la caja haya cambiado de terminal.
    [Fact]
    public async Task UnCommitPerdidoDeUnCobroPointReenviaLaTerminalOriginalAunqueLaCajaCambie()
    {
        var (e, medio) = await PrepararPointAsync("mp-point-commit-perdido");
        await using (await FallarElPasoAPendienteAsync())
        {
            var fallo = await CrearAsync(e, 6022, 100m, medio);
            Assert.False(fallo.IsSuccessStatusCode);
        }

        await AsignarTerminalAsync(e, "PAX_A910__OTRATERMINAL999");
        var cobro = await CrearPointOkAsync(e, medio, 6022, 100m);

        Assert.Equal(TerminalDePrueba, cobro.IdTerminal);
        Assert.All(Creaciones(e), p =>
        {
            using var cuerpo = JsonDocument.Parse(p.Cuerpo);
            Assert.Equal(TerminalDePrueba, cuerpo.RootElement.GetProperty("config").GetProperty("point").GetProperty("terminal_id").GetString());
        });
        Assert.Single(Creaciones(e).Select(p => p.ClaveDeIdempotencia).Distinct());
    }

    // Clausula: el webhook (E3-T4) refresca un cobro Point igual que uno QR.
    [Fact]
    public async Task UnAvisoFirmadoRefrescaUnCobroPointYLoDejaEnLaTerminal()
    {
        var (e, medio) = await PrepararPointAsync("mp-point-aviso");
        var cobro = await CrearPointOkAsync(e, medio, 6023, 100m);
        var idOrden = IdDeLaOrden(e);
        e.Ordenes.Estados[idOrden] = "at_terminal";
        e.Reloj.Ahora = e.Reloj.Ahora.AddSeconds(3);

        var respuesta = await AvisarAsync(ClienteDelWebhook(e), idOrden, FirmaValida(e, idOrden));

        Assert.Equal(HttpStatusCode.OK, respuesta.StatusCode);
        Assert.Equal("en_terminal", Assert.Single(await FilasAsync(e.IdPuntoVenta))["situacion_terminal"]);
        Assert.True((await LeerAsync<CobroDeMercadoPago>(await ConsultarAsync(e, cobro.IdCobro))).EnTerminal);
    }

    // Clausula: apartar un cobro Point aprobado sin venta funciona como en un QR.
    [Fact]
    public async Task ApartarUnCobroPointAprobadoLoDejaSinVenta()
    {
        var (e, medio) = await PrepararPointAsync("mp-point-apartar");
        var cobro = await CrearPointOkAsync(e, medio, 6024, 100m);
        e.Ordenes.Estados[IdDeLaOrden(e)] = "processed";
        await ConsultarAsync(e, cobro.IdCobro);

        var apartado = await LeerAsync<CobroDeMercadoPago>(await ApartarAsync(e, cobro.IdCobro));

        Assert.Equal("sin_venta", apartado.Estado);
        Assert.Equal("point", apartado.Tipo);
    }
}
