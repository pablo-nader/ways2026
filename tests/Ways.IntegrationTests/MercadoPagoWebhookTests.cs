using System.Net;
using System.Security.Cryptography;
using System.Text;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;
using Ways.Application.MercadoPago;

namespace Ways.IntegrationTests;

/// <summary>
/// El webhook de Mercado Pago (<c>POST /api/integraciones/mercadopago/webhook</c>, E3-T4) contra Postgres real y el manejador falso de
/// Mercado Pago de <see cref="MercadoPagoCobrosTests"/>: la firma <c>x-signature</c> es la única credencial (sin sesión), un aviso válido
/// refresca el cobro de su orden con el contexto de SU tenant sin respetar el límite de una consulta cada 2 segundos, y todo lo que no
/// es un aviso <c>order</c> firmado de una orden nuestra no cambia nada. El índice único de la orden tiene su traducción y su carrera.
/// </summary>
public partial class MercadoPagoCobrosTests
{
    private const string SecretoDelWebhook = "whsec-de-prueba-e3t4-que-no-es-real";
    private const string RutaDelWebhook = "/api/integraciones/mercadopago/webhook";
    private const string IdDeSolicitudDelWebhook = "req-9f1c2e3d-0001";

    /// <summary>La firma de Mercado Pago, calculada por una vía independiente de la implementación (manifiesto y HMAC escritos acá).</summary>
    private static string EncabezadoDeFirma(
        string secreto, string idDelManifiesto, string? requestId, long ts, string? tsDelManifiesto = null)
    {
        var manifiesto = $"id:{idDelManifiesto};request-id:{requestId};ts:{tsDelManifiesto ?? ts.ToString()};";
        var hex = Convert.ToHexString(
            new HMACSHA256(Encoding.UTF8.GetBytes(secreto)).ComputeHash(Encoding.UTF8.GetBytes(manifiesto))).ToLowerInvariant();
        return $"ts={ts},v1={hex}";
    }

    /// <summary>Firma válida de un aviso de <paramref name="idDeOrden"/> (alfanumérico: minúsculas en el manifiesto) en el instante del reloj.</summary>
    private static string FirmaValida(Escenario e, string idDeOrden, TimeSpan? desplazamiento = null) =>
        EncabezadoDeFirma(
            SecretoDelWebhook, idDeOrden.ToLowerInvariant(), IdDeSolicitudDelWebhook,
            (e.Reloj.Ahora + (desplazamiento ?? TimeSpan.Zero)).ToUnixTimeSeconds());

    /// <summary>Un cliente sin sesión ni cookies contra el mismo host de la prueba, con el secreto del webhook configurado.</summary>
    private static HttpClient ClienteDelWebhook(Escenario e, bool conSecreto = true) =>
        (conSecreto
            ? e.Ctx.Host.WithWebHostBuilder(b => b.ConfigureServices(s =>
                s.Configure<OpcionesDeMercadoPago>(o => o.SecretoWebhook = SecretoDelWebhook)))
            : e.Ctx.Host).CreateClient();

    private static Task<HttpResponseMessage> AvisarAsync(
        HttpClient cliente, string idEnLaUrl, string? firma, string? requestId = IdDeSolicitudDelWebhook, string? tipoEnLaUrl = "order",
        string cuerpo = "{}")
    {
        var url = $"{RutaDelWebhook}?data.id={Uri.EscapeDataString(idEnLaUrl)}" + (tipoEnLaUrl is null ? string.Empty : $"&type={tipoEnLaUrl}");
        var solicitud = new HttpRequestMessage(HttpMethod.Post, url)
        {
            Content = new StringContent(cuerpo, Encoding.UTF8, "application/json")
        };
        if (firma is not null)
        {
            solicitud.Headers.Add("x-signature", firma);
        }

        if (requestId is not null)
        {
            solicitud.Headers.Add("x-request-id", requestId);
        }

        return cliente.SendAsync(solicitud);
    }

