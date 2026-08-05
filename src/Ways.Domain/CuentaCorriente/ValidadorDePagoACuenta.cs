using Ways.Domain.Catalogos;
using Ways.Domain.Common;
using Ways.Domain.Ventas;

namespace Ways.Domain.CuentaCorriente;

/// <summary>
/// Valida la mezcla de pagos de una RC (design decisión 6, pinned: "sibling class", no una rama
/// de <see cref="ValidadorDePagos"/>): pura, DB-free, mismo criterio que
/// <see cref="ValidadorDePagos"/> — pero un pago a cuenta no es un checkout. De las 9 reglas de
/// <see cref="ValidadorDePagos"/>, tres son inaplicables acá: la 2 (<c>tolerancia_pago</c>) le
/// permitiría a la RC acreditar más deuda de la que efectivamente ingresó (el legacy nunca
/// tolera un pago recibido de menos); la 5/6 (CF-bloquea-CC / límite de crédito) hablan de
/// CONSUMIR cuenta corriente, no de pagarla; la 8 (vuelto coherente contra un <c>total</c> fijo)
/// es vacía acá porque no hay <c>total</c> independiente — <c>importeAplicado</c> se DERIVA de
/// <c>Σ importe − Σ vuelto</c>, así que esa desigualdad se cumple siempre por construcción.
///
/// El orden es OBSERVABLE, mismo contrato que <see cref="ValidadorDePagos"/>: cada regla corta la
/// validación en el primer rechazo, nunca acumula errores.
/// </summary>
public static class ValidadorDePagoACuenta
{
    /// <param name="pagos">La mezcla de pagos pedida — ya resuelta contra su
    /// <see cref="Catalogos.MedioPago"/> (mismo shape que <see cref="ValidadorDePagos.Validar"/>).</param>
    /// <param name="vueltoMaximo">Resuelto por <c>ServicioDeParametros</c> — nunca un literal.</param>
    /// <returns><c>importeAplicado = Σ importe − Σ vuelto</c> (legacy parity,
    /// <c>cuenta-corriente.php:11</c>) — la RC no tiene ningún campo de importe propio, este es el
    /// único lugar donde ese número existe.</returns>
    public static decimal Validar(IReadOnlyList<PagoAValidar> pagos, decimal vueltoMaximo)
    {
        // 1: Importe negativo — mismo motivo que la regla 0 de ValidadorDePagos, corta ANTES que
        // cualquier otra regla: sin esto, un Importe negativo podría manipular Σ importe sin que
        // ninguna regla de abajo lo note.
        foreach (var pago in pagos)
        {
            if (pago.Importe < 0m)
            {
                throw new ErrorDominio(
                    "pago_importe_negativo", "El importe de un pago no puede ser negativo.", 400);
            }
        }

        // 2: Vuelto negativo — mismo motivo que la regla 0b de ValidadorDePagos.
        foreach (var pago in pagos)
        {
            if (pago.Vuelto < 0m)
            {
                throw new ErrorDominio(
                    "vuelto_negativo", "El vuelto de un pago no puede ser negativo.", 400);
            }
        }

        // 3 (regla nueva, sin equivalente en ValidadorDePagos — spec: RC Forbids Cuenta
        // Corriente Medios): una deuda no puede pagar otra deuda. A diferencia de la regla 5 de
        // ValidadorDePagos (que solo bloquea CC para Consumidor Final), acá CC está prohibido
        // sin importar el cliente.
        foreach (var pago in pagos)
        {
            if (pago.Comportamiento == ComportamientoMedioPago.CuentaCorriente)
            {
                throw new ErrorDominio(
                    "pago_a_cuenta_sin_medios_fisicos",
                    "Un pago a cuenta no admite cuenta corriente como medio de pago.",
                    400);
            }
        }

        // 4: vuelto sobre un medio con AdmiteVuelto = false — mismo criterio que la regla 4 de
        // ValidadorDePagos.
        foreach (var pago in pagos)
        {
            if (pago.Vuelto > 0m && !pago.AdmiteVuelto)
            {
                throw new ErrorDominio(
                    "medio_no_admite_vuelto", "El medio de pago elegido no admite vuelto.", 400);
            }
        }

        // 5: Σ vuelto > vuelto_maximo — mismo criterio que la regla 3 de ValidadorDePagos,
        // parametrizado (nunca un literal).
        var sumaVueltos = pagos.Sum(p => p.Vuelto);
        if (sumaVueltos > vueltoMaximo)
        {
            throw new ErrorDominio("vuelto_excedido", "El vuelto supera el máximo permitido.", 400);
        }

        // 6: RequiereReferencia sin referencia — mismo criterio que la regla 7 de ValidadorDePagos.
        foreach (var pago in pagos)
        {
            if (pago.RequiereReferencia && string.IsNullOrWhiteSpace(pago.Referencia))
            {
                throw new ErrorDominio(
                    "referencia_de_pago_requerida", "Este medio de pago requiere una referencia.", 400);
            }
        }

        // 7 (regla nueva, sin equivalente en ValidadorDePagos): importeAplicado <= 0 — un pago a
        // cuenta sin ningún importe efectivo (pagos vacíos, o Σ importe == Σ vuelto) no tiene
        // sentido de negocio. Se evalúa último a propósito: es el único chequeo que depende del
        // valor derivado, después de que las reglas 1/2 ya garantizaron que ninguno de los dos
        // sumandos es negativo.
        var sumaImportes = pagos.Sum(p => p.Importe);
        var importeAplicado = sumaImportes - sumaVueltos;
        if (importeAplicado <= 0m)
        {
            throw new ErrorDominio(
                "pago_a_cuenta_sin_importe", "Tenés que ingresar al menos un pago a cuenta.", 400);
        }

        return importeAplicado;
    }
}
