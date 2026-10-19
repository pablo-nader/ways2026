using System.Globalization;
using Ways.Application.Compras;
using Ways.Domain.Articulos;
using Ways.Domain.Common;
using Ways.Domain.Compras;

namespace Ways.Api.ConectorMcp;

/// <summary>Lo que <c>crear_borrador_de_compra</c> le contesta a Claude: el borrador creado, las compras que ya
/// tienen ese número, o por qué no se creó nada, dicho en los argumentos de la herramienta. Puro.</summary>
public static class TextosDelBorradorDeCompra
{
    public const string ErrorDeCarrera =
        "No se creó el borrador. Mientras se cargaba se guardó otra compra no anulada de ese proveedor con el mismo " +
        "tipo y número de comprobante. Volvé a llamar a crear_borrador_de_compra con los mismos datos para ver cuál es.";

    public const string ResultadoIncierto =
        "Resultado incierto: verificá en el listado de compras si el borrador quedó creado antes de reintentar.";

    public const string ValorFueraDeRango =
        "No se creó el borrador. Un importe o una cantidad no entra en Ways: revisá contra la factura las cantidades, " +
        "los costos unitarios, las bonificaciones y los importes.";

    /// <summary>Un fallo de la base ya clasificado por <c>ManejadorDeErrores.ClasificarPostgresException</c>, la
    /// misma clasificación que usa la API; uno sin traducción conserva su título.</summary>
    public static string TraducirFalloDeBase(string codigo, string titulo) => codigo switch
    {
        "compra_duplicada" => ErrorDeCarrera,
        "valor_fuera_de_rango" => ValorFueraDeRango,
        _ => "No se creó el borrador. " + titulo
    };

    private static readonly IReadOnlyDictionary<int, (decimal? MargenGrupo, decimal? MargenProveedor)> SinMargenes =
        new Dictionary<int, (decimal? MargenGrupo, decimal? MargenProveedor)>();

    /// <summary>Un código de error del servicio de compras pasado a qué corregir; uno sin traducción conserva
    /// el mensaje del servicio.</summary>
    public static string Traducir(ErrorDominio error) =>
        "No se creó el borrador. " + error.Codigo switch
        {
            "numero_comprobante_invalido" =>
                "El número de la factura no es válido: puntoVentaFactura va de 1 a 99999 y numeroFactura de 1 a " +
                "99999999 (en 00003-00012345, puntoVentaFactura es 3 y numeroFactura 12345). Revisalos en la factura.",
            "no_encontrado" =>
                $"{error.Message} El proveedor sale de buscar_proveedor y el punto de venta de contexto_de_compras.",
            "referencia_invalida" =>
                $"{error.Message} Las alícuotas de IVA salen de contexto_de_compras y los artículos de buscar_articulos.",
            "tipo_de_compra_invalido" =>
                $"{error.Message} Usá uno de los tipos de comprobante de compra de contexto_de_compras.",
            "discrimina_iva_incompatible" =>
                $"{error.Message} En una factura no mandes discriminaIva: lo fija la letra (la A discrimina, la B y la C no).",
            "precios_incluyen_iva_sin_discriminar" =>
                "preciosIncluyenIva solo vale en un comprobante que discrimina IVA. En una factura B o C los precios ya " +
                "son finales: mandalo en false.",
            "iva_impreso_sin_discriminar" =>
                "Este comprobante no discrimina IVA (una factura B o C, o un remito sin discriminaIva), así que no lleva ivaImpreso.",
            "iva_impreso_duplicado" =>
                $"{error.Message} Mandá un solo importe por alícuota en ivaImpreso.",
            "iva_impreso_alicuota_desconocida" =>
                $"{error.Message} Cada idAlicuotaIva de ivaImpreso tiene que ser el de alguna línea.",
            "iva_impreso_en_alicuota_sin_iva" =>
                $"{error.Message} Sacala de ivaImpreso.",
            "iva_impreso_fuera_de_tolerancia" =>
                $"{error.Message} Las líneas no explican el IVA que imprime la factura: revisá cantidades, costos " +
                "unitarios, bonificaciones, la alícuota de cada línea y preciosIncluyenIva. Si todo coincide con la " +
                "factura, preguntale al usuario cómo seguir.",
            "percepciones_sin_libro_iva" =>
                "Este tipo de comprobante no registra libro IVA (por ejemplo, un remito) y no lleva percepciones: sacalas " +
                "o usá el tipo de factura que corresponde.",
            "percepcion_duplicada" =>
                $"{error.Message} Ways guarda una percepción por tipo: si la factura trae varias del mismo tipo (por " +
                "ejemplo, IIBB de distintas jurisdicciones), sumá sus importes en una y avisale al usuario.",
            "percepcion_iva_sin_discriminar" =>
                "La percepción de IVA solo va en un comprobante que discrimina IVA.",
            "percepcion_tipo_invalido" =>
                $"{error.Message} Es el tipo de cada percepción.",
            "percepcion_importes_invalidos" =>
                "El importe y la baseImponible de una percepción no pueden ser negativos.",
            "percepcion_alicuota_invalida" =>
                "La alicuota de una percepción va de 0 a 100, en porcentaje (3 para 3 %).",
            "percepcion_decimales_invalidos" =>
                "En una percepción, importe y baseImponible admiten hasta 2 decimales y alicuota hasta 3.",
            "concepto_con_bultos" =>
                "Una línea de concepto no lleva bultos ni unidadesPorBulto: mandá su cantidad en unidades.",
            "concepto_con_lote" =>
                "Una línea de concepto no lleva codigoLote ni fechaVencimiento.",
            "lote_vencido_en_recepcion" =>
                $"{error.Message} Preguntale al usuario qué hacer con esa línea.",
            "codigo_proveedor_muy_largo" =>
                $"codigoProveedor admite hasta {ReglaDeCodigoProveedor.LongitudMaxima} caracteres.",
            _ => error.Message
        };

