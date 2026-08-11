namespace Ways.Application.Exportacion;

/// <summary>
/// Opciones de exportación a planillas. <see cref="TopeDeFilas"/> es una opción bindable desde
/// configuración y NO una constante (decisión 5 del design de la etapa 11): un tope que solo se
/// puede ejercitar sembrando 25.001 filas es un tope cuya guarda nunca se prueba de verdad —
/// borrar el <c>if</c> que lo aplica dejaría todos los tests en verde. Con una opción, el fixture
/// de integración la baja a un número chico y la mutación se observa en menos de un segundo.
/// </summary>
public sealed class OpcionesDeExportacion
{
    public const string Seccion = "Exportacion";

    public int TopeDeFilas { get; set; } = 25_000;
}
