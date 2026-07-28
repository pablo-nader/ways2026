namespace Ways.Application.Abstracciones;

/// <summary>Fuente de tiempo inyectable, para poder testear vencimientos y bloqueos.</summary>
public interface IRelojDelSistema
{
    DateTimeOffset Ahora { get; }
}

public sealed class RelojDelSistema : IRelojDelSistema
{
    public DateTimeOffset Ahora => DateTimeOffset.UtcNow;
}
