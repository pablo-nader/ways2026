using Ways.Domain.Common;
using Ways.Domain.Precios;

namespace Ways.Domain.Compras;

/// <summary>
/// Una línea de compra tal como llega del request (design: Interfaces/Contracts) — <see
/// cref="Unidades"/>/<see cref="Bultos"/>/<see cref="UnidadesPorBulto"/> son los inputs crudos;
/// <see cref="CalculadorDeCompra.Calcular"/> deriva <c>cantidad</c> a partir de ellos (design
/// decisión 3: ningún endpoint acepta <c>cantidad</c> directamente). Con <see cref="IdArticulo"/>
/// nulo la línea es un concepto: un importe libre con descripción, sin stock, costo ni lote.
/// </summary>
public sealed record LineaDeCompra(
    int Orden, int? IdArticulo, string Descripcion,
    decimal Unidades, decimal? Bultos, decimal? UnidadesPorBulto,
    decimal CostoUnitario, decimal Descuento,
    int IdAlicuotaIva, decimal PorcentajeIva, bool ActualizaCosto);

/// <summary>Una línea ya calculada (design: Compra Arithmetic) — <see cref="CostoEfectivo"/> es
/// lo que <c>ServicioDeCompras.ConfirmarAsync</c> escribe en <c>articulos.costo_nominal</c>
/// (design decisión 4); <see cref="PrecioSugerido"/> es la sugerencia vía <see
/// cref="SugeridorDePrecio"/>, nunca aplicada por el cálculo en sí.</summary>
public sealed record ItemCalculado(
    int Orden, int? IdArticulo, decimal Cantidad, decimal Total,
    decimal CostoEfectivo, decimal? PrecioSugerido);

/// <summary>Resultado completo de <see cref="CalculadorDeCompra.Calcular"/>.</summary>
public sealed record CompraCalculada(
    decimal Subtotal, decimal DescuentoTotal, decimal? IvaTotal, decimal Total,
    IReadOnlyList<ItemCalculado> Items);

/// <summary>
/// Aritmética de una compra (design: Compra Arithmetic) — pura, sin acceso a base de datos, el
/// único lugar donde estas fórmulas existen (mismo listón que <c>CalculadorDeArqueo</c>/
/// <c>CalculadorDeTotales</c>). <see cref="MidpointRounding.AwayFromZero"/> en cada redondeo,
/// nunca el banker's rounding default de .NET — mismo criterio POS que el resto del proyecto.
/// </summary>
public static class CalculadorDeCompra
{
    /// <summary>
    /// <paramref name="discriminaIva"/> viene de <c>tipos_comprobante.discrimina_iva</c> del tipo
    /// de la compra; <paramref name="margenes"/> alimenta al <see cref="SugeridorDePrecio"/>
    /// existente, que devuelve <c>null</c> cuando no hay margen configurado para ese artículo.
    /// </summary>
    public static CompraCalculada Calcular(
        IReadOnlyList<LineaDeCompra> lineas, bool discriminaIva,
        IReadOnlyDictionary<int, (decimal? MargenGrupo, decimal? MargenProveedor)> margenes)
    {
        var items = new List<ItemCalculado>(lineas.Count);
        var subtotal = 0m;
        var descuentoTotal = 0m;
        var ivaTotal = discriminaIva ? 0m : (decimal?)null;

        foreach (var linea in lineas)
        {
            if (linea.IdArticulo is null)
            {
                ValidarLineaDeConcepto(linea);
            }

            var cantidad = Redondear(linea.Unidades + (linea.Bultos ?? 0m) * (linea.UnidadesPorBulto ?? 0m), 3);

            if (cantidad <= 0m)
            {
                throw new ErrorDominio(
                    "cantidad_de_item_invalida", "La cantidad de un ítem de compra tiene que ser positiva.", 400);
            }

            if (linea.CostoUnitario < 0m)
            {
                throw new ErrorDominio(
                    "costo_de_item_invalido", "El costo unitario de un ítem de compra no puede ser negativo.", 400);
            }

            if (linea.Descuento < 0m)
            {
                throw new ErrorDominio(
                    "importes_de_item_invalidos", "El descuento de un ítem de compra no puede ser negativo.", 400);
            }

            var bruto = Redondear(cantidad * linea.CostoUnitario, 2);

            if (linea.Descuento > bruto)
            {
                throw new ErrorDominio(
                    "descuento_de_item_invalido", "El descuento de un ítem no puede superar su importe bruto.", 400);
            }

            var total = bruto - linea.Descuento;

            decimal costoEfectivo;
            if (discriminaIva)
            {
                var ivaDeLinea = Redondear(total * linea.PorcentajeIva / 100m, 2);
                ivaTotal += ivaDeLinea;
                costoEfectivo = Redondear(total * (1 + linea.PorcentajeIva / 100m) / cantidad, 2);
            }
            else
            {
                costoEfectivo = Redondear(total / cantidad, 2);
            }

            decimal? precioSugerido = null;
            if (linea.IdArticulo is { } idArticulo)
            {
                var (margenGrupo, margenProveedor) = margenes.TryGetValue(idArticulo, out var margen)
                    ? margen
                    : (null, null);
                precioSugerido = SugeridorDePrecio.Sugerir(costoEfectivo, null, null, margenGrupo, margenProveedor);
            }

            items.Add(new ItemCalculado(linea.Orden, linea.IdArticulo, cantidad, total, costoEfectivo, precioSugerido));

            subtotal += bruto;
            descuentoTotal += linea.Descuento;
        }

        var total2 = discriminaIva ? subtotal - descuentoTotal + (ivaTotal ?? 0m) : subtotal - descuentoTotal;

        return new CompraCalculada(subtotal, descuentoTotal, ivaTotal, total2, items);
    }