    /// <summary>Un cobro pendiente y el id de su orden en Mercado Pago.</summary>
    private async Task<string> PendienteConOrdenAsync(Escenario e, long numero)
    {
        await CrearOkAsync(e, numero, 100m);
        return (string)Assert.Single(await FilasAsync(e.IdPuntoVenta))["id_orden_mercadopago"]!;
    }

    private async Task<string> EstadoDelCobroAsync(Escenario e) =>
        (string)Assert.Single(await FilasAsync(e.IdPuntoVenta))["estado"]!;

    private async Task AssertRechazadoSinEfectosAsync(Escenario e, HttpResponseMessage respuesta)
    {
        await AssertCodigoAsync(respuesta, HttpStatusCode.Unauthorized, "mercadopago_firma_invalida");
        Assert.DoesNotContain(SecretoDelWebhook, await respuesta.Content.ReadAsStringAsync(), StringComparison.Ordinal);
        Assert.Empty(Lecturas(e));
        Assert.Equal("pendiente", await EstadoDelCobroAsync(e));
    }

    // ---- Aviso válido ------------------------------------------------------------------------------------------------------

    // Clausula: un aviso order con firma valida, sin sesion, lee la orden y aprueba el cobro (el id de la URL va en mayusculas y el
    // manifiesto en minusculas).
    [Fact]
    public async Task UnAvisoFirmadoDeUnaOrdenPagadaApruebaElCobroSinSesion()
    {
        var e = await PrepararAsync("mp-webhook-aprueba");
        var orden = await PendienteConOrdenAsync(e, 7001);
        e.Ordenes.Estados[orden] = "processed";

        var respuesta = await AvisarAsync(ClienteDelWebhook(e), orden, FirmaValida(e, orden));

        Assert.Equal(HttpStatusCode.OK, respuesta.StatusCode);
        Assert.DoesNotContain(SecretoDelWebhook, await respuesta.Content.ReadAsStringAsync(), StringComparison.Ordinal);
        var fila = Assert.Single(await FilasAsync(e.IdPuntoVenta));
        Assert.Equal("aprobado", fila["estado"]);
        Assert.Equal($"PAY{orden}", fila["id_pago_mercadopago"]);
        Assert.NotNull(fila["fecha_aprobacion"]);
        Assert.Equal(100m, fila["importe_pagado"]);
        var lectura = Assert.Single(Lecturas(e));
        Assert.Equal($"/v1/orders/{orden}", lectura.Uri.AbsolutePath);
    }

    [Fact]
    public async Task ElTipoPuedeVenirSoloEnElCuerpoDelAviso()
    {
        var e = await PrepararAsync("mp-webhook-tipo-cuerpo");
        var orden = await PendienteConOrdenAsync(e, 7002);
        e.Ordenes.Estados[orden] = "processed";

        var respuesta = await AvisarAsync(
            ClienteDelWebhook(e), orden, FirmaValida(e, orden), tipoEnLaUrl: null,
            cuerpo: $$$"""{"action": "order.processed", "type": "order", "data": {"id": "{{{orden}}}"}}""");

        Assert.Equal(HttpStatusCode.OK, respuesta.StatusCode);
        Assert.Equal("aprobado", await EstadoDelCobroAsync(e));
    }

    [Theory]
    [InlineData("""{"type": "payment", "data": {"id": "1"}}""")]
    [InlineData("esto no es json")]
    [InlineData("[1, 2]")]
    [InlineData("""{"type": 7}""")]
    [InlineData("")]
    public async Task SinTipoOrderEnLaUrlNiEnElCuerpoElAvisoSeIgnora(string cuerpo)
    {
        var e = await PrepararAsync("mp-webhook-sin-tipo");
        var orden = await PendienteConOrdenAsync(e, 7003);
        e.Ordenes.Estados[orden] = "processed";

        var respuesta = await AvisarAsync(ClienteDelWebhook(e), orden, FirmaValida(e, orden), tipoEnLaUrl: null, cuerpo: cuerpo);

        Assert.Equal(HttpStatusCode.OK, respuesta.StatusCode);
        Assert.Empty(Lecturas(e));
        Assert.Equal("pendiente", await EstadoDelCobroAsync(e));
    }

