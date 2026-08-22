using System.Globalization;
using System.Text;
using System.Xml.Linq;
using Ways.Application.Abstracciones;
using Ways.Application.Fiscal;
using Ways.Domain.Common;
using Ways.Domain.Fiscal;

namespace Ways.Infrastructure.Fiscal;

/// <summary>
/// Implementa <see cref="IClienteWsfe"/> contra un mock local en 19a (misma decisión 8 del
/// proposal que <see cref="ClienteWsaa"/>). Arma los sobres vía <see cref="MapeadorWsfe"/>, los
/// envía con un backoff exponencial acotado (design.md: "Transport: timeout, 5xx, socket, circuit
/// open" → <c>arca_no_definitivo</c>, reintentable) y un circuit breaker que abre tras
/// <see cref="_umbralDeAperturaDeCircuito"/> llamadas fallidas consecutivas — mientras está
/// abierto, CERO requests HTTP salen (target 50). Mapea la taxonomía de errores WSFE (design.md:
/// The ARCA error taxonomy → domain codes, fila WSFE) y <c>10016</c> (I1) a
/// <see cref="ErrorDominio"/> nombrados. Sin caller de producción hasta la slice 5.
///
/// <b>DEVIATION (registered)</b> — el "invalidar el TA y reintentar una vez" del código WSFE
/// <c>600</c> (design.md taxonomy) NO se implementa DENTRO de este cliente: requiere
/// <c>IClienteWsaa</c> + un certificado (<c>IAlmacenDeClavesFiscales</c>, que recién existe en la
/// slice 4) para pedir un TA nuevo, y este cliente no tiene ninguno de los dos. Esta slice
/// implementa la mitad que SÍ es de su responsabilidad — detectar el código <c>600</c> y mapearlo
/// al <see cref="ErrorDominio"/> reintentable <c>ticket_de_acceso_invalido</c> (503) — y registra el
/// "reintentar una vez con un TA fresco" como contrato vinculante de
/// <c>ServicioDeFacturacionFiscal</c> (slice 5), que sí orquesta WSAA+WSFE juntos (design.md data
/// flow, paso 5).
/// </summary>
public sealed class ClienteWsfe : IClienteWsfe
{
    private const int CodigoWsfeTicketInvalido = 600;
    private const int CodigoWsfeNumeroNoCorrelativo = 10016;

    private readonly HttpClient _http;
    private readonly IRelojDelSistema _reloj;
    private readonly IEsperador _esperador;
    private readonly int _intentosMaximos;
    private readonly TimeSpan _retardoBase;
    private readonly int _umbralDeAperturaDeCircuito;
    private readonly TimeSpan _duracionDeApertura;

    private readonly object _cerrojoDelCircuito = new();
    private int _fallosConsecutivos;
    private DateTimeOffset? _circuitoAbiertoHasta;

    public ClienteWsfe(
        HttpClient http,
        IRelojDelSistema reloj,
        IEsperador? esperador = null,
        int intentosMaximos = 3,
        TimeSpan? retardoBase = null,
        int umbralDeAperturaDeCircuito = 5,
        TimeSpan? duracionDeApertura = null)
    {
        _http = http;
        _reloj = reloj;
        _esperador = esperador ?? EsperadorReal.Instancia;
        _intentosMaximos = intentosMaximos;
        _retardoBase = retardoBase ?? TimeSpan.FromSeconds(1);
        _umbralDeAperturaDeCircuito = umbralDeAperturaDeCircuito;
        _duracionDeApertura = duracionDeApertura ?? TimeSpan.FromSeconds(30);
    }

    public async Task<RespuestaCae> SolicitarCaeAsync(
        TicketDeAcceso ticket,
        string cuitRepresentado,
        PermisoDeSolicitud permiso,
        SolicitudDeCae solicitud,
        CancellationToken ct)
    {
        var sobre = MapeadorWsfe.ConstruirFecaeSolicitar(ticket, cuitRepresentado, solicitud);
        var cuerpo = await EnviarAsync("FECAESolicitar", sobre, ct);
        return LeerRespuestaCae(cuerpo);
    }

