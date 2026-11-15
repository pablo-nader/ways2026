using Ways.Domain.Common;

namespace Ways.Domain.Compras;

/// <summary>La factura a la que una nota de crédito de compra dice corregir, tal como la ve quien la valida:
/// el estado y el proveedor del comprobante, el signo y si registra libro IVA de su tipo, y la empresa de su punto
/// de venta.</summary>
public sealed record FacturaAsociada(
    EstadoCompra Estado, int IdProveedor, short SignoDelTipo, bool RegistraLibroIva, int IdEmpresa);

/// <summary>
/// Reglas de una nota de crédito de compra (tipo con signo negativo). Se carga con cantidades e importes positivos,
/// igual que una factura: el signo lo aplican los consumidores (stock, cuenta corriente, libro IVA, reportes). Es una
/// regla pura: el servicio la aplica con lo leído de la base, primero al guardar el borrador y otra vez bajo lock al
/// confirmar.
/// </summary>
public static class ReglaDeNotaDeCredito
{
    public static bool EsNotaDeCredito(short signoDelTipo) => signoDelTipo < 0;

    /// <summary>Lo que el tipo del comprobante exige del resto del pedido. Una nota de crédito no se liga a una orden
    /// de compra (no recibe mercadería) y no actualiza costos: pedirlo es 400, nunca se descarta en silencio. La
    /// asociación a una factura solo existe en una nota de crédito.</summary>
    public static void ExigirCoherenciaConElTipo(
        short signoDelTipo, int? idComprobanteAsociado, int? idOrdenCompra, IEnumerable<bool?> actualizaCostoPorLinea)
    {
        if (!EsNotaDeCredito(signoDelTipo))
        {
            if (idComprobanteAsociado is not null)
            {
                throw new ErrorDominio(
                    "asociacion_solo_en_nota_de_credito",
                    "Solo una nota de crédito puede asociarse a una factura.", 400);
            }

            return;
        }

        if (idOrdenCompra is not null)
        {
            throw new ErrorDominio(
                "nota_de_credito_sin_orden_de_compra",
                "Una nota de crédito no puede ligarse a una orden de compra.", 400);
        }

        if (actualizaCostoPorLinea.Any(a => a == true))
        {
            throw new ErrorDominio(
                "nota_de_credito_no_actualiza_costo",
                "Una nota de crédito no actualiza el costo de los artículos.", 400);
        }
    }

    /// <summary>La factura asociada tiene que existir, ser una factura (un tipo con signo positivo que registra libro
    /// IVA: ni un remito ni otra nota de crédito), estar confirmada y ser del mismo proveedor y de la misma empresa que
    /// la nota. Cada rechazo es 400 con su propio código.</summary>
    public static void ExigirFacturaAsociable(
        FacturaAsociada? asociada, int idAsociada, int idProveedorDeLaNota, int idEmpresaDeLaNota)
    {
        if (asociada is null)
        {
            throw new ErrorDominio("referencia_invalida", $"No existe la compra asociada {idAsociada}.", 400);
        }

        if (EsNotaDeCredito(asociada.SignoDelTipo) || !asociada.RegistraLibroIva)
        {
            throw new ErrorDominio(
                "compra_asociada_no_es_factura",
                "La compra asociada tiene que ser una factura: ni un remito ni otra nota de crédito.", 400);
        }

        if (asociada.Estado != EstadoCompra.Confirmada)
        {
            throw new ErrorDominio(
                "compra_asociada_no_confirmada", "La factura asociada tiene que estar confirmada.", 400);
        }

        if (asociada.IdProveedor != idProveedorDeLaNota)
        {
            throw new ErrorDominio(
                "compra_asociada_de_otro_proveedor",
                "La factura asociada es de otro proveedor.", 400);
        }

        if (asociada.IdEmpresa != idEmpresaDeLaNota)
        {
            throw new ErrorDominio(
                "compra_asociada_de_otra_empresa",
                "La factura asociada pertenece a otra empresa.", 400);
        }
    }
}
