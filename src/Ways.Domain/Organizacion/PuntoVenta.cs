using Ways.Domain.Common;

namespace Ways.Domain.Organizacion;

/// <summary>
/// La unidad operativa: el local físico o virtual donde se abren turnos de caja, se
/// emiten comprobantes y se cuenta stock. Coincide con el punto de venta de AFIP/ARCA.
/// </summary>
public class PuntoVenta : EntidadTenant
{
    public int Id { get; set; }

    /// <summary>FK compuesta a <see cref="Empresa"/> (ver configuración de EF): una fila
    /// de un tenant no puede referenciar la empresa de otro tenant ni por bug.</summary>
    public int IdEmpresa { get; set; }

    public required string Nombre { get; set; }

    public string? Domicilio { get; set; }
    public string? Horario { get; set; }
    public string? Whatsapp { get; set; }
    public string? Instagram { get; set; }
    public string? Facebook { get; set; }
    public string? Web { get; set; }

    /// <summary>Punto de venta ARCA asignado (1..99999, stage-19a proposal.md §C, decisión 2) —
    /// SEPARADO de <see cref="Id"/>, que sigue numerando la serie histórica (TX/NCX/TXR/RC/PRE/
    /// REM) sin cambios. NULLABLE: un punto de venta que nunca factura fiscalmente sigue siendo
    /// legal para siempre — un local puede operar puntos fiscales y no fiscales a la vez.
    /// UNIQUE por empresa (<c>ux_puntos_venta_numero_fiscal</c>, PARCIAL) — portante, no
    /// cosmético: es lo que vuelve inyectivo el mapa de la serie de ARCA <c>(PtoVta, CbteTipo)</c>
    /// a <c>(id_punto_venta, codigo_afip)</c>.</summary>
    public int? NumeroFiscal { get; set; }
}
