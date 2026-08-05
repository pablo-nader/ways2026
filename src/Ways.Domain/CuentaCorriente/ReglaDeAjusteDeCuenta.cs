using Ways.Domain.Common;

namespace Ways.Domain.CuentaCorriente;

/// <summary>
/// Regla pura del ajuste manual (design decisión 8, pinned: "no necesita esquema, ni CHECK";
/// tasks.md task 4.1) — orden de validación IGUAL al de design: Transactions, AJUSTE MANUAL
/// ("fuera: ReglaDeAjusteDeCuenta … ; cliente ; punto de venta"), corre ANTES de tocar la base.
/// <see cref="Validar"/> no distingue signo: <c>importe</c> positivo aumenta la deuda, negativo la
/// reduce (spec: Ajuste Requires A Detalle — "importe MAY be positive or negative").
/// </summary>
public static class ReglaDeAjusteDeCuenta
{
    /// <summary>Design decisión 8: <c>length(btrim(detalle)) &gt;= 5</c> — mismo umbral que
    /// <c>ck_movimientos_caja_motivo_minimo</c> (stage-6), acá vive en Domain porque no hay CHECK
    /// (stage-5 ya escribe filas <c>tipo = ajuste</c> con <c>detalle</c> NULL para el
    /// contramovimiento de anulación, así que una CHECK de esquema rompería datos existentes).</summary>
    public const int LongitudMinimaDetalle = 5;

    public static void Validar(decimal importe, string? detalle)
    {
        if (importe == 0m)
        {
            throw new ErrorDominio("ajuste_importe_invalido", "El importe del ajuste no puede ser cero.", 400);
        }

        var detalleNormalizado = detalle?.Trim();
        if (string.IsNullOrEmpty(detalleNormalizado) || detalleNormalizado.Length < LongitudMinimaDetalle)
        {
            throw new ErrorDominio(
                "ajuste_detalle_requerido",
                $"El detalle del ajuste es obligatorio y tiene que tener al menos {LongitudMinimaDetalle} caracteres.",
                400);
        }
    }
}
