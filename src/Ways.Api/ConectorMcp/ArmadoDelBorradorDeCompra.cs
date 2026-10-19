using System.ComponentModel;
using System.Globalization;
using Ways.Application.Compras;

namespace Ways.Api.ConectorMcp;

/// <summary>Una línea de la factura para <c>crear_borrador_de_compra</c>: un artículo de Ways
/// (<see cref="IdArticulo"/>) o un concepto (<see cref="Concepto"/>), nunca los dos. Cada campo va a un
/// campo de <see cref="LineaDeCompraSolicitada"/>; <see cref="ArmadoDelBorradorDeCompra"/> rechaza lo que el
/// contrato no puede guardar tal cual. Los obligatorios son nulables para que faltar sea un error y no un
/// cero.</summary>
public sealed record LineaDeBorradorDeCompra(
    [property: Description("Id del artículo de Ways, el de buscar_articulos. Sin él la línea es un concepto y lleva concepto.")]
    int? IdArticulo = null,
    [property: Description(
        "Solo en una línea sin idArticulo: el texto de la línea tal como lo imprime la factura (un flete, un " +
        "servicio). Con idArticulo no va: el detalle de la línea es el nombre del artículo en Ways.")]
    string? Concepto = null,
    [property: Description(
        "Código con que el proveedor imprime la línea, si lo trae. Se guarda en la línea y, al confirmar la " +
        "compra, queda asociado al artículo si el código está libre: en la próxima factura buscar_articulos lo " +
        "encuentra por ese código.")]
    string? CodigoProveedor = null,
    [property: Description(
        "Unidades sueltas. La cantidad de la línea es unidades + bultos × unidadesPorBulto. Hasta 3 decimales " +
        "(artículos por peso). Por defecto 0.")]
    decimal Unidades = 0m,
    [property: Description(
        "Bultos (cajas, packs) si la factura cuenta por bulto. Va junto con unidadesPorBulto. Hasta 2 decimales.")]
    decimal? Bultos = null,
    [property: Description("Unidades que trae cada bulto. Va junto con bultos. Hasta 2 decimales.")]
    decimal? UnidadesPorBulto = null,
    [property: Description(
        "Obligatorio. Precio de UNA unidad, no del bulto (si la factura da el precio por bulto, dividilo por " +
        "unidadesPorBulto). Sin IVA, salvo que preciosIncluyenIva sea true. Hasta 4 decimales.")]
    decimal? CostoUnitario = null,
    [property: Description(
        "Importe en pesos de la bonificación de la línea, no un porcentaje: si la factura imprime el %, " +
        "calculalo sobre cantidad × costoUnitario redondeado a centavos. Hasta 2 decimales. Por defecto 0.")]
    decimal Descuento = 0m,
    [property: Description(
        "Obligatorio. Id de la alícuota de IVA de la línea (de contexto_de_compras), aunque el comprobante no " +
        "discrimine IVA: si la factura no la imprime, la del artículo.")]
    int? IdAlicuotaIva = null,
    [property: Description("Lote impreso para la línea, si lo trae. Solo con idArticulo y junto con fechaVencimiento.")]
    string? CodigoLote = null,
    [property: Description("Vencimiento impreso para la línea, yyyy-MM-dd. Solo con idArticulo; obligatorio si hay codigoLote.")]
    string? FechaVencimiento = null);

/// <summary>El IVA que imprime la factura para una alícuota (<see cref="IvaImpresoSolicitado"/>).</summary>
public sealed record IvaImpresoDeLaFactura(
    [property: Description("Obligatorio. Id de la alícuota de IVA, la misma que usan las líneas.")]
    int? IdAlicuotaIva = null,
    [property: Description("Obligatorio. Importe de IVA impreso para esa alícuota. Hasta 2 decimales.")]
    decimal? Iva = null);

/// <summary>Una percepción tal como la imprime la factura (<see cref="PercepcionSolicitada"/>).</summary>
public sealed record PercepcionDeLaFactura(
    [property: Description("Obligatorio. iibb o iva.")]
    string? Tipo = null,
    [property: Description("Obligatorio. Importe percibido, el que suma al total. Hasta 2 decimales.")]
    decimal? Importe = null,
    [property: Description("Base imponible impresa; 0 si la factura no la imprime.")]
    decimal BaseImponible = 0m,
    [property: Description("Alícuota impresa, en porcentaje (3 para 3 %); 0 si la factura no la imprime.")]
    decimal Alicuota = 0m);

