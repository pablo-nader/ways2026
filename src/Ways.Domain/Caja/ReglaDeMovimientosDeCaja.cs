using Ways.Domain.Common;

namespace Ways.Domain.Caja;

/// <summary>
/// Reglas de negocio puras de <see cref="MovimientoCaja"/> (design decisión 8), sin
/// dependencias — mismo criterio que <see cref="ReglaDeTurnos"/>. <c>ServicioDeTurnos.
/// RegistrarMovimientoAsync</c> (Slice 2) pasa por acá antes de tocar la fila.
/// </summary>
public static class ReglaDeMovimientosDeCaja
{
    /// <summary>Longitud mínima de <see cref="MovimientoCaja.Motivo"/>, uniforme para los tres
    /// <see cref="TipoMovimientoCaja"/> (design decisión 8: "una regla, sin rama por tipo") —
    /// paridad con el F12 del legacy (doc-01:157), llevada a los otros dos tipos en vez de
    /// inventar una regla distinta para cada uno.</summary>
    private const int LongitudMinimaDeMotivo = 5;

    /// <summary><see cref="TipoMovimientoCaja.AperturaCajon"/> exige <c>importe = 0</c> exacto
    /// (es un rastro de auditoría, nunca dinero); los otros dos tipos exigen <c>importe &gt;
    /// 0</c> — mover dinero físico en cero o en negativo no tiene significado de negocio (design
    /// decisión 8, <c>ck_movimientos_caja_importe</c>).</summary>
    public static void ExigirImporteValido(TipoMovimientoCaja tipo, decimal importe)
    {
        if (tipo == TipoMovimientoCaja.AperturaCajon)
        {
            if (importe != 0m)
            {
                throw new ErrorDominio(
                    "movimiento_de_caja_importe_invalido",
                    "El importe de una apertura de cajón tiene que ser exactamente 0.", 400);
            }

            return;
        }

        if (importe <= 0m)
        {
            throw new ErrorDominio(
                "movimiento_de_caja_importe_invalido", "El importe tiene que ser mayor a 0.", 400);
        }
    }

    /// <summary>Un único chequeo de longitud (<see cref="LongitudMinimaDeMotivo"/>) para los tres
    /// tipos (design decisión 8) — el código de error sí distingue por tipo, para que la UX
    /// nombre la causa real: <c>apertura_cajon</c> usa <c>motivo_de_apertura_cajon_invalido</c>
    /// (spec: Apertura De Cajón Follows Legacy F12 Parity), <c>retiro</c>/<c>refuerzo</c> usan
    /// <c>movimiento_de_caja_sin_motivo</c> (spec: Motivo Required For Retiro And Refuerzo) tanto
    /// para un motivo ausente como para uno más corto que el mínimo — ambos son la misma falla de
    /// negocio ("no diste una razón válida").</summary>
    public static void ExigirMotivoValido(TipoMovimientoCaja tipo, string? motivo)
    {
        var limpio = motivo?.Trim();
        var esValido = !string.IsNullOrEmpty(limpio) && limpio.Length >= LongitudMinimaDeMotivo;

        if (esValido)
        {
            return;
        }

        if (tipo == TipoMovimientoCaja.AperturaCajon)
        {
            throw new ErrorDominio(
                "motivo_de_apertura_cajon_invalido",
                $"El motivo de una apertura de cajón requiere al menos {LongitudMinimaDeMotivo} caracteres.", 400);
        }

        throw new ErrorDominio(
            "movimiento_de_caja_sin_motivo", "Este movimiento requiere un motivo.", 400);
    }
}
