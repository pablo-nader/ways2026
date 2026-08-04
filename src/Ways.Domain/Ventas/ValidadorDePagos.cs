using Ways.Domain.Catalogos;
using Ways.Domain.Common;

namespace Ways.Domain.Ventas;

/// <summary>
/// Un pago a validar, ya resuelto contra su <see cref="Domain.Catalogos.MedioPago"/> (design:
/// Checkout Contract) — <see cref="ValidadorDePagos"/> no consulta nada, recibe todo lo que
/// necesita ya cargado.
/// </summary>
public readonly record struct PagoAValidar(
    int IdMedioPago,
    ComportamientoMedioPago Comportamiento,
    bool AdmiteVuelto,
    bool RequiereReferencia,
    decimal Importe,
    decimal Vuelto,
    string? Referencia);

/// <summary>
/// Valida la mezcla de pagos de un checkout (design decisión 5, Checkout Contract): pura,
/// DB-free, mismo criterio que <see cref="Ofertas.ReglaDeOfertas"/>. Implementa el orden de
/// rechazo del legacy B6 (parametrizado — <b>ningún literal <c>10</c>/<c>20</c> acá</b>,
/// <see cref="ToleranciaPago"/>/<see cref="VueltoMaximo"/> siempre llegan como parámetro, nunca
/// hardcodeados) más las dos reglas nuevas del proyecto (referencia obligatoria, vuelto máximo
/// coherente con lo efectivamente pagado).
///
/// El orden es OBSERVABLE (spec: "a payload violating rules 2 and 6 reports 2") — cada regla
/// corta la validación en el primer rechazo, nunca acumula errores.
/// </summary>
public static class ValidadorDePagos
{
    /// <param name="total">Total ya calculado por <see cref="CalculadorDeTotales"/> — con signo
    /// (negativo en NCX).</param>
    /// <param name="pagos">La mezcla de pagos pedida.</param>
    /// <param name="toleranciaPago">Resuelto por <c>ServicioDeParametros</c> (punto de venta >
    /// empresa > default) — nunca un literal.</param>
    /// <param name="vueltoMaximo">Idem <paramref name="toleranciaPago"/>.</param>
    /// <param name="esConsumidorFinal"><c>Cliente.EsConsumidorFinal</c> del comprobante.</param>
    /// <param name="saldoCliente"><c>Cliente.Saldo</c> ANTES de este checkout.</param>
    /// <param name="limiteCredito"><c>Cliente.LimiteCredito</c>.</param>
    /// <param name="creditoIlimitado"><c>Cliente.CreditoIlimitado</c> — si es <c>true</c>, la
    /// regla 6 nunca se evalúa.</param>
    public static void Validar(
        decimal total,
        IReadOnlyList<PagoAValidar> pagos,
        decimal toleranciaPago,
        decimal vueltoMaximo,
        bool esConsumidorFinal,
        decimal saldoCliente,
        decimal limiteCredito,
        bool creditoIlimitado)
    {
        // 0 (nuevo, sin numeración legacy — corta ANTES que cualquier otra regla): un
        // Importe negativo permite manipular Σ importe sin que ninguna regla de abajo lo note
        // (p. ej. {Efectivo, 150}, {CuentaCorriente, -50} sobre un total de 100 pasa la regla 2
        // porque Σ importe da 100, y nunca dispara las reglas 5/6 porque
        // <c>consumoCuentaCorriente > 0m</c> es falso con un consumo negativo) — un Importe
        // negativo no tiene significado de negocio para un pago, se rechaza de plano.
        foreach (var pago in pagos)
        {
            if (pago.Importe < 0m)
            {
                throw new ErrorDominio(
                    "pago_importe_negativo", "El importe de un pago no puede ser negativo.", 400);
            }
        }

        var sumaImportes = pagos.Sum(p => p.Importe);
        var sumaVueltos = pagos.Sum(p => p.Vuelto);

        // 1: todos los medios en 0 y total > 0.
        if (sumaImportes == 0m && total > 0m)
        {
            throw new ErrorDominio("pago_no_ingresado", "Tenés que ingresar al menos un pago.", 400);
        }

        // 2: Σ importe + tolerancia < total.
        if (sumaImportes + toleranciaPago < total)
        {
            throw new ErrorDominio(
                "tolerancia_de_pago_superada", "El pago ingresado no cubre el total, ni siquiera con la tolerancia.", 400);
        }

        // 3: Σ vuelto > vuelto_maximo.
        if (sumaVueltos > vueltoMaximo)
        {
            throw new ErrorDominio("vuelto_excedido", "El vuelto supera el máximo permitido.", 400);
        }

        // 4: vuelto sobre un medio con AdmiteVuelto = false (generaliza "tarjetas" y "cuenta
        // corriente" del legacy).
        foreach (var pago in pagos)
        {
            if (pago.Vuelto > 0m && !pago.AdmiteVuelto)
            {
                throw new ErrorDominio(
                    "medio_no_admite_vuelto", "El medio de pago elegido no admite vuelto.", 400);
            }
        }

        // La regla 0 ya garantizó Importe >= 0 en cada pago, así que "> 0m" abajo distingue sin
        // ambigüedad "hubo consumo de cuenta corriente" de "no lo hubo" — sin la regla 0, un
        // Importe negativo en un pago de CuentaCorriente podía compensar uno positivo y esconder
        // el consumo real detrás de las reglas 5/6.
        var consumoCuentaCorriente = pagos
            .Where(p => p.Comportamiento == ComportamientoMedioPago.CuentaCorriente)
            .Sum(p => p.Importe);

        // 5 (sin numeración legacy — regla nueva insertada en esta posición, design: Checkout
        // Contract): cuenta corriente con Consumidor Final, sin importar el límite.
        if (consumoCuentaCorriente > 0m && esConsumidorFinal)
        {
            throw new ErrorDominio(
                "cuenta_corriente_no_permitida", "El Consumidor Final no puede pagar con cuenta corriente.", 400);
        }

        // 6: saldo + consumo > limite_credito, salvo credito_ilimitado.
        if (consumoCuentaCorriente > 0m && !creditoIlimitado
            && saldoCliente + consumoCuentaCorriente > limiteCredito)
        {
            throw new ErrorDominio("limite_credito_excedido", "El pago supera el límite de crédito del cliente.", 400);
        }

        // 7 (nuevo): RequiereReferencia sin referencia.
        foreach (var pago in pagos)
        {
            if (pago.RequiereReferencia && string.IsNullOrWhiteSpace(pago.Referencia))
            {
                throw new ErrorDominio(
                    "referencia_de_pago_requerida", "Este medio de pago requiere una referencia.", 400);
            }
        }

        // 8 (nuevo): Σ vuelto > max(0, Σ importe − total) — el vuelto no puede superar lo que
        // efectivamente sobra del pago sobre el total, más allá de que ya haya pasado las reglas
        // 3/4 individualmente.
        var vueltoMaximoCoherente = Math.Max(0m, sumaImportes - total);
        if (sumaVueltos > vueltoMaximoCoherente)
        {
            throw new ErrorDominio("vuelto_invalido", "El vuelto no coincide con lo que sobra del pago.", 400);
        }
    }
}