    public async Task<ConsultaDeComprobante> ConsultarAsync(
        TicketDeAcceso ticket, string cuitRepresentado, ClaveDeSerie clave, long numero, CancellationToken ct)
    {
        var sobre = MapeadorWsfe.ConstruirFeCompConsultar(ticket, cuitRepresentado, clave, numero);
        var cuerpo = await EnviarAsync("FECompConsultar", sobre, ct);

        var resultado = cuerpo.Elements().First(e => e.Name.LocalName == "FECompConsultarResult");
        var errores = LeerLista(resultado, "Errors", "Err");

        if (errores.Count > 0)
        {
            return new ConsultaDeComprobante(false, null, null, null);
        }

        var resultGet = resultado.Elements().FirstOrDefault(e => e.Name.LocalName == "ResultGet");
        if (resultGet is null)
        {
            return new ConsultaDeComprobante(false, null, null, null);
        }

        var cae = LeerCampo(resultGet, "CodAutorizacion");
        var vencimientoTexto = LeerCampo(resultGet, "FchVto");
        var resultadoArca = LeerCampo(resultGet, "Resultado")!.Single();
        var observaciones = LeerLista(resultGet, "Observaciones", "Obs");
        var estado = MaquinaDeEstadosCae.Mapear(resultadoArca, observaciones.Count > 0);

        return new ConsultaDeComprobante(
            true, cae, vencimientoTexto is null ? null : LeerFecha(vencimientoTexto), estado);
    }

    public async Task<long> UltimoAutorizadoAsync(
        TicketDeAcceso ticket, string cuitRepresentado, ClaveDeSerie clave, CancellationToken ct)
    {
        var sobre = MapeadorWsfe.ConstruirFeCompUltimoAutorizado(ticket, cuitRepresentado, clave);
        var cuerpo = await EnviarAsync("FECompUltimoAutorizado", sobre, ct);

        var resultado = cuerpo.Elements().First(e => e.Name.LocalName == "FECompUltimoAutorizadoResult");
        var cbteNro = LeerCampo(resultado, "CbteNro") ?? "0";
        return long.Parse(cbteNro, CultureInfo.InvariantCulture);
    }

    public async Task<IReadOnlyList<ParametroArca>> ParametrosAsync(
        TicketDeAcceso ticket, string cuitRepresentado, string operacion, CancellationToken ct)
    {
        var sobre = MapeadorWsfe.ConstruirParametros(ticket, cuitRepresentado, operacion);
        var cuerpo = await EnviarAsync(operacion, sobre, ct);

        var resultado = cuerpo.Elements().First(e => e.Name.LocalName == operacion + "Result");
        var resultGet = resultado.Elements().FirstOrDefault(e => e.Name.LocalName == "ResultGet");

        if (resultGet is null)
        {
            return [];
        }

        return resultGet.Elements()
            .Select(e => new ParametroArca(LeerCampo(e, "Id") ?? string.Empty, LeerCampo(e, "Desc") ?? string.Empty))
            .ToList();
    }

    /// <summary>design.md: The ARCA error taxonomy → codigos de dominio (fila
    /// <c>FECAESolicitar</c>). <c>10016</c> se detecta ACÁ, antes de convertir nada en
    /// <see cref="RespuestaCae"/>: D13 exige que nunca se trate como un rechazo más — dispara la
    /// reconciliación, nunca un auto-avance.</summary>
    private static RespuestaCae LeerRespuestaCae(XElement cuerpoRespuesta)
    {
        var resultado = cuerpoRespuesta.Elements().First(e => e.Name.LocalName == "FECAESolicitarResult");

        // Errores de NIVEL DE LLAMADA (p. ej. 600 — TA inválido): no hay FeDetResp en absoluto,
        // porque ARCA no llegó a procesar ningún detalle.
        var erroresDeNivelSuperior = LeerLista(resultado, "Errors", "Err");
        var errorDeTicket = erroresDeNivelSuperior.FirstOrDefault(e => e.Codigo == CodigoWsfeTicketInvalido);
        if (errorDeTicket is not null)
        {
            throw new ErrorDominio("ticket_de_acceso_invalido", errorDeTicket.Mensaje, 503);
        }

        if (erroresDeNivelSuperior.Count > 0)
        {
            throw new ErrorDominio("arca_rechazo", erroresDeNivelSuperior[0].Mensaje, 409);
        }

        var detalle = resultado
            .Elements().First(e => e.Name.LocalName == "FeDetResp")
            .Elements().First(e => e.Name.LocalName == "FECAEDetResponse");

        var resultadoArca = LeerCampo(detalle, "Resultado")!.Single();
        var observacionesCrudas = LeerLista(detalle, "Observaciones", "Obs");

        if (resultadoArca == 'R')
        {
            var numeroNoCorrelativo = observacionesCrudas.Any(o => o.Codigo == CodigoWsfeNumeroNoCorrelativo);
            if (numeroNoCorrelativo)
            {
                throw new ErrorDominio(
                    "numeracion_fiscal_desincronizada",
                    "ARCA reportó un número fuera de secuencia (10016) — la numeración local está " +
                    "desincronizada; requiere reconciliación (D13), nunca un auto-avance.",
                    409);
            }

            return new RespuestaCae(ResultadoFiscal.Rechazado, null, null, [], observacionesCrudas);
        }

        var hayObservaciones = observacionesCrudas.Count > 0;
        var estado = MaquinaDeEstadosCae.Mapear(resultadoArca, hayObservaciones);
        var cae = LeerCampo(detalle, "CAE");
        var vencimientoTexto = LeerCampo(detalle, "CAEFchVto");

        return new RespuestaCae(
            estado, cae, vencimientoTexto is null ? null : LeerFecha(vencimientoTexto), observacionesCrudas, []);
    }

