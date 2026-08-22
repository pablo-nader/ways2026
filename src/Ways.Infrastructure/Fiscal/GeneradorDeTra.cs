using System.Globalization;
using System.Threading;
using Ways.Application.Abstracciones;

namespace Ways.Infrastructure.Fiscal;

/// <summary>
/// Arma el XML <c>loginTicketRequest</c> (TRA) del manual WSAA — puro salvo
/// <see cref="IRelojDelSistema"/> (design.md:212-224). Orden de elementos EXACTO (target 24):
/// <c>uniqueId</c>, <c>generationTime</c>, <c>expirationTime</c> dentro de <c>header</c>, y
/// <c>service</c> como hermano de <c>header</c>, nunca hijo. <c>uniqueId</c> mezcla el reloj con
/// un desambiguador <see cref="Interlocked"/> POR INSTANCIA (no estático — así un test puede
/// fijar el valor construyendo una instancia nueva y llamando <see cref="Construir"/> una sola
/// vez, D3) porque WSAA rechaza un par <c>(uniqueId, generationTime)</c> repetido para el mismo
/// CUIT: dos TRAs armadas en el mismo tick de reloj tienen que diferir (target 26).
/// </summary>
public sealed class GeneradorDeTra(IRelojDelSistema reloj)
{
    public static readonly TimeSpan Ventana = TimeSpan.FromMinutes(10);

    private long _contador;

    public string Construir(string servicio)
    {
        var ahora = reloj.Ahora;
        var desambiguador = Interlocked.Increment(ref _contador) % 1000;
        var uniqueId = (ahora.ToUnixTimeSeconds() * 1000) + desambiguador;

        var generationTime = Formatear(ahora - Ventana);
        var expirationTime = Formatear(ahora + Ventana);

        return "<?xml version=\"1.0\" encoding=\"UTF-8\"?>" +
            "<loginTicketRequest version=\"1.0\">" +
            "<header>" +
            $"<uniqueId>{uniqueId}</uniqueId>" +
            $"<generationTime>{generationTime}</generationTime>" +
            $"<expirationTime>{expirationTime}</expirationTime>" +
            "</header>" +
            $"<service>{servicio}</service>" +
            "</loginTicketRequest>";
    }

    private static string Formatear(DateTimeOffset momento) =>
        momento.ToString("yyyy-MM-ddTHH:mm:sszzz", CultureInfo.InvariantCulture);
}
