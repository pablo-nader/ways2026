using Ways.Api.ConectorMcp;
using Ways.Application.Compras;
using Ways.Domain.Common;
using Ways.Domain.Compras;

namespace Ways.IntegrationTests;

/// <summary>Pruebas puras de <see cref="ArmadoDelBorradorDeCompra"/> y <see cref="TextosDelBorradorDeCompra"/>: el
/// paso de los argumentos de <c>crear_borrador_de_compra</c> al contrato de compras y los textos que vuelven. No
/// levantan la app ni Docker.</summary>
public class ConectorMcpArmadoDelBorradorDeCompraTests
{
    private const int IdArticulo = 41;
    private const string NombreDelArticulo = "Yerba Mate Suave 1 kg";

    private static readonly Dictionary<int, string> Nombres = new() { [IdArticulo] = NombreDelArticulo };

    private static LineaDeBorradorDeCompra Concepto() =>
        new(Concepto: "Flete", Unidades: 1m, CostoUnitario: 500m, IdAlicuotaIva: 5);

    private static FacturaParaBorrador Factura(params LineaDeBorradorDeCompra?[] lineas) =>
        new(7, 8, 9, "2026-10-15", lineas.Length == 0 ? [Concepto()] : lineas, PuntoVentaFactura: 3, NumeroFactura: 12345);

    private static SolicitudDeCompra Armar(FacturaParaBorrador factura, bool registraLibroIva = true)
    {
        ArmadoDelBorradorDeCompra.ValidarForma(factura);
        return ArmadoDelBorradorDeCompra.Armar(factura, registraLibroIva, Nombres);
    }

    private static string ErrorDeForma(FacturaParaBorrador factura) =>
        Assert.Throws<ErrorDeHerramienta>(() => ArmadoDelBorradorDeCompra.ValidarForma(factura)).Message;

    [Fact]
    public void CadaArgumentoDelEncabezadoVaASuCampoDeLaSolicitud()
    {
        var factura = new FacturaParaBorrador(
            IdProveedor: 7,
            IdTipoComprobante: 8,
            IdPuntoVenta: 9,
            FechaComprobante: " 2026-10-15 ",
            Lineas: [Concepto()],
            PuntoVentaFactura: 3,
            NumeroFactura: 12345,
            DiscriminaIva: true,
            PreciosIncluyenIva: true,
            IvaImpreso: [new IvaImpresoDeLaFactura(5, 1113.40m)],
            Percepciones: [new PercepcionDeLaFactura(" IIBB ", 189.04m, 6301.25m, 3m)],
            Observaciones: "Nota");

        var solicitud = Armar(factura);

        Assert.Equal(7, solicitud.IdProveedor);
        Assert.Equal(8, solicitud.IdTipoComprobante);
        Assert.Equal(9, solicitud.IdPuntoVenta);
        Assert.Equal("00003-00012345", solicitud.NumeroExterno);
        Assert.Equal(new DateOnly(2026, 10, 15), solicitud.FechaComprobante);
        Assert.Equal("Nota", solicitud.Observaciones);
        Assert.Null(solicitud.IdOrdenCompra);
        Assert.True(solicitud.DiscriminaIva);
        Assert.True(solicitud.PreciosIncluyenIva);
        Assert.Equal([new IvaImpresoSolicitado(5, 1113.40m)], solicitud.IvaImpreso!);
        Assert.Equal([new PercepcionSolicitada("iibb", 6301.25m, 3m, 189.04m)], solicitud.Percepciones!);
    }

    [Fact]
    public void SinLosOpcionalesLaSolicitudDejaQueDecidaElTipoYNoLlevaIvaImpresoNiPercepciones()
    {
        var solicitud = Armar(Factura());

        Assert.Null(solicitud.DiscriminaIva);
        Assert.False(solicitud.PreciosIncluyenIva);
        Assert.Null(solicitud.IvaImpreso);
        Assert.Null(solicitud.Percepciones);
        Assert.Null(solicitud.Observaciones);
    }

