using System.Data.Common;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Ways.Application.Abstracciones;
using Ways.Application.Fiscal;
using Ways.Application.Parametros;
using Ways.Application.Stock;
using Ways.Domain.Caja;
using Ways.Domain.Organizacion;
using Ways.Domain.Usuarios;
using Ways.Infrastructure.Fiscal;
using Ways.Infrastructure.Multitenancy;
using Ways.Infrastructure.Persistencia;

namespace Ways.IntegrationTests;

/// <summary>
/// Facturación por medio de pago, T5b: <c>POST /api/fiscal/facturaciones/procesar</c> procesa en lote las decisiones pendientes de un
/// punto de venta con los espías de WSAA/WSFE y Postgres real. Comparte el escenario y los espías de T5a (clase parcial).
///
/// Mutation-proof-tests: las cláusulas bajo prueba son el filtro de espera (<c>proximo_intento_en</c>, incluido el borde), el orden
/// por decisión, el tope por defecto y el máximo, el cabeza de serie primero, la espera del cabeza que salta toda su serie, el
/// aislamiento de una decisión que falla, el corte por presupuesto que no es un fallo, el filtro de punto de venta (candidatas y
/// pendientes restantes), el timeout aplicado al cliente de WSFE y la comparación del ticket al invalidarlo. Las carreras se
/// resuelven pausando el espía de WSFE, sin esperas por tiempo.
/// </summary>
public partial class FacturacionDeVentasEmisionTests
{
    private const string RutaDelLote = "/api/fiscal/facturaciones/procesar";

    private const short CodigoFacturaB = 6;

    private const short CodigoFacturaA = 1;

    private static Task<HttpResponseMessage> PostLoteAsync(HttpClient cliente, object cuerpo) =>
        cliente.PostAsJsonAsync(RutaDelLote, cuerpo);

    private static async Task<ResultadoDeLoteDeFacturacion> ProcesarLoteAsync(
        HttpClient cliente, int idPuntoVenta, int? maximo = null)
    {
        var respuesta = await PostLoteAsync(cliente, new { idPuntoVenta, maximo });
        var cuerpo = await respuesta.Content.ReadAsStringAsync();
        Assert.True(respuesta.StatusCode == HttpStatusCode.OK, cuerpo);
        return JsonSerializer.Deserialize<ResultadoDeLoteDeFacturacion>(cuerpo, OpcionesJson)!;
    }

    private static string FecaeTicketInvalido() =>
        "<?xml version=\"1.0\" encoding=\"UTF-8\"?><soapenv:Envelope xmlns:soapenv=\"http://schemas.xmlsoap.org/soap/envelope/\">" +
        "<soapenv:Body><FECAESolicitarResponse xmlns=\"http://ar.gov.afip.dif.FEV1/\"><FECAESolicitarResult>" +
        "<Errors><Err><Code>600</Code><Msg>Token inválido.</Msg></Err></Errors><Events />" +
        "</FECAESolicitarResult></FECAESolicitarResponse></soapenv:Body></soapenv:Envelope>";

    /// <summary>Siembra <paramref name="cantidad"/> ventas decididas del mismo cliente; solo la primera siembra los acumulados.</summary>
    private async Task<List<Venta>> SembrarVariasAsync(Ctx c, int cantidad, int? idCliente = null)
    {
        var ventas = new List<Venta>();
        for (var i = 0; i < cantidad; i++)
        {
            ventas.Add(await SembrarVentaDecididaAsync(c, idCliente, sembrarAcumulados: i == 0));
        }

        return ventas;
    }

    private Task PonerEsperaAsync(int idFacturacion, DateTimeOffset? hasta) => EjecutarComoOwnerAsync(
        "UPDATE facturaciones_de_venta SET proximo_intento_en = $2 WHERE id_facturacion_venta = $1", idFacturacion, hasta);

    /// <summary>Deja a la venta con su espejo pendiente (número 1 de la serie FB) y la espera de 30 s de un intento sin respuesta, y
    /// reinicia el espía. El reloj queda donde estaba: la espera de la decisión todavía no venció.</summary>
    private static async Task DejarEspejoPendienteAsync(Ctx c, Venta venta)
    {
        c.Wsfe.Solicitar = RespondeHttp500();
        var estado = await ProcesarAsync(c.Admin, venta.IdFacturacion);
        Assert.Equal(EstadosFiscalesDeVenta.Pendiente, estado.Estado);
        c.Wsfe.Reiniciar();
    }

    private static IEnumerable<string> Numeros(EspiaWsfe wsfe) =>
        wsfe.CuerposDeSolicitud.Select(cuerpo => CampoDelCuerpo(cuerpo, "CbteDesde"));

    // ===================================================================================================
    // Selección, orden y tope
    // ===================================================================================================

