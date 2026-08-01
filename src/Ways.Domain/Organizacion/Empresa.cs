using Ways.Domain.Common;

namespace Ways.Domain.Organizacion;

/// <summary>
/// La unidad fiscal dentro de un tenant: razón social, CUIT. Un tenant puede operar con
/// varias empresas (varias razones sociales).
/// </summary>
public class Empresa : EntidadTenant
{
    public int Id { get; set; }

    public required string RazonSocial { get; set; }

    public string? NombreFantasia { get; set; }

    public string? Cuit { get; set; }
}
