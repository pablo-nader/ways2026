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

/// <summary>El desglose de una alícuota: <see cref="Neto"/> es la suma de los totales de línea con
/// esa alícuota e <see cref="Iva"/> el importe que se guarda (el calculado, o el impreso por el
/// proveedor cuando se lo informó). Exento y no gravado salen con IVA cero.</summary>
public sealed record AlicuotaCalculada(int IdAlicuotaIva, decimal Porcentaje, decimal Neto, decimal Iva);

/// <summary>Resultado completo de <see cref="CalculadorDeCompra.Calcular"/>. <see cref="Alicuotas"/>
/// queda vacío cuando el comprobante no discrimina IVA; <see cref="IvaTotal"/> es entonces
/// <c>null</c> y, si no, la suma de <see cref="AlicuotaCalculada.Iva"/>.</summary>
public sealed record CompraCalculada(
    decimal Subtotal, decimal DescuentoTotal, decimal? IvaTotal, decimal Total,
    IReadOnlyList<ItemCalculado> Items, IReadOnlyList<AlicuotaCalculada> Alicuotas);

/// <summary>
/// Aritmética de una compra (design: Compra Arithmetic) — pura, sin acceso a base de datos, el
/// único lugar donde estas fórmulas existen (mismo listón que <c>CalculadorDeArqueo</c>/
/// <c>CalculadorDeTotales</c>). <see cref="MidpointRounding.AwayFromZero"/> en cada redondeo,
/// nunca el banker's rounding default de .NET — mismo criterio POS que el resto del proyecto.
/// </summary>
public static class CalculadorDeCompra
{
    /// <summary>Diferencia máxima, en pesos, entre el IVA calculado y el impreso por el proveedor
    /// para una alícuota: absorbe el redondeo por línea contra el redondeo por alícuota sin dejar
    /// pasar un importe que ya no es una diferencia de centavos.</summary>
    public const decimal ToleranciaDeIvaImpreso = 1.00m;

    /// <summary>
    /// <paramref name="discriminaIva"/> viene del comprobante (<c>comprobantes_compra.
    /// discrimina_iva</c>), no del tipo; <paramref name="margenes"/> alimenta al
    /// <see cref="SugeridorDePrecio"/> existente, que devuelve <c>null</c> cuando no hay margen
    /// configurado para ese artículo. <paramref name="ivaImpreso"/> es el IVA que el proveedor
    /// imprimió por alícuota (id de alícuota → importe): se acepta si difiere del calculado en
    /// hasta <see cref="ToleranciaDeIvaImpreso"/> y se rechaza con 400 si el comprobante no
    /// discrimina IVA, si nombra una alícuota que no está en las líneas o si se pasa de la
    /// tolerancia — nunca se acepta y se descarta.
    /// </summary>
    public static CompraCalculada Calcular(
        IReadOnlyList<LineaDeCompra> lineas, bool discriminaIva,
        IReadOnlyDictionary<int, (decimal? MargenGrupo, decimal? MargenProveedor)> margenes,
        IReadOnlyDictionary<int, decimal>? ivaImpreso = null)
    {
        if (!discriminaIva && ivaImpreso is { Count: > 0 })
        {
            throw new ErrorDominio(
                "iva_impreso_sin_discriminar",
                "El IVA impreso solo puede informarse en un comprobante que discrimina IVA.",
                400);
        }

        var items = new List<ItemCalculado>(lineas.Count);
        var subtotal = 0m;
        var descuentoTotal = 0m;
        var netoPorAlicuota = new SortedDictionary<int, (decimal Porcentaje, decimal Neto)>();

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
                netoPorAlicuota[linea.IdAlicuotaIva] = netoPorAlicuota.TryGetValue(linea.IdAlicuotaIva, out var acumulado)
                    ? (acumulado.Porcentaje, acumulado.Neto + total)
                    : (linea.PorcentajeIva, total);
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

        var alicuotas = ArmarAlicuotas(netoPorAlicuota, ivaImpreso);
        var ivaTotal = discriminaIva ? alicuotas.Sum(a => a.Iva) : (decimal?)null;
        var totalComprobante = subtotal - descuentoTotal + (ivaTotal ?? 0m);

        return new CompraCalculada(subtotal, descuentoTotal, ivaTotal, totalComprobante, items, alicuotas);
    }

    /// <summary>IVA por alícuota sobre el neto de cada una, redondeado una sola vez — la convención
    /// de una factura —, y después el override del impreso con su tolerancia.</summary>
    private static List<AlicuotaCalculada> ArmarAlicuotas(
        SortedDictionary<int, (decimal Porcentaje, decimal Neto)> netoPorAlicuota,
        IReadOnlyDictionary<int, decimal>? ivaImpreso)
    {
        foreach (var idImpreso in ivaImpreso?.Keys ?? Enumerable.Empty<int>())
        {
            if (!netoPorAlicuota.ContainsKey(idImpreso))
            {
                throw new ErrorDominio(
                    "iva_impreso_alicuota_desconocida",
                    $"La alícuota {idImpreso} no figura en las líneas del comprobante.",
                    400);
            }
        }

        var alicuotas = new List<AlicuotaCalculada>(netoPorAlicuota.Count);

        foreach (var (idAlicuota, (porcentaje, neto)) in netoPorAlicuota)
        {
            var calculado = Redondear(neto * porcentaje / 100m, 2);
            var iva = calculado;

            if (ivaImpreso is not null && ivaImpreso.TryGetValue(idAlicuota, out var impreso))
            {
                if (porcentaje == 0m && impreso != 0m)
                {
                    throw new ErrorDominio(
                        "iva_impreso_en_alicuota_sin_iva",
                        $"La alícuota {idAlicuota} es de 0% (exento, no gravado o 0%): su IVA solo puede ser 0.",
                        400);
                }

                if (impreso < 0m || Math.Abs(impreso - calculado) > ToleranciaDeIvaImpreso)
                {
                    throw new ErrorDominio(
                        "iva_impreso_fuera_de_tolerancia",
                        $"El IVA impreso ({impreso:0.00}) difiere del calculado ({calculado:0.00}) en más de " +
                        $"{ToleranciaDeIvaImpreso:0.00}.",
                        400);
                }

                iva = Redondear(impreso, 2);
            }

            alicuotas.Add(new AlicuotaCalculada(idAlicuota, porcentaje, neto, iva));
        }

        return alicuotas;
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