/// <summary>Los argumentos de <c>crear_borrador_de_compra</c> que van a la <see cref="SolicitudDeCompra"/>,
/// tal como llegan.</summary>
public sealed record FacturaParaBorrador(
    int IdProveedor,
    int IdTipoComprobante,
    int IdPuntoVenta,
    string? FechaComprobante,
    IReadOnlyList<LineaDeBorradorDeCompra?>? Lineas,
    long? PuntoVentaFactura = null,
    long? NumeroFactura = null,
    string? NumeroRemito = null,
    bool? DiscriminaIva = null,
    bool PreciosIncluyenIva = false,
    IReadOnlyList<IvaImpresoDeLaFactura?>? IvaImpreso = null,
    IReadOnlyList<PercepcionDeLaFactura?>? Percepciones = null,
    string? Observaciones = null);

/// <summary>
/// Pasa los argumentos de <c>crear_borrador_de_compra</c> a una <see cref="SolicitudDeCompra"/>. Puro, sin
/// base de datos. <see cref="ValidarForma"/> rechaza con <see cref="ErrorDeHerramienta"/> lo que el contrato
/// aceptaría y guardaría distinto (un valor faltante que sería cero, bultos sin unidades por bulto, más decimales
/// que la columna) y las reglas de cada línea que no necesitan la base; el resto (IVA, percepciones, conceptos
/// con lote o bultos, mercadería vencida) lo valida el servicio.
/// </summary>
public static class ArmadoDelBorradorDeCompra
{
    public const int MaximoDeLineas = 100;

    private const string FormatoDeFecha = "yyyy-MM-dd";

    /// <summary>Valida, sin la base: entre 1 y <see cref="MaximoDeLineas"/> líneas, cada una con
    /// <see cref="ValidarLinea"/>; <c>fechaComprobante</c> en yyyy-MM-dd; cada IVA impreso con alícuota e importe
    /// de hasta 2 decimales; y cada percepción con importe. <see cref="Armar"/> supone que esto pasó.</summary>
    public static void ValidarForma(FacturaParaBorrador factura)
    {
        if (factura.Lineas is not { Count: > 0 and <= MaximoDeLineas } lineas)
        {
            throw new ErrorDeHerramienta($"Pasá entre 1 y {MaximoDeLineas} líneas.");
        }

        for (var i = 0; i < lineas.Count; i++)
        {
            ValidarLinea(lineas[i], i + 1);
        }

        LeerFecha(factura.FechaComprobante, "fechaComprobante");

        var ivaImpreso = factura.IvaImpreso ?? [];
        for (var i = 0; i < ivaImpreso.Count; i++)
        {
            var numero = i + 1;
            if (ivaImpreso[i] is not { IdAlicuotaIva: not null, Iva: { } iva })
            {
                throw new ErrorDeHerramienta($"ivaImpreso {numero}: necesita idAlicuotaIva e iva.");
            }

            ExigirDecimales(iva, 2, $"ivaImpreso {numero}: iva");
        }

        var percepciones = factura.Percepciones ?? [];
        for (var i = 0; i < percepciones.Count; i++)
        {
            if (percepciones[i] is not { Importe: not null })
            {
                throw new ErrorDeHerramienta($"Percepción {i + 1}: falta el importe.");
            }
        }
    }

    public static IReadOnlyList<int> IdsDeArticulo(FacturaParaBorrador factura) =>
        [.. (factura.Lineas ?? []).Select(l => l?.IdArticulo).OfType<int>().Distinct()];

