namespace Ways.Application.Vitrinas;

/// <summary>
/// El ETag del logo sale de la marca de tiempo de su última carga: cambia exactamente cuando cambian los
/// bytes, y se puede calcular sin leerlos.
/// </summary>
public static class EtagDelLogo
{
    public static string De(DateTimeOffset actualizadoEn) => $"\"{actualizadoEn.UtcTicks:x}\"";
}
