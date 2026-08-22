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
