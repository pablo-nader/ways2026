namespace Ways.Application.Exportacion;

/// <summary>Encabezado del archivo (proposal decisión 7): empresa, punto de venta o
/// <c>"Todos"</c>, rango de fechas, y quién/cuándo lo generó junto con su zona horaria.
/// <see cref="GeneradoEl"/> viene ya expresado en <see cref="ZonaHoraria"/> (vía
/// <see cref="InstanteDeGeneracion.En"/>), nunca en UTC crudo — la Infrastructure que escribe el
/// XLSX no resuelve zonas horarias, solo imprime la hora de pared que recibe.
/// <see cref="Cobertura"/> lleva el texto de la línea de cobertura de costo estimado
/// (stage-10) cuando el reporte exportado la trae; queda <c>null</c> para el resto.</summary>
public sealed record ContextoDeExportacion(
    string Empresa,
    string? PuntoVenta,
    DateOnly Desde,
    DateOnly Hasta,
    string ZonaHoraria,
    string Usuario,
    DateTimeOffset GeneradoEl,
    string? Cobertura);
