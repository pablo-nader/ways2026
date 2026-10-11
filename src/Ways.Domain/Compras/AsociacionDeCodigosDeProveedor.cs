namespace Ways.Domain.Compras;

/// <summary>Una línea de compra vista por la asociación de códigos: solo lo que la decisión necesita.</summary>
public readonly record struct LineaConCodigoDeProveedor(int Orden, int? IdArticulo, string? CodigoProveedor);

/// <summary>Un par (artículo, código) que la confirmación intenta asociar al proveedor de la compra.</summary>
public readonly record struct AsociacionCandidata(int IdArticulo, string Codigo);

/// <summary>
/// Decide qué códigos de proveedor de una compra se intentan asociar al confirmarla. Solo una línea
/// con artículo y con código participa; si dos líneas traen el mismo código gana la de menor
/// <see cref="LineaConCodigoDeProveedor.Orden"/> y la otra queda sin asociar. Que el código ya
/// pertenezca a otro artículo del proveedor lo resuelve la base, no esta regla.
/// </summary>
public static class AsociacionDeCodigosDeProveedor
{
    /// <summary>Devuelve los candidatos sin repetir código (sin distinguir mayúsculas, como la
    /// columna <c>citext</c>), ordenados por código para que dos confirmaciones concurrentes tomen
    /// las filas del índice único en el mismo orden.</summary>
    public static IReadOnlyList<AsociacionCandidata> Seleccionar(IEnumerable<LineaConCodigoDeProveedor> lineas)
    {
        var yaVistos = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        return lineas
            .Where(l => l.IdArticulo is not null && l.CodigoProveedor is not null)
            .OrderBy(l => l.Orden)
            .Where(l => yaVistos.Add(l.CodigoProveedor!))
            .Select(l => new AsociacionCandidata(l.IdArticulo!.Value, l.CodigoProveedor!))
            .OrderBy(c => c.Codigo, StringComparer.OrdinalIgnoreCase)
            .ThenBy(c => c.Codigo, StringComparer.Ordinal)
            .ToList();
    }
}
