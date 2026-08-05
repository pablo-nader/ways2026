namespace Ways.Domain.Caja;

/// <summary>
/// Entrada de <see cref="CalculadorDeArqueo"/> (design: The Derivation; Interfaces/Contracts) —
/// armada por <c>Ways.Application.Caja.LectorDeMovimientosDelTurno</c>, la única fuente de estos
/// datos tanto para el cierre como para el resumen parcial (spec: Resumen Parcial Uses The Same
/// Derivation As Cierre).
///
/// <see cref="FondoInicial"/>/<see cref="Refuerzos"/>/<see cref="Retiros"/> son montos del turno
/// completo (nunca por medio): solo aplican a la línea del ancla, decisión que
/// <see cref="CalculadorDeArqueo"/> toma internamente. <c>vueltosTotales</c> NO viaja acá como
/// campo propio — se deriva sumando <see cref="ActividadDeMedio.Vueltos"/> de
/// <see cref="Actividad"/>, así que solo existe una fuente para ese número.
/// </summary>
public sealed record InsumosDeArqueo(
    decimal FondoInicial,
    decimal Refuerzos,
    decimal Retiros,
    IReadOnlyList<ActividadDeMedio> Actividad);