    [Fact]
    public void UnaLineaDeArticuloLlevaElNombreDelArticuloYCadaCampoASuLugar()
    {
        var linea = new LineaDeBorradorDeCompra(
            IdArticulo: IdArticulo, CodigoProveedor: " YM-001 ", Unidades: 2.5m, Bultos: 3m, UnidadesPorBulto: 12m,
            CostoUnitario: 150.1234m, Descuento: 16.5m, IdAlicuotaIva: 5, CodigoLote: "L-1", FechaVencimiento: "2027-03-01");

        var armada = Assert.Single(Armar(Factura(linea)).Items);

        Assert.Equal(
            new LineaDeCompraSolicitada(
                IdArticulo, NombreDelArticulo, 2.5m, 3m, 12m, 150.1234m, 16.5m, 5, ActualizaCosto: null, "L-1",
                new DateOnly(2027, 3, 1), " YM-001 "),
            armada);
    }

    [Fact]
    public void UnaLineaDeConceptoLlevaSuTextoRecortadoYNingunArticulo()
    {
        var armada = Assert.Single(Armar(Factura(Concepto() with { Concepto = "  Flete Rosario  " })).Items);

        Assert.Equal(new LineaDeCompraSolicitada(null, "Flete Rosario", 1m, null, null, 500m, 0m, 5), armada);
    }

    [Fact]
    public void LasLineasConservanElOrdenDeLaFactura()
    {
        var solicitud = Armar(Factura(
            Concepto() with { Concepto = "Primera" },
            new LineaDeBorradorDeCompra(IdArticulo: IdArticulo, Unidades: 1m, CostoUnitario: 1m, IdAlicuotaIva: 5),
            Concepto() with { Concepto = "Tercera" }));

        Assert.Equal(["Primera", NombreDelArticulo, "Tercera"], solicitud.Items.Select(i => i.Descripcion));
    }

    [Fact]
    public void UnArticuloQueNoEstaEntreLosDelTenantEsUnErrorQueNombraLaLinea()
    {
        var factura = Factura(Concepto(), new LineaDeBorradorDeCompra(IdArticulo: 99, Unidades: 1m, CostoUnitario: 1m, IdAlicuotaIva: 5));

        var error = Assert.Throws<ErrorDeHerramienta>(() => Armar(factura));

        Assert.Equal(
            "Línea 2: no existe el artículo 99. Buscalo con buscar_articulos o cargá la línea como concepto.", error.Message);
    }

    [Theory]
    [InlineData(3L, 12345L, "00003-00012345")]
    [InlineData(99999L, 99999999L, "99999-99999999")]
    [InlineData(123456L, 1L, "123456-00000001")]
    public void EnUnaFacturaElNumeroSeArmaConLosDosNumerosImpresosYElServicioJuzgaSiEntran(long puntoDeVenta, long numero, string esperado) =>
        Assert.Equal(esperado, ArmadoDelBorradorDeCompra.ArmarNumero(true, puntoDeVenta, numero, numeroRemito: null));

