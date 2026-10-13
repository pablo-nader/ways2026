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

    public const double TimeoutDeLlamadaPorDefectoEnSegundos = 8;

    public const double TimeoutDeWsaaPorDefectoEnSegundos = 15;

    public const double PresupuestoDeLotePorDefectoEnSegundos = 20;

    /// <summary>Piso de los dos timeouts: un valor positivo pero menor se vuelve un milisegundo, porque un <c>HttpClient</c> con
    /// timeout cero lanza al usarse.</summary>
    public const double MinimoEnSegundos = 0.001;

    /// <summary>Piso del presupuesto de un lote: un valor positivo pero menor se vuelve un segundo. Con uno ínfimo cada llamada
    /// cortaría antes de intentar nada, en silencio y para siempre.</summary>
    public const double PresupuestoDeLoteMinimoEnSegundos = 1;

    /// <summary>Tope del timeout de una llamada a WSFE y a WSAA. Más allá de esto un valor deja de ser un timeout útil, y uno
    /// infinito o fuera del rango de <c>TimeSpan</c>/<c>HttpClient</c> lanzaría al usarse.</summary>
    public const double TimeoutMaximoEnSegundos = 120;

    /// <summary>Tope del presupuesto de un lote: un request HTTP no puede quedar abierto indefinidamente.</summary>
    public const double PresupuestoDeLoteMaximoEnSegundos = 300;

    /// <summary>Tiempo máximo, en segundos, de CADA ida y vuelta HTTP a WSFE (un reintento de transporte de
    /// <c>ClienteWsfe</c> es otra ida y vuelta, pero un timeout no se reintenta). Una llamada que lo supera no es un rechazo ni una
    /// cancelación del llamador: es una respuesta no definitiva. Un valor ausente, cero, negativo o NaN usa el valor por defecto;
    /// uno mayor que <see cref="TimeoutMaximoEnSegundos"/> se acota a ese tope.</summary>
    public double? TimeoutDeLlamadaSegundos { get; set; }

    /// <summary>Tiempo máximo, en segundos, de la ida y vuelta a WSAA (firmar un ticket de acceso). Es más largo que el de WSFE
    /// porque WSAA no emite un ticket nuevo mientras hay uno vigente: abandonar una firma cuya respuesta se perdió bloquea al
    /// emisor hasta que el ticket vence. Tiene que ser menor que <see cref="PresupuestoDeLoteSegundos"/> (15 s contra 20 s por
    /// defecto): la firma no se cancela con el corte del lote, así que solo una que vence dentro del presupuesto se anota como
    /// intento fallido con su espera; una que lo supera termina cuando ya se cortó y se posterga sin penalidad. Mismas reglas de
    /// valor ausente y de tope que <see cref="TimeoutDeLlamadaSegundos"/>.</summary>
    public double? TimeoutDeWsaaSegundos { get; set; }

    /// <summary>Tiempo, en segundos, que un lote del procesador de facturaciones pendientes tiene para EMPEZAR trabajo. Un valor
    /// ausente, cero, negativo o NaN usa el valor por defecto; uno menor que <see cref="PresupuestoDeLoteMinimoEnSegundos"/> se
    /// sube a ese piso y uno mayor que <see cref="PresupuestoDeLoteMaximoEnSegundos"/> se acota a ese tope.</summary>
    public double? PresupuestoDeLoteSegundos { get; set; }

    public TimeSpan TimeoutDeLlamada =>
        Acotar(TimeoutDeLlamadaSegundos, TimeoutDeLlamadaPorDefectoEnSegundos, TimeoutMaximoEnSegundos);

    public TimeSpan TimeoutDeWsaa =>
        Acotar(TimeoutDeWsaaSegundos, TimeoutDeWsaaPorDefectoEnSegundos, TimeoutMaximoEnSegundos);

    public TimeSpan PresupuestoDeLote =>
        Acotar(
            PresupuestoDeLoteSegundos, PresupuestoDeLotePorDefectoEnSegundos, PresupuestoDeLoteMaximoEnSegundos,
            PresupuestoDeLoteMinimoEnSegundos);

    private static TimeSpan Acotar(double? segundos, double porDefecto, double maximo, double minimo = MinimoEnSegundos) =>
        TimeSpan.FromSeconds(segundos is > 0 ? Math.Clamp(segundos.Value, minimo, maximo) : porDefecto);

    /// <summary>El ambiente configurado; cualquier valor distinto de "produccion" es homologación.</summary>
    public AmbienteFiscal ResolverAmbiente() =>
        string.Equals(Ambiente, "produccion", StringComparison.OrdinalIgnoreCase)
            ? AmbienteFiscal.Produccion
            : AmbienteFiscal.Homologacion;
}
