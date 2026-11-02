using System.Globalization;

namespace Ways.Domain.MercadoPago;

/// <summary>Lo que el conciliador necesita saber de un cobro de Ways (<c>cobros_mercadopago</c>) para cotejarlo con una fila del reporte.</summary>
public sealed record CobroParaConciliar(
    int Id,
    string ExternalReference,
    long? ReferenciaPago,
    string? IdPago,
    string Estado,
    int? IdComprobanteVenta,
    decimal ImporteEsperado,
    bool YaConciliado);

/// <summary>El resultado de cotejar una fila: <see cref="AplicaAlCobro"/> es verdadero cuando la comisión, el neto y la liberación de la
/// fila se guardan en el cobro (solo una fila <see cref="ResultadosDeConciliacionMercadoPago.Conciliado"/> de un cobro todavía no
/// conciliado).</summary>
public sealed record ClasificacionDeFilaMercadoPago(string Resultado, int? IdCobroMercadoPago, bool AplicaAlCobro);

/// <summary>
/// Cotejo de las filas del reporte con los cobros de Ways. Orden: primero <c>EXTERNAL_REFERENCE</c> contra el <c>external_reference</c> del
/// cobro; si no hay cobro, <c>SOURCE_ID</c> contra la <c>referencia_pago_mercadopago</c> (numérica) o el <c>id_pago_mercadopago</c>.
/// Un cobro aprobado y ligado a una venta, por el importe que Mercado Pago dice (<see cref="CobroParaConciliar.ImporteEsperado"/>), es
/// <see cref="ResultadosDeConciliacionMercadoPago.Conciliado"/>; con otro importe, <c>importe_distinto</c>; cualquier otro estado, o
/// aprobado sin venta, es <c>sin_venta</c>; sin cobro, <c>sin_cobro_en_ways</c>. Devoluciones y contracargos se ligan al cobro como un pago
/// pero nunca lo modifican; los retiros no se cotejan.
/// </summary>
public sealed class ConciliadorMercadoPago
{
    private readonly Dictionary<string, CobroParaConciliar> _porReferencia = new(StringComparer.Ordinal);
    private readonly Dictionary<long, CobroParaConciliar> _porReferenciaDePago = [];
    private readonly Dictionary<string, CobroParaConciliar> _porIdDePago = new(StringComparer.Ordinal);

    public ConciliadorMercadoPago(IEnumerable<CobroParaConciliar> cobros)
    {
        foreach (var cobro in cobros.OrderBy(c => c.Id))
        {
            _porReferencia.TryAdd(cobro.ExternalReference, cobro);
            if (cobro.ReferenciaPago is { } referencia)
            {
                _porReferenciaDePago.TryAdd(referencia, cobro);
            }

            if (!string.IsNullOrEmpty(cobro.IdPago))
            {
                _porIdDePago.TryAdd(cobro.IdPago, cobro);
            }
        }
    }

    public ClasificacionDeFilaMercadoPago Clasificar(FilaDeReporteMercadoPago fila)
    {
        if (TiposDeTransaccionMercadoPago.EsRetiro(fila.TipoTransaccion))
        {
            return new ClasificacionDeFilaMercadoPago(ResultadosDeConciliacionMercadoPago.Retiro, null, false);
        }

        var cobro = Buscar(fila);
        switch (fila.TipoTransaccion)
        {
            case TiposDeTransaccionMercadoPago.Refund:
                return new ClasificacionDeFilaMercadoPago(ResultadosDeConciliacionMercadoPago.Devolucion, cobro?.Id, false);
            case TiposDeTransaccionMercadoPago.Chargeback or TiposDeTransaccionMercadoPago.Dispute:
                return new ClasificacionDeFilaMercadoPago(ResultadosDeConciliacionMercadoPago.Contracargo, cobro?.Id, false);
        }

        if (cobro is null)
        {
            return new ClasificacionDeFilaMercadoPago(ResultadosDeConciliacionMercadoPago.SinCobroEnWays, null, false);
        }

        if (cobro.Estado != EstadosDeCobroMercadoPago.Aprobado || cobro.IdComprobanteVenta is null)
        {
            return new ClasificacionDeFilaMercadoPago(ResultadosDeConciliacionMercadoPago.SinVenta, cobro.Id, false);
        }

        return fila.Importe == cobro.ImporteEsperado
            ? new ClasificacionDeFilaMercadoPago(ResultadosDeConciliacionMercadoPago.Conciliado, cobro.Id, !cobro.YaConciliado)
            : new ClasificacionDeFilaMercadoPago(ResultadosDeConciliacionMercadoPago.ImporteDistinto, cobro.Id, false);
    }

    private CobroParaConciliar? Buscar(FilaDeReporteMercadoPago fila)
    {
        if (!string.IsNullOrEmpty(fila.ExternalReference) && _porReferencia.TryGetValue(fila.ExternalReference, out var porReferencia))
        {
            return porReferencia;
        }

        if (long.TryParse(fila.SourceId, NumberStyles.None, CultureInfo.InvariantCulture, out var numerico)
            && _porReferenciaDePago.TryGetValue(numerico, out var porNumero))
        {
            return porNumero;
        }

        return _porIdDePago.GetValueOrDefault(fila.SourceId);
    }
}

/// <summary>Un pago de venta sin verificar (aprobación manual del cajero sin red) que puede ser candidato a una fila sin cobro en Ways.</summary>
public sealed record PagoSinVerificarParaConciliar(
    int IdPago, int IdComprobanteVenta, DateTimeOffset FechaVenta, decimal Importe, string? ExternalPosIdDeLaCaja);

/// <summary>
/// Candidatos para confirmar una fila <c>sin_cobro_en_ways</c> contra una aprobación manual: mismo importe, la <c>TRANSACTION_DATE</c> a
/// no más de un día (24 horas, ambos extremos incluidos) de la fecha de la venta y, cuando la fila trae <c>EXTERNAL_POS_ID</c>, la
/// caja del punto de venta de la venta con ese mismo id. Nunca se aplican solos: los confirma un Admin. Primero el más cercano en el tiempo.
/// </summary>
public static class CandidatosDeAprobacionManualMercadoPago
{
    public static readonly TimeSpan Ventana = TimeSpan.FromDays(1);

    public static IReadOnlyList<PagoSinVerificarParaConciliar> Buscar(
        decimal importe, DateTimeOffset fechaTransaccion, string? externalPosId, IEnumerable<PagoSinVerificarParaConciliar> pagos) =>
        pagos
            .Where(p => p.Importe == importe
                && (p.FechaVenta - fechaTransaccion).Duration() <= Ventana
                && (string.IsNullOrEmpty(externalPosId)
                    || string.Equals(p.ExternalPosIdDeLaCaja, externalPosId, StringComparison.Ordinal)))
            .OrderBy(p => (p.FechaVenta - fechaTransaccion).Duration())
            .ThenBy(p => p.IdPago)
            .ToList();
}
