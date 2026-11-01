using System.Text.Json.Serialization;

namespace Ways.Domain.Caja;

/// <summary>
/// Tipo de un <see cref="MovimientoTesoreria"/> (doc 10 §7, ex <c>cajaz</c> del legacy). Enum
/// nativo de Postgres (<c>tipo_movimiento_tesoreria</c>). Esta etapa solo abre camino de
/// escritura para <see cref="RetiroCaja"/> (el cierre lo encadena automáticamente,
/// design: The Cierre Transaction) — <see cref="Deposito"/>, <see cref="Pago"/> y
/// <see cref="Ajuste"/> son valores reservados sin escritor todavía (decisión 4: entradas
/// manuales de tesorería fuera de alcance).
/// </summary>
public enum TipoMovimientoTesoreria
{
    RetiroCaja,
    Deposito,

    /// <summary>Valor del contrato HTTP congelado en "Gasto" (clientes POS instalados).</summary>
    [JsonStringEnumMemberName("Gasto")]
    Pago,

    /// <summary>Compensación del sistema: la escribe la edición o la baja de un pago de origen tesorería.</summary>
    Ajuste,

    /// <summary>Ajuste manual del fondo de efectivo cargado por un Admin; lo distingue de <see cref="Ajuste"/>.</summary>
    AjusteManual
}