    /// <summary>Un concepto no mueve stock ni toca <c>articulos.costo_nominal</c>, así que no admite
    /// los inputs que solo tienen sentido para un artículo. Se rechaza en vez de ignorarlo: aceptar
    /// y descartar <c>bultos</c> o <c>actualizaCosto</c> mentiría sobre lo que se guardó.</summary>
    private static void ValidarLineaDeConcepto(LineaDeCompra linea)
    {
        if (string.IsNullOrWhiteSpace(linea.Descripcion))
        {
            throw new ErrorDominio(
                "concepto_sin_descripcion", "Una línea por concepto necesita una descripción.", 400);
        }

        if (linea.Bultos is not null || linea.UnidadesPorBulto is not null)
        {
            throw new ErrorDominio(
                "concepto_con_bultos", "Una línea por concepto no admite bultos ni unidades por bulto.", 400);
        }

        if (linea.ActualizaCosto)
        {
            throw new ErrorDominio(
                "concepto_actualiza_costo", "Una línea por concepto no puede actualizar el costo de un artículo.", 400);
        }
    }

    /// <summary>Deriva <c>costoEfectivo</c> directo de los valores YA persistidos de un item
    /// (<c>total</c>/<c>cantidad</c>/<c>porcentaje_iva</c>) — usado por
    /// <c>ServicioDeCompras.ConfirmarAsync</c>, que no vuelve a pasar por <see cref="Calcular"/>
    /// (evita re-derivar <c>cantidad</c> desde <c>unidades</c>/<c>bultos</c> una segunda vez).
    /// Misma fórmula que <see cref="Calcular"/> (design: Compra Arithmetic), aplicada al dato ya
    /// congelado en la fila.</summary>
    public static decimal CalcularCostoEfectivoDesdeItem(decimal total, decimal cantidad, decimal porcentajeIva, bool discriminaIva) =>
        discriminaIva
            ? Redondear(total * (1 + porcentajeIva / 100m) / cantidad, 2)
            : Redondear(total / cantidad, 2);

    /// <summary>Design: Compra Arithmetic — "dos líneas del mismo artículo... el costo_nominal se
    /// deduplica en memoria con el mayor orden ganando, así que se emite exactamente un UPDATE
    /// por artículo". Filtra por <c>actualizaCosto AND costoUnitario &gt; 0</c> (design decisión
    /// 4, el guard anti-bonificación) antes de dedupear. Un concepto (sin artículo) nunca entra.</summary>
    public static IReadOnlyDictionary<int, decimal> ResolverActualizacionesDeCosto(
        IReadOnlyList<(int Orden, int? IdArticulo, bool ActualizaCosto, decimal CostoUnitario, decimal CostoEfectivo)> items)
    {
        var ganador = new Dictionary<int, (int Orden, decimal Costo)>();

        foreach (var item in items)
        {
            if (item.IdArticulo is not { } idArticulo || !item.ActualizaCosto || item.CostoUnitario <= 0m)
            {
                continue;
            }

            if (!ganador.TryGetValue(idArticulo, out var actual) || item.Orden > actual.Orden)
            {
                ganador[idArticulo] = (item.Orden, item.CostoEfectivo);
            }
        }

        return ganador.ToDictionary(kv => kv.Key, kv => kv.Value.Costo);
    }

    private static decimal Redondear(decimal valor, int decimales) =>
        Math.Round(valor, decimales, MidpointRounding.AwayFromZero);
}