    /// <summary>Envía un sobre ya armado con backoff exponencial acotado a
    /// <see cref="_intentosMaximos"/> intentos (solo ante falla de TRANSPORTE — timeout/5xx/socket,
    /// nunca ante una respuesta de negocio ya parseada) y respeta el circuito abierto sin emitir
    /// ningún request mientras dure (target 50).</summary>
    private async Task<XElement> EnviarAsync(string operacion, string sobre, CancellationToken ct)
    {
        VerificarCircuitoCerrado();

        for (var intento = 1; intento <= _intentosMaximos; intento++)
        {
            try
            {
                using var mensaje = new HttpRequestMessage(HttpMethod.Post, string.Empty)
                {
                    Content = new StringContent(sobre, Encoding.UTF8, "text/xml")
                };
                mensaje.Headers.TryAddWithoutValidation(
                    "SOAPAction", SobreSoap.AccionDe(SobreSoap.EspacioWsfe, operacion));

                using var respuestaHttp = await _http.SendAsync(mensaje, ct);

                if ((int)respuestaHttp.StatusCode >= 500)
                {
                    throw new HttpRequestException($"WSFE respondió {(int)respuestaHttp.StatusCode}.");
                }

                var texto = await respuestaHttp.Content.ReadAsStringAsync(ct);
                var leida = SobreSoap.Leer(texto);

                if (leida.Fault is { } fault)
                {
                    throw new ErrorDominio("wsfe_fault_soap", fault.FaultString, 502);
                }

                RegistrarExitoDelCircuito();
                return leida.Cuerpo!;
            }
            catch (Exception ex) when (EsFallaTransitoria(ex))
            {
                // El filtro captura TODOS los intentos, incluido el último — así el fallo cae al
                // registro del circuito + el ErrorDominio de abajo en vez de propagar la excepción
                // cruda de transporte. Solo se espera (backoff) si queda otro intento por delante.
                if (intento < _intentosMaximos)
                {
                    var demora = TimeSpan.FromTicks(_retardoBase.Ticks * (1L << (intento - 1)));
                    await _esperador.EsperarAsync(demora, ct);
                }
            }
        }

        RegistrarFalloDelCircuito();
        throw new ErrorDominio("arca_no_definitivo", "WSFE no respondió de forma definitiva.", 503);
    }

    private static bool EsFallaTransitoria(Exception ex) => ex is HttpRequestException or TaskCanceledException;

    private void VerificarCircuitoCerrado()
    {
        lock (_cerrojoDelCircuito)
        {
            if (_circuitoAbiertoHasta is { } hasta && _reloj.Ahora < hasta)
            {
                throw new ErrorDominio(
                    "arca_no_definitivo", "Circuito WSFE abierto: sin llamadas hasta que cierre.", 503);
            }
        }
    }

    private void RegistrarExitoDelCircuito()
    {
        lock (_cerrojoDelCircuito)
        {
            _fallosConsecutivos = 0;
            _circuitoAbiertoHasta = null;
        }
    }

    private void RegistrarFalloDelCircuito()
    {
        lock (_cerrojoDelCircuito)
        {
            _fallosConsecutivos++;
            if (_fallosConsecutivos >= _umbralDeAperturaDeCircuito)
            {
                _circuitoAbiertoHasta = _reloj.Ahora + _duracionDeApertura;
            }
        }
    }

    private static IReadOnlyList<ObservacionArca> LeerLista(XElement padre, string contenedor, string item)
    {
        var contenedorElemento = padre.Elements().FirstOrDefault(e => e.Name.LocalName == contenedor);
        if (contenedorElemento is null)
        {
            return [];
        }

        return contenedorElemento.Elements()
            .Where(e => e.Name.LocalName == item)
            .Select(e => new ObservacionArca(
                int.Parse(LeerCampo(e, "Code") ?? "0", CultureInfo.InvariantCulture),
                LeerCampo(e, "Msg") ?? string.Empty))
            .ToList();
    }

    private static string? LeerCampo(XElement padre, string nombre) =>
        padre.Elements().FirstOrDefault(e => e.Name.LocalName == nombre)?.Value;

    private static DateOnly LeerFecha(string yyyymmdd) =>
        DateOnly.ParseExact(yyyymmdd, "yyyyMMdd", CultureInfo.InvariantCulture);
}