    [Fact]
    public async Task UnLoteEmiteLasPendientesDelPuntoDeVentaEnOrdenYDevuelveElResumen()
    {
        var c = await PrepararAsync("loteorden");
        var ventas = await SembrarVariasAsync(c, 3);

        var lote = await ProcesarLoteAsync(c.Admin, c.IdPuntoVenta);

        Assert.Equal((3, 0, 0, 0, false), (lote.Emitidas, lote.Rechazadas, lote.Canceladas, lote.PendientesRestantes, lote.CortadoPorPresupuesto));
        Assert.Empty(lote.SeriesBloqueadas);
        Assert.Equal(ventas.Select(v => v.IdFacturacion), lote.Resultados.Select(r => r.IdFacturacion));
        Assert.Equal(ventas.Select(v => v.IdVenta), lote.Resultados.Select(r => r.IdComprobanteVenta));
        Assert.All(lote.Resultados, r => Assert.Equal(EstadosFiscalesDeVenta.Aprobado, r.Fiscal.Estado));
        Assert.Equal([1L, 2L, 3L], lote.Resultados.Select(r => r.Fiscal.Comprobante!.Numero));
        Assert.Equal(["1", "2", "3"], Numeros(c.Wsfe));
        Assert.Equal(3, (await LeerEspejosAsync(c)).Count);

        // Idempotente: otro lote no encuentra nada que hacer y no llama a ARCA.
        c.Wsfe.Reiniciar();
        var vacio = await ProcesarLoteAsync(c.Admin, c.IdPuntoVenta);
        Assert.Equal((0, 0, 0, 0), (vacio.Emitidas, vacio.Rechazadas, vacio.Canceladas, vacio.PendientesRestantes));
        Assert.Empty(vacio.Resultados);
        Assert.Empty(c.Wsfe.Operaciones);
    }

    [Fact]
    public async Task ElMaximoAcotaElLoteYSinMaximoSonDiez()
    {
        var c = await PrepararAsync("lotemax");
        await SembrarVariasAsync(c, 12);

        var sinMaximo = await ProcesarLoteAsync(c.Admin, c.IdPuntoVenta);
        Assert.Equal((10, 2), (sinMaximo.Emitidas, sinMaximo.PendientesRestantes));
        Assert.Equal(10, c.Wsfe.Contar("FECAESolicitar"));

        var uno = await ProcesarLoteAsync(c.Admin, c.IdPuntoVenta, 1);
        Assert.Equal((1, 1), (uno.Emitidas, uno.PendientesRestantes));
        Assert.Equal(11, c.Wsfe.Contar("FECAESolicitar"));

        var tope = await ProcesarLoteAsync(c.Admin, c.IdPuntoVenta, 25);
        Assert.Equal((1, 0), (tope.Emitidas, tope.PendientesRestantes));
        Assert.Equal(12, (await LeerEspejosAsync(c)).Count);
    }

    [Fact]
    public async Task ElTopeDeVeinticincoSeRespetaConMasPendientesQueElTope()
    {
        var c = await PrepararAsync("lotetope");
        await SembrarVariasAsync(c, 26);

        var lote = await ProcesarLoteAsync(c.Admin, c.IdPuntoVenta, 25);

        Assert.Equal((25, 1), (lote.Emitidas, lote.PendientesRestantes));
        Assert.Equal(25, c.Wsfe.Contar("FECAESolicitar"));
    }

    [Fact]
    public async Task LosParametrosInvalidosSeRechazanYUnPuntoDeVentaAjenoEsUn404Primero()
    {
        var c = await PrepararAsync("loteval");
        var otro = await PrepararAsync("loteotro");

        // 404 antes que la validación del máximo: el punto de venta inexistente y el de otro tenant.
        await AssertCodigoAsync(
            await PostLoteAsync(c.Admin, new { idPuntoVenta = 987654, maximo = 0 }), HttpStatusCode.NotFound, "no_encontrado");
        await AssertCodigoAsync(
            await PostLoteAsync(c.Admin, new { idPuntoVenta = otro.IdPuntoVenta, maximo = 99 }), HttpStatusCode.NotFound, "no_encontrado");

        foreach (var maximo in new[] { 0, -1, 26 })
        {
            await AssertCodigoAsync(
                await PostLoteAsync(c.Admin, new { idPuntoVenta = c.IdPuntoVenta, maximo }),
                HttpStatusCode.BadRequest, "solicitud_invalida");
        }

        await AssertCodigoAsync(await PostLoteAsync(c.Admin, new { }), HttpStatusCode.BadRequest, "solicitud_invalida");
        await AssertCodigoAsync(
            await PostLoteAsync(c.Admin, new { idPuntoVenta = c.IdPuntoVenta, maximo = "muchos" }),
            HttpStatusCode.BadRequest, "cuerpo_invalido");

        // El tope exacto y el uno se aceptan.
        Assert.Equal(0, (await ProcesarLoteAsync(c.Admin, c.IdPuntoVenta, 25)).Emitidas);
        Assert.Equal(0, (await ProcesarLoteAsync(c.Admin, c.IdPuntoVenta, 1)).Emitidas);
        Assert.Empty(c.Wsfe.Operaciones);
    }

