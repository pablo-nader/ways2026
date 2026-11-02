using Ways.Domain.Common;

namespace Ways.Domain.MercadoPago;

/// <summary>Qué propone una conciliación a la tesorería (<c>propuestas_tesoreria_mercadopago.tipo</c>).</summary>
public static class TiposDePropuestaDeTesoreriaMercadoPago
{
    /// <summary>Lo que Mercado Pago cobró de comisión de más o de menos que lo que el cierre de turno acreditó: un ajuste de la cuenta del medio.</summary>
    public const string DiferenciaComision = "diferencia_comision";

    /// <summary>Un retiro o payout de la cuenta de Mercado Pago: sale de esa cuenta y entra a la cuenta bancaria que elige el Admin.</summary>
    public const string Retiro = "retiro";

    public static readonly IReadOnlyList<string> Todos = [DiferenciaComision, Retiro];
}

/// <summary>Estado de una propuesta (<c>propuestas_tesoreria_mercadopago.estado</c>). Aplicada y descartada son finales.</summary>
public static class EstadosDePropuestaDeTesoreriaMercadoPago
{
    public const string Pendiente = "pendiente";

    public const string Aplicada = "aplicada";

    public const string Descartada = "descartada";

    public static readonly IReadOnlyList<string> Todos = [Pendiente, Aplicada, Descartada];
}

/// <summary>
/// Un movimiento de tesorería que una conciliación propone y que nada escribe hasta que un Admin lo aplica (doc 10 §7). Se calcula al importar
/// el reporte, en la misma transacción. <see cref="ClaveOrigen"/> identifica de dónde sale dentro de la conciliación
/// (<c>cuenta:{id}</c> la diferencia de comisión de una cuenta, <c>linea:{n}</c> el retiro de una línea del archivo), y con el tipo es única por
/// conciliación: volver a calcular no duplica.
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

    /// <summary>La cuenta que se ajusta (diferencia) o de la que sale el dinero (retiro). En un retiro es la cuenta de la conexión con Mercado Pago de la
    /// empresa, y es nula si la empresa no tiene conexión: entonces no se puede aplicar.</summary>
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

    public static string ClaveDeCuenta(int idCuenta) => $"cuenta:{idCuenta}";

    public static string ClaveDeLinea(int numeroLinea) => $"linea:{numeroLinea}";
}
