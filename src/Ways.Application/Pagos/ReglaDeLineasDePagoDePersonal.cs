using System.Globalization;
using Ways.Domain.Common;
using Ways.Domain.Pagos;

namespace Ways.Application.Pagos;

/// <summary>Regla pura de la corrección de las líneas de un pago de personal: el total no cambia, porque el asiento del
/// personal y el descuento de cuenta corriente quedan como están.</summary>
public static class ReglaDeLineasDePagoDePersonal
{
    public static void ExigirMismoTotal(PlanDeLineasDePago plan, decimal importeDelPago)
    {
        if (plan.Total != importeDelPago)
        {
            throw new ErrorDominio(
                "importe_distinto_del_pago",
                $"Las líneas tienen que sumar {Formato(importeDelPago)}, el importe del pago; suman {Formato(plan.Total)}.",
                400);
        }
    }

    private static string Formato(decimal importe) => importe.ToString("0.00", CultureInfo.InvariantCulture);
}