    [Fact]
    public async Task ElLoteNoToqueNiInformaLasDecisionesDeOtroPuntoDeVentaNiDeOtroTenant()
    {
        var c = await PrepararAsync("loteais");
        var ajeno = await PrepararAsync("loteaje");
        var propia = await SembrarVentaDecididaAsync(c);

        var idOtroPuntoVenta = await SembrarOtroPuntoDeVentaAsync(c);
        await using (var db = fixture.CrearContextoDeAplicacion(new TenantActualFijo(ModoDeAcceso.Tenant, c.IdTenant)))
        {
            var ahora = DateTimeOffset.UtcNow;
            db.TurnosCaja.Add(new TurnoCaja
            {
                IdTenant = c.IdTenant, IdPuntoVenta = idOtroPuntoVenta, IdEmpleadoApertura = c.IdUsuario, FechaApertura = ahora,
                FondoInicial = 0m, Estado = EstadoTurno.Abierto, CreatedAt = ahora, UpdatedAt = ahora
            });
            await db.SaveChangesAsync();
        }

        var idTurnoDelOtro = (await ConsultarAsync(
            c, "SELECT id_turno_caja FROM turnos_caja WHERE id_punto_venta = $1", l => l.GetInt32(0), idOtroPuntoVenta)).Single();
        var delOtroPuntoDeVenta = await SembrarVentaDecididaAsync(
            c with { IdPuntoVenta = idOtroPuntoVenta, IdTurno = idTurnoDelOtro }, sembrarAcumulados: false);
        var deOtroTenant = await SembrarVentaDecididaAsync(ajeno);

        // La del otro punto de venta ya tiene su espejo pendiente (cabeza de su serie) y su espera vencida: es lo más tentador.
        await DejarEspejoPendienteAsync(c, delOtroPuntoDeVenta);
        var cabezaAjena = await LeerDecisionAsync(c, delOtroPuntoDeVenta.IdFacturacion);
        c.Reloj.Ahora = c.Reloj.Ahora.AddMinutes(1);
        c.Wsfe.Consultar = Responde(ConsultarNoEncontrado());
        c.Wsfe.Solicitar = Responde(FecaeAprobado());

        var lote = await ProcesarLoteAsync(c.Admin, c.IdPuntoVenta);

        Assert.Equal((1, 0), (lote.Emitidas, lote.PendientesRestantes));
        Assert.Equal(propia.IdFacturacion, Assert.Single(lote.Resultados).IdFacturacion);
        Assert.Empty(lote.SeriesBloqueadas);
        Assert.Equal(["FECAESolicitar"], c.Wsfe.Operaciones);
        Assert.Empty(ajeno.Wsfe.Operaciones);
        Assert.Equal(cabezaAjena, await LeerDecisionAsync(c, delOtroPuntoDeVenta.IdFacturacion));
        Assert.Equal("pendiente", (await LeerDecisionAsync(ajeno, deOtroTenant.IdFacturacion)).Estado);

        // El otro punto de venta ve lo suyo y solo lo suyo.
        var delOtro = await ProcesarLoteAsync(c.Admin, idOtroPuntoVenta);
        Assert.Equal(delOtroPuntoDeVenta.IdFacturacion, Assert.Single(delOtro.Resultados).IdFacturacion);
        Assert.Equal((1, 0), (delOtro.Emitidas, delOtro.PendientesRestantes));
        Assert.Equal("pendiente", (await LeerDecisionAsync(ajeno, deOtroTenant.IdFacturacion)).Estado);
    }

    // ===================================================================================================
    // La espera registrada
    // ===================================================================================================

    [Fact]
    public async Task UnaDecisionEnEsperaNoSeIntentaHastaQueVence()
    {
        var c = await PrepararAsync("loteespera");
        var enEspera = await SembrarVentaDecididaAsync(c);
        var libre = await SembrarVentaDecididaAsync(c, sembrarAcumulados: false);
        var justo = await SembrarVentaDecididaAsync(c, sembrarAcumulados: false);
        await PonerEsperaAsync(enEspera.IdFacturacion, c.Reloj.Ahora.AddMinutes(5));
        await PonerEsperaAsync(justo.IdFacturacion, c.Reloj.Ahora);

        var lote = await ProcesarLoteAsync(c.Admin, c.IdPuntoVenta);

        // La que espera no se toca (ni red ni intento); la que vence justo ahora entra; la pendiente cuenta como restante.
        Assert.Equal([libre.IdFacturacion, justo.IdFacturacion], lote.Resultados.Select(r => r.IdFacturacion));
        Assert.Equal((2, 1), (lote.Emitidas, lote.PendientesRestantes));
        Assert.Equal(2, c.Wsfe.Contar("FECAESolicitar"));
        var intacta = await LeerDecisionAsync(c, enEspera.IdFacturacion);
        Assert.Equal(("pendiente", 0), (intacta.Estado, intacta.Intentos));
        Assert.Equal(c.Reloj.Ahora.AddMinutes(5), intacta.ProximoIntento);

        // Pasada la espera, el lote la intenta.
        c.Wsfe.Reiniciar();
        c.Reloj.Ahora = c.Reloj.Ahora.AddMinutes(6);
        var siguiente = await ProcesarLoteAsync(c.Admin, c.IdPuntoVenta);
        Assert.Equal(enEspera.IdFacturacion, Assert.Single(siguiente.Resultados).IdFacturacion);
        Assert.Equal((1, 0), (siguiente.Emitidas, siguiente.PendientesRestantes));
        Assert.Equal(1, c.Wsfe.Contar("FECAESolicitar"));
    }

    [Fact]
    public async Task ElEndpointDeUnaDecisionSueltaSigueIgnorandoLaEspera()
    {
        var c = await PrepararAsync("loteignora");
        var venta = await SembrarVentaDecididaAsync(c);
        await PonerEsperaAsync(venta.IdFacturacion, c.Reloj.Ahora.AddHours(1));

        var estado = await ProcesarAsync(c.Admin, venta.IdFacturacion);

        Assert.Equal(EstadosFiscalesDeVenta.Aprobado, estado.Estado);
        Assert.Equal(1, c.Wsfe.Contar("FECAESolicitar"));
    }

    // ===================================================================================================
    // Cabeza de serie
    // ===================================================================================================

