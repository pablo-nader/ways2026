namespace Ways.Domain.Reportes;

/// <summary>
/// Granularidad de bucketing de los reportes de gestión (stage-10-agregacion-dashboard). El
/// literal SQL que <c>LectorDeSerieTemporal</c> inlinea en el <c>date_trunc</c> sale de un
/// <c>switch</c> sobre este enum, nunca de texto de la request (design decisión 3): el enum es
/// cerrado y se parsea antes de armar la consulta, así que no hay vector de inyección.
/// </summary>
public enum Granularidad
{
    Dia,
    Semana,
    Mes
}
