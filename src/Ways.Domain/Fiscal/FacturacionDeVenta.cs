using Ways.Domain.Common;

namespace Ways.Domain.Fiscal;

/// <summary>
/// Decisión de facturar una venta del POS (el ticket "TX"): una fila por venta que se resolvió "debe
/// facturarse". Entre las decisiones de factura (<see cref="IdFacturacionOrigen"/> nulo) <see cref="IdComprobanteVenta"/>
/// es UNIQUE y es la clave de idempotencia de la decisión: decidir dos veces la misma venta no puede mover el acumulado
/// dos veces. La nota de crédito de una factura es una segunda fila de la misma venta, enlazada por
/// <see cref="IdFacturacionOrigen"/>.
///
/// <see cref="Aportes"/> guarda las variaciones de saldo que la decisión aplicó a cada medio de pago
/// (<c>[{ "idMedioPago": int, "aporte": decimal }]</c>, ver <see cref="AporteDeSaldo"/>), de modo que una
/// anulación pueda revertirlas exactamente, sobre el mismo <see cref="Periodo"/>. Operativa: id_tenant +
/// id_empresa NOT NULL, con la auditoría completa de <see cref="EntidadTenant"/>.
/// </summary>
public class FacturacionDeVenta : EntidadTenant
{
    public int Id { get; set; }

    public int IdEmpresa { get; set; }

    /// <summary>La venta (TX) que debe facturarse. UNIQUE entre las decisiones de factura.</summary>
    public int IdComprobanteVenta { get; set; }

    /// <summary>Por qué se decidió facturar. Nunca <see cref="MotivoDeFacturacion.NoRequerida"/>: una venta
    /// que no se factura no tiene fila.</summary>
    public MotivoDeFacturacion Motivo { get; set; }

    public EstadoFacturacionDeVenta Estado { get; set; } = EstadoFacturacionDeVenta.Pendiente;

    /// <summary>Documento jsonb ya serializado con los aportes de saldo aplicados por medio de pago.</summary>
    public required string Aportes { get; set; }

    /// <summary>Primer día del mes al que se aplicaron los aportes (el cupo es mensual).</summary>
    public DateOnly Periodo { get; set; }

    /// <summary>El comprobante fiscal espejo, una vez emitido. Es una fila de <c>comprobantes_venta</c> cuyo
    /// <c>id_venta_origen</c> apunta a <see cref="IdComprobanteVenta"/>.</summary>
    public int? IdComprobanteFiscal { get; set; }

    /// <summary><c>NULL</c> en la decisión de la factura. Con valor, la fila es la decisión de la nota de crédito de
    /// esa decisión de factura: una factura tiene a lo sumo una nota de crédito. Una nota de crédito no mueve saldos,
    /// así que <see cref="Aportes"/> queda vacío.</summary>
    public int? IdFacturacionOrigen { get; set; }

    public int Intentos { get; set; }

    public string? UltimoError { get; set; }

    /// <summary>Cuándo vuelve a intentarse; <c>NULL</c> con el estado <see cref="EstadoFacturacionDeVenta.Pendiente"/>
    /// significa "ya".</summary>
    public DateTimeOffset? ProximoIntentoEn { get; set; }
}
