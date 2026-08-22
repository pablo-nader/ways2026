using Ways.Domain.Common;

namespace Ways.Domain.Fiscal;

/// <summary>
/// La autorización que <c>IClienteWsfe.SolicitarCaeAsync</c> exige (design decisión D4): SOLO
/// <see cref="MaquinaDeEstadosCae"/> la construye — el constructor es <c>internal</c>, así que
/// ningún código de <c>Ways.Application</c>/<c>Ways.Infrastructure</c> puede fabricar una instancia
/// sin pasar por la máquina. No es un booleano (<c>bool consultarPrimero</c>) a propósito: un
/// booleano se pisa con un <c>if</c> descuidado e invisible en review; un tipo que no compila fuera
/// de este archivo vuelve el error IRREPRESENTABLE, no solo improbable (I2/I3, el invariante más
/// caro de violar de toda la sub-etapa).
/// </summary>
public readonly record struct PermisoDeSolicitud
{
    public int IdComprobante { get; }
    public long Numero { get; }

    internal PermisoDeSolicitud(int idComprobante, long numero)
    {
        IdComprobante = idComprobante;
        Numero = numero;
    }
}

/// <summary>Estado del intento previo para el número que se está por reintentar (I2). Un
/// comprobante ya terminal (<see cref="MaquinaDeEstadosCae.EsTerminal"/>) nunca llega hasta acá —
/// <c>ix_comprobantes_venta_fiscal_pendientes</c> ya lo filtra fuera (I3, slice 1).</summary>
public enum EstadoDeIntento
{
    /// <summary>La primera emisión de este número — nada que consultar todavía.</summary>
    SinIntentoPrevio,

    /// <summary>El intento anterior no terminó en una respuesta definitiva (timeout, error de
    /// transporte, respuesta ambigua) — I2 exige <c>FECompConsultar</c> antes de reintentar.</summary>
    NoDefinitivo
}

/// <summary>Lo que <see cref="MaquinaDeEstadosCae.Decidir"/> resuelve para un
/// <see cref="EstadoDeIntento"/> dado.</summary>
public enum DecisionDeReintento
{
    /// <summary>Puede pedir el permiso directamente — no hay intento previo que reconciliar.</summary>
    EmitirDirecto,

    /// <summary><c>FECompConsultar</c> primero (I2); si ARCA ya autorizó el número, se adopta el
    /// CAE existente y CERO <c>FECAESolicitar</c> se emite.</summary>
    ConsultarPrimero
}

/// <summary>
/// La máquina de estados del CAE (design.md: Ports and the CAE machine, patrón
/// <see cref="Usuarios.PoliticaDeRoles"/>) — pura, sin acceso a base de datos, sin reloj, sin
/// HttpClient. Impone TRES estados de respuesta, nunca dos (spec comprobante-fiscal: "The CAE
/// State Machine Has Three Response States, Not Two"): una aprobación CON observaciones es una
/// factura VÁLIDA y terminal, jamás un fallo a reintentar.
/// </summary>
public static class MaquinaDeEstadosCae
{
    /// <summary>I3: solo las dos aprobaciones son terminales. <c>Rechazado</c> queda deliberadamente
    /// afuera — un comprobante rechazado nunca tuvo CAE que proteger de sobreescritura, y de todos
    /// modos sale de <c>ix_comprobantes_venta_fiscal_pendientes</c> apenas <c>resultado_fiscal</c>
    /// deja de ser <c>'pendiente'</c>, así que jamás vuelve a esta máquina.</summary>
    public static bool EsTerminal(ResultadoFiscal resultado) =>
        resultado is ResultadoFiscal.Aprobado or ResultadoFiscal.AprobadoConObservaciones;

    public static DecisionDeReintento Decidir(EstadoDeIntento previo) => previo switch
    {
        EstadoDeIntento.SinIntentoPrevio => DecisionDeReintento.EmitirDirecto,
        EstadoDeIntento.NoDefinitivo => DecisionDeReintento.ConsultarPrimero,
        _ => throw new ArgumentOutOfRangeException(nameof(previo), previo, "EstadoDeIntento no reconocido.")
    };

    /// <summary>Traduce el <c>Resultado</c> crudo de ARCA (<c>'A'</c>/<c>'R'</c>, un único char en
    /// el wire) más la presencia de <c>Observaciones[]</c> a los tres estados del dominio. Un
    /// resultado que no sea ni <c>'A'</c> ni <c>'R'</c> es un defecto de transcripción o un cambio
    /// de contrato no visto — se rechaza en vez de adivinar (mismo criterio que
    /// <c>ClienteWsaa.MapearFalla</c>'s brazo default).</summary>
    public static ResultadoFiscal Mapear(char resultadoArca, bool hayObservaciones) => resultadoArca switch
    {
        'A' when hayObservaciones => ResultadoFiscal.AprobadoConObservaciones,
        'A' => ResultadoFiscal.Aprobado,
        'R' => ResultadoFiscal.Rechazado,
        _ => throw new ErrorDominio(
            "arca_resultado_no_reconocido", $"Resultado ARCA no reconocido: '{resultadoArca}'.", 502)
    };

    /// <summary>El ÚNICO productor de <see cref="PermisoDeSolicitud"/> (D4) — llamado recién
    /// después de <see cref="Decidir"/> (directo) o de un <c>FECompConsultar</c> que no encontró
    /// nada que adoptar (tras <c>ConsultarPrimero</c>).</summary>
    public static PermisoDeSolicitud AutorizarSolicitud(int idComprobante, long numero) =>
        new(idComprobante, numero);
}
