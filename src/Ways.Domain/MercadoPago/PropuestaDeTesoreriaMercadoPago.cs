using Ways.Domain.Common;

namespace Ways.Domain.MercadoPago;

/// <summary>Qué propone una conciliación a la tesorería (<c>propuestas_tesoreria_mercadopago.tipo</c>).</summary>
public static class TiposDePropuestaDeTesoreriaMercadoPago
{
    /// <summary>Lo que Mercado Pago cobró de comisión de más o de menos que lo que el cierre de turno acreditó: un ajuste de la cuenta del medio.</summary>
    public const string DiferenciaComision = "diferencia_comision";

    /// <summary>Un retiro o payout de la cuenta de Mercado Pago: sale de esa cuenta y entra a la cuenta bancaria que elige el Admin.</summary>
    public const string Retiro = "retiro";

    /// <summary>Mercado Pago canceló (<c>WITHDRAWAL_CANCEL</c>) un retiro que el Admin ya había aplicado: devuelve el dinero de la cuenta que lo recibió a la de
    /// origen. Las dos cuentas son las del retiro aplicado, así que el Admin solo confirma; aplicarla escribe los dos ajustes inversos en una transacción.</summary>
    public const string RetiroCancelado = "retiro_cancelado";

    public static readonly IReadOnlyList<string> Todos = [DiferenciaComision, Retiro, RetiroCancelado];
}

/// <summary>Estado de una propuesta (<c>propuestas_tesoreria_mercadopago.estado</c>). Aplicada es final; una descartada se puede reabrir (vuelve a pendiente).</summary>
public static class EstadosDePropuestaDeTesoreriaMercadoPago
{
    public const string Pendiente = "pendiente";

    public const string Aplicada = "aplicada";

    public const string Descartada = "descartada";

    public static readonly IReadOnlyList<string> Todos = [Pendiente, Aplicada, Descartada];
}

/// <summary>
/// Un movimiento de tesorería que una conciliación propone y que nada escribe hasta que un Admin lo aplica (doc 10 §7). Se calcula al importar
/// el reporte, en la misma transacción. <see cref="ClaveOrigen"/> identifica de dónde sale: <c>cuenta:{id}</c> la diferencia de comisión de una
/// cuenta (única por conciliación) y el <c>SOURCE_ID</c> de Mercado Pago el retiro (único por empresa, así que dos reportes que se pisan no lo
/// proponen dos veces).
///
/// <see cref="Importe"/> es el efecto sobre la cuenta de origen: en una diferencia de comisión va con signo (negativo si Mercado Pago cobró
/// más de lo que el cierre descontó); en un retiro es siempre el monto positivo que sale de la cuenta de origen.
/// <see cref="IdCuentaDestino"/> solo existe en un retiro aplicado (la cuenta bancaria elegida).
/// </summary>
public class PropuestaDeTesoreriaMercadoPago : EntidadTenant
{
    public int Id { get; set; }

    public int IdEmpresa { get; set; }

    public int IdConciliacion { get; set; }

    /// <see cref="TiposDePropuestaDeTesoreriaMercadoPago"/>.
    public required string Tipo { get; set; }

    public required string ClaveOrigen { get; set; }

    public decimal Importe { get; set; }

    /// <summary>La fila del reporte de la que sale un retiro; nula en una diferencia de comisión.</summary>
    public int? IdFila { get; set; }

    /// <summary>La cuenta que se ajusta (diferencia) o de la que sale el dinero (retiro). En un retiro es la cuenta de los medios de pago con proveedor
    /// Mercado Pago de la empresa cuando es una sola; nula si no hay o hay varias: entonces el Admin la elige al aplicar y queda escrita.</summary>
    public int? IdCuentaOrigen { get; set; }

    public int? IdCuentaDestino { get; set; }

    /// <see cref="EstadosDePropuestaDeTesoreriaMercadoPago"/>.
    public required string Estado { get; set; }

    /// <summary>JSON con el cálculo de una diferencia de comisión (totales y las líneas que más pesan); nulo en un retiro.</summary>
    public string? Detalle { get; set; }

    /// <summary>La fila del libro de la cuenta de origen que escribió la aplicación.</summary>
    public int? IdMovimientoCuenta { get; set; }

    /// <summary>La fila del libro de la cuenta de destino de un retiro aplicado.</summary>
    public int? IdMovimientoCuentaDestino { get; set; }

    public int? IdUsuarioResolucion { get; set; }

    public DateTimeOffset? FechaResolucion { get; set; }

    /// <summary>Por qué se descartó sin que lo decidiera el Admin (<see cref="MotivoCancelado"/>); nulo si se descartó a mano o no está descartada.</summary>
    public string? MotivoResolucion { get; set; }

    /// <summary>Mercado Pago canceló la operación de esta propuesta (hay una fila <c>WITHDRAWAL_CANCEL</c> con su SOURCE_ID en alguna conciliación de la empresa):
    /// el retiro que descartó o que nació descartado por eso, el retiro aplicado que se revierte y su reversión. No se borra al reabrir y una propuesta con la
    /// marca no se puede reabrir (409 <c>propuesta_cancelada_por_mercadopago</c>): reabrirla dejaría aplicar un retiro que Mercado Pago ya canceló.</summary>
    public bool CanceladoPorMercadoPago { get; set; }

    public const string MotivoCancelado = "cancelado por Mercado Pago";

    public static string ClaveDeCuenta(int idCuenta) => $"cuenta:{idCuenta}";

    /// <summary>La clave de un retiro es el id de la operación en Mercado Pago, sin prefijo: el índice parcial es por empresa.</summary>
    public static string ClaveDeRetiro(string sourceId) => sourceId;
}
