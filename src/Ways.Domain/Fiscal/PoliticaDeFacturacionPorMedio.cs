namespace Ways.Domain.Fiscal;

public enum MotivoDeFacturacion
{
    NoRequerida = 0,
    Forzada = 1,
    MedioExigeFactura = 2,
    CupoAlcanzado = 3
}

/// <summary>Un pago del ticket con el porcentaje (0..100) a facturar de su medio de pago.</summary>
public readonly record struct PagoDeTicket(int IdMedioPago, decimal Importe, decimal Porcentaje);

/// <summary>Variación del saldo a facturar de un medio: saldo nuevo menos saldo anterior.</summary>
public readonly record struct AporteDeSaldo(int IdMedioPago, decimal Aporte);

public sealed record DecisionDeFacturacion(
    bool Facturar,
    MotivoDeFacturacion Motivo,
    IReadOnlyList<AporteDeSaldo> Aportes);

/// <summary>
/// Decide si un ticket completo se factura y cómo se mueve el saldo a facturar de cada medio.
/// Regla pura, sin dependencias: vive acá para poder testearla sin base de datos.
///
/// Orden de las reglas:
///   1. Factura forzada por el cajero: factura.
///   2. Algún medio del ticket al 100 %: factura, sin mirar saldos.
///   3. Si no, cada medio devenga <c>importe × porcentaje / 100</c> sobre su saldo; factura si algún
///      medio con porcentaje mayor a 0 queda con ese saldo tentativo por encima de la mitad del
///      importe del ticket para ese medio.
///   4. Facturado, cada medio del ticket (también los de 0 %) queda en saldo tentativo menos su
///      importe; sin facturar, queda en el saldo tentativo.
///
/// Un medio que se factura porque OTRO medio del ticket disparó (o porque el ticket es forzado)
/// se debita su importe completo mientras solo devenga su propio porcentaje, así que su saldo puede
/// volverse negativo sin cota inferior. Es intencional: lo facturado "por arrastre" cuenta para el
/// cupo de ese medio. Acotarlo en el tiempo es responsabilidad de quien llama, no de esta política.
/// </summary>
public static class PoliticaDeFacturacionPorMedio
{
    public static DecisionDeFacturacion Decidir(
        IReadOnlyList<PagoDeTicket> pagos,
        IReadOnlyDictionary<int, decimal> saldosPorMedio,
        bool forzada)
    {
        ArgumentNullException.ThrowIfNull(pagos);
        ArgumentNullException.ThrowIfNull(saldosPorMedio);
        if (pagos.Count == 0)
        {
            throw new ArgumentException("El ticket debe tener al menos un pago.", nameof(pagos));
        }

        var medios = Agregar(pagos);

        var motivo = MotivoDeFacturacion.NoRequerida;
        if (forzada)
        {
            motivo = MotivoDeFacturacion.Forzada;
        }
        else if (medios.Any(m => m.Porcentaje == 100m))
        {
            motivo = MotivoDeFacturacion.MedioExigeFactura;
        }
        else if (medios.Any(m => m.Porcentaje > 0m && Tentativo(m, saldosPorMedio) > m.Importe / 2m))
        {
            // Redondeo al más cercano: para el medio que dispara, facturar lo deja más cerca de cero que no facturar.
            motivo = MotivoDeFacturacion.CupoAlcanzado;
        }

        var facturar = motivo != MotivoDeFacturacion.NoRequerida;
        var aportes = medios
            .Select(m => new AporteDeSaldo(
                m.IdMedioPago,
                m.Importe * m.Porcentaje / 100m - (facturar ? m.Importe : 0m)))
            .ToList();

        return new DecisionDeFacturacion(facturar, motivo, aportes);
    }

    /// <summary>Aportes opuestos a los registrados: aplicarlos deshace exactamente lo aplicado.</summary>
    public static IReadOnlyList<AporteDeSaldo> Revertir(IEnumerable<AporteDeSaldo> aportes)
    {
        ArgumentNullException.ThrowIfNull(aportes);
        return aportes.Select(a => a with { Aporte = -a.Aporte }).ToList();
    }

    private static decimal Tentativo(PagoDeTicket medio, IReadOnlyDictionary<int, decimal> saldos) =>
        saldos.GetValueOrDefault(medio.IdMedioPago) + medio.Importe * medio.Porcentaje / 100m;

    private static List<PagoDeTicket> Agregar(IReadOnlyList<PagoDeTicket> pagos)
    {
        var porMedio = new SortedDictionary<int, PagoDeTicket>();
        foreach (var pago in pagos)
        {
            if (pago.Importe <= 0m)
            {
                throw new ArgumentException("El importe de cada pago debe ser mayor a cero.", nameof(pagos));
            }

            if (pago.Porcentaje is < 0m or > 100m)
            {
                throw new ArgumentException("El porcentaje debe estar entre 0 y 100.", nameof(pagos));
            }

            if (!porMedio.TryGetValue(pago.IdMedioPago, out var acumulado))
            {
                porMedio[pago.IdMedioPago] = pago;
            }
            else if (acumulado.Porcentaje != pago.Porcentaje)
            {
                throw new ArgumentException(
                    "Un mismo medio de pago no puede tener dos porcentajes distintos en un ticket.", nameof(pagos));
            }
            else
            {
                porMedio[pago.IdMedioPago] = acumulado with { Importe = acumulado.Importe + pago.Importe };
            }
        }

        return [.. porMedio.Values];
    }
}
