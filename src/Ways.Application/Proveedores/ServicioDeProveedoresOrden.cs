using Ways.Application.Busqueda;
using Ways.Application.Listados;
using Ways.Domain.Proveedores;

namespace Ways.Application.Proveedores;

/// <summary>Columnas de datos de la grilla de proveedores: una por cada columna que la pantalla muestra, menos
/// Acciones.</summary>
public enum ColumnaDeOrdenDeProveedores
{
    RazonSocial,
    Cuit,
    Telefono,
    Email,
    Margen,
    Estado,
}

/// <summary>Filtros por columna de <c>GET /api/proveedores</c>. Los de texto contienen el valor, sin mayúsculas ni
/// acentos; el margen es un rango inclusivo y <see cref="Activo"/> es exacto. Todos se combinan con Y. El CUIT
/// exacto por dígitos es otro parámetro (<c>cuit</c>): este contiene el texto tal como lo muestra la celda.</summary>
public sealed record FiltrosDeProveedores(
    string? RazonSocial = null,
    string? CuitContiene = null,
    string? Telefono = null,
    string? Email = null,
    decimal? MargenDesde = null,
    decimal? MargenHasta = null,
    bool? Activo = null);

public static class OrdenDeProveedores
{
    /// <summary>Lista cerrada de claves de <c>orden</c>: cada una ordena por lo que la columna muestra.</summary>
    public static readonly IReadOnlyDictionary<string, ColumnaDeOrdenDeProveedores> Claves =
        new Dictionary<string, ColumnaDeOrdenDeProveedores>(StringComparer.Ordinal)
        {
            ["razonSocial"] = ColumnaDeOrdenDeProveedores.RazonSocial,
            ["cuit"] = ColumnaDeOrdenDeProveedores.Cuit,
            ["telefono"] = ColumnaDeOrdenDeProveedores.Telefono,
            ["email"] = ColumnaDeOrdenDeProveedores.Email,
            ["margen"] = ColumnaDeOrdenDeProveedores.Margen,
            ["estado"] = ColumnaDeOrdenDeProveedores.Estado,
        };

    public static OrdenDeListado<ColumnaDeOrdenDeProveedores>? Interpretar(string? orden, string? direccion) =>
        OrdenDeListado.Interpretar(orden, direccion, Claves);

    public static IQueryable<Proveedor> Filtrar(IQueryable<Proveedor> query, FiltrosDeProveedores filtros)
    {
        if (FiltrosDeListado.PatronONulo(filtros.RazonSocial) is { } razonSocial)
        {
            // La celda muestra la razón social y, debajo, el nombre de fantasía: el filtro mira las dos.
            query = query.Where(p =>
                BusquedaSinAcentos.Coincide(p.RazonSocial, razonSocial) ||
                BusquedaSinAcentos.Coincide(p.NombreFantasia, razonSocial));
        }

        if (FiltrosDeListado.PatronONulo(filtros.CuitContiene) is { } cuit)
        {
            query = query.Where(p => BusquedaSinAcentos.Coincide(p.Cuit, cuit));
        }

        if (FiltrosDeListado.PatronONulo(filtros.Telefono) is { } telefono)
        {
            query = query.Where(p => BusquedaSinAcentos.Coincide(p.Telefono, telefono));
        }

        if (FiltrosDeListado.PatronONulo(filtros.Email) is { } email)
        {
            query = query.Where(p => BusquedaSinAcentos.Coincide(p.Email, email));
        }

        if (filtros.MargenDesde is decimal desde)
        {
            query = query.Where(p => p.Margen != null && p.Margen >= desde);
        }

        if (filtros.MargenHasta is decimal hasta)
        {
            query = query.Where(p => p.Margen != null && p.Margen <= hasta);
        }

        if (filtros.Activo is bool activo)
        {
            query = query.Where(p => p.Activo == activo);
        }

        return query;
    }

    /// <summary>Orden de SQL, aplicado antes de paginar. Sin orden pedido: razón social. Una celda vacía va al final
    /// en las dos direcciones. "Estado" ordena por su etiqueta: Activo antes que Inactivo en ascendente.</summary>
    public static IOrderedQueryable<Proveedor> Ordenar(
        IQueryable<Proveedor> query, OrdenDeListado<ColumnaDeOrdenDeProveedores>? orden)
    {
        if (orden is null)
        {
            return query.OrderBy(p => p.RazonSocial).ThenBy(p => p.Id);
        }

        var desc = orden.Value.Descendente;
        var ordenado = orden.Value.Columna switch
        {
            ColumnaDeOrdenDeProveedores.RazonSocial => desc
                ? query.OrderByDescending(p => p.RazonSocial)
                : query.OrderBy(p => p.RazonSocial),
            ColumnaDeOrdenDeProveedores.Cuit => desc
                ? query.OrderBy(p => p.Cuit == null).ThenByDescending(p => p.Cuit)
                : query.OrderBy(p => p.Cuit == null).ThenBy(p => p.Cuit),
            ColumnaDeOrdenDeProveedores.Telefono => desc
                ? query.OrderBy(p => p.Telefono == null).ThenByDescending(p => p.Telefono)
                : query.OrderBy(p => p.Telefono == null).ThenBy(p => p.Telefono),
            ColumnaDeOrdenDeProveedores.Email => desc
                ? query.OrderBy(p => p.Email == null).ThenByDescending(p => p.Email)
                : query.OrderBy(p => p.Email == null).ThenBy(p => p.Email),
            ColumnaDeOrdenDeProveedores.Margen => desc
                ? query.OrderBy(p => p.Margen == null).ThenByDescending(p => p.Margen)
                : query.OrderBy(p => p.Margen == null).ThenBy(p => p.Margen),
            ColumnaDeOrdenDeProveedores.Estado => desc
                ? query.OrderBy(p => p.Activo)
                : query.OrderByDescending(p => p.Activo),
            _ => query.OrderBy(p => p.RazonSocial),
        };

        return ordenado.ThenBy(p => p.Id);
    }
}
