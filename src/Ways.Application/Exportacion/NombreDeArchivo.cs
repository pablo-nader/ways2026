namespace Ways.Application.Exportacion;

/// <summary>
/// Construye el nombre determinístico de un archivo exportado: mismos parámetros ⇒ mismo
/// nombre, siempre, sin timestamp ni sufijo aleatorio. ASCII por construcción — <paramref
/// name="reporte"/> y <paramref name="alcance"/> deben ser ids (p. ej. "ventas_resumen",
/// "pv3"), nunca nombres libres ingresados por un usuario.
/// </summary>
public static class NombreDeArchivo
{
    public static string Construir(string reporte, string alcance, DateOnly desde, DateOnly hasta) =>
        $"{reporte}_{alcance}_{desde:yyyy-MM-dd}_{hasta:yyyy-MM-dd}.xlsx";
}
