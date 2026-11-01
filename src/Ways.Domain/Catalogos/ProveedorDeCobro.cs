using Ways.Domain.Common;

namespace Ways.Domain.Catalogos;

/// <summary>
/// Proveedor que cobra por un medio de pago: elegirlo en el POS inicia un cobro con él en vez de pedir una referencia. Hoy
/// solo Mercado Pago, con QR dinámico o con terminal Point. Solo un medio <see cref="ComportamientoMedioPago.Electronico"/>
/// puede tenerlo (<c>ck_medios_pago_proveedor_solo_electronico</c> es el respaldo).
/// </summary>
public static class ProveedorDeCobro
{
    public const string MercadoPagoQr = "mercadopago_qr";

    public const string MercadoPagoPoint = "mercadopago_point";

    public const int LargoMaximo = 30;

    public static bool EsConocido(string proveedor) => proveedor is MercadoPagoQr or MercadoPagoPoint;

    /// <summary>
    /// Resuelve el proveedor que queda guardado. Un valor enviado reemplaza al guardado (null o en blanco lo quita); si no vino se
    /// conserva, salvo que el medio deje de ser electrónico, caso en que se limpia en vez de fallar (mismo criterio que la
    /// cuenta de tesorería). Un valor enviado se valida antes que el comportamiento.
    /// </summary>
    public static string? Resolver(
        ComportamientoMedioPago comportamiento, string? guardado, bool enviado, string? valorEnviado)
    {
        string? resuelto;
        if (enviado)
        {
            resuelto = string.IsNullOrWhiteSpace(valorEnviado) ? null : valorEnviado.Trim();
            if (resuelto is not null && !EsConocido(resuelto))
            {
                throw new ErrorDominio(
                    "proveedor_cobro_invalido",
                    $"El proveedor de cobro tiene que ser {MercadoPagoQr} o {MercadoPagoPoint}.",
                    400);
            }
        }
        else
        {
            resuelto = guardado;
        }

        if (resuelto is null || comportamiento == ComportamientoMedioPago.Electronico)
        {
            return resuelto;
        }

        if (enviado)
        {
            throw new ErrorDominio(
                "proveedor_solo_electronico",
                "El proveedor de cobro solo se admite en medios de pago electrónicos.",
                400);
        }

        return null;
    }
}
