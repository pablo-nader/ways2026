namespace Ways.Domain.Plantel;

/// <summary>
/// Diferencia entre las horas ya acreditadas de un día y el total pedido, sin base de datos. El libro es
/// append-only: subir el total agrega un asiento al valor hora vigente; bajarlo agrega asientos con horas
/// negativas que consumen las capas positivas del día de la última a la primera (LIFO), cada uno al valor
/// hora congelado de su capa, para que el importe revierta lo que esa capa acreditó. Las bajas anteriores
/// se aplican igual, de modo que solo se reducen las horas que siguen vivas.
/// </summary>
public static class PlanificadorDeHorasDelDia
{
    /// <summary>Un asiento de tipo horas ya escrito, en el orden de inserción del libro.</summary>
    public sealed record AsientoExistente(decimal Horas, decimal ValorHora);

    /// <summary>Un asiento de tipo horas por escribir. <see cref="Horas"/> lleva signo.</summary>
    public sealed record AsientoPlanificado(decimal Horas, decimal ValorHora, decimal Importe);

    public static IReadOnlyList<AsientoPlanificado> Planificar(
        IReadOnlyList<AsientoExistente> existentes, decimal horasDeseadas, decimal valorHoraVigente)
    {
        ReglaDeSemanaDeCarga.ValidarHorasDelDia(horasDeseadas);

        var capas = CapasVivas(existentes);
        var acreditadas = capas.Sum(c => c.Horas);

        if (horasDeseadas == acreditadas)
        {
            return [];
        }

        if (horasDeseadas > acreditadas)
        {
            var diferencia = horasDeseadas - acreditadas;
            var importe = ReglaDeMovimientoDePersonal.CalcularImporteDeHoras(diferencia, valorHoraVigente);

            return [new AsientoPlanificado(diferencia, valorHoraVigente, importe)];
        }

        var porReducir = acreditadas - horasDeseadas;
        var reducciones = new List<AsientoPlanificado>();

        for (var i = capas.Count - 1; i >= 0 && porReducir > 0; i--)
        {
            var (horas, valorHora) = capas[i];
            var quitar = Math.Min(horas, porReducir);

            reducciones.Add(new AsientoPlanificado(-quitar, valorHora, Math.Round(-quitar * valorHora, 2, MidpointRounding.AwayFromZero)));
            porReducir -= quitar;
        }

        return reducciones;
    }

    /// <summary>Las capas positivas que todavía no fueron revertidas: cada baja anterior consumió las de más arriba.</summary>
    private static List<(decimal Horas, decimal ValorHora)> CapasVivas(IReadOnlyList<AsientoExistente> existentes)
    {
        var capas = new List<(decimal Horas, decimal ValorHora)>();

        foreach (var asiento in existentes)
        {
            if (asiento.Horas > 0)
            {
                capas.Add((asiento.Horas, asiento.ValorHora));
                continue;
            }

            var porConsumir = -asiento.Horas;
            while (porConsumir > 0)
            {
                if (capas.Count == 0)
                {
                    throw new InvalidOperationException(
                        "El libro tiene una baja de horas sin horas acreditadas que revertir — invariante del día violado.");
                }

                var (horas, valorHora) = capas[^1];
                var consumo = Math.Min(horas, porConsumir);
                porConsumir -= consumo;

                if (consumo == horas)
                {
                    capas.RemoveAt(capas.Count - 1);
                }
                else
                {
                    capas[^1] = (horas - consumo, valorHora);
                }
            }
        }

        return capas;
    }
}
