using Ways.Domain.Common;

namespace Ways.Domain.Organizacion;

/// <summary>
/// El local físico de una empresa: el nivel entre la empresa y el punto de venta (tenant →
/// empresa → sucursal → punto de venta). En esta etapa cada punto de venta tiene la suya (1:1) y
/// el stock sigue colgado del punto de venta; las etapas siguientes mueven el stock acá.
/// </summary>
public class Sucursal : EntidadTenant
{
    public int Id { get; set; }

    /// <summary>FK compuesta a <see cref="Empresa"/> (ver configuración de EF): una fila de un
    /// tenant no puede referenciar la empresa de otro tenant ni por bug.</summary>
    public int IdEmpresa { get; set; }

    public required string Nombre { get; set; }
}
