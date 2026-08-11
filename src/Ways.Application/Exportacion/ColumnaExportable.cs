namespace Ways.Application.Exportacion;

/// <summary>Encabezado de una columna de <see cref="TablaExportable"/>: el título visible y el
/// <see cref="TipoDeColumna"/> que toda celda de esa columna debe respetar.</summary>
public sealed record ColumnaExportable(string Titulo, TipoDeColumna Tipo);
