using System.Security.Cryptography.X509Certificates;
using Ways.Domain.Fiscal;

namespace Ways.Application.Fiscal;

/// <summary>
/// Clave de cache del Ticket de Acceso (design.md: Ports and the CAE machine) — un TA es válido
/// por <c>(empresa, ambiente, servicio)</c>, nunca por tenant a secas: dos empresas del mismo
/// tenant pueden tener certificados distintos, y homologación/producción son credenciales
/// separadas por construcción (D6).
/// </summary>
public readonly record struct ClaveDeTicket(int IdEmpresa, AmbienteFiscal Ambiente, string Servicio);

/// <summary>
/// Credenciales que devuelve WSAA tras un <c>loginCms</c> exitoso (spec fiscal-arca: "The Access
/// Ticket Is Cached In Memory..."). <see cref="Token"/>/<see cref="Sign"/> son un PORTADOR válido
/// hasta <see cref="Expiracion"/> — nunca material de clave asimétrica (dto-contract-honesty:
/// ningún campo de este contrato es una clave privada, a diferencia de
/// <c>CertificadoFiscalDto</c> que sí tiene esa exclusión explícita en la slice 4). El
/// <see cref="ToString"/> generado por el compilador para un <c>record</c> imprime TODAS las
/// propiedades — sobreescrito acá para que un log/excepción que interpole esta instancia no
/// filtre el bearer token.
/// </summary>
public sealed record TicketDeAcceso(string Token, string Sign, DateTimeOffset Expiracion)
{
    public override string ToString() => $"TicketDeAcceso {{ Expiracion = {Expiracion:o} }}";
}

/// <summary>
/// Lo que hace falta para pedir un TA: la <see cref="ClaveDeTicket"/> más el certificado con el
/// que se firma la TRA (design.md: <c>IClienteWsaa.ObtenerTicketAsync</c>). El certificado no se
/// persiste ni viaja más allá de esta solicitud — en 19a quien la arma es siempre un test
/// (<c>CertificadoDePrueba</c>, D7); el resolver real de producción
/// (<c>IAlmacenDeClavesFiscales</c>) llega en la slice 4, y <c>ClienteWsaa</c> no tiene ningún
/// caller de producción hasta la slice 5. El <see cref="ToString"/> generado por el compilador
/// para un <c>record</c> imprime TODAS las propiedades, incluido el <see cref="X509Certificate2"/>
/// vivo — sobreescrito acá, mismo motivo que <see cref="TicketDeAcceso"/> arriba, para que un
/// log/excepción que interpole esta instancia no filtre el certificado.
/// </summary>
public sealed record SolicitudDeTicket(ClaveDeTicket Clave, X509Certificate2 Certificado)
{
    public override string ToString() => $"SolicitudDeTicket {{ Clave = {Clave} }}";
}

/// <summary>
/// Clave de una serie fiscal en la NUMERACIÓN DE ARCA (design.md: Ports and the CAE machine,
/// <c>IClienteWsfe</c>). <b>No es</b> la clave de <c>numeraciones_fiscales</c>/
/// <c>AsignadorDeNumeroFiscal</c> (que usa <c>id_punto_venta</c>, el PK interno) — acá
/// <see cref="PtoVta"/> es <c>puntos_venta.numero_fiscal</c> (el número que ARCA conoce) y
/// <see cref="CbteTipo"/> es <c>codigo_afip</c> del tipo de comprobante; ambas parejas están
/// pareadas 1:1 pero son identificadores distintos — el slice 5 hace la traducción.
/// </summary>
public readonly record struct ClaveDeSerie(int PtoVta, short CbteTipo);

/// <summary>Una entrada de <c>Iva[]</c> del pedido/respuesta WSFE — nunca lleva Exento/No gravado
/// (design decisión 11, <c>ComposicionDeTotalesFiscales</c>).</summary>
public sealed record ItemIvaFiscal(short Id, decimal BaseImp, decimal Importe);

/// <summary>Una entrada cruda de <c>Observaciones[]</c>/<c>Errors[]</c> de ARCA
/// (design decisión 14: ambos arrays comparten esta forma).</summary>
public sealed record ObservacionArca(int Codigo, string Mensaje);

/// <summary>
/// El pedido de CAE ya compuesto (design.md: Ports and the CAE machine, formato SOAP en
/// <c>ClienteWsfe</c>/<c>MapeadorWsfe</c>). <see cref="FchServDesde"/>/<see cref="FchServHasta"/>/
/// <see cref="FchVtoPago"/> solo aplican a <c>Concepto</c> 2/3 (servicios) — <c>NULL</c> en el
/// único camino que 19a construye (<c>Concepto = 1</c>, productos) y por eso OMITIDOS del sobre,
/// nunca emitidos vacíos (target 39). <c>MonId</c>/<c>MonCotiz</c> no viajan acá: son constantes
/// fijas (<c>"PES"</c>/<c>1</c>) que el mapper siempre escribe, no una decisión del llamador.
/// </summary>
public sealed record SolicitudDeCae(
    ClaveDeSerie Serie,
    long CbteDesde,
    long CbteHasta,
    int Concepto,
    short DocTipo,
    long DocNro,
    DateOnly CbteFch,
    decimal ImpTotal,
    decimal ImpTotConc,
    decimal ImpNeto,
    decimal ImpOpEx,
    decimal ImpTrib,
    decimal ImpIVA,
    short CondicionIVAReceptorId,
    IReadOnlyList<ItemIvaFiscal> Iva,
    DateOnly? FchServDesde = null,
    DateOnly? FchServHasta = null,
    DateOnly? FchVtoPago = null);

/// <summary>
/// La respuesta ya parseada de <c>FECAESolicitar</c> (design decisión 14): <see cref="Observaciones"/>
/// se llena en una aprobación (con o sin observaciones), <see cref="Errors"/> se llena en un rechazo
/// — nunca ambas a la vez, mismo criterio que la columna <c>observaciones_fiscales</c> que las
/// persiste juntas. <c>10016</c> (número fuera de secuencia) NUNCA llega hasta acá: se detecta antes
/// y se lanza como <c>numeracion_fiscal_desincronizada</c> (D13) en vez de devolverse como un
/// rechazo más.</summary>
public sealed record RespuestaCae(
    ResultadoFiscal Resultado,
    string? Cae,
    DateOnly? CaeVencimiento,
    IReadOnlyList<ObservacionArca> Observaciones,
    IReadOnlyList<ObservacionArca> Errors);

/// <summary>Resultado de <c>FECompConsultar</c> (I2). <see cref="Encontrado"/> <c>false</c> cubre
/// tanto "nunca se intentó" como "ARCA no tiene registro de ese número" — el llamador no distingue
/// esos dos casos, en ninguno hay nada para adoptar.</summary>
public sealed record ConsultaDeComprobante(
    bool Encontrado,
    string? Cae,
    DateOnly? CaeVencimiento,
    ResultadoFiscal? Resultado);

/// <summary>Una fila de <c>FEParamGetTiposCbte</c>/<c>FEParamGetTiposIva</c>/
/// <c>FEParamGetCondicionIvaReceptor</c> — misma forma <c>{Id, Desc}</c> en los tres catálogos.
/// Sin consumidor en 19a (los mapeos ya están en la migración, decisión 11); confirmarlos contra
/// esto es tarea de 19b.</summary>
public sealed record ParametroArca(string Id, string Descripcion);
