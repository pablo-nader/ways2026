namespace Ways.Domain.CuentaCorriente;

/// <summary>
/// Línea histórica de un consumo a reliquidar — snapshot de <c>ItemComprobanteVenta</c> (design:
/// The Re-Pricing Derivation). <see cref="PrecioUnitario"/>/<see cref="Descuento"/> NO entran en
/// la fórmula (solo <see cref="TotalHistorico"/>, el <c>items.total</c> verbatim, nunca
/// recalculado) — viajan acá únicamente para el detalle auditable. La señal de "esta línea tuvo
/// oferta" es <c>Descuento &gt; 0</c>, nunca <c>id_oferta IS NOT NULL</c>: por eso este record ni
/// siquiera trae <c>id_oferta</c> — la fórmula revierte cualquier descuento por construcción
/// (<see cref="ReliquidadorDeConsumos.Calcular"/> recalcula el total del día desde cero, sin
/// descuento, sin importar de dónde vino el descuento histórico).
/// </summary>
public readonly record struct LineaAReliquidar(
    int? IdArticulo, decimal Cantidad, decimal PrecioUnitario, decimal Descuento, decimal TotalHistorico);

/// <summary>Un <c>Consumo</c> elegible con sus líneas ya resueltas (design: Interfaces/Contracts).
/// <see cref="ImporteFinanciado"/> es <c>movimientos_cuenta_corriente.importe</c> del propio
/// consumo (la porción fiada del comprobante); <see cref="TotalComprobante"/> es
/// <c>comprobantes_venta.total</c> — el cociente de los dos es la fracción financiada (design:
/// "Financed fraction").</summary>
public sealed record ConsumoAReliquidar(
    int IdMovimiento, int IdComprobanteVenta, decimal ImporteFinanciado, decimal TotalComprobante,
    IReadOnlyList<LineaAReliquidar> Lineas);

/// <summary>Detalle auditable de una línea (design: "sufficient to reconstruct the calculation").
/// <see cref="Motivo"/> no nulo ⇒ línea omitida (<see cref="PrecioActual"/>/<see cref="TotalDelDia"/>
/// quedan <c>null</c>, <see cref="Delta"/> queda en <c>0</c>) — nunca fatal, nunca acredita.</summary>
public sealed record DetalleDeLinea(
    int? IdArticulo, decimal Cantidad, decimal PrecioHistorico, decimal? PrecioActual, decimal TotalHistorico,
    decimal? TotalDelDia, decimal Delta, string? Motivo);

/// <summary>Detalle auditable de un consumo cubierto — <see cref="Delta"/> ya lleva aplicada la
/// fracción financiada (design: "Only the financed money is re-indexed").</summary>
public sealed record DetalleDeConsumo(
    int IdMovimiento, int IdComprobanteVenta, decimal Delta, IReadOnlyList<DetalleDeLinea> Lineas);

/// <summary><see cref="IdsMovimientosCubiertos"/> lista TODOS los consumos procesados en esta
/// corrida (hasta el cap), incluidos los que aportaron delta <c>0</c> por líneas no precificables
/// — el llamador (<c>ServicioDeReliquidacion</c>) decide si los marca, según si <see cref="Delta"/>
/// (el total de la corrida) es distinto de cero (design: "Zero delta ⇒ no movement and no
/// marker").</summary>
public sealed record ResultadoDeReliquidacion(
    decimal Delta, IReadOnlyList<int> IdsMovimientosCubiertos, IReadOnlyList<DetalleDeConsumo> Detalle, bool HayMas);

