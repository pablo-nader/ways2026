using Ways.Domain.Catalogos;

namespace Ways.Domain.Caja;

/// <summary>Una fila ya persistida de <see cref="ArqueoTurno"/>, lo único que el recálculo necesita
/// de ella.</summary>
public sealed record ArqueoPersistido(int IdMedioPago, decimal ImporteEsperado, decimal? ImporteEsperadoOriginal);

/// <summary>Una fila a actualizar (<see cref="EsNueva"/> false) o a insertar.</summary>
public sealed record CambioDeArqueo(int IdMedioPago, decimal ImporteEsperado, decimal ImporteEsperadoOriginal, bool EsNueva);

/// <summary>
/// Recálculo del arqueo de un turno ya cerrado, sin base de datos: compara la derivación del
/// cierre (<see cref="CalculadorDeArqueo"/>, con el ancla pineada) contra las filas persistidas.
/// Nunca elimina filas.
/// </summary>
public static class RecalculadorDeArqueo
{
    /// <summary>
    /// <list type="bullet">
    /// <item>Fila cuyo medio sigue siendo arqueable: toma el esperado recalculado.</item>
    /// <item>Fila cuyo medio dejó de tener actividad: su esperado pasa a 0 (la fila se conserva).
    /// Solo si el medio sigue visible en <paramref name="actividad"/> y no es de cuenta corriente;
    /// si no, no hay derivación honesta para él y la fila queda como está.</item>
    /// <item>Medio arqueable sin fila: fila nueva con original 0 (el cierre no esperaba nada).</item>
    /// </list>
    /// El original se fija la primera vez que una fila cambia y nunca se pisa después.
    /// </summary>
    public static IReadOnlyList<CambioDeArqueo> Planificar(
        IReadOnlyList<LineaDeArqueo> lineas,
        IReadOnlyList<ActividadDeMedio> actividad,
        IReadOnlyList<ArqueoPersistido> persistidos)
    {
        var esperadoPorMedio = lineas.ToDictionary(l => l.IdMedioPago, l => l.ImporteEsperado);
        var mediosSinActividadPosibles = actividad
            .Where(a => a.Comportamiento != ComportamientoMedioPago.CuentaCorriente)
            .Select(a => a.IdMedioPago)
            .ToHashSet();

        var cambios = new List<CambioDeArqueo>();

        foreach (var persistido in persistidos)
        {
            decimal nuevo;
            if (esperadoPorMedio.TryGetValue(persistido.IdMedioPago, out var recalculado))
            {
                nuevo = recalculado;
            }
            else if (mediosSinActividadPosibles.Contains(persistido.IdMedioPago))
            {
                nuevo = 0m;
            }
            else
            {
                continue;
            }

            if (nuevo != persistido.ImporteEsperado)
            {
                cambios.Add(new CambioDeArqueo(
                    persistido.IdMedioPago, nuevo,
                    persistido.ImporteEsperadoOriginal ?? persistido.ImporteEsperado, EsNueva: false));
            }
        }

        var conFila = persistidos.Select(p => p.IdMedioPago).ToHashSet();
        cambios.AddRange(lineas
            .Where(l => !conFila.Contains(l.IdMedioPago))
            .Select(l => new CambioDeArqueo(l.IdMedioPago, l.ImporteEsperado, 0m, EsNueva: true)));

        return cambios.OrderBy(c => c.IdMedioPago).ToList();
    }
}
