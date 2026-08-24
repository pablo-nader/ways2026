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

/// <summary>
/// Alta de un certificado fiscal (slice 4, <c>ServicioDeCertificados</c>) — el operador sube el
/// PFX exportado desde ARCA con su contraseña; el servicio extrae la parte pública (PEM), la
/// huella SHA-256, la vigencia (del propio X.509, nunca <c>DEFAULT now()</c>) y la clave privada,
/// que cifra antes de persistir (<c>IAlmacenDeClavesFiscales.CifrarAsync</c>, D5). <see cref="Pfx"/>/
/// <see cref="PasswordPfx"/> viven SOLO durante este request — no hay columna que los guarde tal
/// cual (dto-contract-honesty: nada especulativo, ningún campo de acá aparece en
/// <see cref="CertificadoFiscalDto"/>). LÍMITE HONESTO (judgment-day 19a-slice-4 ronda 2 juez A,
/// completado en la misma ronda tras la pasada acotada del juez B): el <c>try/finally</c> de
/// <see cref="ServicioDeCertificados.RegistrarAsync"/> arranca ANTES de cargar el PFX, así que
/// <see cref="System.Security.Cryptography.CryptographicOperations.ZeroMemory"/> limpia
/// <see cref="Pfx"/> en TODOS los caminos de salida — incluido el más probable del ABM, contraseña
/// incorrecta (PFX inválido), y el PFX sin clave RSA — no solo el camino feliz. Es un <c>byte[]</c>,
/// se puede. <see cref="PasswordPfx"/> NO: es un
/// <see cref="string"/> inmutable de .NET, nunca se puede zerear de forma confiable (queda en el
/// heap gestionado hasta que el GC lo recolecte, potencialmente duplicado por interning o por
/// promoción de generación) — no hay API que lo garantice, y fingir que sí sería una garantía
/// falsa.</summary>
public sealed record RegistroDeCertificadoFiscal(
    int IdEmpresa,
    AmbienteFiscal Ambiente,
    string Alias,
    string CuitTitular,
    byte[] Pfx,
    string PasswordPfx);

/// <summary>
/// Lectura de un certificado fiscal — <c>dto-contract-honesty</c> rule 1 (design T5): SIN
/// <c>CertificadoPem</c> (público, pero sin consumidor hasta la pantalla de 19c) y, sobre todo,
/// SIN <see cref="Domain.Fiscal.CertificadoFiscal.ClavePrivadaCifrada"/>/<c>Nonce</c>/
/// <c>TagAutenticacion</c>/<c>IdClaveMaestra</c> — ninguno de los cuatro tiene una propiedad acá,
/// ni con otro nombre (spec certificados-fiscales: "Key Material Never Appears In Any DTO, Log,
/// Or API Response").</summary>
public sealed record CertificadoFiscalDto(
    int Id,
    int IdEmpresa,
    AmbienteFiscal Ambiente,
    string Alias,
    string CuitTitular,
    DateTimeOffset VigenciaDesde,
    DateTimeOffset VigenciaHasta,
    bool Activo);

/// <summary>Body de <c>PUT /api/fiscal/empresas/{id}/condicion-fiscal</c> — proposal.md §B: sin
/// default honesto, el camino fiscal exige el valor explícito.</summary>
public sealed record CondicionFiscalDeEmpresaEdicion(int IdCondicionFiscal);

/// <summary>Body de <c>PUT /api/fiscal/puntos-venta/{id}/numero-fiscal</c> — proposal.md §C
/// decisión 2: el punto de venta ARCA (1..99999, <c>ck_puntos_venta_numero_fiscal_rango</c>),
/// separado de <c>PuntoVenta.Id</c>.</summary>
public sealed record NumeroFiscalDePuntoVentaEdicion(int NumeroFiscal);

// --- Slice 5: ServicioDeFacturacionFiscal — POST /api/fiscal/comprobantes(.../reintentar) ---

/// <summary>Una línea de la emisión fiscal (design.md D12/T1: el write plan es
/// <c>comprobante + items</c> ÚNICAMENTE — sin motor de precios/ofertas/stock). Llega YA resuelta
/// por el llamador: <see cref="IdArea"/>/<see cref="IdListaPrecio"/> son <c>NOT NULL</c> en
/// <c>items_comprobante_venta</c> desde la slice 1, así que viajan explícitos en vez de resolverse
/// contra <c>ServicioDeOfertas</c>/el motor de precios que este camino, a propósito, nunca toca
/// (D9: <c>ResolverTipoFiscalAsync</c> "nunca toca <c>ServicioDeVentas</c>"). <see cref="Cantidad"/>/
/// <see cref="PrecioUnitario"/>/<see cref="DescuentoUnitario"/> pasan por
/// <c>Ways.Domain.Ventas.CalculadorDeTotales</c> (dominio puro, no <c>ServicioDeVentas</c>) para el
/// mismo redondeo (<c>MidpointRounding.AwayFromZero</c>) que el resto del proyecto.</summary>
public sealed record LineaDeEmisionFiscal(
    int? IdArticulo,
    string Descripcion,
    int IdArea,
    int IdListaPrecio,
    int IdAlicuotaIva,
    decimal Cantidad,
    decimal PrecioUnitario,
    decimal DescuentoUnitario);

/// <summary>Body de <c>POST /api/fiscal/comprobantes</c>. <see cref="CodigoTipoComprobante"/> es el
/// código del catálogo (<c>FA</c>/<c>FB</c>/<c>FC</c> en 19a) — <c>ResolverTipoFiscalAsync</c>
/// (D9, gate 3) lo valida; el <b>letra</b> efectiva del comprobante la sigue decidiendo el servidor
/// vía <c>ResolvedorDeLetraComprobante</c> (su primer caller, design.md data flow), nunca el
/// request.</summary>
public sealed record SolicitudDeEmisionFiscal(
    int IdPuntoVenta,
    string CodigoTipoComprobante,
    int IdCliente,
    IReadOnlyList<LineaDeEmisionFiscal> Lineas,
    string? Observaciones);

/// <summary>Respuesta de la emisión/reintento fiscal — <c>dto-contract-honesty</c>: ningún campo de
/// certificado ni de material de clave (task 5.23, mismo scan recursivo por nombre de propiedad que
/// el target 62 de la slice 4). <see cref="PayloadQr"/> viaja solo cuando el comprobante quedó
/// aprobado (con o sin observaciones) — un <c>pendiente</c>/<c>rechazado</c> no tiene CAE que
/// codificar en el QR (RG 4291).</summary>
public sealed record ComprobanteFiscalEmitido(
    int Id,
    string CodigoTipoComprobante,
    char Letra,
    int IdPuntoVenta,
    long Numero,
    DateOnly Fecha,
    ResultadoFiscal ResultadoFiscal,
    string? Cae,
    DateOnly? CaeVencimiento,
    string? PayloadQr);