    /// <summary>Arma la solicitud con el número según el tipo y el nombre actual de cada artículo como detalle
    /// de su línea, igual que la web al elegir el artículo. Un artículo que no está en
    /// <paramref name="nombreDeArticulo"/> no existe para el tenant de la request.</summary>
    public static SolicitudDeCompra Armar(
        FacturaParaBorrador factura, bool registraLibroIva, IReadOnlyDictionary<int, string> nombreDeArticulo)
    {
        var lineas = factura.Lineas!.Select((linea, i) => ArmarLinea(linea!, i + 1, nombreDeArticulo)).ToList();

        return new SolicitudDeCompra(
            factura.IdProveedor,
            factura.IdTipoComprobante,
            factura.IdPuntoVenta,
            ArmarNumero(registraLibroIva, factura.PuntoVentaFactura, factura.NumeroFactura, factura.NumeroRemito),
            LeerFecha(factura.FechaComprobante, "fechaComprobante"),
            factura.Observaciones,
            lineas,
            IdOrdenCompra: null,
            factura.DiscriminaIva,
            factura.IvaImpreso?.Select(i => new IvaImpresoSolicitado(i!.IdAlicuotaIva!.Value, i.Iva!.Value)).ToList(),
            factura.PreciosIncluyenIva,
            factura.Percepciones?
                .Select(p => new PercepcionSolicitada(
                    p!.Tipo?.Trim().ToLowerInvariant() ?? string.Empty, p.BaseImponible, p.Alicuota, p.Importe!.Value))
                .ToList());
    }

    /// <summary>En un tipo que registra el libro IVA (una factura) arma <c>PPPPP-NNNNNNNN</c> con los dos
    /// números impresos, sin validarlos: el servicio rechaza el que no entra (punto de venta de más de 5 dígitos,
    /// número de más de 8, cero o negativo) con <c>numero_comprobante_invalido</c>. En los demás tipos el número
    /// es el texto libre de <paramref name="numeroRemito"/>, que puede faltar.</summary>
    public static string? ArmarNumero(bool registraLibroIva, long? puntoVentaFactura, long? numeroFactura, string? numeroRemito)
    {
        if (registraLibroIva)
        {
            if (!string.IsNullOrWhiteSpace(numeroRemito))
            {
                throw new ErrorDeHerramienta(
                    "Este tipo de comprobante es una factura: el número va en puntoVentaFactura y numeroFactura, no en numeroRemito.");
            }

            if (puntoVentaFactura is not { } puntoDeVenta || numeroFactura is not { } numero)
            {
                throw new ErrorDeHerramienta(
                    "Una factura necesita puntoVentaFactura y numeroFactura, los dos números que imprime: en " +
                    "00003-00012345, puntoVentaFactura es 3 y numeroFactura 12345.");
            }

            return string.Create(CultureInfo.InvariantCulture, $"{puntoDeVenta:D5}-{numero:D8}");
        }

        if (puntoVentaFactura is not null || numeroFactura is not null)
        {
            throw new ErrorDeHerramienta(
                "Este tipo de comprobante no es una factura: su número va como texto en numeroRemito (opcional), no en " +
                "puntoVentaFactura ni numeroFactura.");
        }

        return numeroRemito;
    }

