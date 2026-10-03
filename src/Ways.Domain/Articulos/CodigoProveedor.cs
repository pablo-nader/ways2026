using Ways.Domain.Common;

namespace Ways.Domain.Articulos;

/// <summary>
/// Código con el que un proveedor identifica un artículo en sus facturas (doc 10 §3):
/// tenant-wide, N por par (artículo, proveedor). Cada código es único por proveedor entre los
/// artículos vivos del tenant.
/// </summary>
public class CodigoProveedor : EntidadTenant
{
    public int Id { get; set; }

    public int IdArticulo { get; set; }

    public int IdProveedor { get; set; }

    /// <summary>Único por proveedor (<c>ux_codigos_proveedor_proveedor_codigo</c>, índice parcial
    /// <c>WHERE deleted_at IS NULL</c>); la comparación ignora mayúsculas por ser <c>citext</c>.</summary>
    public required string Codigo { get; set; }
}