    [Fact]
    public async Task ElTsEnMilisegundosTambienSeAcepta()
    {
        var e = await PrepararAsync("mp-webhook-ms");
        var orden = await PendienteConOrdenAsync(e, 7004);
        e.Ordenes.Estados[orden] = "processed";
        var firma = EncabezadoDeFirma(
            SecretoDelWebhook, orden.ToLowerInvariant(), IdDeSolicitudDelWebhook, e.Reloj.Ahora.ToUnixTimeMilliseconds());

        Assert.Equal(HttpStatusCode.OK, (await AvisarAsync(ClienteDelWebhook(e), orden, firma)).StatusCode);

        Assert.Equal("aprobado", await EstadoDelCobroAsync(e));
    }

    // ---- Aviso rechazado: 401 y nada cambia ------------------------------------------------------------------------------

    [Fact]
    public async Task UnSecretoEquivocadoEsUn401SinConsultarAMercadoPago()
    {
        var e = await PrepararAsync("mp-webhook-secreto");
        var orden = await PendienteConOrdenAsync(e, 7010);
        e.Ordenes.Estados[orden] = "processed";
        var firma = EncabezadoDeFirma("otro-secreto", orden.ToLowerInvariant(), IdDeSolicitudDelWebhook, e.Reloj.Ahora.ToUnixTimeSeconds());

        await AssertRechazadoSinEfectosAsync(e, await AvisarAsync(ClienteDelWebhook(e), orden, firma));
    }

    [Fact]
    public async Task UnIdAlteradoEsUn401()
    {
        var e = await PrepararAsync("mp-webhook-id");
        var orden = await PendienteConOrdenAsync(e, 7011);
        e.Ordenes.Estados[orden] = "processed";

        // La firma es de otra orden: se reutiliza en un aviso de la nuestra.
        await AssertRechazadoSinEfectosAsync(e, await AvisarAsync(ClienteDelWebhook(e), orden, FirmaValida(e, "ORD999999999")));
    }

    [Fact]
    public async Task UnRequestIdAlteradoEsUn401()
    {
        var e = await PrepararAsync("mp-webhook-request-id");
        var orden = await PendienteConOrdenAsync(e, 7012);
        e.Ordenes.Estados[orden] = "processed";

        await AssertRechazadoSinEfectosAsync(
            e, await AvisarAsync(ClienteDelWebhook(e), orden, FirmaValida(e, orden), requestId: "req-distinto"));
    }

    [Fact]
    public async Task SinElEncabezadoDeFirmaEsUn401()
    {
        var e = await PrepararAsync("mp-webhook-sin-firma");
        var orden = await PendienteConOrdenAsync(e, 7013);
        e.Ordenes.Estados[orden] = "processed";

        await AssertRechazadoSinEfectosAsync(e, await AvisarAsync(ClienteDelWebhook(e), orden, firma: null));
    }

    [Fact]
    public async Task SinElRequestIdEsUn401AunqueLaFirmaSeaDeUnManifiestoSinEl()
    {
        var e = await PrepararAsync("mp-webhook-sin-request-id");
        var orden = await PendienteConOrdenAsync(e, 7014);
        e.Ordenes.Estados[orden] = "processed";
        var ts = e.Reloj.Ahora.ToUnixTimeSeconds();
        var hex = Convert.ToHexString(
            new HMACSHA256(Encoding.UTF8.GetBytes(SecretoDelWebhook))
                .ComputeHash(Encoding.UTF8.GetBytes($"id:{orden.ToLowerInvariant()};ts:{ts};"))).ToLowerInvariant();

        await AssertRechazadoSinEfectosAsync(
            e, await AvisarAsync(ClienteDelWebhook(e), orden, $"ts={ts},v1={hex}", requestId: null));
    }

