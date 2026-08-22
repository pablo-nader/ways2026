using Ways.Domain.Fiscal;

namespace Ways.Application.Fiscal;

/// <summary>
/// Puerto hacia WSFE: pide/consulta CAE y lee los catálogos ARCA (design.md: Ports and the CAE
/// machine). Implementado por <c>Ways.Infrastructure.Fiscal.ClienteWsfe</c> contra un mock local
/// en 19a (misma decisión 8 del proposal que <see cref="IClienteWsaa"/>). El <c>Ticket</c>/<c>Cuit</c>
/// del emisor viajan explícitos en cada llamada — <b>DEVIATION (registered)</b>: el snippet abreviado
/// de design.md no los lista, pero <c>Auth</c> (Token/Sign/Cuit) es obligatorio en todo request WSFE
/// y esta interfaz no tiene ningún otro lugar de donde tomarlos — <c>ClienteWsfe</c> no orquesta la
/// obtención/renovación del TA (eso vive en <c>ServicioDeFacturacionFiscal</c>, slice 5, junto con
/// <c>IClienteWsaa</c>/<c>IAlmacenDeClavesFiscales</c>, que tampoco existe todavía). <c>SolicitarCaeAsync</c>
/// solo es invocable con un <see cref="PermisoDeSolicitud"/>, que únicamente
/// <c>MaquinaDeEstadosCae</c> emite (design D4) — esta slice todavía no tiene caller de producción,
/// así que el gate es hoy puramente estructural (por tipo), verificado en runtime recién en la
/// slice 5.
/// </summary>
public interface IClienteWsfe
{
    Task<RespuestaCae> SolicitarCaeAsync(
        TicketDeAcceso ticket,
        string cuitRepresentado,
        PermisoDeSolicitud permiso,
        SolicitudDeCae solicitud,
        CancellationToken ct);

    Task<ConsultaDeComprobante> ConsultarAsync(
        TicketDeAcceso ticket,
        string cuitRepresentado,
        ClaveDeSerie clave,
        long numero,
        CancellationToken ct);

    /// <summary>0 = serie sin usar (respuesta legal de ARCA, no un error).</summary>
    Task<long> UltimoAutorizadoAsync(
        TicketDeAcceso ticket, string cuitRepresentado, ClaveDeSerie clave, CancellationToken ct);

    Task<IReadOnlyList<ParametroArca>> ParametrosAsync(
        TicketDeAcceso ticket, string cuitRepresentado, string operacion, CancellationToken ct);
}
