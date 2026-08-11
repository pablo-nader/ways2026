using Ways.Domain.Reportes;

namespace Ways.Application.Reportes;

/// <summary>Un bucket de la serie de ventas ya rellenada (sin huecos, design decisión 4).
/// <see cref="TicketPromedio"/> es <c>null</c>, nunca <c>0</c>, cuando el bucket no tuvo ningún
/// TX — un denominador vacío no tiene respuesta.</summary>
public sealed record BucketDeVentas(string Etiqueta, DateOnly Inicio, decimal Neto, int CantidadTx, decimal? TicketPromedio);

/// <summary>Respuesta de <c>GET /api/reportes/ventas/resumen</c> (design: Interfaces /
/// Contracts). <see cref="ZonaHoraria"/> es la zona efectivamente resuelta y aplicada al
/// bucketing — echo obligatorio (design decisión 5): un número cuyo corte de día es invisible no
/// es auditable.</summary>
public sealed record ResumenDeVentas(
    DateOnly Desde, DateOnly Hasta, Granularidad Granularidad, string ZonaHoraria,
    IReadOnlyList<BucketDeVentas> Serie,
    decimal NetoVendido, int CantidadTx, decimal? TicketPromedio, int CantidadNcx, decimal NetoNcx);
