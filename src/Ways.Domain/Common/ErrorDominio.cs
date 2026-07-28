namespace Ways.Domain.Common;

/// <summary>
/// Regla de negocio violada. La API la traduce a un ProblemDetails con el código incluido.
/// No se usa para errores técnicos ni para validación de formato.
/// </summary>
public class ErrorDominio(string codigo, string mensaje, int estadoHttp = 422)
    : Exception(mensaje)
{
    public string Codigo { get; } = codigo;
    public int EstadoHttp { get; } = estadoHttp;

    public static ErrorDominio NoEncontrado(string mensaje) =>
        new("no_encontrado", mensaje, 404);

    public static ErrorDominio Prohibido(string mensaje) =>
        new("prohibido", mensaje, 403);

    public static ErrorDominio Conflicto(string codigo, string mensaje) =>
        new(codigo, mensaje, 409);
}
