using Microsoft.Extensions.Options;
using Ways.Application.MercadoPago;

namespace Ways.Api.MercadoPago;

/// <summary>
/// Renueva las conexiones de Mercado Pago que están por vencer. Corre poco después del arranque y después cada
/// <see cref="OpcionesDeMercadoPago.IntervaloDeRenovacion"/>. Sin credenciales de Mercado Pago, o con
/// <see cref="OpcionesDeMercadoPago.RenovacionAutomatica"/> apagado, no hace nada y lo avisa una sola vez. Una excepción de un
/// ciclo se registra y no tumba el host.
/// </summary>
public sealed class RenovacionDeConexionesMercadoPago(
    IServiceScopeFactory alcances,
    IOptions<OpcionesDeMercadoPago> opciones,
    TimeProvider reloj,
    ILogger<RenovacionDeConexionesMercadoPago> log) : BackgroundService
{
    public static readonly TimeSpan RetardoInicial = TimeSpan.FromMinutes(1);

    public bool Habilitada => opciones.Value.RenovacionAutomatica && opciones.Value.EstaConfigurado;

    public async Task EjecutarCicloAsync(CancellationToken ct)
    {
        await using var alcance = alcances.CreateAsyncScope();
        var servicio = alcance.ServiceProvider.GetRequiredService<ServicioDeRenovacionDeConexionesMercadoPago>();

        var resumen = await servicio.RenovarConexionesPorVencerAsync(reloj.GetUtcNow(), ct);

        log.LogInformation(
            "Renovación de conexiones de Mercado Pago: {Seleccionadas} seleccionadas, {Renovadas} renovadas, {Omitidas} omitidas, " +
            "{RequierenReconexion} requieren reconexión, {Fallidas} fallidas.",
            resumen.Seleccionadas, resumen.Renovadas, resumen.Omitidas, resumen.RequierenReconexion, resumen.Fallidas);
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!Habilitada)
        {
            log.LogInformation(
                "La renovación automática de Mercado Pago está desactivada: faltan las credenciales o está apagada por configuración.");
            return;
        }

        try
        {
            await Task.Delay(RetardoInicial, reloj, stoppingToken);

            using var temporizador = new PeriodicTimer(opciones.Value.IntervaloDeRenovacion, reloj);
            do
            {
                try
                {
                    await EjecutarCicloAsync(stoppingToken);
                }
                catch (Exception error) when (error is not OperationCanceledException)
                {
                    log.LogError(error, "La renovación de conexiones de Mercado Pago falló; se reintenta en el próximo ciclo.");
                }
            }
            while (await temporizador.WaitForNextTickAsync(stoppingToken));
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
        }
    }
}