    public static string DescribirDuplicadas(IReadOnlyList<CompraDuplicada> duplicadas, Func<int, string> link)
    {
        List<string> lineas =
        [
            "No se creó el borrador: ese proveedor ya tiene una compra no anulada con el mismo tipo y número de " +
            "comprobante (id | estado | origen | fecha | número guardado | link):"
        ];
        lineas.AddRange(duplicadas.Select(d =>
            $"- {d.Id} | {Minusculas(d.Estado)} | {Minusculas(d.Origen)} | {Fecha(d.FechaComprobante)} | " +
            $"{d.NumeroExterno.ReplaceLineEndings(" ")} | {link(d.Id)}"));
        lineas.Add("Contale al usuario cuál es: si es la misma factura, ya está cargada; si no, revisá el número impreso.");

        return string.Join(Environment.NewLine, lineas);
    }

    /// <param name="totalImpreso">El total que imprime la factura, si Claude lo mandó: solo se compara.</param>
    public static string DescribirCreado(CompraDetalle compra, string nombreDelTipo, string link, decimal? totalImpreso)
    {
        var deArticulo = compra.Items.Count(i => i.IdArticulo is not null);
        var percepciones = compra.Percepciones.Sum(p => p.Importe ?? 0m);
        var iva = compra.IvaTotal is { } ivaTotal ? Importe(ivaTotal) : "no discriminado";

        List<string> lineas =
        [
            $"Borrador de compra {compra.Id} creado con origen {Minusculas(compra.Origen)}: {link}",
            "Para confirmarlo, el usuario lo abre en ese link, lo revisa contra la factura y toca Guardar. Hasta " +
            "entonces no mueve stock, costos ni la cuenta corriente del proveedor.",
            $"Comprobante: {nombreDelTipo} {compra.NumeroExterno?.ReplaceLineEndings(" ") ?? "sin número"}, " +
            $"fecha {Fecha(compra.FechaComprobante)}",
            $"Líneas: {deArticulo} de artículo y {compra.Items.Count - deArticulo} de concepto",
            $"Subtotal: {Importe(compra.Subtotal)} | Descuentos: {Importe(compra.DescuentoTotal)} | IVA: {iva} | " +
            $"Percepciones: {Importe(percepciones)} | Total: {Importe(compra.Total)}"
        ];

        List<string> advertencias = [];
        if (totalImpreso is { } impreso)
        {
            var diferencia = compra.Total - impreso;
            lineas.Add($"Total impreso: {Importe(impreso)} | Diferencia (calculado - impreso): {Importe(diferencia)}");

            if (diferencia != 0m)
            {
                advertencias.Add(
                    "El total calculado no coincide con el impreso: revisá contra la factura cantidades, costos unitarios, " +
                    "bonificaciones, alícuotas y percepciones, o avisale al usuario antes de que lo confirme.");
            }
        }

        advertencias.AddRange(IvaDistintoDelCalculado(compra).Select(a =>
            $"Alícuota {Porcentaje(a.Porcentaje)}: quedó el IVA impreso ({Importe(a.Guardado)}), que difiere del " +
            $"calculado ({Importe(a.Calculado)}) en {Importe(a.Guardado - a.Calculado)}."));

        if (advertencias.Count > 0)
        {
            lineas.Add("Advertencias:");
            lineas.AddRange(advertencias.Select(a => "- " + a));
        }

        return string.Join(Environment.NewLine, lineas);
    }

    /// <summary>Las alícuotas cuyo IVA guardado no es el que calculan las líneas: el servidor se quedó con el
    /// impreso porque difería en hasta <see cref="CalculadorDeCompra.ToleranciaDeIvaImpreso"/>. El cálculo sin
    /// impreso se rehace con <see cref="CalculadorDeCompra"/> sobre las líneas guardadas, cuya cantidad ya está
    /// derivada.</summary>
    private static IEnumerable<(decimal Porcentaje, decimal Guardado, decimal Calculado)> IvaDistintoDelCalculado(
        CompraDetalle compra)
    {
        // Los costos vienen siempre: el permiso de compras es solo de Admin, que los ve en la proyección.
        var lineas = compra.Items
            .Select(i => new LineaDeCompra(
                i.Orden, i.IdArticulo, i.Descripcion, i.Cantidad, Bultos: null, UnidadesPorBulto: null,
                i.CostoUnitario!.Value, i.Descuento!.Value, i.IdAlicuotaIva, i.PorcentajeIva, ActualizaCosto: false))
            .ToList();
        var calculado = CalculadorDeCompra
            .Calcular(lineas, compra.DiscriminaIva, SinMargenes, ivaImpreso: null, compra.PreciosIncluyenIva)
            .Alicuotas
            .ToDictionary(a => a.IdAlicuotaIva, a => a.Iva);

        return compra.Alicuotas
            .Where(a => a.Iva is { } guardado && guardado != calculado[a.IdAlicuotaIva])
            .Select(a => (a.Porcentaje, a.Iva!.Value, calculado[a.IdAlicuotaIva]))
            .ToList();
    }

    private static string Importe(decimal valor) => valor.ToString("0.00", CultureInfo.InvariantCulture);

    private static string Porcentaje(decimal valor) => valor.ToString("0.##", CultureInfo.InvariantCulture) + " %";

    private static string Fecha(DateOnly? fecha) =>
        fecha?.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture) ?? "sin fecha";

    private static string Minusculas<T>(T valor) where T : struct, Enum => valor.ToString().ToLowerInvariant();
}
