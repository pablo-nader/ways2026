namespace Ways.Application.Articulos;

/// <summary>
/// Tope de candidatos que <see cref="ServicioDeGrillaDeArticulos"/> puede resolver en memoria
/// cuando el filtro de precio está activo (un cap RECHAZA, nunca trunca — mismo criterio que
/// <see cref="Exportacion.OpcionesDeExportacion.TopeDeFilas"/>). Bindable desde configuración y
/// NO una constante: un tope que solo se puede ejercitar sembrando decenas de miles de filas es
/// un tope cuya guarda nunca se prueba de verdad — con una opción, el fixture de integración la
/// baja a un número chico y la mutación se observa sin sembrar ese volumen.
/// </summary>
public sealed class OpcionesDeGrillaDeArticulos
{
    public const string Seccion = "GrillaDeArticulos";

    public int TopeDeCandidatosPorFiltroDePrecio { get; set; } = 20_000;

    /// <summary>Tope de candidatos para el filtro de oferta vigente: resolver ofertas cuesta varias consultas por cada
    /// lote de 200 artículos, así que es menor que el del precio. Rechaza, nunca trunca.</summary>
    public int TopeDeCandidatosPorFiltroDeOferta { get; set; } = 5_000;
}
