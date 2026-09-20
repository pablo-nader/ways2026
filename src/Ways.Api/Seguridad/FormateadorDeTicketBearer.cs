using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.DataProtection;

namespace Ways.Api.Seguridad;

/// <summary>
/// Serializa/deserializa el <see cref="AuthenticationTicket"/> de una sesión de cajero hacia y
/// desde el string opaco que viaja como token bearer — la MISMA técnica (Data Protection +
/// <see cref="TicketDataFormat"/>) que <c>CookieAuthenticationHandler</c> usa por debajo para la
/// cookie de sesión, expuesta acá como un valor que el cliente puede guardar y mandar en un
/// header en vez de una cookie.
///
/// Decisión de diseño: se eligió esto en vez de sumar JWT (librería nueva, una segunda forma de
/// firmar/validar) — el token es un blob opaco cifrado por el Data Protection que la app ya usa
/// para la cookie, y ninguna afirmación DENTRO del token es la autoridad final: la revocación
/// sigue viviendo 100% en <see cref="ValidadorDeSesion"/>, que corre en cada request contra la
/// base, esté la sesión en una cookie o en un bearer.
///
/// Propósito de protección PROPIO (<c>"Ways.Api.Seguridad.SesionBearer.v1"</c>), distinto del
/// que usa el esquema de cookie: un token bearer nunca puede reutilizarse como valor de la
/// cookie <c>ways.sesion</c> ni viceversa, aunque los dos cifren el mismo tipo de ticket.
/// Singleton (mismo criterio que <c>IDataProtectionProvider</c>): crear el protector una sola
/// vez por proceso, no por request.
/// </summary>
public sealed class FormateadorDeTicketBearer
{
    public ISecureDataFormat<AuthenticationTicket> Formato { get; }

    public FormateadorDeTicketBearer(IDataProtectionProvider proveedor)
    {
        var protector = proveedor.CreateProtector("Ways.Api.Seguridad.SesionBearer.v1");
        Formato = new TicketDataFormat(protector);
    }
}
