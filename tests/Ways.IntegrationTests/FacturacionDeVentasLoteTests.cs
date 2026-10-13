using System.Data.Common;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Time.Testing;
using Ways.Application.Abstracciones;
using Ways.Application.Fiscal;
using Ways.Application.Parametros;
using Ways.Application.Stock;
using Ways.Domain.Caja;
using Ways.Domain.Common;
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
/// aislamiento de una decisión que falla (anotada como intento con un código estable y postergada), el corte por presupuesto que
/// no es un fallo y posterga la decisión sin tocar sus intentos, el filtro de punto de venta (candidatas, cabezas y pendientes
/// restantes), el paginado de las candidatas, la falla del propio lote que se propaga, el timeout aplicado a los clientes de WSFE
/// y WSAA, que una firma de WSAA en vuelo no se abandona con el corte, que una falla real de esa firma que llega pasado el
/// presupuesto se anota como intento, que no se arranca una decisión con el presupuesto agotado y la comparación del ticket al
/// invalidarlo. El presupuesto del lote se maneja con un <see cref="FakeTimeProvider"/> que el test avanza recién cuando el espía
/// o la lectura confirman que se llegó al punto buscado, y las carreras se resuelven pausando el espía: esos tests no esperan
/// tiempo. Los tests de pedidos colgados (<c>UnPedidoQueSupera…</c> y <c>UnPedidoColgado…</c>) SÍ esperan tiempo real, a
/// propósito: el timeout del HttpClient (<c>TimeoutDeWsfeDeLosTests</c>, 3 s) es lo que se está probando y solo vence una
/// llamada que se cuelga.
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
        // Una candidata SIN espejo en el otro punto de venta, del mismo tenant: solo el filtro de punto de venta de la consulta de
        // candidatas la deja afuera (la de arriba la deja afuera además por tener un espejo pendiente).
        var sinEspejoDelOtro = await SembrarVentaDecididaAsync(
            c with { IdPuntoVenta = idOtroPuntoVenta, IdTurno = idTurnoDelOtro }, sembrarAcumulados: false);

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
        var intactaDelOtro = await LeerDecisionAsync(c, sinEspejoDelOtro.IdFacturacion);
        Assert.Equal(("pendiente", 0, null), (intactaDelOtro.Estado, intactaDelOtro.Intentos, intactaDelOtro.ProximoIntento));

        // El otro punto de venta ve lo suyo y solo lo suyo: la cabeza primero y después su candidata.
        var delOtro = await ProcesarLoteAsync(c.Admin, idOtroPuntoVenta);
        Assert.Equal(
            [delOtroPuntoDeVenta.IdFacturacion, sinEspejoDelOtro.IdFacturacion], delOtro.Resultados.Select(r => r.IdFacturacion));
        Assert.Equal((2, 0), (delOtro.Emitidas, delOtro.PendientesRestantes));
        Assert.Equal("pendiente", (await LeerDecisionAsync(ajeno, deOtroTenant.IdFacturacion)).Estado);
    }

    /// <summary>Copia por SQL la venta y la decisión de <paramref name="plantilla"/> <paramref name="cantidad"/> veces (solo cambia el
    /// número de la venta): son candidatas válidas para el lote sin pagar la siembra completa de cada una.</summary>
    private Task ClonarDecisionesAsync(Venta plantilla, int cantidad) => EjecutarComoOwnerAsync(
        $"""
        DO $$
        DECLARE
            columnas_venta text;
            columnas_decision text;
            nueva_venta int;
            i int;
        BEGIN
            SELECT string_agg(quote_ident(column_name), ', ' ORDER BY ordinal_position) INTO columnas_venta
            FROM information_schema.columns
            WHERE table_schema = 'public' AND table_name = 'comprobantes_venta'
              AND column_name NOT IN ('id_comprobante_venta', 'numero');
            SELECT string_agg(quote_ident(column_name), ', ' ORDER BY ordinal_position) INTO columnas_decision
            FROM information_schema.columns
            WHERE table_schema = 'public' AND table_name = 'facturaciones_de_venta'
              AND column_name NOT IN ('id_facturacion_venta', 'id_comprobante_venta');
            FOR i IN 1..{cantidad} LOOP
                EXECUTE format(
                    'INSERT INTO comprobantes_venta (%s, numero) SELECT %s, numero + %s FROM comprobantes_venta ' ||
                    'WHERE id_comprobante_venta = %s RETURNING id_comprobante_venta',
                    columnas_venta, columnas_venta, 1000000 + i, {plantilla.IdVenta}) INTO nueva_venta;
                EXECUTE format(
                    'INSERT INTO facturaciones_de_venta (%s, id_comprobante_venta) SELECT %s, %s FROM facturaciones_de_venta ' ||
                    'WHERE id_facturacion_venta = %s',
                    columnas_decision, columnas_decision, nueva_venta, {plantilla.IdFacturacion});
            END LOOP;
        END $$;
        """);

    /// <summary>Cuenta las lecturas de candidatas del lote (una por página).</summary>
    private sealed class InterceptorQueCuentaLasLecturasDeCandidatas : DbCommandInterceptor
    {
        private int _lecturas;

        public int Lecturas => Volatile.Read(ref _lecturas);

        public override ValueTask<InterceptionResult<DbDataReader>> ReaderExecutingAsync(
            DbCommand command, CommandEventData eventData, InterceptionResult<DbDataReader> result,
            CancellationToken cancellationToken = default)
        {
            if (command.CommandText.Contains("NOT EXISTS (SELECT 1 FROM comprobantes_venta e", StringComparison.Ordinal))
            {
                Interlocked.Increment(ref _lecturas);
            }

            return base.ReaderExecutingAsync(command, eventData, result, cancellationToken);
        }
    }

    /// <summary>Las candidatas de una serie detenida se saltean sin gastar cupo, así que se leen de a páginas de 100: más de una
    /// página de ésas seguida de una elegible tiene que llegar a la elegible, y la cantidad de lecturas (3: dos páginas con filas
    /// y una vacía que termina) es la que implica el tamaño de página de 100. Prueba que la lectura no para en la primera
    /// página y que el tamaño de página no crece sin que el test lo note. NO distingue el cursor <c>&gt;</c> del <c>&gt;=</c>:
    /// una fila repetida al principio de la página siguiente ya no es elegible (si se procesó, deja de estar pendiente o en
    /// espera; si está bloqueada, se saltea de nuevo), así que no cambia ningún resultado observable.</summary>
    [Fact]
    public async Task MasDeUnaPaginaDeCandidatasDeUnaSerieEnEsperaNoImpideLlegarALaSiguienteSerie()
    {
        var c = await PrepararAsync("lotepag");
        var cabeza = await SembrarVentaDecididaAsync(c);
        await DejarEspejoPendienteAsync(c, cabeza);
        var plantilla = await SembrarVentaDecididaAsync(c, sembrarAcumulados: false);
        await ClonarDecisionesAsync(plantilla, 105);
        var elegible = await SembrarVentaDecididaAsync(c, c.IdClienteRi, sembrarAcumulados: false);
        c.Wsfe.Solicitar = Responde(FecaeAprobado());
        var lecturas = new InterceptorQueCuentaLasLecturasDeCandidatas();
        var cliente = await ClienteConServiciosAsync(
            c, services => services.AddDbContext<WaysDbContext>((_, opciones) => opciones.AddInterceptors(lecturas)));

        var lote = await ProcesarLoteAsync(cliente, c.IdPuntoVenta);

        // 106 candidatas de la serie B (la plantilla y sus 105 copias) preceden a la factura A: la primera página (100) es
        // toda de la serie en espera, y la elegible está en la segunda.
        Assert.Equal(elegible.IdFacturacion, Assert.Single(lote.Resultados).IdFacturacion);
        Assert.Equal([new SerieBloqueadaDeLote(CodigoFacturaB, ProcesadorDeFacturacionesPorLote.CabezaEnEspera)], lote.SeriesBloqueadas);
        Assert.Equal((1, 107), (lote.Emitidas, lote.PendientesRestantes));
        Assert.Equal(["FECAESolicitar"], c.Wsfe.Operaciones);
        Assert.Equal(0, (await LeerDecisionAsync(c, plantilla.IdFacturacion)).Intentos);
        Assert.Equal(3, lecturas.Lecturas);
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

    /// <summary>Hace fallar los primeros <paramref name="cuantos"/> commits del host con la excepción que arma
    /// <paramref name="excepcion"/>: algo que NO es del presupuesto.</summary>
    private sealed class InterceptorQueFallaLosPrimerosCommits(int cuantos, Func<Exception> excepcion) : DbTransactionInterceptor
    {
        private int _contador;

        public bool Armado { get; set; }

        public override ValueTask<InterceptionResult> TransactionCommittingAsync(
            DbTransaction transaction, TransactionEventData eventData, InterceptionResult result,
            CancellationToken cancellationToken = default)
        {
            if (Armado && Interlocked.Increment(ref _contador) <= cuantos)
            {
                throw excepcion();
            }

            return base.TransactionCommittingAsync(transaction, eventData, result, cancellationToken);
        }
    }

    private async Task<HttpClient> ClienteQueFallaAlComitarAsync(Ctx c, int cuantos, Func<Exception> excepcion)
    {
        var falla = new InterceptorQueFallaLosPrimerosCommits(cuantos, excepcion);
        var cliente = await ClienteConServiciosAsync(
            c, services => services.AddDbContext<WaysDbContext>((_, opciones) => opciones.AddInterceptors(falla)));
        falla.Armado = true;
        return cliente;
    }

    [Fact]
    public async Task UnaDecisionQueLanzaSeAnotaComoIntentoConUnCodigoEstableYNoSeIntentaHastaQueVence()
    {
        var c = await PrepararAsync("loteexc");
        var ventas = await SembrarVariasAsync(c, 2);
        var cliente = await ClienteQueFallaAlComitarAsync(
            c, 1, () => new InvalidOperationException("detalle interno que no tiene que salir"));

        var lote = await ProcesarLoteAsync(cliente, c.IdPuntoVenta);

        // Aislada: la siguiente sigue. Sin detalles internos: el código estable y nada más (ni el tipo de la excepción).
        Assert.Equal([ventas[0].IdFacturacion, ventas[1].IdFacturacion], lote.Resultados.Select(r => r.IdFacturacion));
        Assert.Equal(EstadosFiscalesDeVenta.Pendiente, lote.Resultados[0].Fiscal.Estado);
        Assert.Equal("error_inesperado", lote.Resultados[0].Fiscal.Error);
        Assert.Equal(EstadosFiscalesDeVenta.Aprobado, lote.Resultados[1].Fiscal.Estado);
        Assert.Equal((1, 1, false), (lote.Emitidas, lote.PendientesRestantes, lote.CortadoPorPresupuesto));
        Assert.Equal(1, c.Wsfe.Contar("FECAESolicitar"));
        Assert.Equal(1, Assert.Single(await LeerEspejosAsync(c)).Numero);

        // Anotada como un intento fallido, con la espera inicial de un fallo no definitivo (30 s).
        var lanzo = await LeerDecisionAsync(c, ventas[0].IdFacturacion);
        Assert.Equal(("pendiente", 1, "error_inesperado"), (lanzo.Estado, lanzo.Intentos, lanzo.UltimoError));
        Assert.Equal(c.Reloj.Ahora.AddSeconds(30), lanzo.ProximoIntento);

        // La llamada siguiente no la intenta mientras espera (ni red ni intento nuevo)...
        c.Wsfe.Reiniciar();
        var enEspera = await ProcesarLoteAsync(cliente, c.IdPuntoVenta);
        Assert.Empty(enEspera.Resultados);
        Assert.Equal((0, 1), (enEspera.Emitidas, enEspera.PendientesRestantes));
        Assert.Empty(c.Wsfe.Operaciones);
        Assert.Equal(1, (await LeerDecisionAsync(c, ventas[0].IdFacturacion)).Intentos);

        // ...y vencida la espera, la retoma.
        c.Reloj.Ahora = c.Reloj.Ahora.AddSeconds(31);
        var vencida = await ProcesarLoteAsync(cliente, c.IdPuntoVenta);
        Assert.Equal(ventas[0].IdFacturacion, Assert.Single(vencida.Resultados).IdFacturacion);
        Assert.Equal((1, 0), (vencida.Emitidas, vencida.PendientesRestantes));
    }

    [Fact]
    public async Task UnErrorDeDominioInesperadoSeInformaConSuCodigoYElMaximoDeDecisionesQueLanzanNoDejaSinCupoALasJovenes()
    {
        var c = await PrepararAsync("loteexcmax");
        var ventas = await SembrarVariasAsync(c, 3);
        var cliente = await ClienteQueFallaAlComitarAsync(
            c, 2, () => new ErrorDominio("falla_de_prueba", "mensaje con detalle", 409));

        var primera = await ProcesarLoteAsync(cliente, c.IdPuntoVenta, 2);

        // Dos decisiones que lanzan gastan los dos cupos: la tercera no se intentó. El código de dominio, sin su mensaje.
        Assert.Equal([ventas[0].IdFacturacion, ventas[1].IdFacturacion], primera.Resultados.Select(r => r.IdFacturacion));
        Assert.All(primera.Resultados, r => Assert.Equal("falla_de_prueba", r.Fiscal.Error));
        Assert.Equal((0, 3), (primera.Emitidas, primera.PendientesRestantes));
        Assert.Empty(c.Wsfe.Operaciones);
        foreach (var venta in ventas.Take(2))
        {
            var anotada = await LeerDecisionAsync(c, venta.IdFacturacion);
            Assert.Equal(("pendiente", 1, "falla_de_prueba"), (anotada.Estado, anotada.Intentos, anotada.UltimoError));
        }

        // Postergadas, ya no son las candidatas más viejas: la llamada siguiente llega a la tercera.
        var segunda = await ProcesarLoteAsync(cliente, c.IdPuntoVenta, 2);
        Assert.Equal(ventas[2].IdFacturacion, Assert.Single(segunda.Resultados).IdFacturacion);
        Assert.Equal((1, 2), (segunda.Emitidas, segunda.PendientesRestantes));
        Assert.Equal(1, c.Wsfe.Contar("FECAESolicitar"));
        Assert.Equal(1, (await LeerDecisionAsync(c, ventas[0].IdFacturacion)).Intentos);
        Assert.Equal(1, (await LeerDecisionAsync(c, ventas[1].IdFacturacion)).Intentos);
    }

    /// <summary>Falla la consulta de las candidatas número <c>numero</c> (la lectura del propio lote, fuera de toda decisión).</summary>
    private sealed class InterceptorQueFallaLaLecturaDeCandidatas(int numero) : DbCommandInterceptor
    {
        private int _contador;

        public bool Armado { get; set; }

        public override ValueTask<InterceptionResult<DbDataReader>> ReaderExecutingAsync(
            DbCommand command, CommandEventData eventData, InterceptionResult<DbDataReader> result,
            CancellationToken cancellationToken = default)
        {
            if (Armado
                && command.CommandText.Contains("NOT EXISTS (SELECT 1 FROM comprobantes_venta e", StringComparison.Ordinal)
                && Interlocked.Increment(ref _contador) == numero)
            {
                throw new InvalidOperationException("Falla simulada de la lectura del lote.");
            }

            return base.ReaderExecutingAsync(command, eventData, result, cancellationToken);
        }
    }

    [Fact]
    public async Task UnaFallaDelPropioLoteNoSeTragaComoSiNoHubieraNadaYLoYaComiteadoQueda()
    {
        var c = await PrepararAsync("lotefalla");
        var venta = await SembrarVentaDecididaAsync(c);
        var falla = new InterceptorQueFallaLaLecturaDeCandidatas(2);
        var cliente = await ClienteConServiciosAsync(
            c, services => services.AddDbContext<WaysDbContext>((_, opciones) => opciones.AddInterceptors(falla)));
        falla.Armado = true;

        // La primera página trae la decisión y la emite; la segunda lectura del lote falla.
        var respuesta = await PostLoteAsync(cliente, new { idPuntoVenta = c.IdPuntoVenta });

        await AssertCodigoAsync(respuesta, HttpStatusCode.InternalServerError, "error_interno");
        Assert.Equal("emitida", (await LeerDecisionAsync(c, venta.IdFacturacion)).Estado);
        Assert.Equal(1, c.Wsfe.Contar("FECAESolicitar"));
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

    /// <summary>Un procesador armado a mano sobre un host derivado, con el <see cref="TimeProvider"/> que se le pase: el presupuesto
    /// del lote nace de él, así que un test lo avanza cuando el espía confirma que el pedido llegó, sin esperar tiempo real. Los
    /// valores de <paramref name="opciones"/> también se copian al host, porque de ahí salen los timeouts de los clientes HTTP.</summary>
    private (ProcesadorDeFacturacionesPorLote Procesador, WaysDbContext Db) NuevoProcesadorDirecto(
        Ctx c, OpcionesFiscales opciones, TimeProvider tiempo, params IInterceptor[] interceptores)
    {
        var host = fixture.WithWebHostBuilder(builder => builder.ConfigureServices(services =>
        {
            c.Servicios(services);
            services.Configure<OpcionesFiscales>(o =>
            {
                o.TimeoutDeLlamadaSegundos = opciones.TimeoutDeLlamadaSegundos;
                o.TimeoutDeWsaaSegundos = opciones.TimeoutDeWsaaSegundos;
                o.PresupuestoDeLoteSegundos = opciones.PresupuestoDeLoteSegundos;
            });
        }));
        var servicios = host.Services;
        var db = fixture.CrearContextoDeAplicacion(new TenantActualFijo(ModoDeAcceso.Tenant, c.IdTenant), interceptores);
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
            db, c.Reloj, contexto, facturacion, opcionesInyectadas, tiempo,
            NullLogger<ProcesadorDeFacturacionesPorLote>.Instance), db);
    }

    private async Task<long> SesionesIdleEnTransaccionAsync()
    {
        await using var conexion = await AbrirOwnerAsync();
        await using var comando = conexion.CreateCommand();
        comando.CommandText =
            "SELECT count(*) FROM pg_stat_activity WHERE state LIKE 'idle in transaction%' AND pid <> pg_backend_pid()";
        return Convert.ToInt64(await comando.ExecuteScalarAsync());
    }

    private static Task<ResultadoDeLoteDeFacturacion> LoteDirectoAsync(ProcesadorDeFacturacionesPorLote procesador, int idPuntoVenta) =>
        procesador.ProcesarLoteAsync(new SolicitudDeLoteDeFacturacion(idPuntoVenta, null));

    /// <summary>Un presupuesto de lote más largo que el tope de espera de los tests: si el token del lote naciera del reloj real en
    /// vez del <see cref="TimeProvider"/>, el corte no llegaría a tiempo y el test fallaría por el tope.</summary>
    private static OpcionesFiscales OpcionesConPresupuestoLargo() => new() { PresupuestoDeLoteSegundos = 120 };

    private static readonly TimeSpan MasQueElPresupuesto = TimeSpan.FromSeconds(121);

    [Fact]
    public async Task UnCortePorPresupuestoPosponeLaDecisionSinContarUnIntentoYLaProximaLlamadaLlegaALasDemas()
    {
        var c = await PrepararAsync("lotecorte");
        var cortada = await SembrarVentaDecididaAsync(c);
        var deOtraSerie = await SembrarVentaDecididaAsync(c, c.IdClienteRi, sembrarAcumulados: false);
        var llego = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        c.Wsfe.SolicitarConToken = async (_, ct) =>
        {
            llego.TrySetResult();
            await Task.Delay(Timeout.Infinite, ct);
            throw new InvalidOperationException("No llega: la espera solo termina cancelada.");
        };
        var tiempo = new FakeTimeProvider();
        var (procesador, db) = NuevoProcesadorDirecto(c, OpcionesConPresupuestoLargo(), tiempo);
        await using var contextoDelProcesador = db;

        var tarea = LoteDirectoAsync(procesador, c.IdPuntoVenta);
        await llego.Task.WaitAsync(Tope);
        tiempo.Advance(MasQueElPresupuesto);
        var lote = await ConTopeAsync(tarea);

        // El pedido de la primera quedó colgado hasta el corte: el lote termina, informa el corte y no intenta la otra serie.
        Assert.True(lote.CortadoPorPresupuesto);
        Assert.Empty(lote.Resultados);
        Assert.Equal((0, 2), (lote.Emitidas, lote.PendientesRestantes));
        Assert.Equal(1, c.Wsfe.Contar("FECAESolicitar"));

        // No es un fallo de la decisión: sin intento ni error. Queda postergada un minuto. El espejo sí quedó, con su número.
        var decision = await LeerDecisionAsync(c, cortada.IdFacturacion);
        Assert.Equal(("pendiente", 0, null), (decision.Estado, decision.Intentos, decision.UltimoError));
        Assert.Equal(c.Reloj.Ahora.AddMinutes(1), decision.ProximoIntento);
        var espejo = Assert.Single(await LeerEspejosAsync(c));
        Assert.Equal(("pendiente", 1L), (espejo.Resultado, espejo.Numero));
        var sinTocar = await LeerDecisionAsync(c, deOtraSerie.IdFacturacion);
        Assert.Equal(("pendiente", 0, null, null), (sinTocar.Estado, sinTocar.Intentos, sinTocar.UltimoError, sinTocar.ProximoIntento));

        // Sin transacción abierta ni lock de la serie retenido.
        Assert.Equal(0, await SesionesIdleEnTransaccionAsync());
        await using (var conexion = await AbrirOwnerAsync())
        {
            await using var comando = conexion.CreateCommand();
            comando.CommandText = "SELECT 1 FROM numeraciones_fiscales WHERE id_punto_venta = $1 FOR UPDATE NOWAIT";
            comando.Parameters.Add(new Npgsql.NpgsqlParameter { Value = c.IdPuntoVenta });
            Assert.Equal(1, Convert.ToInt32(await comando.ExecuteScalarAsync()));
        }

        // La misma instancia: la serie de la cortada espera (sin red) y la llamada llega a la otra, que antes no se alcanzó.
        c.Wsfe.SolicitarConToken = null;
        c.Wsfe.Reiniciar();
        c.Wsfe.Consultar = Responde(ConsultarNoEncontrado());
        c.Wsfe.Solicitar = Responde(FecaeAprobado());

        var siguiente = await ConTopeAsync(LoteDirectoAsync(procesador, c.IdPuntoVenta));

        Assert.False(siguiente.CortadoPorPresupuesto);
        Assert.Equal(deOtraSerie.IdFacturacion, Assert.Single(siguiente.Resultados).IdFacturacion);
        Assert.Equal([new SerieBloqueadaDeLote(CodigoFacturaB, ProcesadorDeFacturacionesPorLote.CabezaEnEspera)], siguiente.SeriesBloqueadas);
        Assert.Equal((1, 1), (siguiente.Emitidas, siguiente.PendientesRestantes));
        Assert.Equal(["FECAESolicitar"], c.Wsfe.Operaciones);
        Assert.Equal(0, (await LeerDecisionAsync(c, cortada.IdFacturacion)).Intentos);

        // Cumplido el minuto, la retoma: consulta el espejo antes de pedir y conserva su número.
        c.Wsfe.Reiniciar();
        c.Reloj.Ahora = c.Reloj.Ahora.AddSeconds(61);
        var final = await ConTopeAsync(LoteDirectoAsync(procesador, c.IdPuntoVenta));
        Assert.Equal(cortada.IdFacturacion, Assert.Single(final.Resultados).IdFacturacion);
        Assert.Equal((1, 0), (final.Emitidas, final.PendientesRestantes));
        Assert.Equal(["FECompConsultar", "FECAESolicitar"], c.Wsfe.Operaciones);
        Assert.Equal(["1"], Numeros(c.Wsfe));
    }

    /// <summary>WSAA no emite un ticket nuevo mientras hay uno vigente: si el corte abandonara la firma en vuelo y se perdiera su
    /// respuesta, el emisor quedaría sin facturar hasta que el ticket venza. La firma termina, el ticket se guarda y recién
    /// entonces se mira el corte.</summary>
    [Fact]
    public async Task UnCorteDelPresupuestoDuranteLaFirmaDeWsaaNoAbandonaLaFirmaYElTicketQuedaGuardado()
    {
        var c = await PrepararAsync("lotefirma");
        var venta = await SembrarVentaDecididaAsync(c);
        var firmando = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var liberar = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        c.Wsaa.AntesDeResponderConToken = async ct =>
        {
            firmando.TrySetResult();
            await liberar.Task.WaitAsync(ct);
        };
        var tiempo = new FakeTimeProvider();
        var (procesador, db) = NuevoProcesadorDirecto(c, OpcionesConPresupuestoLargo(), tiempo);
        await using var contextoDelProcesador = db;

        var tarea = LoteDirectoAsync(procesador, c.IdPuntoVenta);
        await firmando.Task.WaitAsync(Tope);
        tiempo.Advance(MasQueElPresupuesto);
        liberar.TrySetResult();
        var lote = await ConTopeAsync(tarea);

        // La firma terminó (no se cortó) y el corte se vio justo después: no se llegó a WSFE ni a crear el espejo.
        Assert.True(lote.CortadoPorPresupuesto);
        Assert.Equal(1, c.Wsaa.Solicitudes);
        Assert.Empty(c.Wsfe.Operaciones);
        Assert.Empty(await LeerEspejosAsync(c));
        var decision = await LeerDecisionAsync(c, venta.IdFacturacion);
        Assert.Equal(("pendiente", 0, null), (decision.Estado, decision.Intentos, decision.UltimoError));

        // El ticket quedó guardado: el lote siguiente no vuelve a firmar.
        c.Wsaa.AntesDeResponderConToken = null;
        c.Reloj.Ahora = c.Reloj.Ahora.AddSeconds(61);
        var siguiente = await ConTopeAsync(LoteDirectoAsync(procesador, c.IdPuntoVenta));
        Assert.Equal((1, 0), (siguiente.Emitidas, siguiente.PendientesRestantes));
        Assert.Equal(1, c.Wsaa.Solicitudes);
    }

    /// <summary>Una firma de WSAA no se cancela con el corte, así que puede fallar (timeout de WSAA) cuando el presupuesto ya
    /// venció. Eso es una falla real, no una cancelación: se anota como intento fallido con su código y su espera de siempre
    /// (30 s), no como la postergación de un minuto sin intento. El lote se corta igual.</summary>
    [Fact]
    public async Task UnaFirmaDeWsaaQueVenceDespuesDelPresupuestoSeAnotaComoIntentoYElLoteSeCorta()
    {
        var c = await PrepararAsync("lotefirmatarde");
        var venta = await SembrarVentaDecididaAsync(c);
        var deOtraSerie = await SembrarVentaDecididaAsync(c, c.IdClienteRi, sembrarAcumulados: false);
        var firmando = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var liberar = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        c.Wsaa.AntesDeResponderConToken = async _ =>
        {
            firmando.TrySetResult();
            await liberar.Task;
            throw new ErrorDominio("arca_no_definitivo", "WSAA no respondió dentro del tiempo configurado.", 503);
        };
        var tiempo = new FakeTimeProvider();
        var (procesador, db) = NuevoProcesadorDirecto(c, OpcionesConPresupuestoLargo(), tiempo);
        await using var contextoDelProcesador = db;

        var tarea = LoteDirectoAsync(procesador, c.IdPuntoVenta);
        await firmando.Task.WaitAsync(Tope);
        tiempo.Advance(MasQueElPresupuesto);
        liberar.TrySetResult();
        var lote = await ConTopeAsync(tarea);

        // El lote se cortó (no intentó la otra serie) y la falla quedó informada y anotada como intento.
        Assert.True(lote.CortadoPorPresupuesto);
        var informada = Assert.Single(lote.Resultados);
        Assert.Equal(venta.IdFacturacion, informada.IdFacturacion);
        Assert.Equal((EstadosFiscalesDeVenta.Pendiente, "arca_no_definitivo"), (informada.Fiscal.Estado, informada.Fiscal.Error));
        Assert.Empty(c.Wsfe.Operaciones);
        var decision = await LeerDecisionAsync(c, venta.IdFacturacion);
        Assert.Equal(("pendiente", 1, "arca_no_definitivo"), (decision.Estado, decision.Intentos, decision.UltimoError));
        Assert.Equal(c.Reloj.Ahora.AddSeconds(30), decision.ProximoIntento);
        var sinTocar = await LeerDecisionAsync(c, deOtraSerie.IdFacturacion);
        Assert.Equal(("pendiente", 0, null, null), (sinTocar.Estado, sinTocar.Intentos, sinTocar.UltimoError, sinTocar.ProximoIntento));
    }

    /// <summary>Vence el presupuesto cuando el lote termina de leer una página de candidatas: avanza el reloj al liberarse el
    /// lector, con las filas ya materializadas, para que el corte llegue entre una decisión y la siguiente.</summary>
    private sealed class InterceptorQueVenceElPresupuestoAlLeerLasCandidatas(FakeTimeProvider tiempo) : DbCommandInterceptor
    {
        public bool Armado { get; set; }

        public override InterceptionResult DataReaderDisposing(
            DbCommand command, DataReaderDisposingEventData eventData, InterceptionResult result)
        {
            if (Armado && command.CommandText.Contains("NOT EXISTS (SELECT 1 FROM comprobantes_venta e", StringComparison.Ordinal))
            {
                tiempo.Advance(MasQueElPresupuesto);
            }

            return base.DataReaderDisposing(command, eventData, result);
        }
    }

    /// <summary>Con el presupuesto vencido entre dos decisiones, la siguiente no se arranca: ni se posterga ni se anota (no se
    /// intentó), el lote informa el corte y la llamada siguiente la procesa.</summary>
    [Fact]
    public async Task UnPresupuestoVencidoEntreDosDecisionesNoArrancaLaSiguienteNiLaPosterga()
    {
        var c = await PrepararAsync("lotecorteentre");
        var cabeza = await SembrarVentaDecididaAsync(c);
        await DejarEspejoPendienteAsync(c, cabeza);
        var siguiente = await SembrarVentaDecididaAsync(c, c.IdClienteRi, sembrarAcumulados: false);
        c.Reloj.Ahora = c.Reloj.Ahora.AddSeconds(31);
        c.Wsfe.Consultar = Responde(ConsultarNoEncontrado());
        c.Wsfe.Solicitar = Responde(FecaeAprobado());
        var tiempo = new FakeTimeProvider();
        var vence = new InterceptorQueVenceElPresupuestoAlLeerLasCandidatas(tiempo) { Armado = true };
        var (procesador, db) = NuevoProcesadorDirecto(c, OpcionesConPresupuestoLargo(), tiempo, vence);
        await using var contextoDelProcesador = db;

        // La cabeza se resuelve entera; recién al terminar de leer las candidatas vence el presupuesto.
        var corte = await ConTopeAsync(LoteDirectoAsync(procesador, c.IdPuntoVenta));

        Assert.True(corte.CortadoPorPresupuesto);
        Assert.Equal(cabeza.IdFacturacion, Assert.Single(corte.Resultados).IdFacturacion);
        Assert.Equal((1, 1), (corte.Emitidas, corte.PendientesRestantes));
        Assert.Equal(["FECompConsultar", "FECAESolicitar"], c.Wsfe.Operaciones);
        var intacta = await LeerDecisionAsync(c, siguiente.IdFacturacion);
        Assert.Equal(("pendiente", 0, null, null), (intacta.Estado, intacta.Intentos, intacta.UltimoError, intacta.ProximoIntento));

        // La llamada siguiente (presupuesto nuevo) la procesa.
        vence.Armado = false;
        c.Wsfe.Reiniciar();
        var final = await ConTopeAsync(LoteDirectoAsync(procesador, c.IdPuntoVenta));
        Assert.False(final.CortadoPorPresupuesto);
        Assert.Equal(siguiente.IdFacturacion, Assert.Single(final.Resultados).IdFacturacion);
        Assert.Equal((1, 0), (final.Emitidas, final.PendientesRestantes));
    }

    /// <summary>Limitación documentada: una decisión que ya no está pendiente (solo conserva un espejo pendiente) no se puede
    /// postergar. El UPDATE de la postergación está guardado por <c>estado = 'pendiente'</c> y no la toca.</summary>
    [Fact]
    public async Task UnCorteSobreUnaCabezaCuyaDecisionYaNoEstaPendienteNoLaPosterga()
    {
        var c = await PrepararAsync("lotecorteno");
        var cabeza = await SembrarVentaDecididaAsync(c);
        await DejarEspejoPendienteAsync(c, cabeza);
        await EjecutarComoOwnerAsync(
            "UPDATE facturaciones_de_venta SET estado = 'cancelada'::estado_facturacion_venta WHERE id_facturacion_venta = $1",
            cabeza.IdFacturacion);
        var antes = await LeerDecisionAsync(c, cabeza.IdFacturacion);
        Assert.Equal("cancelada", antes.Estado);
        var llego = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        c.Wsfe.ConsultarConToken = async (_, ct) =>
        {
            llego.TrySetResult();
            await Task.Delay(Timeout.Infinite, ct);
            throw new InvalidOperationException("No llega: la espera solo termina cancelada.");
        };
        var tiempo = new FakeTimeProvider();
        var (procesador, db) = NuevoProcesadorDirecto(c, OpcionesConPresupuestoLargo(), tiempo);
        await using var contextoDelProcesador = db;

        var tarea = LoteDirectoAsync(procesador, c.IdPuntoVenta);
        await llego.Task.WaitAsync(Tope);
        tiempo.Advance(MasQueElPresupuesto);
        var lote = await ConTopeAsync(tarea);

        Assert.True(lote.CortadoPorPresupuesto);
        Assert.Equal(["FECompConsultar"], c.Wsfe.Operaciones);
        Assert.Equal(antes, await LeerDecisionAsync(c, cabeza.IdFacturacion));
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

    /// <summary>Segundos del timeout de WSFE en los tests que cuelgan un pedido a propósito: alcanza para que una llamada normal
    /// del espía (inmediata) nunca lo toque, así que solo puede vencer la que se cuelga.</summary>
    private const double TimeoutDeWsfeDeLosTests = 3;

    [Fact]
    public async Task UnPedidoQueSuperaElTimeoutConfiguradoEsNoDefinitivoSinReintentoYDejaElEspejoPendienteConSuNumero()
    {
        var c = await PrepararAsync("lotetimeout");
        var venta = await SembrarVentaDecididaAsync(c);
        c.Wsfe.SolicitarConToken = async (_, ct) =>
        {
            await Task.Delay(Timeout.Infinite, ct);
            throw new InvalidOperationException("No llega: la espera solo termina cancelada.");
        };
        var cliente = await ClienteConServiciosAsync(
            c, services => services.Configure<OpcionesFiscales>(o => o.TimeoutDeLlamadaSegundos = TimeoutDeWsfeDeLosTests));

        var estado = await ConTopeAsync(ProcesarAsync(cliente, venta.IdFacturacion));

        // Un timeout no es un rechazo ni una cancelación del llamador: la decisión sigue pendiente con el intento anotado, y el
        // pedido se mandó UNA vez (ARCA pudo haberlo procesado: reenviarlo daría un error de numeración, no el CAE).
        Assert.Equal(EstadosFiscalesDeVenta.Pendiente, estado.Estado);
        Assert.StartsWith("arca_no_definitivo:", estado.Error, StringComparison.Ordinal);
        Assert.Equal(1, c.Wsfe.Contar("FECAESolicitar"));
        Assert.Equal(1, c.Wsaa.Solicitudes);
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

    /// <summary>Con el timeout de una llamada más corto que el presupuesto del lote, un pedido colgado se anota como intento
    /// fallido (con su espera) en vez de cortar el lote cada vez: la serie espera y las demás siguen.</summary>
    [Fact]
    public async Task UnPedidoColgadoDentroDelLoteSeAnotaComoIntentoYSuSerieEsperaMientrasLasDemasSiguen()
    {
        var c = await PrepararAsync("lotehang");
        var colgada = await SembrarVentaDecididaAsync(c);
        var deOtraSerie = await SembrarVentaDecididaAsync(c, c.IdClienteRi, sembrarAcumulados: false);
        c.Wsfe.SolicitarConToken = async (req, ct) =>
        {
            if (CampoDelCuerpo(await req.Content!.ReadAsStringAsync(ct), "CbteTipo") == CodigoFacturaB.ToString(System.Globalization.CultureInfo.InvariantCulture))
            {
                await Task.Delay(Timeout.Infinite, ct);
            }

            return RespuestaXml(FecaeAprobado());
        };
        // El reloj del presupuesto no avanza: el lote solo puede terminar por el timeout de la llamada.
        var (procesador, db) = NuevoProcesadorDirecto(
            c, new OpcionesFiscales { TimeoutDeLlamadaSegundos = TimeoutDeWsfeDeLosTests }, new FakeTimeProvider());
        await using var contextoDelProcesador = db;

        var primero = await ConTopeAsync(LoteDirectoAsync(procesador, c.IdPuntoVenta));

        Assert.False(primero.CortadoPorPresupuesto);
        Assert.Equal([colgada.IdFacturacion, deOtraSerie.IdFacturacion], primero.Resultados.Select(r => r.IdFacturacion));
        Assert.Equal(EstadosFiscalesDeVenta.Pendiente, primero.Resultados[0].Fiscal.Estado);
        Assert.StartsWith("arca_no_definitivo:", primero.Resultados[0].Fiscal.Error, StringComparison.Ordinal);
        Assert.Equal(EstadosFiscalesDeVenta.Aprobado, primero.Resultados[1].Fiscal.Estado);
        Assert.Equal((1, 1), (primero.Emitidas, primero.PendientesRestantes));
        Assert.Equal(
            1, c.Wsfe.CuerposDeSolicitud.Count(cuerpo => CampoDelCuerpo(cuerpo, "CbteTipo") == "6"));
        Assert.Equal(2, c.Wsfe.Contar("FECAESolicitar"));
        var anotada = await LeerDecisionAsync(c, colgada.IdFacturacion);
        Assert.Equal(("pendiente", 1), (anotada.Estado, anotada.Intentos));
        Assert.StartsWith("arca_no_definitivo:", anotada.UltimoError, StringComparison.Ordinal);
        Assert.Equal(c.Reloj.Ahora.AddSeconds(30), anotada.ProximoIntento);
        var espejo = Assert.Single(await LeerEspejosAsync(c), e => e.IdVentaOrigen == colgada.IdVenta);
        Assert.Equal(("pendiente", 1L), (espejo.Resultado, espejo.Numero));

        // La llamada siguiente salta la serie de la colgada sin red y procesa una decisión de la otra serie.
        var otraDeLaSerieA = await SembrarVentaDecididaAsync(c, c.IdClienteRi, sembrarAcumulados: false);
        c.Wsfe.SolicitarConToken = null;
        c.Wsfe.Reiniciar();
        c.Wsfe.Solicitar = Responde(FecaeAprobado());

        var segundo = await ConTopeAsync(LoteDirectoAsync(procesador, c.IdPuntoVenta));

        Assert.Equal(otraDeLaSerieA.IdFacturacion, Assert.Single(segundo.Resultados).IdFacturacion);
        Assert.Equal([new SerieBloqueadaDeLote(CodigoFacturaB, ProcesadorDeFacturacionesPorLote.CabezaEnEspera)], segundo.SeriesBloqueadas);
        Assert.Equal((1, 1), (segundo.Emitidas, segundo.PendientesRestantes));
        Assert.Equal(["FECAESolicitar"], c.Wsfe.Operaciones);
        Assert.Equal(anotada, await LeerDecisionAsync(c, colgada.IdFacturacion));

        // Cumplida la espera, la retoma: consulta antes de pedir y conserva el número.
        c.Wsfe.Reiniciar();
        c.Reloj.Ahora = c.Reloj.Ahora.AddSeconds(31);
        c.Wsfe.Consultar = Responde(ConsultarNoEncontrado());
        var tercero = await ConTopeAsync(LoteDirectoAsync(procesador, c.IdPuntoVenta));
        Assert.Equal(colgada.IdFacturacion, Assert.Single(tercero.Resultados).IdFacturacion);
        Assert.Equal(["FECompConsultar", "FECAESolicitar"], c.Wsfe.Operaciones);
        Assert.Equal(["1"], Numeros(c.Wsfe));
    }

    /// <summary>El timeout de cada cliente sale de la configuración: WSFE de <c>TimeoutDeLlamadaSegundos</c> (8 s por defecto) y
    /// WSAA de <c>TimeoutDeWsaaSegundos</c> (15 s por defecto: más largo que el de WSFE y menor que el presupuesto del lote).</summary>
    [Fact]
    public void ElTimeoutDeLosClientesDeArcaSaleDeLaConfiguracionYWsaaTieneElSuyo()
    {
        var porDefecto = fixture.WithWebHostBuilder(_ => { }).Services.GetRequiredService<IHttpClientFactory>();
        Assert.Equal(TimeSpan.FromSeconds(8), porDefecto.CreateClient(nameof(IClienteWsfe)).Timeout);
        Assert.Equal(TimeSpan.FromSeconds(15), porDefecto.CreateClient(nameof(IClienteWsaa)).Timeout);

        var configurado = fixture.WithWebHostBuilder(builder => builder.ConfigureServices(services =>
            services.Configure<OpcionesFiscales>(o =>
            {
                o.TimeoutDeLlamadaSegundos = 7;
                o.TimeoutDeWsaaSegundos = 45;
            }))).Services.GetRequiredService<IHttpClientFactory>();
        Assert.Equal(TimeSpan.FromSeconds(7), configurado.CreateClient(nameof(IClienteWsfe)).Timeout);
        Assert.Equal(TimeSpan.FromSeconds(45), configurado.CreateClient(nameof(IClienteWsaa)).Timeout);
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
