namespace Ways.Domain.Fiscal;

/// <summary>Lo que el acumulado de facturación ya tiene aplicado por un ticket, según el estado de su decisión.</summary>
public enum SituacionFiscalDelTicket
{
    /// <summary>Sin decisión de facturar: el ticket devengó su porcentaje y sumó lo vendido.</summary>
    NoFacturado,

    /// <summary>Decisión pendiente: además del devengo, debitó el importe y lo sumó como facturado.</summary>
    Facturado,

    /// <summary>Decisión rechazada: el rechazo devolvió el débito y lo facturado; queda lo mismo que sin decisión, con los
    /// aportes guardados de la decisión.</summary>
    Rechazado
}

/// <summary>Variación de una fila del acumulado: saldo a facturar, total vendido y total facturado.</summary>
public readonly record struct VariacionDeAcumulado(int IdMedioPago, decimal Saldo, decimal Vendido, decimal Facturado);

/// <summary>Los aportes que el ticket pasa a tener con sus medios nuevos y cómo se mueve cada fila del acumulado.</summary>
public sealed record CambioDeAportes(IReadOnlyList<AporteDeSaldo> AportesNuevos, IReadOnlyList<VariacionDeAcumulado> Variaciones);

/// <summary>
/// Mueve la contribución de un ticket al acumulado de facturación cuando cambian sus medios de pago, sin decidir de nuevo:
/// el ticket conserva si se factura o no. Lo aplicado con los medios anteriores se deshace con sus aportes registrados y lo
/// que cada medio vendió, y se aplica lo que corresponde a los medios nuevos con la misma regla
/// (<see cref="PoliticaDeFacturacionPorMedio.Aportes"/>). Pura, sin base de datos.
/// </summary>
public static class CambioDeMediosEnFacturacion
{
    public static CambioDeAportes Planificar(
        SituacionFiscalDelTicket situacion,
        IReadOnlyList<AporteDeSaldo> aportesAnteriores,
        IReadOnlyList<PagoDeTicket> anteriores,
        IReadOnlyList<PagoDeTicket> nuevos)
    {
        ArgumentNullException.ThrowIfNull(aportesAnteriores);
        ArgumentNullException.ThrowIfNull(anteriores);
        ArgumentNullException.ThrowIfNull(nuevos);

        var facturado = situacion == SituacionFiscalDelTicket.Facturado;
        var aportesNuevos = nuevos.Count == 0
            ? []
            : PoliticaDeFacturacionPorMedio.Aportes(nuevos, situacion != SituacionFiscalDelTicket.NoFacturado);

        var aporteAnterior = aportesAnteriores.GroupBy(a => a.IdMedioPago).ToDictionary(g => g.Key, g => g.Sum(a => a.Aporte));
        var aporteNuevo = aportesNuevos.ToDictionary(a => a.IdMedioPago, a => a.Aporte);
        var vendidoAnterior = VendidoPorMedio(anteriores);
        var vendidoNuevo = VendidoPorMedio(nuevos);

        // Rechazado: el rechazo ya devolvió el débito, así que el saldo del medio quedó en aporte + vendido.
        var conDebitoDevuelto = situacion == SituacionFiscalDelTicket.Rechazado;

        var variaciones = aporteAnterior.Keys.Concat(aporteNuevo.Keys).Concat(vendidoAnterior.Keys).Concat(vendidoNuevo.Keys)
            .Distinct()
            .Order()
            .Select(id =>
            {
                var vendidoAntes = vendidoAnterior.GetValueOrDefault(id);
                var vendidoDespues = vendidoNuevo.GetValueOrDefault(id);
                var saldo = aporteNuevo.GetValueOrDefault(id) - aporteAnterior.GetValueOrDefault(id)
                    + (conDebitoDevuelto ? vendidoDespues - vendidoAntes : 0m);
                var vendido = vendidoDespues - vendidoAntes;

                return new VariacionDeAcumulado(id, saldo, vendido, facturado ? vendido : 0m);
            })
            .Where(v => v.Saldo != 0m || v.Vendido != 0m || v.Facturado != 0m)
            .ToList();

        return new CambioDeAportes(aportesNuevos, variaciones);
    }

    private static Dictionary<int, decimal> VendidoPorMedio(IEnumerable<PagoDeTicket> pagos) =>
        pagos.GroupBy(p => p.IdMedioPago).ToDictionary(g => g.Key, g => g.Sum(p => p.Importe));
}