    [Fact]
    public async Task ElEspejoPendienteDeLaSerieSeResuelveAntesQueLasCandidatasEnLaMismaLlamada()
    {
        var c = await PrepararAsync("lotecabeza");
        var candidata = await SembrarVentaDecididaAsync(c);
        var cabeza = await SembrarVentaDecididaAsync(c, sembrarAcumulados: false);
        await DejarEspejoPendienteAsync(c, cabeza);
        c.Reloj.Ahora = c.Reloj.Ahora.AddSeconds(31);
        c.Wsfe.Consultar = Responde(ConsultarNoEncontrado());
        c.Wsfe.Solicitar = Responde(FecaeAprobado());

        var lote = await ProcesarLoteAsync(c.Admin, c.IdPuntoVenta);

        // El cabeza (id mayor) va primero: se consulta antes de pedir y conserva su número; la candidata sigue con el 2.
        Assert.Equal([cabeza.IdFacturacion, candidata.IdFacturacion], lote.Resultados.Select(r => r.IdFacturacion));
        Assert.Equal((2, 0), (lote.Emitidas, lote.PendientesRestantes));
        Assert.Empty(lote.SeriesBloqueadas);
        Assert.Equal(["FECompConsultar", "FECAESolicitar", "FECAESolicitar"], c.Wsfe.Operaciones);
        Assert.Equal(["1", "2"], Numeros(c.Wsfe));
        Assert.Equal([1L, 2L], lote.Resultados.Select(r => r.Fiscal.Comprobante!.Numero));
    }

    [Fact]
    public async Task ElCabezaEnEsperaSaltaTodaSuSerieSinRedNiPenalidadYNoFrenaLasOtrasSeries()
    {
        var c = await PrepararAsync("lotecabespera");
        var candidata = await SembrarVentaDecididaAsync(c);
        var cabeza = await SembrarVentaDecididaAsync(c, sembrarAcumulados: false);
        var deOtraSerie = await SembrarVentaDecididaAsync(c, c.IdClienteRi, sembrarAcumulados: false);
        await DejarEspejoPendienteAsync(c, cabeza);
        c.Wsfe.Solicitar = Responde(FecaeAprobado());
        var cabezaAntes = await LeerDecisionAsync(c, cabeza.IdFacturacion);
        Assert.True(cabezaAntes.ProximoIntento > c.Reloj.Ahora);

        // Con maximo 1: la candidata saltada de la serie B (la más vieja) no gasta el cupo de la factura A.
        var lote = await ProcesarLoteAsync(c.Admin, c.IdPuntoVenta, 1);

        // Solo se intentó la factura A: el cabeza espera y su candidata no se prueba (ni consulta ni pedido ni intento).
        Assert.Equal(deOtraSerie.IdFacturacion, Assert.Single(lote.Resultados).IdFacturacion);
        Assert.Equal([new SerieBloqueadaDeLote(CodigoFacturaB, ProcesadorDeFacturacionesPorLote.CabezaEnEspera)], lote.SeriesBloqueadas);
        Assert.Equal((1, 2), (lote.Emitidas, lote.PendientesRestantes));
        Assert.Equal(["FECAESolicitar"], c.Wsfe.Operaciones);
        Assert.Contains(await LeerSeriesAsync(c), s => s.CodigoAfip == CodigoFacturaA && s.Proximo == 2);
        Assert.DoesNotContain(await LeerSeriesAsync(c), s => s.CodigoAfip == CodigoFacturaB && s.Proximo != 2);
        Assert.Equal(cabezaAntes, await LeerDecisionAsync(c, cabeza.IdFacturacion));
        var sinPenalidad = await LeerDecisionAsync(c, candidata.IdFacturacion);
        Assert.Equal(("pendiente", 0, null), (sinPenalidad.Estado, sinPenalidad.Intentos, sinPenalidad.ProximoIntento));

        // Vencida la espera del cabeza, el lote retoma la serie.
        c.Wsfe.Reiniciar();
        c.Reloj.Ahora = c.Reloj.Ahora.AddMinutes(1);
        c.Wsfe.Consultar = Responde(ConsultarNoEncontrado());
        var siguiente = await ProcesarLoteAsync(c.Admin, c.IdPuntoVenta);
        Assert.Equal((2, 0), (siguiente.Emitidas, siguiente.PendientesRestantes));
        Assert.Equal([cabeza.IdFacturacion, candidata.IdFacturacion], siguiente.Resultados.Select(r => r.IdFacturacion));
    }

    [Fact]
    public async Task UnCabezaQueSigueSinRespuestaMantieneBloqueadaSuSerieSinProbarLasCandidatas()
    {
        var c = await PrepararAsync("lotecabpend");
        var candidata = await SembrarVentaDecididaAsync(c);
        var cabeza = await SembrarVentaDecididaAsync(c, sembrarAcumulados: false);
        await DejarEspejoPendienteAsync(c, cabeza);
        c.Reloj.Ahora = c.Reloj.Ahora.AddSeconds(31);
        c.Wsfe.Consultar = Responde(ConsultarNoEncontrado());

        var lote = await ProcesarLoteAsync(c.Admin, c.IdPuntoVenta);

        // El cabeza se intentó (consulta y pedido sin respuesta): su espejo sigue pendiente y la candidata ni se prueba.
        Assert.Equal(cabeza.IdFacturacion, Assert.Single(lote.Resultados).IdFacturacion);
        Assert.Equal([new SerieBloqueadaDeLote(CodigoFacturaB, ProcesadorDeFacturacionesPorLote.CabezaPendiente)], lote.SeriesBloqueadas);
        Assert.Equal((0, 2), (lote.Emitidas, lote.PendientesRestantes));
        Assert.Equal(["FECompConsultar", "FECAESolicitar"], Pasos(c.Wsfe));
        var sinTocar = await LeerDecisionAsync(c, candidata.IdFacturacion);
        Assert.Equal(("pendiente", 0, null), (sinTocar.Estado, sinTocar.Intentos, sinTocar.ProximoIntento));
        Assert.Equal(2, (await LeerDecisionAsync(c, cabeza.IdFacturacion)).Intentos);
    }