/// <summary>
/// El re-pricer puro — el centro de la etapa (design: The Re-Pricing Derivation, "one formula,
/// exists once"). Sin DB, mismo listón que <c>CalculadorDeArqueo</c>: la MISMA función alimenta el
/// preview (<c>GET</c>, sin lock) y el commit (<c>POST</c>, bajo el lock del cliente) — un re-pricer
/// que existiera dos veces sería el defecto que convertiría esta feature en un incidente de
/// confianza (design: Technical Approach).
///
/// <para><c>totalDelDia(i) = round(cantidad × precioActual, 2, AwayFromZero)</c> — SIN descuento,
/// nunca <c>precioActual × (totalHistorico / (cantidad × precioUnitario))</c> (esa fórmula
/// preservaría el ratio del descuento en vez de anularlo, exactamente el error que doc-01:398
/// prohíbe). <c>delta(i) = totalDelDia(i) − totalHistorico(i)</c> decompone en el re-pricing MÁS
/// el descuento anulado sin que este código tenga que separar los dos términos — la resta sola ya
/// los suma.</para>
/// </summary>
public static class ReliquidadorDeConsumos
{
    /// <summary>Design: Eligibility — "capped at 500 consumos per run". El lector pasa hasta
    /// <c>LimiteConsumosPorCorrida + 1</c> filas (ordenadas por fecha ASC) para que este método
    /// pueda derivar <see cref="ResultadoDeReliquidacion.HayMas"/> de su propio input, sin
    /// necesitar un parámetro aparte ni una segunda consulta de conteo.</summary>
    public const int LimiteConsumosPorCorrida = 500;

    public static ResultadoDeReliquidacion Calcular(
        IReadOnlyList<ConsumoAReliquidar> consumos, IReadOnlyDictionary<int, decimal?> precioActualPorArticulo)
    {
        var hayMas = consumos.Count > LimiteConsumosPorCorrida;
        var aProcesar = hayMas ? consumos.Take(LimiteConsumosPorCorrida).ToList() : consumos;

        var deltaTotal = 0m;
        var idsCubiertos = new List<int>(aProcesar.Count);
        var detalle = new List<DetalleDeConsumo>(aProcesar.Count);

        foreach (var consumo in aProcesar)
        {
            var (deltaConsumo, detallesDeLinea) = CalcularConsumo(consumo, precioActualPorArticulo);

            deltaTotal += deltaConsumo;
            idsCubiertos.Add(consumo.IdMovimiento);
            detalle.Add(new DetalleDeConsumo(consumo.IdMovimiento, consumo.IdComprobanteVenta, deltaConsumo, detallesDeLinea));
        }

        return new ResultadoDeReliquidacion(deltaTotal, idsCubiertos, detalle, hayMas);
    }

    private static (decimal Delta, IReadOnlyList<DetalleDeLinea> Detalle) CalcularConsumo(
        ConsumoAReliquidar consumo, IReadOnlyDictionary<int, decimal?> precioActualPorArticulo)
    {
        var detallesDeLinea = new List<DetalleDeLinea>(consumo.Lineas.Count);
        var deltaBruto = 0m;

        foreach (var linea in consumo.Lineas)
        {
            if (linea.IdArticulo is not { } idArticulo)
            {
                detallesDeLinea.Add(new DetalleDeLinea(
                    null, linea.Cantidad, linea.PrecioUnitario, null, linea.TotalHistorico, null, 0m,
                    "Línea de concepto libre (sin artículo) — no re-precificable."));
                continue;
            }

            var precioActual = precioActualPorArticulo.TryGetValue(idArticulo, out var precio) ? precio : null;
            if (precioActual is null)
            {
                detallesDeLinea.Add(new DetalleDeLinea(
                    idArticulo, linea.Cantidad, linea.PrecioUnitario, null, linea.TotalHistorico, null, 0m,
                    "Sin precio vigente en la lista actual del cliente."));
                continue;
            }

            var totalDelDia = Math.Round(linea.Cantidad * precioActual.Value, 2, MidpointRounding.AwayFromZero);
            var deltaLinea = totalDelDia - linea.TotalHistorico;
            deltaBruto += deltaLinea;

            detallesDeLinea.Add(new DetalleDeLinea(
                idArticulo, linea.Cantidad, linea.PrecioUnitario, precioActual, linea.TotalHistorico, totalDelDia,
                deltaLinea, null));
        }

        // Fracción financiada (design: "Financed fraction", deviación declarada) — con
        // financiamiento total (factor = 1) colapsa exacto a la fórmula legacy (asserted por
        // test). comprobante.total == 0 nunca debería llegar acá (la elegibilidad ya exige
        // comprobante.total > 0, design: Eligibility) — defensa en profundidad, no un caso de
        // negocio alcanzable.
        var factor = consumo.TotalComprobante == 0m
            ? 0m
            : Math.Min(1m, consumo.ImporteFinanciado / consumo.TotalComprobante);
        var deltaConsumo = Math.Round(deltaBruto * factor, 2, MidpointRounding.AwayFromZero);

        return (deltaConsumo, detallesDeLinea);
    }
}
