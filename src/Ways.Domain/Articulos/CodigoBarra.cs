using Ways.Domain.Common;

namespace Ways.Domain.Articulos;

/// <summary>
/// Código de barras de un artículo (doc 10 §3): tenant-wide, N por artículo, cada código
/// pertenece a exactamente un artículo del tenant — sin overrides por empresa (spec: Codigo
/// De Barra Schema And Cardinality).
/// </summary>
public class CodigoBarra : EntidadTenant
{
    public int Id { get; set; }

    public int IdArticulo { get; set; }

    /// <summary>Único por tenant (<c>ux_codigos_barra_codigo_tenant</c>, partial index
    /// <c>WHERE deleted_at IS NULL</c>) — el mismo código puede repetirse entre tenants
    /// distintos sin conflicto (spec: Same barcode across different tenants is allowed).</summary>
    public required string Codigo { get; set; }

    public bool Activo { get; set; } = true;
}
