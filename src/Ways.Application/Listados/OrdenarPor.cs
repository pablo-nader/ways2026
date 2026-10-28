using System.Linq.Expressions;

namespace Ways.Application.Listados;

/// <summary>Armado de <c>ORDER BY</c> para los listados que ordenan en el servidor antes de paginar.</summary>
public static class OrdenarPor
{
    /// <summary>Ordena por una clave que siempre tiene valor.</summary>
    public static IOrderedQueryable<T> Clave<T, TClave>(
        IQueryable<T> query, Expression<Func<T, TClave>> clave, bool descendente) =>
        descendente ? query.OrderByDescending(clave) : query.OrderBy(clave);

    /// <summary>Ordena por una clave que puede faltar (<typeparamref name="TClave"/> nullable o referencia): los
    /// vacíos van al final en las DOS direcciones, porque la primera clave es <c>clave IS NULL</c>.</summary>
    public static IOrderedQueryable<T> ClaveConVacios<T, TClave>(
        IQueryable<T> query, Expression<Func<T, TClave>> clave, bool descendente)
    {
        var esVacia = Expression.Lambda<Func<T, bool>>(
            Expression.Equal(clave.Body, Expression.Constant(null, typeof(TClave))),
            clave.Parameters);

        var porVacio = query.OrderBy(esVacia);

        return descendente ? porVacio.ThenByDescending(clave) : porVacio.ThenBy(clave);
    }
}