    /// <summary>Una línea, con su número en cada error: es un objeto; trae <c>idArticulo</c> o <c>concepto</c>,
    /// no los dos; trae costo y alícuota; bultos y unidades por bulto van juntos; cada número tiene los decimales
    /// de su columna; la cantidad es positiva, el costo y el descuento no son negativos y el descuento no supera el
    /// bruto; un lote trae vencimiento, y el vencimiento es yyyy-MM-dd.</summary>
    private static void ValidarLinea(LineaDeBorradorDeCompra? linea, int numero)
    {
        if (linea is null)
        {
            throw new ErrorDeHerramienta($"Línea {numero}: tiene que ser un objeto.");
        }

        var esArticulo = linea.IdArticulo is not null;
        if (esArticulo == !string.IsNullOrWhiteSpace(linea.Concepto))
        {
            throw new ErrorDeHerramienta(esArticulo
                ? $"Línea {numero}: trae idArticulo y concepto. Con un artículo de Ways el detalle es su nombre: sacá concepto."
                : $"Línea {numero}: necesita idArticulo (un artículo de Ways) o concepto (el texto de una línea que no es un artículo).");
        }

        if (linea.CostoUnitario is null || linea.IdAlicuotaIva is null)
        {
            throw new ErrorDeHerramienta($"Línea {numero}: necesita costoUnitario e idAlicuotaIva.");
        }

        if ((linea.Bultos is null) != (linea.UnidadesPorBulto is null))
        {
            throw new ErrorDeHerramienta(
                $"Línea {numero}: bultos y unidadesPorBulto van juntos (la cantidad es unidades + bultos × unidadesPorBulto).");
        }

        ExigirDecimales(linea.Unidades, 3, $"Línea {numero}: unidades");
        ExigirDecimales(linea.Bultos, 2, $"Línea {numero}: bultos");
        ExigirDecimales(linea.UnidadesPorBulto, 2, $"Línea {numero}: unidadesPorBulto");
        ExigirDecimales(linea.CostoUnitario, 4, $"Línea {numero}: costoUnitario");
        ExigirDecimales(linea.Descuento, 2, $"Línea {numero}: descuento");

        var costo = linea.CostoUnitario.Value;
        var cantidad = Redondear(linea.Unidades + (linea.Bultos ?? 0m) * (linea.UnidadesPorBulto ?? 0m), 3);
        if (cantidad <= 0m)
        {
            throw new ErrorDeHerramienta(
                $"Línea {numero}: la cantidad (unidades + bultos × unidadesPorBulto) tiene que ser positiva.");
        }

        if (costo < 0m)
        {
            throw new ErrorDeHerramienta($"Línea {numero}: costoUnitario no puede ser negativo.");
        }

        if (linea.Descuento < 0m)
        {
            throw new ErrorDeHerramienta($"Línea {numero}: descuento no puede ser negativo.");
        }

        var bruto = Redondear(cantidad * costo, 2);
        if (linea.Descuento > bruto)
        {
            throw new ErrorDeHerramienta(
                $"Línea {numero}: descuento supera el importe bruto de la línea (cantidad × costoUnitario = " +
                $"{bruto.ToString("0.00", CultureInfo.InvariantCulture)}): es la bonificación en pesos, no un porcentaje.");
        }

        if (!string.IsNullOrWhiteSpace(linea.CodigoLote) && string.IsNullOrWhiteSpace(linea.FechaVencimiento))
        {
            throw new ErrorDeHerramienta($"Línea {numero}: codigoLote va con fechaVencimiento.");
        }

        if (!string.IsNullOrWhiteSpace(linea.FechaVencimiento))
        {
            LeerFecha(linea.FechaVencimiento, $"Línea {numero}: fechaVencimiento");
        }
    }

    /// <summary>El redondeo de <see cref="Ways.Domain.Compras.CalculadorDeCompra"/>: la cantidad de la línea va a 3
    /// decimales y su bruto a 2, lejos del cero en el punto medio. Con las mismas cuentas, una línea que pasa
    /// <see cref="ValidarForma"/> pasa también las reglas de cantidad, costo y descuento del servicio.</summary>
    private static decimal Redondear(decimal valor, int decimales) =>
        Math.Round(valor, decimales, MidpointRounding.AwayFromZero);

    private static LineaDeCompraSolicitada ArmarLinea(
        LineaDeBorradorDeCompra linea, int numero, IReadOnlyDictionary<int, string> nombreDeArticulo)
    {
        string descripcion;
        if (linea.IdArticulo is { } idArticulo)
        {
            descripcion = nombreDeArticulo.TryGetValue(idArticulo, out var nombre)
                ? nombre
                : throw new ErrorDeHerramienta(
                    $"Línea {numero}: no existe el artículo {idArticulo}. Buscalo con buscar_articulos o cargá la línea como concepto.");
        }
        else
        {
            descripcion = linea.Concepto!.Trim();
        }

        return new LineaDeCompraSolicitada(
            linea.IdArticulo,
            descripcion,
            linea.Unidades,
            linea.Bultos,
            linea.UnidadesPorBulto,
            linea.CostoUnitario!.Value,
            linea.Descuento,
            linea.IdAlicuotaIva!.Value,
            ActualizaCosto: null,
            linea.CodigoLote,
            string.IsNullOrWhiteSpace(linea.FechaVencimiento) ? null : LeerFecha(linea.FechaVencimiento, "fechaVencimiento"),
            linea.CodigoProveedor);
    }

    private static DateOnly LeerFecha(string? texto, string argumento) =>
        DateOnly.TryParseExact(texto?.Trim(), FormatoDeFecha, CultureInfo.InvariantCulture, DateTimeStyles.None, out var fecha)
            ? fecha
            : throw new ErrorDeHerramienta($"{argumento} tiene que ser una fecha {FormatoDeFecha}, por ejemplo 2026-10-15.");

    private static void ExigirDecimales(decimal? valor, int decimales, string campo)
    {
        if (valor is { } v && v != Math.Round(v, decimales))
        {
            throw new ErrorDeHerramienta($"{campo} admite hasta {decimales} decimales.");
        }
    }
}
