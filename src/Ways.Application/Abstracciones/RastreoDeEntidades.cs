using Microsoft.EntityFrameworkCore;

namespace Ways.Application.Abstracciones;

/// <summary>
/// Qué rastrea el <c>ChangeTracker</c> antes de una escritura y cómo soltar solo lo que la escritura agregó
/// si falla. El contexto vive todo el request: una transacción revertida deja rastreadas las entidades que
/// agregó, en el estado en que hayan quedado. Un <c>SaveChangesAsync</c> posterior sobre el mismo contexto
/// vuelve a escribir, por detrás de quien lo llama, las que quedaron <c>Added</c>, <c>Modified</c> o
/// <c>Deleted</c>. Las que quedaron <c>Unchanged</c> porque su guardado ya había corrido no se reescriben:
/// son estado rastreado desactualizado, porque la transacción que las guardó se revirtió.
/// <c>ChangeTracker.Clear()</c> no sirve para esto: también suelta lo que el llamador ya tenía rastreado.
/// </summary>
internal static class RastreoDeEntidades
{
    /// <summary>Las entidades rastreadas ahora, por referencia.</summary>
    public static IReadOnlySet<object> Instantanea(IWaysDbContext db) =>
        db.ChangeTracker.Entries()
            .Select(entrada => entrada.Entity)
            .ToHashSet(ReferenceEqualityComparer.Instance);

    /// <summary>Suelta del <c>ChangeTracker</c> todo lo rastreado ahora que no estaba en
    /// <paramref name="yaRastreadas"/>, en el estado en que esté, y nada más: lo que el llamador ya tenía
    /// rastreado queda como estaba.</summary>
    public static void SoltarLoAgregadoDesde(IWaysDbContext db, IReadOnlySet<object> yaRastreadas)
    {
        var agregadas = db.ChangeTracker.Entries()
            .Where(entrada => !yaRastreadas.Contains(entrada.Entity))
            .ToList();

        foreach (var entrada in agregadas)
        {
            entrada.State = EntityState.Detached;
        }
    }
}
