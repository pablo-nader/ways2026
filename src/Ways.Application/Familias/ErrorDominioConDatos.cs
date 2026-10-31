using Ways.Domain.Common;

namespace Ways.Application.Familias;

/// <summary>
/// Un <see cref="ErrorDominio"/> que además de su código y su mensaje lleva datos estructurados para el cliente.
/// <c>ManejadorDeErrores</c> los escribe en la extensión <c>datos</c> del ProblemDetails: el cliente no tiene que
/// leerlos del texto del mensaje. Los nombres de las claves son camelCase y forman parte del contrato del código de
/// error que los lleva.
/// </summary>
public sealed class ErrorDominioConDatos(
    string codigo, string mensaje, int estadoHttp, IReadOnlyDictionary<string, object?> datos)
    : ErrorDominio(codigo, mensaje, estadoHttp)
{
    public IReadOnlyDictionary<string, object?> Datos { get; } = datos;

    public static ErrorDominioConDatos Conflicto(
        string codigo, string mensaje, IReadOnlyDictionary<string, object?> datos) =>
        new(codigo, mensaje, 409, datos);
}
