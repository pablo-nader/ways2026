using Ways.Domain.Catalogos;
using Ways.Domain.Common;

namespace Ways.Domain.Caja;

/// <summary>
/// Resuelve el ancla de la derivación (design decisión 3): el único medio del tenant con
/// <c>Comportamiento = Efectivo</c>, sobre TODAS las filas del catálogo sin importar
/// <c>Activo</c> (un medio desactivado a mitad de turno puede seguir teniendo pagos). Cero o dos
/// o más filas ⇒ freno duro <c>409 caja_sin_medio_efectivo_unico</c>, la misma excepción tanto
/// para el resumen como para el cierre (design: The Derivation — "so the misconfiguration
/// surfaces during the shift, not at the close").
///
/// Pura, sin base de datos — recibe la actividad ya leída por
/// <c>Ways.Application.Caja.LectorDeMovimientosDelTurno</c> (el catálogo completo de medios ya
/// viaja ahí, así que no hace falta una consulta aparte).
/// </summary>
public static class ResolvedorDeMedioDeCajaFisica
{
    public static int Resolver(IReadOnlyList<ActividadDeMedio> medios)
    {
        var efectivos = medios.Where(m => m.Comportamiento == ComportamientoMedioPago.Efectivo).ToList();

        if (efectivos.Count != 1)
        {
            throw new ErrorDominio(
                "caja_sin_medio_efectivo_unico",
                "El tenant tiene que tener exactamente un medio de pago con comportamiento efectivo.",
                409);
        }

        return efectivos[0].IdMedioPago;
    }
}