    [Fact]
    public async Task UnaSerieQueDaSerieBloqueadaNoSeVuelveAIntentarEnLaMismaLlamada()
    {
        var c = await PrepararAsync("loteserie");
        var ventas = await SembrarVariasAsync(c, 3);
        var deOtraSerie = await SembrarVentaDecididaAsync(c, c.IdClienteRi, sembrarAcumulados: false);
        c.Wsfe.Solicitar = RespondeHttp500();

        var lote = await ProcesarLoteAsync(c.Admin, c.IdPuntoVenta);

        // La primera queda sin respuesta (su espejo detiene la serie), la segunda lo descubre como serie_bloqueada y la tercera ni
        // se prueba. La factura A, de otra serie, sigue su camino (también sin respuesta, porque el espía contesta 500).
        Assert.Equal(
            [ventas[0].IdFacturacion, ventas[1].IdFacturacion, deOtraSerie.IdFacturacion],
            lote.Resultados.Select(r => r.IdFacturacion));
        Assert.StartsWith(ServicioDeFacturacionDeVentas.SerieBloqueada + ":", lote.Resultados[1].Fiscal.Error, StringComparison.Ordinal);
        Assert.Equal([new SerieBloqueadaDeLote(CodigoFacturaB, ServicioDeFacturacionDeVentas.SerieBloqueada)], lote.SeriesBloqueadas);
        Assert.Equal(0, (await LeerDecisionAsync(c, ventas[2].IdFacturacion)).Intentos);
        Assert.Equal(0, (await LeerDecisionAsync(c, ventas[1].IdFacturacion)).Intentos);
        Assert.Equal((0, 4), (lote.Emitidas, lote.PendientesRestantes));
    }

    // ===================================================================================================
    // Aislamiento y cancelación
    // ===================================================================================================

    [Fact]
    public async Task UnaDecisionQueFallaUnaCompuertaNoImpideLasSiguientes()
    {
        var c = await PrepararAsync("loteisla");
        var conCompuerta = await SembrarVentaDecididaAsync(c, c.IdClienteNoResp);
        var sana = await SembrarVentaDecididaAsync(c, sembrarAcumulados: false);

        var lote = await ProcesarLoteAsync(c.Admin, c.IdPuntoVenta);

        Assert.Equal([conCompuerta.IdFacturacion, sana.IdFacturacion], lote.Resultados.Select(r => r.IdFacturacion));
        Assert.Equal(EstadosFiscalesDeVenta.Pendiente, lote.Resultados[0].Fiscal.Estado);
        Assert.StartsWith("condicion_fiscal_receptor_no_mapeada:", lote.Resultados[0].Fiscal.Error, StringComparison.Ordinal);
        Assert.Equal(EstadosFiscalesDeVenta.Aprobado, lote.Resultados[1].Fiscal.Estado);
        Assert.Equal((1, 1), (lote.Emitidas, lote.PendientesRestantes));
        Assert.Equal(1, (await LeerDecisionAsync(c, conCompuerta.IdFacturacion)).Intentos);
    }

    [Fact]
    public async Task UnaDecisionSinRespuestaDefinitivaNoImpideLasDeOtraSerie()
    {
        var c = await PrepararAsync("loteslow");
        var sinRespuesta = await SembrarVentaDecididaAsync(c);
        var deOtraSerie = await SembrarVentaDecididaAsync(c, c.IdClienteRi, sembrarAcumulados: false);
        c.Wsfe.Solicitar = async req =>
        {
            var tipo = CampoDelCuerpo(await req.Content!.ReadAsStringAsync(), "CbteTipo");
            return tipo == "1" ? RespuestaXml(FecaeAprobado()) : new HttpResponseMessage(HttpStatusCode.InternalServerError);
        };

        var lote = await ProcesarLoteAsync(c.Admin, c.IdPuntoVenta);

        Assert.Equal(EstadosFiscalesDeVenta.Pendiente, lote.Resultados[0].Fiscal.Estado);
        Assert.Equal(sinRespuesta.IdFacturacion, lote.Resultados[0].IdFacturacion);
        Assert.Equal(deOtraSerie.IdFacturacion, lote.Resultados[1].IdFacturacion);
        Assert.Equal(EstadosFiscalesDeVenta.Aprobado, lote.Resultados[1].Fiscal.Estado);
        Assert.Equal((1, 1), (lote.Emitidas, lote.PendientesRestantes));
        Assert.Equal(1, (await LeerDecisionAsync(c, sinRespuesta.IdFacturacion)).Intentos);
    }