    [Fact]
    public async Task UnEncabezadoIlegibleEsUn401()
    {
        var e = await PrepararAsync("mp-webhook-ilegible");
        var orden = await PendienteConOrdenAsync(e, 7015);

        await AssertRechazadoSinEfectosAsync(e, await AvisarAsync(ClienteDelWebhook(e), orden, "ts=abc,v1=xyz"));
    }

    [Fact]
    public async Task UnAvisoFirmadoHaceMasDeDiezMinutosEsUn401()
    {
        var e = await PrepararAsync("mp-webhook-viejo");
        var orden = await PendienteConOrdenAsync(e, 7016);
        e.Ordenes.Estados[orden] = "processed";

        await AssertRechazadoSinEfectosAsync(
            e, await AvisarAsync(ClienteDelWebhook(e), orden, FirmaValida(e, orden, TimeSpan.FromMinutes(-11))));
    }

    [Fact]
    public async Task UnAvisoFirmadoConMasDeDosMinutosDeAdelantoEsUn401()
    {
        var e = await PrepararAsync("mp-webhook-futuro");
        var orden = await PendienteConOrdenAsync(e, 7017);
        e.Ordenes.Estados[orden] = "processed";

        await AssertRechazadoSinEfectosAsync(
            e, await AvisarAsync(ClienteDelWebhook(e), orden, FirmaValida(e, orden, TimeSpan.FromMinutes(3))));
    }

    [Fact]
    public async Task SinSecretoConfiguradoElWebhookEsUn503()
    {
        var e = await PrepararAsync("mp-webhook-sin-secreto");
        var orden = await PendienteConOrdenAsync(e, 7018);

        var respuesta = await AvisarAsync(ClienteDelWebhook(e, conSecreto: false), orden, FirmaValida(e, orden));

        await AssertCodigoAsync(respuesta, HttpStatusCode.ServiceUnavailable, "mercadopago_no_configurado");
        Assert.Empty(Lecturas(e));
    }

    // ---- Avisos que no son nuestros ------------------------------------------------------------------------------------------

    [Fact]
    public async Task UnaOrdenDesconocidaEsUn200SinConsultarAMercadoPago()
    {
        var e = await PrepararAsync("mp-webhook-desconocida");
        await PendienteConOrdenAsync(e, 7020);
        const string desconocida = "ORDDESCONOCIDA0001";

        var respuesta = await AvisarAsync(ClienteDelWebhook(e), desconocida, FirmaValida(e, desconocida));

        Assert.Equal(HttpStatusCode.OK, respuesta.StatusCode);
        Assert.Empty(Lecturas(e));
        Assert.Equal("pendiente", await EstadoDelCobroAsync(e));
    }

    [Fact]
    public async Task UnTopicoQueNoEsOrderEsUn200SinConsultarAMercadoPago()
    {
        var e = await PrepararAsync("mp-webhook-otro-topico");
        var orden = await PendienteConOrdenAsync(e, 7021);
        e.Ordenes.Estados[orden] = "processed";

        var respuesta = await AvisarAsync(ClienteDelWebhook(e), orden, FirmaValida(e, orden), tipoEnLaUrl: "payment");

        Assert.Equal(HttpStatusCode.OK, respuesta.StatusCode);
        Assert.Empty(Lecturas(e));
        Assert.Equal("pendiente", await EstadoDelCobroAsync(e));
    }

    // ---- Aislamiento por tenant --------------------------------------------------------------------------------------------

    // Clausula: la busqueda por orden usa el contexto de plataforma y el refresco el del tenant del cobro; el aviso de la orden de un
    // tenant actualiza solo su cobro, aunque otro tenant tenga cobros mas viejos, y la solicitud no tiene tenant.
    [Fact]
    public async Task ElAvisoActualizaElCobroDelTenantDeSuOrdenYNoElDeOtro()
    {
        var a = await PrepararAsync("mp-webhook-tenant-a");
        var b = await PrepararAsync("mp-webhook-tenant-b");
        var ordenA = await PendienteConOrdenAsync(a, 7030);
        var ordenB = await PendienteConOrdenAsync(b, 7031);
        a.Ordenes.Estados[ordenA] = "processed";
        b.Ordenes.Estados[ordenB] = "processed";

        var respuesta = await AvisarAsync(ClienteDelWebhook(b), ordenB, FirmaValida(b, ordenB));

        Assert.Equal(HttpStatusCode.OK, respuesta.StatusCode);
        Assert.Equal("aprobado", await EstadoDelCobroAsync(b));
        Assert.Equal("pendiente", await EstadoDelCobroAsync(a));
        Assert.Empty(Lecturas(a));
        Assert.Equal($"/v1/orders/{ordenB}", Assert.Single(Lecturas(b)).Uri.AbsolutePath);
    }

