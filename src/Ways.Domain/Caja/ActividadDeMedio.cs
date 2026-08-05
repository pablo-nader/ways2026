using Ways.Domain.Catalogos;

namespace Ways.Domain.Caja;

/// <summary>
/// Actividad de un medio de pago dentro de un turno (design: The Derivation; Interfaces/
/// Contracts) — una fila por medio del catálogo del tenant, sin importar si tuvo actividad o no
/// (<see cref="TuvoFilas"/> es lo que distingue "medio con actividad" de "medio sin actividad",
/// nunca el valor de <see cref="Pagos"/>/<see cref="Gastos"/>: un medio puede netear exactamente
/// 0 y seguir debiendo una declaración — spec: Arqueo Rows Only For Medios With Activity).
///
/// <see cref="Vueltos"/> es SIEMPRE por medio del pago original (nunca reasignado al ancla acá):
/// es <see cref="CalculadorDeArqueo"/> quien suma <c>vueltosTotales</c> sobre TODOS los medios y
/// lo resta únicamente en la línea del ancla (design decisión 2) — este record solo transporta el
/// dato crudo.
/// </summary>
public readonly record struct ActividadDeMedio(
    int IdMedioPago,
    ComportamientoMedioPago Comportamiento,
    decimal Pagos,
    decimal Vueltos,
    decimal Gastos,
    bool TuvoFilas);
