using Ways.Domain.Fiscal;

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

    public const double TimeoutDeLlamadaPorDefectoEnSegundos = 10;

    public const double PresupuestoDeLotePorDefectoEnSegundos = 20;

    /// <summary>Tiempo máximo, en segundos, de CADA ida y vuelta HTTP a WSAA o WSFE (un reintento de transporte de
    /// <c>ClienteWsfe</c> es otra ida y vuelta). Una llamada que lo supera no es un rechazo ni una cancelación del llamador:
    /// es una respuesta no definitiva. Un valor ausente, cero o negativo usa el valor por defecto.</summary>
    public double? TimeoutDeLlamadaSegundos { get; set; }

    /// <summary>Tiempo total, en segundos, de un lote del procesador de facturaciones pendientes. Un valor ausente, cero o
    /// negativo usa el valor por defecto.</summary>
    public double? PresupuestoDeLoteSegundos { get; set; }

    public TimeSpan TimeoutDeLlamada => TimeSpan.FromSeconds(
        TimeoutDeLlamadaSegundos is > 0 ? TimeoutDeLlamadaSegundos.Value : TimeoutDeLlamadaPorDefectoEnSegundos);

    public TimeSpan PresupuestoDeLote => TimeSpan.FromSeconds(
        PresupuestoDeLoteSegundos is > 0 ? PresupuestoDeLoteSegundos.Value : PresupuestoDeLotePorDefectoEnSegundos);

    /// <summary>El ambiente configurado; cualquier valor distinto de "produccion" es homologación.</summary>
    public AmbienteFiscal ResolverAmbiente() =>
        string.Equals(Ambiente, "produccion", StringComparison.OrdinalIgnoreCase)
            ? AmbienteFiscal.Produccion
            : AmbienteFiscal.Homologacion;
}
