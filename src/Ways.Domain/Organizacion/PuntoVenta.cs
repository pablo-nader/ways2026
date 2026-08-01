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
}
