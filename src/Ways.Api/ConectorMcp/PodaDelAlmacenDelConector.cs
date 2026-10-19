using OpenIddict.Abstractions;

namespace Ways.Api.ConectorMcp;

/// <summary>
/// Una vez por día borra del almacén del conector los tokens y las autorizaciones que ya no sirven
/// (vencidos, canjeados, revocados o autorizaciones sin tokens) creados hace más de
/// <see cref="Antiguedad"/>. Un token todavía válido nunca se borra, sea cual sea su edad.
/// </summary>
public sealed class PodaDelAlmacenDelConector(
    IServiceScopeFactory alcances,
    EstadoDelConectorMcp estado,
    TimeProvider reloj,
    ILogger<PodaDelAlmacenDelConector> log) : BackgroundService
{
    /// <summary>Igual a la vida del refresh token: mientras un refresh canjeado puede volver a
    /// presentarse, su entrada sigue en la base y el reuso se detecta.</summary>
    public static readonly TimeSpan Antiguedad = TimeSpan.FromDays(14);

    public static readonly TimeSpan Intervalo = TimeSpan.FromDays(1);

    public async Task PodarAsync(CancellationToken ct)
    {
        await using var alcance = alcances.CreateAsyncScope();
        var servicios = alcance.ServiceProvider;
        var umbral = reloj.GetUtcNow() - Antiguedad;

        var tokens = await servicios.GetRequiredService<IOpenIddictTokenManager>().PruneAsync(umbral, ct);
        var autorizaciones = await servicios.GetRequiredService<IOpenIddictAuthorizationManager>().PruneAsync(umbral, ct);

        log.LogInformation(
            "Poda del almacén del conector MCP: {Tokens} tokens y {Autorizaciones} autorizaciones borrados.",
            tokens, autorizaciones);
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var temporizador = new PeriodicTimer(Intervalo, reloj);
        do
        {
            if (!estado.Activo)
            {
                continue;
            }

            try
            {
                await PodarAsync(stoppingToken);
            }
            catch (Exception error) when (error is not OperationCanceledException)
            {
                log.LogError(error, "La poda del almacén del conector MCP falló; se reintenta en el próximo ciclo.");
            }
        }
        while (await temporizador.WaitForNextTickAsync(stoppingToken));
    }
}