    /// <summary>Hace fallar el commit número <paramref name="numero"/> del host: una excepción que NO es del presupuesto.</summary>
    [Fact]
    public async Task UnaDecisionQueLanzaNoImpideLasSiguientesNiHaceLanzarAlLote()
    {
        var c = await PrepararAsync("loteexc");
        var ventas = await SembrarVariasAsync(c, 2);
        var falla = new InterceptorQueFallaAlComitar(1);
        var cliente = await ClienteConServiciosAsync(
            c, services => services.AddDbContext<WaysDbContext>((_, opciones) => opciones.AddInterceptors(falla)));
        falla.Armado = true;

        var lote = await ProcesarLoteAsync(cliente, c.IdPuntoVenta);

        Assert.Equal([ventas[0].IdFacturacion, ventas[1].IdFacturacion], lote.Resultados.Select(r => r.IdFacturacion));
        Assert.Equal(EstadosFiscalesDeVenta.Pendiente, lote.Resultados[0].Fiscal.Estado);
        Assert.StartsWith("error_inesperado:", lote.Resultados[0].Fiscal.Error, StringComparison.Ordinal);
        Assert.Equal(EstadosFiscalesDeVenta.Aprobado, lote.Resultados[1].Fiscal.Estado);
        Assert.Equal((1, 1, false), (lote.Emitidas, lote.PendientesRestantes, lote.CortadoPorPresupuesto));
        Assert.Equal(1, c.Wsfe.Contar("FECAESolicitar"));
        Assert.Equal(1, Assert.Single(await LeerEspejosAsync(c)).Numero);
    }

    [Fact]
    public async Task UnaVentaAnuladaAntesDeProcesarseTerminaCanceladaDentroDelLote()
    {
        var c = await PrepararAsync("lotecanc");
        var anulada = await SembrarVentaDecididaAsync(c);
        var sana = await SembrarVentaDecididaAsync(c, sembrarAcumulados: false);
        var respuestaDeAnulacion = await c.Admin.PostAsync($"/api/ventas/{anulada.IdVenta}/anulacion", null);
        Assert.Equal(HttpStatusCode.OK, respuestaDeAnulacion.StatusCode);

        var lote = await ProcesarLoteAsync(c.Admin, c.IdPuntoVenta);

        Assert.Equal((1, 0, 1, 0), (lote.Emitidas, lote.Rechazadas, lote.Canceladas, lote.PendientesRestantes));
        Assert.Equal(EstadosFiscalesDeVenta.Cancelado, lote.Resultados[0].Fiscal.Estado);
        Assert.Equal(anulada.IdFacturacion, lote.Resultados[0].IdFacturacion);
        Assert.Equal(sana.IdFacturacion, lote.Resultados[1].IdFacturacion);
        Assert.Equal("cancelada", (await LeerDecisionAsync(c, anulada.IdFacturacion)).Estado);
        Assert.Equal(1, c.Wsfe.Contar("FECAESolicitar"));
    }

    [Fact]
    public async Task UnRechazoDefinitivoDentroDelLoteSeInformaYNoFrenaElResto()
    {
        var c = await PrepararAsync("loterech");
        var ventas = await SembrarVariasAsync(c, 2);
        var numeroDeSolicitud = 0;
        c.Wsfe.Solicitar = _ => Task.FromResult(RespuestaXml(
            Interlocked.Increment(ref numeroDeSolicitud) == 1 ? FecaeRechazado() : FecaeAprobado()));

        var lote = await ProcesarLoteAsync(c.Admin, c.IdPuntoVenta);

        Assert.Equal((1, 1, 0, 0), (lote.Emitidas, lote.Rechazadas, lote.Canceladas, lote.PendientesRestantes));
        Assert.Equal(EstadosFiscalesDeVenta.Rechazado, lote.Resultados[0].Fiscal.Estado);
        Assert.Equal(ventas[0].IdFacturacion, lote.Resultados[0].IdFacturacion);
        Assert.Equal(1L, lote.Resultados[1].Fiscal.Comprobante!.Numero);
    }

    // ===================================================================================================
    // Presupuesto de tiempo
    // ===================================================================================================

    private (ProcesadorDeFacturacionesPorLote Procesador, WaysDbContext Db) NuevoProcesadorDirecto(Ctx c, OpcionesFiscales opciones)
    {
        var host = fixture.WithWebHostBuilder(builder => builder.ConfigureServices(c.Servicios));
        var servicios = host.Services;
        var db = fixture.CrearContextoDeAplicacion(new TenantActualFijo(ModoDeAcceso.Tenant, c.IdTenant));
        var contexto = new ContextoFijo(c.IdTenant, c.IdUsuario);
        var parametros = new ServicioDeParametros(db, c.Reloj, new ServicioDeLotes(db, c.Reloj, contexto));
        var opcionesInyectadas = Options.Create(opciones);
        var fiscal = new ServicioDeFacturacionFiscal(
            db, c.Reloj, contexto, opcionesInyectadas, servicios.GetRequiredService<IClienteWsaa>(),
            servicios.GetRequiredService<IClienteWsfe>(), servicios.GetRequiredService<IRepositorioDeTicketDeAcceso>(),
            new CifradoDeClavesFiscales(db, servicios.GetRequiredService<IConfiguration>()), parametros);
        var facturacion = new ServicioDeFacturacionDeVentas(
            db, c.Reloj, contexto, fiscal, NullLogger<ServicioDeFacturacionDeVentas>.Instance);
        return (new ProcesadorDeFacturacionesPorLote(
            db, c.Reloj, contexto, facturacion, opcionesInyectadas, NullLogger<ProcesadorDeFacturacionesPorLote>.Instance), db);
    }

    private async Task<long> SesionesIdleEnTransaccionAsync()
    {
        await using var conexion = await AbrirOwnerAsync();
        await using var comando = conexion.CreateCommand();
        comando.CommandText =
            "SELECT count(*) FROM pg_stat_activity WHERE state LIKE 'idle in transaction%' AND pid <> pg_backend_pid()";
        return Convert.ToInt64(await comando.ExecuteScalarAsync());
    }

