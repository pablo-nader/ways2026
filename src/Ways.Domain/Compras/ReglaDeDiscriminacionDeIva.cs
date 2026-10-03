using Ways.Domain.Common;

namespace Ways.Domain.Compras;

/// <summary>
/// Decide si un comprobante de compra discrimina IVA. Un tipo que registra libro IVA es una factura
/// y su letra fija la respuesta (A discrimina, B y C no): pedir lo contrario es un error, no algo
/// que se corrige en silencio. Un tipo que no lo registra (remito, comprobante no fiscal) deja la
/// decisión a quien carga el documento, porque hay proveedores informales que igual separan el IVA.
/// </summary>
public static class ReglaDeDiscriminacionDeIva
{
    public static bool Resolver(bool registraLibroIva, bool discriminaIvaDelTipo, bool? solicitado)
    {
        if (!registraLibroIva)
        {
            return solicitado ?? discriminaIvaDelTipo;
        }

        if (solicitado is { } pedido && pedido != discriminaIvaDelTipo)
        {
            throw new ErrorDominio(
                "discrimina_iva_incompatible",
                discriminaIvaDelTipo
                    ? "Este tipo de comprobante siempre discrimina IVA."
                    : "Este tipo de comprobante nunca discrimina IVA.",
                400);
        }

        return discriminaIvaDelTipo;
    }
}
