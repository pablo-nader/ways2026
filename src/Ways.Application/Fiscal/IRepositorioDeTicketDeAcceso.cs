namespace Ways.Application.Fiscal;

/// <summary>
/// Puerto de cache del TA (design decisión D8): la implementación de 19a vive en memoria —
/// persistir un TA es persistir una credencial portadora, y eso queda como ítem de gate para 19b
/// (proposal decisión 10, tabla <c>tickets_acceso_fiscal</c>). <see cref="ObtenerVigenteAsync"/>
/// devuelve <c>null</c> tanto si no hay ticket cacheado como si el que hay ya cruzó el margen de
/// seguridad — el llamador no distingue esos dos casos, en ninguno hay nada reusable.
///
/// <b>OBLIGACIÓN DEL SLICE 5 RESUELTA (nota vinculante de la slice 2, judgment ronda 2 juez A —
/// ver <c>RepositorioEnMemoriaDeTicketDeAcceso.cs</c>)</b>: <see cref="ObtenerOFirmarAsync"/> sube
/// acá, al puerto — <see cref="ServicioDeFacturacionFiscal"/> (esta slice) es el primer caller real
/// y necesita invocar el cache+single-flight de doble chequeo SIN conocer el tipo concreto
/// (<c>Ways.Infrastructure.Fiscal.RepositorioEnMemoriaDeTicketDeAcceso</c>) — cablearlo contra el
/// tipo concreto habría cruzado el límite hexagonal que el resto del proyecto respeta
/// (<c>Ways.Application</c> nunca referencia <c>Ways.Infrastructure</c>). La DI (slice 5,
/// <c>DependencyInjection.cs</c>) deja de registrar el tipo concreto como singleton propio — con
/// esta subida, ninguna forma alternativa de pedir la instancia esquiva el puerto, así que las dos
/// formas de resolverla que convivían sin decisión (el riesgo que la nota dejó registrado) quedan
/// resueltas a UNA sola.
/// </summary>
public interface IRepositorioDeTicketDeAcceso
{
    Task<TicketDeAcceso?> ObtenerVigenteAsync(ClaveDeTicket clave, CancellationToken ct);
    Task GuardarAsync(ClaveDeTicket clave, TicketDeAcceso ticket, CancellationToken ct);

    /// <summary>Si hay un TA vigente lo devuelve sin invocar <paramref name="obtenerNuevo"/>; si no,
    /// orquesta el cache+single-flight de la implementación concreta (double-checked locking) antes
    /// de invocar el factory — pedidos concurrentes que rondan al primero no vuelven a llamar WSAA
    /// (target 33, D8).</summary>
    Task<TicketDeAcceso> ObtenerOFirmarAsync(
        ClaveDeTicket clave, Func<CancellationToken, Task<TicketDeAcceso>> obtenerNuevo, CancellationToken ct);

    /// <summary>Descarta el TA cacheado de <paramref name="clave"/> (judgment 19a-slice-5 ronda 2 juez
    /// A — WARNING): el WSFE <c>600</c> lo llama para que el re-firmado post-600 vuelva a pasar por
    /// <see cref="ObtenerOFirmarAsync"/> — su single-flight — en vez de firmar directo por fuera del
    /// cerrojo, de modo que dos emisiones concurrentes con el mismo TA invalidado compartan UNA sola
    /// re-firma. Idempotente: invalidar una clave sin ticket cacheado es un no-op.</summary>
    Task InvalidarAsync(ClaveDeTicket clave, CancellationToken ct);
}