    [Theory]
    [InlineData(null, 12345L)]
    [InlineData(3L, null)]
    public void UnaFacturaSinPuntoDeVentaONumeroEsUnError(long? puntoDeVenta, long? numero)
    {
        var error = Assert.Throws<ErrorDeHerramienta>(() => ArmadoDelBorradorDeCompra.ArmarNumero(true, puntoDeVenta, numero, null));

        Assert.StartsWith("Una factura necesita puntoVentaFactura y numeroFactura", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void UnaFacturaConNumeroRemitoEsUnError()
    {
        var error = Assert.Throws<ErrorDeHerramienta>(() => ArmadoDelBorradorDeCompra.ArmarNumero(true, 3, 12345, "R-1"));

        Assert.StartsWith("Este tipo de comprobante es una factura", error.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(3L, null)]
    [InlineData(null, 12345L)]
    public void UnTipoQueNoEsFacturaConNumerosDeFacturaEsUnError(long? puntoDeVenta, long? numero)
    {
        var error = Assert.Throws<ErrorDeHerramienta>(() => ArmadoDelBorradorDeCompra.ArmarNumero(false, puntoDeVenta, numero, null));

        Assert.StartsWith("Este tipo de comprobante no es una factura", error.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(" R-0001-000555 ")]
    [InlineData(null)]
    public void UnTipoQueNoEsFacturaPasaElNumeroLibreTalCual(string? numeroRemito) =>
        Assert.Equal(numeroRemito, ArmadoDelBorradorDeCompra.ArmarNumero(false, null, null, numeroRemito));

    [Fact]
    public void SinLineasOConMasDeCienEsUnError()
    {
        var mensaje = $"Pasá entre 1 y {ArmadoDelBorradorDeCompra.MaximoDeLineas} líneas.";

        Assert.Equal(mensaje, ErrorDeForma(Factura() with { Lineas = null }));
        Assert.Equal(mensaje, ErrorDeForma(Factura() with { Lineas = [] }));
        Assert.Equal(
            mensaje,
            ErrorDeForma(Factura() with { Lineas = Enumerable.Repeat<LineaDeBorradorDeCompra?>(Concepto(), ArmadoDelBorradorDeCompra.MaximoDeLineas + 1).ToList() }));
        ArmadoDelBorradorDeCompra.ValidarForma(
            Factura() with { Lineas = Enumerable.Repeat<LineaDeBorradorDeCompra?>(Concepto(), ArmadoDelBorradorDeCompra.MaximoDeLineas).ToList() });
    }

    private const string BultosJuntos =
        "Línea 2: bultos y unidadesPorBulto van juntos (la cantidad es unidades + bultos × unidadesPorBulto).";

    private static readonly Dictionary<string, (LineaDeBorradorDeCompra? Linea, string Mensaje)> LineasInvalidas = new()
    {
        ["nula"] = (null, "Línea 2: tiene que ser un objeto."),
        ["sin articulo ni concepto"] = (
            Concepto() with { Concepto = "  " },
            "Línea 2: necesita idArticulo (un artículo de Ways) o concepto (el texto de una línea que no es un artículo)."),
        ["articulo y concepto"] = (
            Concepto() with { IdArticulo = IdArticulo },
            "Línea 2: trae idArticulo y concepto. Con un artículo de Ways el detalle es su nombre: sacá concepto."),
        ["sin costo"] = (Concepto() with { CostoUnitario = null }, "Línea 2: necesita costoUnitario e idAlicuotaIva."),
        ["sin alicuota"] = (Concepto() with { IdAlicuotaIva = null }, "Línea 2: necesita costoUnitario e idAlicuotaIva."),
        ["bultos sin unidades por bulto"] = (Concepto() with { Bultos = 2m }, BultosJuntos),
        ["unidades por bulto sin bultos"] = (Concepto() with { UnidadesPorBulto = 12m }, BultosJuntos),
        ["unidades con 4 decimales"] = (Concepto() with { Unidades = 1.0005m }, "Línea 2: unidades admite hasta 3 decimales."),
        ["bultos con 3 decimales"] = (
            Concepto() with { Bultos = 1.005m, UnidadesPorBulto = 2m }, "Línea 2: bultos admite hasta 2 decimales."),
        ["unidades por bulto con 3 decimales"] = (
            Concepto() with { Bultos = 2m, UnidadesPorBulto = 1.005m }, "Línea 2: unidadesPorBulto admite hasta 2 decimales."),
        ["costo con 5 decimales"] = (
            Concepto() with { CostoUnitario = 1.00005m }, "Línea 2: costoUnitario admite hasta 4 decimales."),
        ["descuento con 3 decimales"] = (Concepto() with { Descuento = 1.005m }, "Línea 2: descuento admite hasta 2 decimales."),
        ["vencimiento que no es yyyy-MM-dd"] = (
            Concepto() with { FechaVencimiento = "01/03/2027" },
            "Línea 2: fechaVencimiento tiene que ser una fecha yyyy-MM-dd, por ejemplo 2026-10-15."),
        ["cantidad cero"] = (Concepto() with { Unidades = 0m }, CantidadPositiva),
        ["cantidad que redondea a cero"] = (
            new LineaDeBorradorDeCompra(
                IdArticulo: IdArticulo, Bultos: 0.01m, UnidadesPorBulto: 0.04m, CostoUnitario: 1m, IdAlicuotaIva: 5),
            CantidadPositiva),
        ["costo negativo"] = (Concepto() with { CostoUnitario = -1m }, "Línea 2: costoUnitario no puede ser negativo."),
        ["descuento negativo"] = (Concepto() with { Descuento = -1m }, "Línea 2: descuento no puede ser negativo."),
        ["descuento mayor al bruto"] = (
            Concepto() with { Descuento = 500.01m },
            "Línea 2: descuento supera el importe bruto de la línea (cantidad × costoUnitario = 500.00): es la " +
            "bonificación en pesos, no un porcentaje."),
        ["lote sin vencimiento"] = (
            new LineaDeBorradorDeCompra(IdArticulo: IdArticulo, Unidades: 1m, CostoUnitario: 1m, IdAlicuotaIva: 5, CodigoLote: "L-1"),
            "Línea 2: codigoLote va con fechaVencimiento.")
    };

    private const string CantidadPositiva =
        "Línea 2: la cantidad (unidades + bultos × unidadesPorBulto) tiene que ser positiva.";

    [Theory]
    [InlineData("nula")]
    [InlineData("sin articulo ni concepto")]
    [InlineData("articulo y concepto")]
    [InlineData("sin costo")]
    [InlineData("sin alicuota")]
    [InlineData("bultos sin unidades por bulto")]
    [InlineData("unidades por bulto sin bultos")]
    [InlineData("unidades con 4 decimales")]
    [InlineData("bultos con 3 decimales")]
    [InlineData("unidades por bulto con 3 decimales")]
    [InlineData("costo con 5 decimales")]
    [InlineData("descuento con 3 decimales")]
    [InlineData("vencimiento que no es yyyy-MM-dd")]
    [InlineData("cantidad cero")]
    [InlineData("cantidad que redondea a cero")]
    [InlineData("costo negativo")]
    [InlineData("descuento negativo")]
    [InlineData("descuento mayor al bruto")]
    [InlineData("lote sin vencimiento")]
    public void UnaLineaInvalidaEsUnErrorQueNombraLaLinea(string caso)
    {
        var (linea, mensaje) = LineasInvalidas[caso];

        Assert.Equal(mensaje, ErrorDeForma(Factura(Concepto(), linea)));
    }

    [Fact]
    public void LosDecimalesQueEntranEnLaColumnaSeAceptan() =>
        ArmadoDelBorradorDeCompra.ValidarForma(Factura(Concepto() with
        {
            Unidades = 1.125m, Bultos = 1.25m, UnidadesPorBulto = 2.50m, CostoUnitario = 1.1234m, Descuento = 0.10m
        }));

    /// <summary>Los bordes de las reglas de línea, con el redondeo del servicio: un descuento igual al bruto, un
    /// bruto que redondea hacia arriba (3 × 0,3333 = 0,9999 → 1,00), una cantidad que redondea a 0,001 y un
    /// vencimiento sin lote.</summary>
    [Fact]
    public void LosBordesDeLasReglasDeLineaSeAceptan() =>
        ArmadoDelBorradorDeCompra.ValidarForma(Factura(
            Concepto() with { Descuento = 500m },
            Concepto() with { Unidades = 3m, CostoUnitario = 0.3333m, Descuento = 1m },
            new LineaDeBorradorDeCompra(
                IdArticulo: IdArticulo, Bultos: 0.01m, UnidadesPorBulto: 0.05m, CostoUnitario: 1m, IdAlicuotaIva: 5),
            new LineaDeBorradorDeCompra(
                IdArticulo: IdArticulo, Unidades: 1m, CostoUnitario: 1m, IdAlicuotaIva: 5, FechaVencimiento: "2027-03-01")));

    [Theory]
    [InlineData(null)]
    [InlineData("15/10/2026")]
    [InlineData("2026-13-01")]
    public void UnaFechaDelComprobanteQueNoEsYyyyMmDdEsUnError(string? fecha) =>
        Assert.Equal(
            "fechaComprobante tiene que ser una fecha yyyy-MM-dd, por ejemplo 2026-10-15.",
            ErrorDeForma(Factura() with { FechaComprobante = fecha }));

    private static readonly Dictionary<string, (IvaImpresoDeLaFactura? IvaImpreso, string Mensaje)> IvaImpresoInvalido = new()
    {
        ["nulo"] = (null, "ivaImpreso 1: necesita idAlicuotaIva e iva."),
        ["sin alicuota"] = (new IvaImpresoDeLaFactura(Iva: 21m), "ivaImpreso 1: necesita idAlicuotaIva e iva."),
        ["sin iva"] = (new IvaImpresoDeLaFactura(IdAlicuotaIva: 5), "ivaImpreso 1: necesita idAlicuotaIva e iva."),
        ["iva con 3 decimales"] = (new IvaImpresoDeLaFactura(5, 21.005m), "ivaImpreso 1: iva admite hasta 2 decimales.")
    };

    [Theory]
    [InlineData("nulo")]
    [InlineData("sin alicuota")]
    [InlineData("sin iva")]
    [InlineData("iva con 3 decimales")]
    public void UnIvaImpresoIncompletoOConMasDeDosDecimalesEsUnError(string caso)
    {
        var (ivaImpreso, mensaje) = IvaImpresoInvalido[caso];

        Assert.Equal(mensaje, ErrorDeForma(Factura() with { IvaImpreso = [ivaImpreso] }));
    }

    [Fact]
    public void UnaPercepcionSinImporteEsUnError()
    {
        Assert.Equal("Percepción 1: falta el importe.", ErrorDeForma(Factura() with { Percepciones = [new PercepcionDeLaFactura("iibb")] }));
        Assert.Equal("Percepción 1: falta el importe.", ErrorDeForma(Factura() with { Percepciones = [null] }));
    }

    [Fact]
    public void UnaPercepcionSinTipoLlegaAlServicioConElTipoVacio()
    {
        var percepcion = Assert.Single(Armar(Factura() with { Percepciones = [new PercepcionDeLaFactura(Importe: 10m)] }).Percepciones!);

        Assert.Equal(new PercepcionSolicitada(string.Empty, 0m, 0m, 10m), percepcion);
    }

    [Theory]
    [InlineData("numero_comprobante_invalido", "El número de la factura no es válido: puntoVentaFactura va de 1 a 99999")]
    [InlineData("no_encontrado", "El proveedor sale de buscar_proveedor y el punto de venta de contexto_de_compras.")]
    [InlineData("referencia_invalida", "Las alícuotas de IVA salen de contexto_de_compras y los artículos de buscar_articulos.")]
    [InlineData("tipo_de_compra_invalido", "Usá uno de los tipos de comprobante de compra de contexto_de_compras.")]
    [InlineData("discrimina_iva_incompatible", "En una factura no mandes discriminaIva")]
    [InlineData("precios_incluyen_iva_sin_discriminar", "preciosIncluyenIva solo vale en un comprobante que discrimina IVA.")]
    [InlineData("iva_impreso_sin_discriminar", "así que no lleva ivaImpreso.")]
    [InlineData("iva_impreso_duplicado", "Mandá un solo importe por alícuota en ivaImpreso.")]
    [InlineData("iva_impreso_alicuota_desconocida", "Cada idAlicuotaIva de ivaImpreso tiene que ser el de alguna línea.")]
    [InlineData("iva_impreso_en_alicuota_sin_iva", "Sacala de ivaImpreso.")]
    [InlineData("iva_impreso_fuera_de_tolerancia", "Las líneas no explican el IVA que imprime la factura")]
    [InlineData("percepciones_sin_libro_iva", "no lleva percepciones")]
    [InlineData("percepcion_duplicada", "sumá sus importes en una y avisale al usuario.")]
    [InlineData("percepcion_iva_sin_discriminar", "La percepción de IVA solo va en un comprobante que discrimina IVA.")]
    [InlineData("percepcion_tipo_invalido", "Es el tipo de cada percepción.")]
    [InlineData("percepcion_importes_invalidos", "El importe y la baseImponible de una percepción no pueden ser negativos.")]
    [InlineData("percepcion_alicuota_invalida", "La alicuota de una percepción va de 0 a 100")]
    [InlineData("percepcion_decimales_invalidos", "importe y baseImponible admiten hasta 2 decimales y alicuota hasta 3.")]
    [InlineData("concepto_con_bultos", "Una línea de concepto no lleva bultos ni unidadesPorBulto")]
    [InlineData("concepto_con_lote", "Una línea de concepto no lleva codigoLote ni fechaVencimiento.")]
    [InlineData("lote_vencido_en_recepcion", "Preguntale al usuario qué hacer con esa línea.")]
    [InlineData("codigo_proveedor_muy_largo", "codigoProveedor admite hasta 50 caracteres.")]
    public void CadaErrorConocidoDelServicioDiceQueCorregirEnLosArgumentosDeLaHerramienta(string codigo, string indicacion)
    {
        var texto = TextosDelBorradorDeCompra.Traducir(new ErrorDominio(codigo, "Mensaje del servicio.", 400));

        Assert.StartsWith("No se creó el borrador. ", texto, StringComparison.Ordinal);
        Assert.Contains(indicacion, texto, StringComparison.Ordinal);
    }

    [Fact]
    public void UnErrorSinTraduccionConservaElMensajeDelServicio() =>
        Assert.Equal(
            "No se creó el borrador. Un motivo que la herramienta no conoce.",
            TextosDelBorradorDeCompra.Traducir(new ErrorDominio("codigo_nuevo", "Un motivo que la herramienta no conoce.", 400)));

    [Fact]
    public void LasComprasDuplicadasSeListanConSuEstadoOrigenFechaNumeroYLink()
    {
        var texto = TextosDelBorradorDeCompra.DescribirDuplicadas(
            [
                new CompraDuplicada(10, EstadoCompra.Borrador, "00003-00012345", null, OrigenCompra.Asistente),
                new CompraDuplicada(11, EstadoCompra.Confirmada, "3-12345", new DateOnly(2026, 10, 1), OrigenCompra.Manual)
            ],
            id => $"https://ways.test/admin/compras/{id}");

        var lineas = texto.Split(Environment.NewLine);
        Assert.Equal(4, lineas.Length);
        Assert.Equal("- 10 | borrador | asistente | sin fecha | 00003-00012345 | https://ways.test/admin/compras/10", lineas[1]);
        Assert.Equal("- 11 | confirmada | manual | 2026-10-01 | 3-12345 | https://ways.test/admin/compras/11", lineas[2]);
    }

    [Fact]
    public void UnTotalImpresoDistintoDelCalculadoMuestraLaDiferenciaYLaAdvierte()
    {
        var lineas = TextosDelBorradorDeCompra.DescribirCreado(Compra(), "Factura A de compra", "link", totalImpreso: 1210.02m)
            .Split(Environment.NewLine);

        Assert.Contains("Total impreso: 1210.02 | Diferencia (calculado - impreso): -0.02", lineas);
        Assert.Equal(
            "- El total calculado no coincide con el impreso: revisá contra la factura cantidades, costos unitarios, " +
            "bonificaciones, alícuotas y percepciones, o avisale al usuario antes de que lo confirme.",
            lineas[^1]);
    }

    [Fact]
    public void ConElTotalImpresoIgualAlCalculadoYElIvaCalculadoNoHayAdvertencias()
    {
        var lineas = TextosDelBorradorDeCompra.DescribirCreado(Compra(), "Factura A de compra", "link", totalImpreso: 1210m)
            .Split(Environment.NewLine);

        Assert.Equal("Total impreso: 1210.00 | Diferencia (calculado - impreso): 0.00", lineas[^1]);
        Assert.DoesNotContain("Advertencias:", lineas);
    }

    [Fact]
    public void ConPreciosConIvaElIvaCalculadoSeExtraeDelFinalYUnImpresoDistintoSeAdvierte()
    {
        // 10 × 121 final al 21 %: el calculado es 210.00; el guardado (impreso) es 210.30 y el neto, el resto.
        var compra = Compra() with
        {
            PreciosIncluyenIva = true,
            Items = [new ItemDeCompra(1, null, "Flete", 10m, null, null, 121m, 0m, 5, 21m, 1210m, false, null, null, null, null, null)],
            Subtotal = 1210m,
            IvaTotal = 210.30m,
            Total = 1210m,
            Alicuotas = [new AlicuotaDeCompra(5, 21m, 999.70m, 210.30m)]
        };

        var lineas = TextosDelBorradorDeCompra.DescribirCreado(compra, "Factura A de compra", "link", totalImpreso: null)
            .Split(Environment.NewLine);

        Assert.Equal("- Alícuota 21 %: quedó el IVA impreso (210.30), que difiere del calculado (210.00) en 0.30.", lineas[^1]);
    }

    [Fact]
    public void SinDiscriminarIvaElTextoDiceQueElIvaNoEstaDiscriminado()
    {
        var compra = Compra() with { DiscriminaIva = false, IvaTotal = null, Total = 1000m, Alicuotas = [] };

        var lineas = TextosDelBorradorDeCompra.DescribirCreado(compra, "Factura B de compra", "link", totalImpreso: null)
            .Split(Environment.NewLine);

        Assert.Equal("Subtotal: 1000.00 | Descuentos: 0.00 | IVA: no discriminado | Percepciones: 0.00 | Total: 1000.00", lineas[^1]);
    }

    /// <summary>Una factura A ya guardada: una línea de 10 × 100 al 21 % con el IVA calculado.</summary>
    private static CompraDetalle Compra() => new(
        Id: 5,
        IdProveedor: 7,
        IdTipoComprobante: 8,
        IdPuntoVenta: 9,
        NumeroExterno: "00003-00012345",
        FechaComprobante: new DateOnly(2026, 10, 15),
        FechaRecepcion: null,
        Subtotal: 1000m,
        DescuentoTotal: 0m,
        IvaTotal: 210m,
        Total: 1210m,
        Observaciones: null,
        Estado: EstadoCompra.Borrador,
        Items: [new ItemDeCompra(1, null, "Flete", 10m, null, null, 100m, 0m, 5, 21m, 1000m, false, null, null, null, null, null)],
        IdOrdenCompra: null,
        DiscriminaIva: true,
        Alicuotas: [new AlicuotaDeCompra(5, 21m, 1000m, 210m)],
        PreciosIncluyenIva: false,
        Percepciones: [],
        Pagado: 0m,
        SaldoPendiente: 0m,
        Origen: OrigenCompra.Asistente);
}
