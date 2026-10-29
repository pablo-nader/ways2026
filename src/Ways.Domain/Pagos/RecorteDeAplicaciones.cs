namespace Ways.Domain.Pagos;

/// <summary>Una aplicación viva tal como entra al recorte: lo que decide el orden (fecha e id) y lo que vale hoy.</summary>
public sealed record AplicacionARecortar(int Id, DateTimeOffset Fecha, decimal Importe);

/// <summary>Lo que el recorte hace con una aplicación: <see cref="Baja"/> la da de baja entera (su importe no se reescribe);
/// si no, queda con <see cref="ImporteNuevo"/>, siempre mayor que cero y menor que <see cref="ImporteAnterior"/>.</summary>
public sealed record AplicacionRecortada(int Id, decimal ImporteAnterior, decimal ImporteNuevo, bool Baja);

/// <summary>
/// Recorte de aplicaciones cuando lo aplicado supera un tope (el importe de un pago editado, el total de una compra
/// editada). Se recorta la aplicación más NUEVA primero (fecha y id descendentes): se dan de baja enteras las que caben
/// en el exceso y se achica la última. Lo recortado vuelve a ser disponible del pago y saldo de la compra; nada se
/// reaplica a otros pagos.
/// </summary>
public static class RecorteDeAplicaciones
{
    /// <summary>Plan de recorte para que la suma de <paramref name="aplicaciones"/> no supere <paramref name="tope"/>. Solo
    /// figuran las aplicaciones que cambian, de la más nueva a la más vieja; sin exceso (o con una lista vacía) no figura
    /// ninguna. Un tope negativo se trata como cero.</summary>
    public static IReadOnlyList<AplicacionRecortada> Planificar(
        IEnumerable<AplicacionARecortar> aplicaciones, decimal tope)
    {
        var ordenadas = aplicaciones.OrderByDescending(a => a.Fecha).ThenByDescending(a => a.Id).ToList();
        var exceso = ordenadas.Sum(a => a.Importe) - Math.Max(tope, 0m);
        var plan = new List<AplicacionRecortada>();

        foreach (var aplicacion in ordenadas)
        {
            if (exceso <= 0m)
            {
                break;
            }

            if (aplicacion.Importe <= exceso)
            {
                exceso -= aplicacion.Importe;
                plan.Add(new AplicacionRecortada(aplicacion.Id, aplicacion.Importe, 0m, Baja: true));
                continue;
            }

            plan.Add(new AplicacionRecortada(aplicacion.Id, aplicacion.Importe, aplicacion.Importe - exceso, Baja: false));
            exceso = 0m;
        }

        return plan;
    }
}
