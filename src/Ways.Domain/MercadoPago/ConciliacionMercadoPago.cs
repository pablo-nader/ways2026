using Ways.Domain.Common;

namespace Ways.Domain.MercadoPago;

/// <summary>Qué resultó de cotejar una fila del reporte de transacciones de Mercado Pago con Ways (<c>filas_conciliacion_mercadopago.resultado</c>).</summary>
public static class ResultadosDeConciliacionMercadoPago
{
    /// <summary>Un cobro aprobado y ligado a una venta, por el mismo importe: se le guardan la comisión y el neto reales (la suma de todas las
    /// filas del archivo que lo concilian, por ejemplo las cuotas de un mismo pago).</summary>
    public const string Conciliado = "conciliado";

    /// <summary>La fila repite exactamente otra del mismo archivo (mismos <c>SOURCE_ID</c>, <c>TRANSACTION_TYPE</c>, <c>TRANSACTION_AMOUNT</c> y
    /// <c>TRANSACTION_DATE</c>): no se suma al cobro ni a los totales del resumen.</summary>
    public const string Duplicada = "duplicada";

    /// <summary>El cobro ya lo había conciliado un archivo anterior: la fila no se aplica y muestra lo guardado para compararlo.</summary>
    public const string YaConciliado = "ya_conciliado";

    /// <summary>El dinero entró y el cobro de Ways no pagó ninguna venta (apartado, fallido con pago, sin liga o sin aprobar): lo resuelve el supervisor.</summary>
    public const string SinVenta = "sin_venta";

    /// <summary>El cobro de Ways existe y está ligado a una venta, pero por un importe distinto del que informa Mercado Pago.</summary>
    public const string ImporteDistinto = "importe_distinto";

    /// <summary>Ningún cobro de Ways: un pago recibido fuera de Ways o una aprobación manual (candidata a confirmarse).</summary>
    public const string SinCobroEnWays = "sin_cobro_en_ways";

    /// <summary>Una devolución (<c>REFUND</c>): se muestra, no cambia ninguna venta.</summary>
    public const string Devolucion = "devolucion";

    /// <summary>Un contracargo o disputa (<c>CHARGEBACK</c>, <c>DISPUTE</c>): se muestra, no cambia ninguna venta.</summary>
    public const string Contracargo = "contracargo";

    /// <summary>Un retiro o payout de la cuenta (<c>WITHDRAWAL</c>, <c>WITHDRAWAL_CANCEL</c>, <c>PAYOUT</c>): lo toma la tesorería en E7.</summary>
    public const string Retiro = "retiro";

    /// <summary>La fila no se pudo leer: se guarda con su número de línea y el motivo, sin datos.</summary>
    public const string Invalida = "invalida";

    public static readonly IReadOnlyList<string> Todos =
        [Conciliado, YaConciliado, Duplicada, SinVenta, ImporteDistinto, SinCobroEnWays, Devolucion, Contracargo, Retiro, Invalida];
}

/// <summary>Los <c>TRANSACTION_TYPE</c> del reporte de transacciones que Ways entiende.</summary>
public static class TiposDeTransaccionMercadoPago
{
    public const string Settlement = "SETTLEMENT";

    public const string Refund = "REFUND";

    public const string Chargeback = "CHARGEBACK";

    public const string Dispute = "DISPUTE";

    public const string Withdrawal = "WITHDRAWAL";

    public const string WithdrawalCancel = "WITHDRAWAL_CANCEL";

    public const string Payout = "PAYOUT";

    public static readonly IReadOnlyList<string> Conocidos =
        [Settlement, Refund, Chargeback, Dispute, Withdrawal, WithdrawalCancel, Payout];

    /// <summary>Retiros y payouts: no se cotejan con ningún cobro.</summary>
    public static bool EsRetiro(string tipo) => tipo is Withdrawal or WithdrawalCancel or Payout;
}

/// <summary>
/// Un reporte de transacciones de Mercado Pago subido por el Admin (doc 10 §7): la cabecera. El mismo archivo (huella SHA-256 de sus
/// bytes) no se importa dos veces en la misma empresa (<c>ux_conciliaciones_mercadopago_archivo</c>). El período es el de las
/// <c>TRANSACTION_DATE</c> de las filas leídas.
/// </summary>
public class ConciliacionMercadoPago : EntidadTenant
{
    public int Id { get; set; }

    public int IdEmpresa { get; set; }

    /// <summary>SHA-256 en hexadecimal minúscula (64 caracteres) de los bytes del archivo.</summary>
    public required string HashArchivo { get; set; }

    public string? NombreArchivo { get; set; }

    public DateTimeOffset? FechaDesde { get; set; }

    public DateTimeOffset? FechaHasta { get; set; }

    /// <summary>El Admin que subió el archivo.</summary>
    public int IdUsuario { get; set; }

    /// <summary>Filas del reporte, leídas o no (cada una tiene su fila en <see cref="FilaConciliacionMercadoPago"/>).</summary>
    public int CantidadFilas { get; set; }

    /// <summary>Cuántas de <see cref="CantidadFilas"/> no se pudieron leer (<see cref="ResultadosDeConciliacionMercadoPago.Invalida"/>).</summary>
    public int CantidadInvalidas { get; set; }
}

/// <summary>
/// Una fila del reporte con su resultado. La fila inválida lleva solo <see cref="NumeroLinea"/> y <see cref="Motivo"/>. Una fila
/// <see cref="ResultadosDeConciliacionMercadoPago.SinCobroEnWays"/> que el Admin confirmó contra un pago de venta sin verificar guarda
/// ese pago en <see cref="IdCobro"/>.
/// </summary>
public class FilaConciliacionMercadoPago : EntidadTenant
{
    public int Id { get; set; }

    public int IdEmpresa { get; set; }

    public int IdConciliacion { get; set; }

    /// <summary>Línea del archivo donde empieza el registro (1 es el encabezado).</summary>
    public int NumeroLinea { get; set; }

    /// <see cref="ResultadosDeConciliacionMercadoPago"/>.
    public required string Resultado { get; set; }

    public string? SourceId { get; set; }

    public string? ExternalReference { get; set; }

    /// <see cref="TiposDeTransaccionMercadoPago"/>.
    public string? TipoTransaccion { get; set; }

    public decimal? Importe { get; set; }

    /// <summary><c>FEE_AMOUNT</c> tal como lo informa el reporte (con el IVA incluido), con su signo: el cobro guarda su valor absoluto.</summary>
    public decimal? Comision { get; set; }

    /// <summary><c>SETTLEMENT_NET_AMOUNT</c>.</summary>
    public decimal? Neto { get; set; }

    public DateTimeOffset? FechaTransaccion { get; set; }

    public DateTimeOffset? FechaLiberacion { get; set; }

    public string? ExternalPosId { get; set; }

    /// <summary>Por qué no se pudo leer la fila; solo las inválidas lo tienen.</summary>
    public string? Motivo { get; set; }

    /// <summary>El cobro de Ways (<c>cobros_mercadopago</c>) con el que se cotejó la fila, si hubo.</summary>
    public int? IdCobroMercadoPago { get; set; }

    /// <summary>El pago de venta sin verificar (<c>cobros</c>) que el Admin confirmó contra esta fila; un pago se confirma contra a lo sumo una.</summary>
    public int? IdCobro { get; set; }
}