    [Fact]
    public async Task UnCortePorPresupuestoDetieneElLoteNoSeAnotaComoIntentoYLaMismaInstanciaSigueFuncionando()
    {
        var c = await PrepararAsync("lotecorte");
        var ventas = await SembrarVariasAsync(c, 2);
        var llego = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        c.Wsfe.SolicitarConToken = async (_, ct) =>
        {
            llego.TrySetResult();
            await Task.Delay(Timeout.Infinite, ct);
            throw new InvalidOperationException("No llega: la espera solo termina cancelada.");
        };
        var opciones = new OpcionesFiscales { PresupuestoDeLoteSegundos = 1.5 };
        var (procesador, db) = NuevoProcesadorDirecto(c, opciones);
        await using var contextoDelProcesador = db;

        var lote = await ConTopeAsync(procesador.ProcesarLoteAsync(new SolicitudDeLoteDeFacturacion(c.IdPuntoVenta, null)));

        // El pedido de la primera quedó colgado hasta el corte: el lote termina, informa el corte y no intenta la segunda.
        Assert.True(llego.Task.IsCompletedSuccessfully);
        Assert.True(lote.CortadoPorPresupuesto);
        Assert.Empty(lote.Resultados);
        Assert.Equal((0, 2), (lote.Emitidas, lote.PendientesRestantes));
        Assert.Equal(1, c.Wsfe.Contar("FECAESolicitar"));

        // No es un fallo de la decisión: sin intento, sin error y sin espera. El espejo sí quedó, con su número.
        var decision = await LeerDecisionAsync(c, ventas[0].IdFacturacion);
        Assert.Equal(("pendiente", 0, null, null), (decision.Estado, decision.Intentos, decision.UltimoError, decision.ProximoIntento));
        var espejo = Assert.Single(await LeerEspejosAsync(c));
        Assert.Equal(("pendiente", 1L), (espejo.Resultado, espejo.Numero));
        Assert.Equal(("pendiente", 0), ((await LeerDecisionAsync(c, ventas[1].IdFacturacion)).Estado, (await LeerDecisionAsync(c, ventas[1].IdFacturacion)).Intentos));

        // Sin transacción abierta ni lock de la serie retenido.
        Assert.Equal(0, await SesionesIdleEnTransaccionAsync());
        await using (var conexion = await AbrirOwnerAsync())
        {
            await using var comando = conexion.CreateCommand();
            comando.CommandText = "SELECT 1 FROM numeraciones_fiscales WHERE id_punto_venta = $1 FOR UPDATE NOWAIT";
            comando.Parameters.Add(new Npgsql.NpgsqlParameter { Value = c.IdPuntoVenta });
            Assert.Equal(1, Convert.ToInt32(await comando.ExecuteScalarAsync()));
        }

        // La misma instancia, con un presupuesto normal: consulta el espejo antes de pedir y emite las dos.
        c.Wsfe.SolicitarConToken = null;
        c.Wsfe.Reiniciar();
        c.Wsfe.Consultar = Responde(ConsultarNoEncontrado());
        c.Wsfe.Solicitar = Responde(FecaeAprobado());
        opciones.PresupuestoDeLoteSegundos = null;

        var siguiente = await ConTopeAsync(procesador.ProcesarLoteAsync(new SolicitudDeLoteDeFacturacion(c.IdPuntoVenta, null)));

        Assert.False(siguiente.CortadoPorPresupuesto);
        Assert.Equal((2, 0), (siguiente.Emitidas, siguiente.PendientesRestantes));
        Assert.Equal(["FECompConsultar", "FECAESolicitar", "FECAESolicitar"], c.Wsfe.Operaciones);
        Assert.Equal(["1", "2"], Numeros(c.Wsfe));
    }

    // ===================================================================================================
    // Concurrencia
    // ===================================================================================================

    [Fact]
    public async Task DosLotesSimultaneosDelMismoPuntoDeVentaEmitenCadaDecisionUnaSolaVez()
    {
        var c = await PrepararAsync("loteconc");
        var ventas = await SembrarVariasAsync(c, 3);
        var llego = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var continuar = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var pedidos = 0;
        c.Wsfe.Solicitar = async _ =>
        {
            if (Interlocked.Increment(ref pedidos) == 1)
            {
                llego.TrySetResult();
                await continuar.Task;
            }

            return RespuestaXml(FecaeAprobado());
        };

        var primero = ProcesarLoteAsync(c.Admin, c.IdPuntoVenta);
        await llego.Task.WaitAsync(Tope);

        // El primer lote sostiene el lock de la serie dentro del pedido a ARCA: el segundo ve el espejo pendiente de la cabeza,
        // encuentra la serie ocupada y se rinde sin consultar ni pedir nada.
        var segundo = await ConTopeAsync(ProcesarLoteAsync(c.Admin, c.IdPuntoVenta));
        Assert.Equal(0, segundo.Emitidas);
        Assert.Equal([new SerieBloqueadaDeLote(CodigoFacturaB, ServicioDeFacturacionDeVentas.SerieOcupada)], segundo.SeriesBloqueadas);
        var enCabeza = Assert.Single(segundo.Resultados);
        Assert.Equal(ventas[0].IdFacturacion, enCabeza.IdFacturacion);
        Assert.StartsWith(ServicioDeFacturacionDeVentas.SerieOcupada + ":", enCabeza.Fiscal.Error, StringComparison.Ordinal);
        Assert.Equal(["FECAESolicitar"], c.Wsfe.Operaciones);

        continuar.TrySetResult();
        var resultadoDelPrimero = await ConTopeAsync(primero);

        Assert.Equal((3, 0), (resultadoDelPrimero.Emitidas, resultadoDelPrimero.PendientesRestantes));
        Assert.Equal(["1", "2", "3"], Numeros(c.Wsfe).Order());
        Assert.Equal(3, c.Wsfe.Contar("FECAESolicitar"));
        Assert.Equal(0, c.Wsfe.Contar("FECompConsultar"));
        Assert.Equal(3, (await LeerEspejosAsync(c)).Count);
    }

