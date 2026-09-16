using Ways.Domain.Common;

namespace Ways.Domain.Dispositivos;

/// <summary>Validación pura del nombre de un dispositivo — extraída para poder testearla sin
/// base, mismo criterio que <see cref="TokenDeDispositivo"/>.</summary>
public static class NombreDeDispositivo
{
    public const int LargoMaximo = 100;

    /// <summary>Recorta espacios y valida 1..100 caracteres (doc: "nombre 1..100 trimmed").</summary>
    public static string Normalizar(string? valor)
    {
        var limpio = valor?.Trim() ?? string.Empty;

        if (limpio.Length == 0)
        {
            throw new ErrorDominio("nombre_requerido", "El nombre del dispositivo es obligatorio.", 400);
        }

        if (limpio.Length > LargoMaximo)
        {
            throw new ErrorDominio(
                "nombre_muy_largo",
                $"El nombre del dispositivo no puede superar los {LargoMaximo} caracteres.", 400);
        }

        return limpio;
    }
}
