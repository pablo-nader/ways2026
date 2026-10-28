using Microsoft.EntityFrameworkCore;
using Ways.Application.Abstracciones;
using Ways.Domain.Usuarios;

namespace Ways.Application.Listados;

/// <summary>Texto de cliente que muestra la celda de un listado: número y nombre.</summary>
public sealed record ClienteDeListado(int Numero, string Nombre);

/// <summary>Resuelve, para los ids de UNA página ya leída, el cliente o proveedor que la celda muestra. Una sola
/// consulta por página y por <c>DbSet</c> (que ya excluye las bajas): una referencia dada de baja o colgante no
/// aparece en el resultado y la fila sigue en la página con el dato vacío, igual que cuando el listado ordena
/// por esa columna.</summary>
public static class ReferenciasDeListado
{
    /// <summary>Razón social si existe; si no, nombre y apellido sin huecos. Espeja <c>etiquetaDeCliente</c>
    /// del front.</summary>
    public static string NombreDeCliente(string? razonSocial, string nombre, string? apellido) =>
        razonSocial ?? string.Join(' ', new[] { nombre, apellido }.Where(parte => !string.IsNullOrEmpty(parte)));

    public static async Task<IReadOnlyDictionary<int, ClienteDeListado>> ClientesAsync(
        IWaysDbContext db, IReadOnlyCollection<int> ids, CancellationToken ct)
    {
        if (ids.Count == 0)
        {
            return new Dictionary<int, ClienteDeListado>();
        }

        var distintos = ids.Distinct().ToList();
        var filas = await db.Clientes.AsNoTracking()
            .Where(c => distintos.Contains(c.Id))
            .Select(c => new { c.Id, c.Numero, c.RazonSocial, c.Nombre, c.Apellido })
            .ToListAsync(ct);

        return filas.ToDictionary(
            c => c.Id, c => new ClienteDeListado(c.Numero, NombreDeCliente(c.RazonSocial, c.Nombre, c.Apellido)));
    }

    /// <summary>El catálogo de proveedores es de la gestión de catálogo (<c>GET /api/proveedores</c>, solo Admin): los
    /// listados de compras, que leen todos los roles de operación, devuelven la razón social únicamente a quien ya podía
    /// verla ahí; para el resto el resultado es vacío y la pantalla rotula la fila por su id, como antes.</summary>
    public static Task<IReadOnlyDictionary<int, string>> ProveedoresVisiblesAsync(
        IWaysDbContext db, IContextoDeUsuario contexto, IReadOnlyCollection<int> ids, CancellationToken ct) =>
        contexto.Rol == RolConocido.Admin
            ? ProveedoresAsync(db, ids, ct)
            : Task.FromResult<IReadOnlyDictionary<int, string>>(new Dictionary<int, string>());

    private static async Task<IReadOnlyDictionary<int, string>> ProveedoresAsync(
        IWaysDbContext db, IReadOnlyCollection<int> ids, CancellationToken ct)
    {
        if (ids.Count == 0)
        {
            return new Dictionary<int, string>();
        }

        var distintos = ids.Distinct().ToList();

        return await db.Proveedores.AsNoTracking()
            .Where(p => distintos.Contains(p.Id))
            .ToDictionaryAsync(p => p.Id, p => p.RazonSocial, ct);
    }
}
