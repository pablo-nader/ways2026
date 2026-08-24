namespace Ways.Application.Fiscal;

/// <summary>
/// Opciones fiscales bindables desde configuración (mismo criterio que
/// <c>Ways.Application.Exportacion.OpcionesDeExportacion</c>) — <see cref="Ambiente"/> decide contra
/// qué ambiente (<c>homologacion</c>/<c>produccion</c>) <see cref="ServicioDeFacturacionFiscal"/>
/// busca el certificado activo y arma el TA. <b>DECISIÓN REGISTRADA (slice 5, sin nota explícita en
/// design.md)</b>: el snippet abreviado de design.md no nombra de dónde sale el ambiente de emisión
/// — D6 solo fija que <c>ambiente</c> es parte de la CLAVE del certificado/master-key, no de dónde
/// la emisión lo toma. Default <see cref="AmbienteFiscal.Homologacion"/> si la clave está ausente o
/// no parsea: 19a nunca apunta a un endpoint real (verify criterion 8, OD1), así que un default que
/// jamás resuelve a <c>produccion</c> sin configuración EXPLÍCITA es la opción segura — nunca al
/// revés.
/// </summary>
public sealed class OpcionesFiscales
{
    public const string Seccion = "Ways:Fiscal";

    public string? Ambiente { get; set; }
}
