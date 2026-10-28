using Ways.Domain.Common;

namespace Ways.Application.Listados;

/// <summary>Orden pedido a un listado paginado en el servidor (<c>orden</c> + <c>direccion</c>).</summary>
public readonly record struct OrdenDeListado<TColumna>(TColumna Columna, bool Descendente)
    where TColumna : struct, Enum;

public static class OrdenDeListado
{
    public const string ValorAscendente = "ascendente";
    public const string ValorDescendente = "descendente";

    /// <summary>Lista cerrada: una clave o dirección desconocida se RECHAZA con 400, nunca se ignora.
    /// Sin <paramref name="orden"/> ni <paramref name="direccion"/> devuelve <c>null</c> (orden de
    /// siempre del listado). <paramref name="direccion"/> omitida con <paramref name="orden"/> presente es
    /// ascendente. <paramref name="permitida"/> corta una columna que el rol no ve: ordenar por ella
    /// filtraría su orden relativo.</summary>
    public static OrdenDeListado<TColumna>? Interpretar<TColumna>(
        string? orden,
        string? direccion,
        IReadOnlyDictionary<string, TColumna> claves,
        Func<TColumna, bool>? permitida = null)
        where TColumna : struct, Enum
    {
        if (orden is null)
        {
            return direccion is null
                ? null
                : throw new ErrorDominio("direccion_sin_orden", "direccion exige indicar también orden.", 400);
        }

        if (!claves.TryGetValue(orden, out var columna))
        {
            throw new ErrorDominio(
                "orden_invalido",
                $"orden '{orden}' no es válido; valores posibles: {string.Join(", ", claves.Keys)}.",
                400);
        }

        if (permitida is not null && !permitida(columna))
        {
            throw new ErrorDominio("orden_no_permitido", $"El rol actual no puede ordenar por {orden}.", 400);
        }

        var descendente = direccion switch
        {
            null or ValorAscendente => false,
            ValorDescendente => true,
            _ => throw new ErrorDominio(
                "direccion_invalida",
                $"direccion '{direccion}' no es válida; valores posibles: {ValorAscendente}, {ValorDescendente}.",
                400),
        };

        return new OrdenDeListado<TColumna>(columna, descendente);
    }
}