    // ---- Refresco ------------------------------------------------------------------------------------------------------------

    // Clausula: el aviso no respeta el limite de una consulta cada 2 segundos (RefrescarAsync con respetarIntervalo en false).
    [Fact]
    public async Task ElAvisoRefrescaAunqueElDispositivoHayaConsultadoHaceInstantes()
    {
        var e = await PrepararAsync("mp-webhook-sin-limite");
        var cobro = await CrearOkAsync(e, 7040, 100m);
        var orden = (string)Assert.Single(await FilasAsync(e.IdPuntoVenta))["id_orden_mercadopago"]!;
        Assert.Equal("pendiente", (await LeerAsync<CobroDeMercadoPago>(await ConsultarAsync(e, cobro.IdCobro))).Estado);
        Assert.Single(Lecturas(e));
        e.Ordenes.Estados[orden] = "processed";

        // Sin avanzar el reloj: una consulta del dispositivo en este instante no volvería a leer la orden.
        Assert.Equal("pendiente", (await LeerAsync<CobroDeMercadoPago>(await ConsultarAsync(e, cobro.IdCobro))).Estado);
        Assert.Single(Lecturas(e));

        Assert.Equal(HttpStatusCode.OK, (await AvisarAsync(ClienteDelWebhook(e), orden, FirmaValida(e, orden))).StatusCode);

        Assert.Equal(2, Lecturas(e).Count);
        Assert.Equal("aprobado", await EstadoDelCobroAsync(e));
    }

    [Fact]
    public async Task SiMercadoPagoFallaAlRefrescarElAvisoFirmadoIgualEsUn200YElCobroQuedaComoEstaba()
    {
        var e = await PrepararAsync("mp-webhook-falla");
        var orden = await PendienteConOrdenAsync(e, 7041);
        e.Ordenes.Estados[orden] = "processed";
        e.Ordenes.Antes = p => p.Metodo == HttpMethod.Get && p.Uri.AbsolutePath.StartsWith("/v1/orders/", StringComparison.Ordinal)
            ? Json(HttpStatusCode.InternalServerError, """{"message": "boom"}""")
            : null;

        var respuesta = await AvisarAsync(ClienteDelWebhook(e), orden, FirmaValida(e, orden));

        Assert.Equal(HttpStatusCode.OK, respuesta.StatusCode);
        Assert.NotEmpty(Lecturas(e));
        Assert.Equal("pendiente", await EstadoDelCobroAsync(e));

        e.Ordenes.Antes = null;
        Assert.Equal("aprobado", (await LeerAsync<CobroDeMercadoPago>(await ConsultarAsync(e, Convert.ToInt32((await FilasAsync(e.IdPuntoVenta))[0]["id_cobro_mercadopago"])))).Estado);
    }

    [Fact]
    public async Task UnAvisoDeUnCobroYaResueltoNoVuelveAConsultarAMercadoPago()
    {
        var e = await PrepararAsync("mp-webhook-resuelto");
        var orden = await PendienteConOrdenAsync(e, 7042);
        e.Ordenes.Estados[orden] = "processed";
        var cliente = ClienteDelWebhook(e);
        Assert.Equal(HttpStatusCode.OK, (await AvisarAsync(cliente, orden, FirmaValida(e, orden))).StatusCode);
        Assert.Single(Lecturas(e));

        e.Ordenes.Estados[orden] = "refunded";
        Assert.Equal(HttpStatusCode.OK, (await AvisarAsync(cliente, orden, FirmaValida(e, orden))).StatusCode);

        Assert.Single(Lecturas(e));
        Assert.Equal("aprobado", await EstadoDelCobroAsync(e));
    }

