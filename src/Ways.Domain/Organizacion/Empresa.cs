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

    /// <summary>Condición fiscal del EMISOR ante ARCA (stage-19a, proposal.md §B) — decide la
    /// letra A/B/C junto con la condición fiscal del receptor
    /// (<see cref="Ventas.ResolvedorDeLetraComprobante"/>). NULLABLE A PROPÓSITO: no existe un
    /// default honesto (defaultear a RI emitiría Factura A en silencio a cualquier Responsable
    /// Inscripto) — el camino fiscal exige el valor con un 409 nombrado
    /// (<c>empresa_sin_condicion_fiscal</c>) en vez de asumir.</summary>
    public int? IdCondicionFiscal { get; set; }
}
