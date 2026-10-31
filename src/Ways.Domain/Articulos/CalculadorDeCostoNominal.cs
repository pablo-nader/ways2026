namespace Ways.Domain.Articulos;

/// <summary>
/// Costo nominal de un artículo a partir de lo que se cargó (doc 10 §3): costo de lista por unidad o por
/// bulto, con o sin IVA, menos el descuento del proveedor. Regla pura, sin base de datos.
/// </summary>
public static class CalculadorDeCostoNominal
{
    /// <summary><c>true</c> si el costo se cargó por bulto y no hay unidades por bulto mayores que cero: el
    /// cálculo no es posible y quien lo invoca debe rechazar el pedido antes.</summary>
    public static bool BultoSinUnidades(CostoListaPor por, decimal? unidadesPorBulto) =>
        por == CostoListaPor.Bulto && unidadesPorBulto is not > 0m;

    /// <summary>Lo que entra en el cálculo, tal como está guardado o tal como llega en un pedido.</summary>
    public readonly record struct Entradas(
        decimal? CostoLista,
        CostoListaPor Por,
        bool IncluyeIva,
        decimal? UnidadesPorBulto,
        decimal PorcentajeIva,
        decimal? DescuentoProveedor,
        decimal ImpuestoInterno = 0m);

    /// <summary><c>true</c> si alguna entrada que cuenta para el cálculo cambió: el costo de lista, su base, su IVA y
    /// el descuento siempre; las unidades por bulto solo mientras la base es bulto, antes o después; el porcentaje de la
    /// alícuota y el importe de impuestos internos solo mientras el costo se guarda sin IVA, antes o después. Si ninguna cambió, el costo nominal guardado
    /// se conserva: puede ser el efectivo que dejó una compra.</summary>
    public static bool CambiaAlgunaEntrada(Entradas anterior, Entradas nueva) =>
        anterior.CostoLista != nueva.CostoLista
        || anterior.Por != nueva.Por
        || anterior.IncluyeIva != nueva.IncluyeIva
        || anterior.DescuentoProveedor != nueva.DescuentoProveedor
        || (anterior.UnidadesPorBulto != nueva.UnidadesPorBulto
            && (anterior.Por == CostoListaPor.Bulto || nueva.Por == CostoListaPor.Bulto))
        || ((anterior.PorcentajeIva != nueva.PorcentajeIva
                || anterior.ImpuestoInterno != nueva.ImpuestoInterno)
            && (!anterior.IncluyeIva || !nueva.IncluyeIva));

    /// <summary>Costo por unidad, con IVA e impuestos internos incluidos y neto del descuento, redondeado a 2 decimales
    /// (mitad lejos de cero). Un costo de lista sin IVA suma al neto el IVA y el impuesto interno (<paramref name="impuestoInterno"/>,
    /// un importe sin IVA en la misma base que el costo de lista, que el IVA no grava); uno con IVA ya lo trae todo y se toma tal cual. El descuento
    /// del proveedor se aplica sobre ese total, igual que antes. <c>null</c> si no hay costo de lista. Con <see cref="CostoListaPor.Bulto"/> exige
    /// <paramref name="unidadesPorBulto"/> mayor que cero (ver <see cref="BultoSinUnidades"/>).</summary>
    public static decimal? Calcular(
        decimal? costoLista,
        CostoListaPor por,
        bool incluyeIva,
        decimal? unidadesPorBulto,
        decimal porcentajeIva,
        decimal? descuentoProveedor,
        decimal impuestoInterno = 0m)
    {
        if (costoLista is not { } lista)
        {
            return null;
        }

        if (BultoSinUnidades(por, unidadesPorBulto))
        {
            throw new ArgumentException(
                "Un costo por bulto necesita unidades por bulto mayores que cero.", nameof(unidadesPorBulto));
        }

        var porUnidad = por == CostoListaPor.Bulto ? lista / unidadesPorBulto!.Value : lista;
        var impuestoPorUnidad = por == CostoListaPor.Bulto ? impuestoInterno / unidadesPorBulto!.Value : impuestoInterno;
        var conIva = incluyeIva ? porUnidad : porUnidad * (1 + porcentajeIva / 100m) + impuestoPorUnidad;
        var neto = conIva * (1 - (descuentoProveedor ?? 0m) / 100m);

        return Math.Round(neto, 2, MidpointRounding.AwayFromZero);
    }
}
