namespace Ways.Domain.Reportes;

/// <summary>
/// Cobertura del costo de un período de rentabilidad (stage-9-costo-congelado, tres estados:
/// real / estimado / desconocido). Cada campo lo consume el banner obligatorio de rentabilidad
/// (stage-10 slice 4) — declarado acá, junto al resto de <c>Domain/Reportes/</c>, para que la
/// carpeta aterrice como una sola unidad revisable (design: File Changes), aunque su primer
/// consumidor real llega recién en esa slice.
/// </summary>
public sealed record CoberturaDeCosto(
    int LineasTotales, int LineasConCostoReal, int LineasConCostoEstimado, int LineasSinCosto,
    decimal VentaTotal, decimal VentaConCostoReal, decimal VentaConCostoEstimado, decimal VentaSinCosto,
    bool IncluyeEstimados);