    // ===================================================================================================
    // Timeout de las llamadas a ARCA
    // ===================================================================================================

    [Fact]
    public async Task UnPedidoQueSuperaElTimeoutConfiguradoEsNoDefinitivoYDejaElEspejoPendienteConSuNumero()
    {
        var c = await PrepararAsync("lotetimeout");
        var venta = await SembrarVentaDecididaAsync(c);
        c.Wsfe.SolicitarConToken = async (_, ct) =>
        {
            await Task.Delay(TimeSpan.FromSeconds(5), ct);
            return RespuestaXml(FecaeAprobado());
        };
        var cliente = await ClienteConServiciosAsync(
            c, services => services.Configure<OpcionesFiscales>(o => o.TimeoutDeLlamadaSegundos = 0.2));

        var estado = await ConTopeAsync(ProcesarAsync(cliente, venta.IdFacturacion));

        // Un timeout no es un rechazo ni una cancelación del llamador: la decisión sigue pendiente con el intento anotado.
        Assert.Equal(EstadosFiscalesDeVenta.Pendiente, estado.Estado);
        Assert.StartsWith("arca_no_definitivo:", estado.Error, StringComparison.Ordinal);
        var decision = await LeerDecisionAsync(c, venta.IdFacturacion);
        Assert.Equal(("pendiente", 1), (decision.Estado, decision.Intentos));
        Assert.StartsWith("arca_no_definitivo:", decision.UltimoError, StringComparison.Ordinal);
        Assert.NotNull(decision.ProximoIntento);
        var espejo = Assert.Single(await LeerEspejosAsync(c));
        Assert.Equal(("pendiente", 1L, null), (espejo.Resultado, espejo.Numero, espejo.Cae));

        // El número sobrevive: la llamada siguiente consulta primero y pide el MISMO número.
        c.Wsfe.SolicitarConToken = null;
        c.Wsfe.Reiniciar();
        c.Wsfe.Consultar = Responde(ConsultarNoEncontrado());
        c.Wsfe.Solicitar = Responde(FecaeAprobado());
        var adoptado = await ProcesarAsync(c.Admin, venta.IdFacturacion);
        Assert.Equal(EstadosFiscalesDeVenta.Aprobado, adoptado.Estado);
        Assert.Equal(1L, adoptado.Comprobante!.Numero);
        Assert.Equal(["1"], Numeros(c.Wsfe));
    }

    // ===================================================================================================
    // Invalidación del ticket de acceso: comparar y descartar
    // ===================================================================================================

    [Fact]
    public async Task DosLlamadasConElMismoTicketRechazadoFirmanUnaSolaVezElTicketNuevo()
    {
        var c = await PrepararAsync("loteticket");
        c.Wsaa.CuerpoPorFirma = n => LoginCmsGolden($"TOKEN_{n}");
        var deLaSerieB = await SembrarVentaDecididaAsync(c);
        var deLaSerieA = await SembrarVentaDecididaAsync(c, c.IdClienteRi, sembrarAcumulados: false);

        var tokensDeLosPedidos = new List<string>();
        var bLlego = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var puertaDeB = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        c.Wsfe.Solicitar = async req =>
        {
            var cuerpo = await req.Content!.ReadAsStringAsync();
            var token = CampoDelCuerpo(cuerpo, "Token");
            lock (tokensDeLosPedidos)
            {
                tokensDeLosPedidos.Add($"{CampoDelCuerpo(cuerpo, "CbteTipo")}:{token}");
            }

            if (token != "TOKEN_1")
            {
                return RespuestaXml(FecaeAprobado());
            }

            // Los dos primeros pedidos llevan el ticket 1, que ARCA rechaza con 600. El de la serie B espera a que la serie A
            // termine de renovar el ticket: cuando por fin invalida, el ticket 1 ya no es el guardado.
            if (CampoDelCuerpo(cuerpo, "CbteTipo") == CodigoFacturaB.ToString(System.Globalization.CultureInfo.InvariantCulture))
            {
                bLlego.TrySetResult();
                await puertaDeB.Task;
            }

            return RespuestaXml(FecaeTicketInvalido());
        };

        var tareaB = ProcesarAsync(c.Admin, deLaSerieB.IdFacturacion);
        await bLlego.Task.WaitAsync(Tope);
        var estadoA = await ConTopeAsync(ProcesarAsync(c.Admin, deLaSerieA.IdFacturacion));
        Assert.Equal(EstadosFiscalesDeVenta.Aprobado, estadoA.Estado);
        Assert.Equal(2, c.Wsaa.Solicitudes);

        puertaDeB.TrySetResult();
        var estadoB = await ConTopeAsync(tareaB);

        Assert.Equal(EstadosFiscalesDeVenta.Aprobado, estadoB.Estado);
        Assert.Equal(2, c.Wsaa.Solicitudes);
        lock (tokensDeLosPedidos)
        {
            Assert.Equal(["6:TOKEN_1", "1:TOKEN_1", "1:TOKEN_2", "6:TOKEN_2"], tokensDeLosPedidos);
        }
    }
}
