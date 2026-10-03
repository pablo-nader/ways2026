using Ways.Domain.Common;

namespace Ways.Domain.Articulos;

/// <summary>
/// Normalización y validación puras del código que un proveedor imprime en su factura. El mismo
/// criterio vale para el alta de artículo y para la asociación posterior.
/// </summary>
public static class ReglaDeCodigoProveedor
{
    public const int LongitudMaxima = 50;

    /// <summary>Recorta el valor; un valor nulo o en blanco equivale a "sin código" y devuelve
    /// <c>null</c>.</summary>
    public static string? NormalizarOpcional(string? valor, string campo)
    {
        var limpio = valor?.Trim();

        if (string.IsNullOrEmpty(limpio))
        {
            return null;
        }

        if (limpio.Length > LongitudMaxima)
        {
            throw new ErrorDominio(
                $"{campo}_muy_largo", $"El campo {campo} no puede superar los {LongitudMaxima} caracteres.", 400);
        }

        return limpio;
    }

    public static string NormalizarRequerido(string? valor, string campo) =>
        NormalizarOpcional(valor, campo)
            ?? throw new ErrorDominio($"{campo}_requerido", $"El campo {campo} es obligatorio.", 400);

    /// <summary>Un código de proveedor solo tiene sentido contra un proveedor: sin él no hay a
    /// quién atribuirlo.</summary>
    public static void ExigirProveedor(string? codigoNormalizado, int? idProveedor)
    {
        if (codigoNormalizado is not null && idProveedor is null)
        {
            throw new ErrorDominio(
                "proveedor_habitual_requerido",
                "Para cargar un código de proveedor hay que indicar el proveedor habitual del artículo.",
                400);
        }
    }
}
