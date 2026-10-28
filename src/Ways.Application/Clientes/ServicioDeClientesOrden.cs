using Ways.Application.Busqueda;
using Ways.Application.Listados;
using Ways.Domain.Clientes;

namespace Ways.Application.Clientes;

/// <summary>Columnas de datos de la grilla de clientes: una por cada columna que la pantalla muestra, menos
/// Acciones.</summary>
public enum ColumnaDeOrdenDeClientes
{
    Numero,
    Nombre,
    Documento,
    Telefono,
    Email,
    Estado,
}

/// <summary>Filtros por columna de <c>GET /api/clientes</c>. Los de texto contienen el valor, sin mayúsculas ni
/// acentos; <see cref="Numero"/> y <see cref="Activo"/> son exactos. Todos se combinan con Y.</summary>
public sealed record FiltrosDeClientes(
    int? Numero = null,
    string? Nombre = null,
    string? Documento = null,
    string? Telefono = null,
    string? Email = null,
    bool? Activo = null);

public static class OrdenDeClientes
{
    /// <summary>Lista cerrada de claves de <c>orden</c>: cada una ordena por lo que la columna muestra.</summary>
    public static readonly IReadOnlyDictionary<string, ColumnaDeOrdenDeClientes> Claves =
        new Dictionary<string, ColumnaDeOrdenDeClientes>(StringComparer.Ordinal)
        {
            ["numero"] = ColumnaDeOrdenDeClientes.Numero,
            ["nombre"] = ColumnaDeOrdenDeClientes.Nombre,
            ["documento"] = ColumnaDeOrdenDeClientes.Documento,
            ["telefono"] = ColumnaDeOrdenDeClientes.Telefono,
            ["email"] = ColumnaDeOrdenDeClientes.Email,
            ["estado"] = ColumnaDeOrdenDeClientes.Estado,
        };

    public static OrdenDeListado<ColumnaDeOrdenDeClientes>? Interpretar(string? orden, string? direccion) =>
        OrdenDeListado.Interpretar(orden, direccion, Claves);

    public static IQueryable<Cliente> Filtrar(IQueryable<Cliente> query, FiltrosDeClientes filtros)
    {
        if (filtros.Numero is int numero)
        {
            query = query.Where(c => c.Numero == numero);
        }

        if (FiltrosDeListado.PatronONulo(filtros.Nombre) is { } nombre)
        {
            // La celda "Nombre" muestra nombre y apellido y, debajo, la razón social: el filtro mira las tres.
            query = query.Where(c =>
                BusquedaSinAcentos.Coincide(c.Nombre, nombre) ||
                BusquedaSinAcentos.Coincide(c.Apellido, nombre) ||
                BusquedaSinAcentos.Coincide(c.RazonSocial, nombre) ||
                (c.Apellido != null && BusquedaSinAcentos.Coincide(c.Nombre + " " + c.Apellido, nombre)));
        }

        if (FiltrosDeListado.PatronONulo(filtros.Documento) is { } documento)
        {
            query = query.Where(c => BusquedaSinAcentos.Coincide(c.NumeroDocumento, documento));
        }

        if (FiltrosDeListado.PatronONulo(filtros.Telefono) is { } telefono)
        {
            query = query.Where(c => BusquedaSinAcentos.Coincide(c.Telefono ?? c.Celular, telefono));
        }

        if (FiltrosDeListado.PatronONulo(filtros.Email) is { } email)
        {
            query = query.Where(c => BusquedaSinAcentos.Coincide(c.Email, email));
        }

        if (filtros.Activo is bool activo)
        {
            query = query.Where(c => c.Activo == activo);
        }

        return query;
    }

    /// <summary>Orden de SQL, aplicado antes de paginar. Sin orden pedido: número. Una celda vacía va al final en las
    /// dos direcciones. "Nombre" ordena por el texto que muestra la celda (nombre y apellido), "Documento" por el
    /// número (la celda lo muestra tras el tipo), "Teléfono" por el teléfono o, si falta, el celular, y "Estado" por su
    /// etiqueta: Activo antes que Inactivo en ascendente.</summary>
    public static IOrderedQueryable<Cliente> Ordenar(
        IQueryable<Cliente> query, OrdenDeListado<ColumnaDeOrdenDeClientes>? orden)
    {
        if (orden is null)
        {
            return query.OrderBy(c => c.Numero).ThenBy(c => c.Id);
        }

        var desc = orden.Value.Descendente;
        var ordenado = orden.Value.Columna switch
        {
            ColumnaDeOrdenDeClientes.Numero => desc
                ? query.OrderByDescending(c => c.Numero)
                : query.OrderBy(c => c.Numero),
            ColumnaDeOrdenDeClientes.Nombre => desc
                ? query.OrderByDescending(c => c.Apellido == null || c.Apellido == "" ? c.Nombre : c.Nombre + " " + c.Apellido)
                : query.OrderBy(c => c.Apellido == null || c.Apellido == "" ? c.Nombre : c.Nombre + " " + c.Apellido),
            ColumnaDeOrdenDeClientes.Documento => desc
                ? query.OrderBy(c => c.TipoDocumento == null || c.NumeroDocumento == null).ThenByDescending(c => c.NumeroDocumento)
                : query.OrderBy(c => c.TipoDocumento == null || c.NumeroDocumento == null).ThenBy(c => c.NumeroDocumento),
            ColumnaDeOrdenDeClientes.Telefono => desc
                ? query.OrderBy(c => c.Telefono == null && c.Celular == null).ThenByDescending(c => c.Telefono ?? c.Celular)
                : query.OrderBy(c => c.Telefono == null && c.Celular == null).ThenBy(c => c.Telefono ?? c.Celular),
            ColumnaDeOrdenDeClientes.Email => desc
                ? query.OrderBy(c => c.Email == null).ThenByDescending(c => c.Email)
                : query.OrderBy(c => c.Email == null).ThenBy(c => c.Email),
            ColumnaDeOrdenDeClientes.Estado => desc
                ? query.OrderBy(c => c.Activo)
                : query.OrderByDescending(c => c.Activo),
            _ => query.OrderBy(c => c.Numero),
        };

        return ordenado.ThenBy(c => c.Id);
    }
}
