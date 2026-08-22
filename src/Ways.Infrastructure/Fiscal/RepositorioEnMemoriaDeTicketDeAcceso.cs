using System.Collections.Concurrent;
using Ways.Application.Abstracciones;
using Ways.Application.Fiscal;

namespace Ways.Infrastructure.Fiscal;

/// <summary>
/// Cache en memoria del TA (design D8): <see cref="ConcurrentDictionary{TKey,TValue}"/> + un
/// <see cref="SemaphoreSlim"/> por clave para single-flight — N pedidos concurrentes en frío
/// emiten UN solo <c>loginCms</c> (target 33), nunca N. <see cref="MargenDeSeguridad"/> es
/// ABSOLUTO (10 min), no un porcentaje del TTL de 12h: el riesgo nombrado es el intervalo mínimo
/// de WSAA (10 min Testing / 2 min Producción) — este cache no reintenta <c>loginCms</c> en
/// caliente, y un porcentaje del TTL desperdiciaría un tercio de cada ticket.
/// <see cref="ObtenerOFirmarAsync"/> es la orquestación cache+single-flight; el puerto
/// <see cref="IRepositorioDeTicketDeAcceso"/> solo expone las dos operaciones de storage — la
/// composición vive acá porque el single-flight es un detalle de ESTA implementación en memoria
/// (un cache distribuido necesitaría locking distribuido, fuera de alcance en 19a, decisión 10).
/// Sin caller de producción hasta la slice 5.
/// </summary>
public sealed class RepositorioEnMemoriaDeTicketDeAcceso(IRelojDelSistema reloj) : IRepositorioDeTicketDeAcceso
{
    public static readonly TimeSpan MargenDeSeguridad = TimeSpan.FromMinutes(10);

    private readonly ConcurrentDictionary<ClaveDeTicket, TicketDeAcceso> _tickets = new();
    private readonly ConcurrentDictionary<ClaveDeTicket, SemaphoreSlim> _cerrojos = new();

    public Task<TicketDeAcceso?> ObtenerVigenteAsync(ClaveDeTicket clave, CancellationToken ct)
    {
        if (_tickets.TryGetValue(clave, out var ticket) && EsVigente(ticket))
        {
            return Task.FromResult<TicketDeAcceso?>(ticket);
        }

        return Task.FromResult<TicketDeAcceso?>(null);
    }

    public Task GuardarAsync(ClaveDeTicket clave, TicketDeAcceso ticket, CancellationToken ct)
    {
        _tickets[clave] = ticket;
        return Task.CompletedTask;
    }

    /// <summary>Si hay un TA vigente lo devuelve sin invocar <paramref name="obtenerNuevo"/>; si
    /// no, adquiere el cerrojo de ESTA clave y, ya adentro, vuelve a chequear el cache
    /// (double-checked locking) antes de invocar el factory — el segundo (tercero, ...) pedido
    /// concurrente que entra al cerrojo encuentra el ticket que el primero acaba de guardar y no
    /// llama a WSAA de nuevo.</summary>
    public async Task<TicketDeAcceso> ObtenerOFirmarAsync(
        ClaveDeTicket clave, Func<CancellationToken, Task<TicketDeAcceso>> obtenerNuevo, CancellationToken ct)
    {
        var vigente = await ObtenerVigenteAsync(clave, ct);
        if (vigente is not null)
        {
            return vigente;
        }

        var cerrojo = _cerrojos.GetOrAdd(clave, _ => new SemaphoreSlim(1, 1));
        await cerrojo.WaitAsync(ct);
        try
        {
            vigente = await ObtenerVigenteAsync(clave, ct);
            if (vigente is not null)
            {
                return vigente;
            }

            var nuevo = await obtenerNuevo(ct);
            await GuardarAsync(clave, nuevo, ct);
            return nuevo;
        }
        finally
        {
            cerrojo.Release();
        }
    }

    private bool EsVigente(TicketDeAcceso ticket) => reloj.Ahora < ticket.Expiracion - MargenDeSeguridad;
}
