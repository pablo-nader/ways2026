namespace Ways.Infrastructure.Fiscal;

/// <summary>
/// Puerto de espera para el backoff exponencial de <see cref="ClienteWsfe"/> — mismo motivo que
/// <c>Ways.Application.Abstracciones.IRelojDelSistema</c> existe para el reloj: un test de
/// "3 intentos con backoff" no puede depender de tiempo real transcurrido (dormiría segundos de
/// verdad). Público porque el repo no usa <c>InternalsVisibleTo</c> en ningún lado (mismo criterio
/// que <c>SobreSoap</c>) y los tests de <c>Ways.Application.Tests</c> necesitan inyectar un fake.
/// </summary>
public interface IEsperador
{
    Task EsperarAsync(TimeSpan duracion, CancellationToken ct);
}

/// <summary>Implementación real — <see cref="Task.Delay(TimeSpan, CancellationToken)"/> sin más.</summary>
public sealed class EsperadorReal : IEsperador
{
    public static readonly EsperadorReal Instancia = new();

    public Task EsperarAsync(TimeSpan duracion, CancellationToken ct) => Task.Delay(duracion, ct);
}