    // ---- Índice único de la orden --------------------------------------------------------------------------------------------

    // Clausula: ux_cobros_mercadopago_orden -- Mercado Pago devuelve el mismo id de orden para dos cobros: el segundo guardado choca con
    // el indice y ManejadorDeErrores lo traduce a 409 cobro_mercadopago_orden_duplicada.
    [Fact]
    public async Task UnaOrdenRepetidaEnOtroCobroSeTraduceAUn409()
    {
        var e = await PrepararAsync("mp-webhook-orden-repetida");
        var repetida = $"ORDREP{Guid.NewGuid():N}"[..20];
        e.Ordenes.Antes = p => p.Metodo == HttpMethod.Post && p.Uri.AbsolutePath == "/v1/orders"
            ? Json(HttpStatusCode.Created, $$$"""{"id": "{{{repetida}}}", "status": "created", "type_response": {"qr_data": "000201QR{{{repetida}}}"}}""")
            : null;
        await CrearOkAsync(e, 7050, 100m);

        await AssertCodigoAsync(await CrearAsync(e, 7051, 100m), HttpStatusCode.Conflict, "cobro_mercadopago_orden_duplicada");

        var filas = await FilasAsync(e.IdPuntoVenta);
        Assert.Single(filas, f => Equals(f["id_orden_mercadopago"], repetida));
    }

    // Clausula: dos escrituras concurrentes del mismo id de orden en cobros distintos -> una gana y la otra recibe 23505 con el nombre del
    // indice; los cobros sin orden (nulos) no chocan entre si.
    [Fact]
    public async Task DosEscriturasConcurrentesDeLaMismaOrdenDejanUnaGanadoraYUn23505()
    {
        var e = await PrepararAsync("mp-webhook-orden-carrera");
        await InsertarFilaAsync(e, 7060, "pendiente", 1);
        await InsertarFilaAsync(e, 7061, "pendiente", 1);
        var ids = (await FilasAsync(e.IdPuntoVenta)).Select(f => (int)f["id_cobro_mercadopago"]!).ToArray();
        var orden = $"ORDCARRERA{Guid.NewGuid():N}"[..20];
        using var salida = new Barrier(2);

        async Task<PostgresException?> AsignarAsync(int idCobro)
        {
            await using var conexion = await fixture.AbrirConexionCrudaAsync("plataforma", null);
            await using var comando = conexion.CreateCommand();
            comando.CommandText = "UPDATE cobros_mercadopago SET id_orden_mercadopago = $1 WHERE id_cobro_mercadopago = $2";
            comando.Parameters.Add(new NpgsqlParameter { Value = orden });
            comando.Parameters.Add(new NpgsqlParameter { Value = idCobro });
            salida.SignalAndWait();
            try
            {
                await comando.ExecuteNonQueryAsync();
                return null;
            }
            catch (PostgresException error)
            {
                return error;
            }
        }

        var resultados = await Task.WhenAll(ids.Select(id => Task.Run(() => AsignarAsync(id))));

        Assert.Single(resultados, r => r is null);
        var rechazo = Assert.Single(resultados, r => r is not null)!;
        Assert.Equal("23505", rechazo.SqlState);
        Assert.Equal("ux_cobros_mercadopago_orden", rechazo.ConstraintName);

        await using var conexionNulos = await fixture.AbrirConexionCrudaAsync("plataforma", null);
        await using var anular = conexionNulos.CreateCommand();
        anular.CommandText =
            "UPDATE cobros_mercadopago SET estado = 'expirado', id_orden_mercadopago = NULL WHERE id_punto_venta = $1";
        anular.Parameters.Add(new NpgsqlParameter { Value = e.IdPuntoVenta });
        Assert.Equal(2, await anular.ExecuteNonQueryAsync());
    }
}
