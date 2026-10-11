namespace Ways.Application.Dominios;

/// <summary>El tenant al que apunta un dominio verificado.</summary>
public sealed record DominioResuelto(int IdTenant, string NombreComercio);

/// <summary>
/// Traduce un host al tenant dueño. Solo resuelve dominios verificados y vivos; para cualquier otro
/// host devuelve <c>null</c>. El resultado se guarda en memoria por un lapso corto, así que toda
/// escritura sobre <c>dominios</c> tiene que invalidar el host que toca.
/// </summary>
public interface IResolvedorDeDominios
{
    Task<DominioResuelto?> ResolverAsync(string host, CancellationToken ct = default);

    /// <summary>Descarta lo que se recuerda de <paramref name="host"/>, incluido el "no existe".</summary>
    void Invalidar(string host);
}
