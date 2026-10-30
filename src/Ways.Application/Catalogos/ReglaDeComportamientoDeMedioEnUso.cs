using Ways.Domain.Catalogos;
using Ways.Domain.Common;

namespace Ways.Application.Catalogos;

/// <summary>
/// La parte en cuenta corriente de una venta, y con ella su saldo pendiente y los topes de <c>aplicaciones_cobro</c>, se
/// calcula con el comportamiento ACTUAL de los medios de sus cobros. Un medio que ya figura en algún cobro no puede entrar
/// ni salir de cuenta corriente: si lo hiciera, la parte en cuenta corriente de ventas ya cobradas cambiaría, una
/// aplicación viva podría superarla y anular el recibo o la venta fallaría al comitear. Los cambios entre efectivo y
/// electrónico no tocan esa parte y siguen permitidos.
/// </summary>
public static class ReglaDeComportamientoDeMedioEnUso
{
    public const string Codigo = "medio_pago_comportamiento_en_uso";

    /// <summary>El cambio hace que el medio entre o salga de cuenta corriente.</summary>
    public static bool CambiaLaCuentaCorriente(ComportamientoMedioPago anterior, ComportamientoMedioPago nuevo) =>
        (anterior == ComportamientoMedioPago.CuentaCorriente) != (nuevo == ComportamientoMedioPago.CuentaCorriente);

    public static void Exigir(ComportamientoMedioPago anterior, ComportamientoMedioPago nuevo, bool figuraEnCobros)
    {
        if (figuraEnCobros && CambiaLaCuentaCorriente(anterior, nuevo))
        {
            throw ErrorDominio.Conflicto(
                Codigo,
                "El medio de pago ya se usó en cobros: no puede pasar a cuenta corriente ni dejar de serlo. " +
                "Creá un medio nuevo con el comportamiento que necesitás.");
        }
    }
}
