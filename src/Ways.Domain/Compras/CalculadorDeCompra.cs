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

/// <summary>El costo nuevo que una compra confirmada le escribe a un artículo suelto o a toda una familia
/// (<see cref="CalculadorDeCompra.ResolverActualizacionesDeCosto"/>): con <see cref="IdFamilia"/> <c>null</c> es
/// solo para <see cref="IdArticulo"/>; con valor, es para TODOS los miembros vivos de esa familia (doc 10 §3) y
/// <see cref="IdArticulo"/> es el artículo de la línea ganadora.</summary>
public sealed record ActualizacionDeCosto(int? IdFamilia, int IdArticulo, decimal Costo);

/// <summary>El desglose de una alícuota: <see cref="Neto"/> es el neto gravado de esa alícuota e
/// <see cref="Iva"/> el importe que se guarda (el calculado, o el impreso por el proveedor cuando se
/// lo informó). Con precios netos el neto es la suma de los totales de línea; con precios finales
/// es esa suma menos el IVA, de modo que neto + IVA siempre iguala lo tipeado. Exento y no gravado
/// salen con IVA cero.</summary>
public sealed record AlicuotaCalculada(int IdAlicuotaIva, decimal Porcentaje, decimal Neto, decimal Iva);

/// <summary>Resultado completo de <see cref="CalculadorDeCompra.Calcular"/>. <see cref="Alicuotas"/>
/// queda vacío cuando el comprobante no discrimina IVA; <see cref="IvaTotal"/> es entonces
/// <c>null</c> y, si no, la suma de <see cref="AlicuotaCalculada.Iva"/>. <see cref="Total"/> incluye
/// las <see cref="Percepciones"/> tal como las informó el proveedor. Con precios finales,
/// <see cref="Subtotal"/> y <see cref="DescuentoTotal"/> ya traen el IVA y <see cref="Total"/> es
/// <c>subtotal − descuento + percepciones</c>.</summary>
public sealed record CompraCalculada(
    decimal Subtotal, decimal DescuentoTotal, decimal? IvaTotal, decimal Total,
    IReadOnlyList<ItemCalculado> Items, IReadOnlyList<AlicuotaCalculada> Alicuotas,
    IReadOnlyList<PercepcionDeCompra> Percepciones);

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
    ///
    /// Con <paramref name="preciosIncluyenIva"/> el costo tipeado ya trae el IVA: el total de cada
    /// línea es el precio final, el IVA de cada alícuota se extrae del final de esa alícuota
    /// (<c>round(final × p / (100 + p), 2)</c>, una sola vez por alícuota, igual que con precios
    /// netos) y el neto es el resto, así que neto + IVA suma exactamente lo tipeado. Un IVA impreso
    /// mueve el neto en sentido contrario: el total del comprobante es el que se tipeó. Solo existe
    /// en un comprobante que discrimina IVA (uno que no discrimina ya es un precio final).
    /// <paramref name="percepciones"/> ya viene validada por <see cref="ReglaDePercepciones"/> y se
    /// suma al total tal como la imprimió el proveedor.
    /// </summary>
    public static CompraCalculada Calcular(
        IReadOnlyList<LineaDeCompra> lineas, bool discriminaIva,
        IReadOnlyDictionary<int, (decimal? MargenGrupo, decimal? MargenProveedor)> margenes,
        IReadOnlyDictionary<int, decimal>? ivaImpreso = null,
        bool preciosIncluyenIva = false,
        IReadOnlyList<PercepcionDeCompra>? percepciones = null)
    {
        if (!discriminaIva && ivaImpreso is { Count: > 0 })
        {
            throw new ErrorDominio(
                "iva_impreso_sin_discriminar",
                "El IVA impreso solo puede informarse en un comprobante que discrimina IVA.",
                400);
        }

        if (preciosIncluyenIva && !discriminaIva)
        {
            throw new ErrorDominio(
                "precios_incluyen_iva_sin_discriminar",
                "Los precios con IVA incluido solo se informan en un comprobante que discrimina IVA.",
                400);
        }

        var items = new List<ItemCalculado>(lineas.Count);
        var subtotal = 0m;
        var descuentoTotal = 0m;

        // Neto por alícuota con precios netos; precio final por alícuota con IVA incluido. El
        // segundo componente se llama Monto porque su significado depende de la modalidad
        // (ver ArmarAlicuotas).
        var montoPorAlicuota = new SortedDictionary<int, (decimal Porcentaje, decimal Monto)>();

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

            if (discriminaIva)
            {
                montoPorAlicuota[linea.IdAlicuotaIva] = montoPorAlicuota.TryGetValue(linea.IdAlicuotaIva, out var acumulado)
                    ? (acumulado.Porcentaje, acumulado.Monto + total)
                    : (linea.PorcentajeIva, total);
            }

            var costoEfectivo = CalcularCostoEfectivo(total, cantidad, linea.PorcentajeIva, discriminaIva, preciosIncluyenIva);

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

        var alicuotas = ArmarAlicuotas(montoPorAlicuota, ivaImpreso, preciosIncluyenIva);
        var ivaTotal = discriminaIva ? alicuotas.Sum(a => a.Iva) : (decimal?)null;
        var percepcionesDelComprobante = percepciones ?? [];

        // Con precios finales el IVA ya está dentro de subtotal − descuento: sumarlo de nuevo lo
        // contaría dos veces.
        var montoSinPercepciones = preciosIncluyenIva
            ? subtotal - descuentoTotal
            : subtotal - descuentoTotal + (ivaTotal ?? 0m);
        var totalComprobante = montoSinPercepciones + percepcionesDelComprobante.Sum(p => p.Importe);

        return new CompraCalculada(
            subtotal, descuentoTotal, ivaTotal, totalComprobante, items, alicuotas, percepcionesDelComprobante);
    }

    /// <summary>IVA por alícuota redondeado una sola vez — la convención de una factura —, y después
    /// el override del impreso con su tolerancia. Con precios netos se calcula sobre el neto; con
    /// precios finales se extrae del final de la alícuota y el neto es el resto.</summary>
    private static List<AlicuotaCalculada> ArmarAlicuotas(
        SortedDictionary<int, (decimal Porcentaje, decimal Monto)> montoPorAlicuota,
        IReadOnlyDictionary<int, decimal>? ivaImpreso,
        bool preciosIncluyenIva)
    {
        foreach (var idImpreso in ivaImpreso?.Keys ?? Enumerable.Empty<int>())
        {
            if (!montoPorAlicuota.ContainsKey(idImpreso))
            {
                throw new ErrorDominio(
                    "iva_impreso_alicuota_desconocida",
                    $"La alícuota {idImpreso} no figura en las líneas del comprobante.",
                    400);
            }
        }

        var alicuotas = new List<AlicuotaCalculada>(montoPorAlicuota.Count);

        foreach (var (idAlicuota, (porcentaje, monto)) in montoPorAlicuota)
        {
            var calculado = preciosIncluyenIva
                ? Redondear(monto * porcentaje / (100m + porcentaje), 2)
                : Redondear(monto * porcentaje / 100m, 2);
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

            var neto = preciosIncluyenIva ? monto - iva : monto;

            if (neto < 0m)
            {
                throw new ErrorDominio(
                    "iva_impreso_fuera_de_tolerancia",
                    $"El IVA impreso ({iva:0.00}) deja un neto negativo en la alícuota {idAlicuota}.",
                    400);
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
    /// congelado en la fila. <paramref name="preciosIncluyenIva"/> es el del comprobante: con
    /// precios finales el <c>total</c> de la fila ya trae el IVA.</summary>
    public static decimal CalcularCostoEfectivoDesdeItem(
        decimal total, decimal cantidad, decimal porcentajeIva, bool discriminaIva, bool preciosIncluyenIva = false) =>
        CalcularCostoEfectivo(total, cantidad, porcentajeIva, discriminaIva, preciosIncluyenIva);

    private static decimal CalcularCostoEfectivo(
        decimal total, decimal cantidad, decimal porcentajeIva, bool discriminaIva, bool preciosIncluyenIva) =>
        discriminaIva && !preciosIncluyenIva
            ? Redondear(total * (1 + porcentajeIva / 100m) / cantidad, 2)
            : Redondear(total / cantidad, 2);

    /// <summary>Design: Compra Arithmetic — "dos líneas del mismo artículo... el costo_nominal se
    /// deduplica en memoria con el mayor orden ganando, así que se emite exactamente un UPDATE
    /// por artículo". Filtra por <c>actualizaCosto AND costoUnitario &gt; 0</c> (design decisión
    /// 4, el guard anti-bonificación) antes de dedupear. Un concepto (sin artículo) nunca entra.
    ///
    /// <para>Con familias (doc 10 §3) el dedupe es por FAMILIA: los artículos de una misma familia son
    /// idénticos en <c>costo_nominal</c>, así que de todas las líneas filtradas de una familia gana la de
    /// mayor orden y su costo es el de TODOS los miembros vivos. Un artículo sin familia conserva el dedupe
    /// por artículo. <paramref name="familiaPorArticulo"/> da la familia de cada artículo que es miembro
    /// (una clave ausente es un artículo suelto) y tiene que salir de una lectura bajo el lock de membresía.
    /// El resultado trae, por cada ganador, la familia (<c>null</c> ⇒ solo ese artículo) y el artículo de
    /// su línea, ascendentes por ese artículo; expandir una familia a sus miembros es del llamador, que es
    /// quien los bloquea.</para></summary>
    public static IReadOnlyList<ActualizacionDeCosto> ResolverActualizacionesDeCosto(
        IReadOnlyList<(int Orden, int? IdArticulo, bool ActualizaCosto, decimal CostoUnitario, decimal CostoEfectivo)> items,
        IReadOnlyDictionary<int, int> familiaPorArticulo)
    {
        var ganador = new Dictionary<(int? IdFamilia, int? IdArticulo), (int Orden, int IdArticulo, decimal Costo)>();

        foreach (var item in items)
        {
            if (item.IdArticulo is not { } idArticulo || !item.ActualizaCosto || item.CostoUnitario <= 0m)
            {
                continue;
            }

            var clave = familiaPorArticulo.TryGetValue(idArticulo, out var idFamilia)
                ? (IdFamilia: (int?)idFamilia, IdArticulo: (int?)null)
                : (IdFamilia: null, IdArticulo: idArticulo);

            if (!ganador.TryGetValue(clave, out var actual) || item.Orden > actual.Orden)
            {
                ganador[clave] = (item.Orden, idArticulo, item.CostoEfectivo);
            }
        }

        return
        [
            .. ganador
                .OrderBy(kv => kv.Value.IdArticulo)
                .Select(kv => new ActualizacionDeCosto(kv.Key.IdFamilia, kv.Value.IdArticulo, kv.Value.Costo))
        ];
    }

    /// <summary>Qué líneas con precio sugerido NO se aplican por separado cuando la compra se aplica a una
    /// lista (doc 10 §3): aplicar el precio sugerido de un artículo que es miembro de una familia lo aplica a
    /// toda la familia, así que de las líneas de una misma familia solo se aplica la de MAYOR orden y las demás
    /// quedan superadas por ella. Las líneas de artículos sueltos nunca quedan superadas: cada una se aplica.
    /// <paramref name="familiaPorArticulo"/> da la familia de cada artículo que es miembro (una clave ausente es
    /// un artículo suelto). El resultado mapea el orden de cada línea superada al de la línea que la supera; una
    /// línea que se aplica no figura.</summary>
    public static IReadOnlyDictionary<int, int> ResolverLineasSuperadasDePrecio(
        IReadOnlyList<(int Orden, int IdArticulo)> lineas, IReadOnlyDictionary<int, int> familiaPorArticulo)
    {
        var superadas = new Dictionary<int, int>();

        foreach (var familia in lineas
            .Where(l => familiaPorArticulo.ContainsKey(l.IdArticulo))
            .GroupBy(l => familiaPorArticulo[l.IdArticulo]))
        {
            var ganadora = familia.Max(l => l.Orden);

            foreach (var linea in familia.Where(l => l.Orden != ganadora))
            {
                superadas[linea.Orden] = ganadora;
            }
        }

        return superadas;
    }

    private static decimal Redondear(decimal valor, int decimales) =>
        Math.Round(valor, decimales, MidpointRounding.AwayFromZero);
}
