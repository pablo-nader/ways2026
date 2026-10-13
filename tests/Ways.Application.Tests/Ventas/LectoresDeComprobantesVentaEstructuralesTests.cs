using System.Text.RegularExpressions;
using Ways.Application.Tests.Infraestructura;

namespace Ways.Application.Tests.Ventas;

/// <summary>
/// Facturación por medio de pago, T3: un comprobante fiscal espejo (<c>id_venta_origen</c>) es la factura de una
/// venta que ya está contada como ticket, así que todo lector que AGREGUE o LISTE ventas tiene que excluirlo con el
/// predicado compartido <c>SinEspejosFiscales</c> (o, en SQL crudo, <c>id_venta_origen IS NULL</c>).
///
/// Qué es: un barrido de TEXTO sobre <c>src/</c>. Para cada sentencia que menciona <c>ComprobantesVenta</c>,
/// <c>ItemsComprobanteVenta</c> o <c>PagosComprobante</c> (también como <c>Set&lt;ComprobanteVenta&gt;()</c> y sus
/// equivalentes de items y pagos) o, en SQL crudo, <c>comprobantes_venta</c>, <c>items_comprobante_venta</c> o
/// <c>pagos_comprobante</c>, exige una de dos cosas: que la sentencia contenga el predicado, o que la línea esté en
/// <see cref="LectoresPermitidos"/> con su justificación y la cantidad exacta de apariciones (más o menos falla, así
/// que un lector nuevo con el mismo texto que uno permitido, o una aparición quitada, se notan). Una entrada que ya no
/// coincide con ninguna lectura también falla, para que la lista no acumule fantasmas. Las pruebas de autoverificación
/// alimentan la clasificación con muestras en memoria (una lectura sin protección y una protegida) para que una
/// expresión regular rota no pase en silencio.
///
/// Qué NO ve: lectores alcanzados por navegaciones (<c>Include</c>, colecciones de otra entidad) o por métodos
/// auxiliares que devuelven <c>IQueryable</c> y se consumen en otra sentencia; la carpeta
/// <c>Ways.Infrastructure/Persistencia/</c> (configuración del modelo y migraciones, excluida a propósito); ni si el
/// predicado está en la cláusula correcta de una sentencia que lo nombra. Eso último lo cubren los tests de
/// integración de cada reporte (<c>FacturacionEspejoNoDobleConteoTests</c>), con los que este barrido se complementa.
/// </summary>
public class LectoresDeComprobantesVentaEstructuralesTests
{
    private static readonly string[] MarcasDeProteccion =
    [
        "SinEspejosFiscales",
        "NoEsEspejoFiscal",
        "id_venta_origen IS NULL"
    ];

    /// <summary>Llamada (no declaración) al ayudante privado de <c>ServicioDeReportesDeVentas</c> que ya aplica el
    /// predicado; su propia sentencia se verifica como cualquier otra lectura.</summary>
    private static readonly Regex LlamadaAlAyudanteDelPeriodo = new(
        @"\bawait\s+ComprobantesVentaDelPeriodo\(", RegexOptions.Compiled);

    private const string PorTurno =
        "Filtra por turno de caja: un espejo no tiene turno (ck_comprobantes_venta_espejo_sin_turno).";

    private const string PorId =
        "Lectura de UN comprobante por id (o de sus detalles): no agrega ni lista ventas.";

    private const string PorClaveUnica =
        "Búsqueda por la clave única (punto de venta, tipo, número) de un ticket; un espejo es de un tipo fiscal distinto.";

    private const string LibroIva =
        "Libro IVA ventas: tiene que ver las facturas, incluidos los espejos.";

    private const string PorCuentaCorriente =
        "Parte de movimientos de cuenta corriente: un espejo no escribe cuenta corriente.";

    /// <summary>Lecturas que NO excluyen espejos, por archivo y línea (recortada), con el motivo. Hay dos clases:
    /// las que no deben excluirlos porque necesitan ver la factura, y las que ya quedan fuera por otra vía.</summary>
    private static readonly IReadOnlyDictionary<(string Archivo, string Linea), (int Cantidad, string Motivo)> LectoresPermitidos =
        new Dictionary<(string, string), (int, string)>
        {
            [("Ways.Application/Caja/LectorDeContenidoDeResumen.cs",
                "var comprobantesDelTurno = db.ComprobantesVenta")] =
                (1, PorTurno),
            [("Ways.Application/Caja/LectorDeContenidoDeResumen.cs",
                "var totalesPorArea = await db.ItemsComprobanteVenta")] =
                (1, PorTurno),
            [("Ways.Application/Caja/LectorDeContenidoDeResumen.cs",
                "db.ComprobantesVenta,")] =
                (1, PorTurno),
            [("Ways.Application/Caja/LectorDeLineasDelTurno.cs",
                "var crudos = await db.ComprobantesVenta")] =
                (1, PorTurno),
            [("Ways.Application/Caja/LectorDeMovimientosDelTurno.cs",
                "var pagosDelTurno = db.PagosComprobante")] =
                (1, PorTurno),
            [("Ways.Application/Caja/LectorDeMovimientosDelTurno.cs",
                "db.ComprobantesVenta,")] =
                (1, PorTurno),
            [("Ways.Application/Caja/LectorDeRendicionDeDispositivos.cs",
                "FROM comprobantes_venta cv")] =
                (1, "Cuenta los comprobantes de la serie de un dispositivo por (punto de venta, tipo de ticket, rango de números): un espejo es de un tipo fiscal y numera por la serie fiscal."),
            [("Ways.Application/CuentaCorriente/LectorDeConsumosReliquidables.cs",
                "join c in db.ComprobantesVenta on m.IdComprobanteVenta equals (int?)c.Id")] =
                (1, PorCuentaCorriente),
            [("Ways.Application/CuentaCorriente/LectorDeConsumosReliquidables.cs",
                "var items = await db.ItemsComprobanteVenta")] =
                (1, PorId),
            [("Ways.Application/Fiscal/ProcesadorDeFacturacionesPorLote.cs",
                "FROM comprobantes_venta e")] =
                (1, "Busca los espejos pendientes de UN punto de venta: tiene que verlos, un espejo pendiente detiene su serie."),
            [("Ways.Application/Fiscal/ServicioDeDecisionDeFacturacion.cs",
                "\"FROM pagos_comprobante p \" +")] =
                (1, "Pagos de UNA venta, ya bloqueada por id y que no es un espejo: de ahí sale lo cobrado por medio."),
            [("Ways.Application/Fiscal/ServicioDeFacturacionDeVentas.cs",
                "var venta = await db.ComprobantesVenta.AsNoTracking()")] =
                (1, PorId),
            [("Ways.Application/Fiscal/ServicioDeFacturacionDeVentas.cs",
                "var espejo = await db.ComprobantesVenta.AsNoTracking()")] =
                (1, "Busca el espejo de UNA venta por id_venta_origen: tiene que verlo para saber si la venta ya está facturada."),
            [("Ways.Application/Fiscal/ServicioDeFacturacionDeVentas.cs",
                "var itemsDeLaVenta = await db.ItemsComprobanteVenta.AsNoTracking()")] =
                (1, PorId),
            [("Ways.Application/Fiscal/ServicioDeFacturacionDeVentas.cs",
                "var espejo = await db.ComprobantesVenta.AsNoTracking().FirstAsync(c => c.Id == idEspejo, ct);")] =
                (1, PorId),
            [("Ways.Application/Fiscal/ServicioDeFacturacionDeVentas.cs",
                "\"SELECT EXISTS (SELECT 1 FROM comprobantes_venta c \" +")] =
                (1, "Busca espejos pendientes de la serie fiscal: tiene que verlos, un espejo pendiente detiene la serie."),
            [("Ways.Application/Fiscal/ServicioDeFacturacionDeVentas.cs",
                "\"SELECT resultado_fiscal::text, fecha, numero, xmin::text FROM comprobantes_venta \" +")] =
                (1, PorId),
            [("Ways.Application/Fiscal/ServicioDeFacturacionDeVentas.cs",
                "\"SELECT xmin::text FROM comprobantes_venta WHERE id_comprobante_venta = $1\", ct, idEspejo);")] =
                (1, PorId),
            [("Ways.Application/Fiscal/ServicioDeFacturacionDeVentas.cs",
                "\"SELECT EXISTS (SELECT 1 FROM comprobantes_venta WHERE id_venta_origen = $1)\", ct, idVenta);")] =
                (1, "Pregunta si UNA venta ya tiene espejo, en cualquier estado: tiene que verlo, es justo lo que pregunta."),
            [("Ways.Application/Fiscal/ServicioDeFacturacionDeVentas.cs",
                "\"DELETE FROM items_comprobante_venta WHERE id_comprobante_venta = $1\", ct, idEspejo))")] =
                (1, "Borra los ítems de UN espejo fiscal liberado (rechazado por ARCA o con un número que ARCA no tiene), por id."),
            [("Ways.Application/Fiscal/ServicioDeFacturacionDeVentas.cs",
                "\"DELETE FROM comprobantes_venta WHERE id_comprobante_venta = $1\", ct, idEspejo);")] =
                (1, "Borra UN espejo fiscal liberado (rechazado por ARCA o con un número que ARCA no tiene), por id."),
            [("Ways.Application/Fiscal/ServicioDeFacturacionDeVentas.cs",
                "\"FROM pagos_comprobante p \" +")] =
                (1, "Pagos de UNA venta ya decidida, por id: de ahí sale lo cobrado por medio que la decisión llevó al acumulado."),
            [("Ways.Application/Fiscal/ServicioDeFacturacionFiscal.cs",
                "var actual = await db.ComprobantesVenta.AsNoTracking().FirstAsync(c => c.Id == idComprobante, ct);")] =
                (1, PorId),
            [("Ways.Application/Fiscal/ServicioDeFacturacionFiscal.cs",
                "var items = await db.ItemsComprobanteVenta.AsNoTracking()")] =
                (1, PorId),
            [("Ways.Application/Reportes/ServicioDeLibroIva.cs",
                "var consulta = db.ComprobantesVenta")] =
                (1, LibroIva),
            [("Ways.Application/Reportes/ServicioDeLibroIva.cs",
                "var items = await db.ItemsComprobanteVenta")] =
                (1, LibroIva),
            [("Ways.Application/Stock/ServicioDeLotes.cs",
                "? await db.ItemsComprobanteVenta")] =
                (1, PorId),
            [("Ways.Application/Ventas/ServicioDePresupuestos.cs",
                "var idComprobanteVenta = await db.ComprobantesVenta.AsNoTracking()")] =
                (1, "Busca la venta que convirtió un presupuesto: id_presupuesto_origen es único y lo lleva el ticket."),
            [("Ways.Application/Ventas/ServicioDeVentas.cs",
                "? await db.ComprobantesVenta.FirstOrDefaultAsync(c => c.Id == idAsociado, ct)")] =
                (1, PorId),
            [("Ways.Application/Ventas/ServicioDeVentas.cs",
                "&& await db.ComprobantesVenta.AnyAsync(")] =
                (1, PorClaveUnica),
            [("Ways.Application/Ventas/ServicioDeVentas.cs",
                "var vendidos = await db.ItemsComprobanteVenta")] =
                (2, PorId),
            [("Ways.Application/Ventas/ServicioDeVentas.cs",
                "var comprobante = await db.ComprobantesVenta.FirstOrDefaultAsync(")] =
                (1, PorClaveUnica),
            [("Ways.Application/Ventas/ServicioDeVentas.cs",
                "var items = await db.ItemsComprobanteVenta")] =
                (2, PorId),
            [("Ways.Application/Ventas/ServicioDeVentas.cs",
                "var pagos = await db.PagosComprobante")] =
                (2, PorId),
            [("Ways.Application/Ventas/ServicioDeVentas.cs",
                "var crudos = await db.ComprobantesVenta")] =
                (1, PorTurno),
            [("Ways.Application/Ventas/ServicioDeVentas.cs",
                "var pagos = await db.PagosComprobante.AsNoTracking()")] =
                (1, "Pagos de los ids de la página del listado, que ya salen sin espejos."),
            [("Ways.Application/Ventas/ServicioDeVentas.cs",
                "var comprobantePreLectura = await db.ComprobantesVenta.AsNoTracking().FirstOrDefaultAsync(c => c.Id == id, ct);")] =
                (1, PorId),
            [("Ways.Application/Ventas/ServicioDeVentas.cs",
                "var existe = await db.ComprobantesVenta.AnyAsync(c => c.Id == id, ct);")] =
                (1, PorId),
            [("Ways.Application/Ventas/ServicioDeVentas.cs",
                "await db.ComprobantesVenta.FirstOrDefaultAsync(c => c.Id == id, ct)")] =
                (1, PorId),
            [("Ways.Application/Ventas/ServicioDeVentas.cs",
                "\"SELECT EXISTS (SELECT 1 FROM comprobantes_venta \" +")] =
                (1, "Pregunta por el espejo de UNA venta por id_venta_origen: tiene que verlo para rechazar la anulación."),
            [("Ways.Application/Ventas/ServicioDeVentas.cs",
                "\"JOIN comprobantes_venta c ON c.id_turno_caja = t.id_turno_caja AND c.id_tenant = t.id_tenant \" +")] =
                (1, "Resuelve el turno de UN comprobante por id."),
        };

    /// <summary>Las lecturas que tienen que VER los espejos: si alguien les agrega el predicado, el libro IVA ventas
    /// pierde las facturas.</summary>
    private static readonly string[] LectoresQueDebenVerEspejos =
    [
        "Ways.Application/Reportes/ServicioDeLibroIva.cs"
    ];

    private sealed record Lectura(string Archivo, string Linea, string Sentencia);

    private static readonly Regex LecturaLinq = new(
        @"\b(?:ComprobantesVenta|ItemsComprobanteVenta|PagosComprobante)\b(?!\s*\.\s*(?:Add|AddRange)\()"
        + @"|\bSet\s*<\s*(?:ComprobanteVenta|ItemComprobanteVenta|PagoComprobante)\s*>\s*\(",
        RegexOptions.Compiled);

    private static readonly Regex LecturaSql = new(
        @"\b(?:FROM|JOIN)\s+(?:comprobantes_venta|items_comprobante_venta|pagos_comprobante)\b",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    private static string Normalizar(string ruta) => ruta.Replace('\\', '/');

    private static bool EsFuenteDeEsquema(string relativa) =>
        relativa.StartsWith("Ways.Infrastructure/Persistencia/", StringComparison.Ordinal)
        || relativa == "Ways.Application/Abstracciones/IWaysDbContext.cs"
        || relativa == "Ways.Domain/Ventas/ConsultasDeComprobantesVenta.cs";

    private static IReadOnlyList<Lectura> LeerLecturas()
    {
        var src = Path.Combine(RaizDelRepositorio.Resolver(), "src");
        var lecturas = new List<Lectura>();

        foreach (var archivo in Directory.EnumerateFiles(src, "*.cs", SearchOption.AllDirectories))
        {
            var relativa = Normalizar(Path.GetRelativePath(src, archivo));
            if (relativa.Contains("/obj/") || relativa.Contains("/bin/") || EsFuenteDeEsquema(relativa))
            {
                continue;
            }

            lecturas.AddRange(LeerLecturasDe(relativa, File.ReadAllText(archivo)));
        }

        return lecturas;
    }

    /// <summary>La clasificación en sí, sobre un texto dado: la usan el barrido real y las pruebas de autoverificación.</summary>
    private static IReadOnlyList<Lectura> LeerLecturasDe(string relativa, string texto)
    {
        var contenido = texto.Replace("\r\n", "\n");
        var lecturas = new List<Lectura>();
        foreach (var coincidencia in LecturaLinq.Matches(contenido).Concat(LecturaSql.Matches(contenido)))
        {
            var linea = LineaDe(contenido, coincidencia.Index);
            if (linea.StartsWith("//", StringComparison.Ordinal) || linea.StartsWith('*'))
            {
                continue;
            }

            lecturas.Add(new Lectura(relativa, linea, SentenciaDe(contenido, coincidencia.Index)));
        }

        return lecturas;
    }

    private static string LineaDe(string contenido, int indice)
    {
        var inicio = contenido.LastIndexOf('\n', Math.Max(indice - 1, 0)) + 1;
        var fin = contenido.IndexOf('\n', indice);
        return contenido[inicio..(fin < 0 ? contenido.Length : fin)].Trim();
    }

    /// <summary>Del renglón siguiente al último que cierra una sentencia o un bloque (termina en <c>;</c> o es solo
    /// <c>{</c>/<c>}</c>) hasta el próximo <c>;</c>. Se mira por renglones y no por llaves sueltas porque los tipos
    /// anónimos de una consulta (<c>new { … }</c>) tienen llaves en medio de la sentencia.</summary>
    private static string SentenciaDe(string contenido, int indice)
    {
        var renglones = contenido[..indice].Split('\n');
        var inicio = indice - renglones[^1].Length;
        for (var i = renglones.Length - 2; i >= 0; i--)
        {
            var recortado = renglones[i].Trim();
            if (recortado.EndsWith(';') || recortado is "{" or "}")
            {
                break;
            }

            inicio -= renglones[i].Length + 1;
        }

        var fin = contenido.IndexOf(';', indice);
        var sentencia = contenido[inicio..(fin < 0 ? contenido.Length : fin)];

        // Un comentario que nombra el predicado no es el predicado.
        return string.Join('\n', sentencia.Split('\n').Where(r => !r.TrimStart().StartsWith("//", StringComparison.Ordinal)));
    }

    private static bool Protegida(Lectura lectura) =>
        MarcasDeProteccion.Any(marca => lectura.Sentencia.Contains(marca, StringComparison.Ordinal))
        || LlamadaAlAyudanteDelPeriodo.IsMatch(lectura.Sentencia);

    [Fact]
    public void ElBarridoEncuentraLectoresDeComprobantesVenta()
    {
        var lecturas = LeerLecturas();

        Assert.Contains(lecturas, l => l.Archivo == "Ways.Application/Reportes/ServicioDeReportesDeVentas.cs");
        Assert.Contains(lecturas, l => l.Archivo == "Ways.Application/Reportes/LectorDeSerieTemporal.cs");
        Assert.Contains(lecturas, l => l.Archivo == "Ways.Application/Reportes/ServicioDeLibroIva.cs");
    }

    [Fact]
    public void TodoLectorExcluyeLosEspejosFiscalesOEstaJustificado()
    {
        var sinPredicadoNiJustificacion = LeerLecturas()
            .Where(l => !Protegida(l))
            .Where(l => !LectoresPermitidos.ContainsKey((l.Archivo, l.Linea)))
            .Select(l => $"{l.Archivo}: {l.Linea}\n    sentencia: {l.Sentencia.Trim()}")
            .Distinct()
            .ToList();

        Assert.True(
            sinPredicadoNiJustificacion.Count == 0,
            "Estas lecturas de comprobantes de venta no excluyen los espejos fiscales (SinEspejosFiscales) ni están " +
            "justificadas en LectoresPermitidos:\n" + string.Join("\n", sinPredicadoNiJustificacion));
    }

    [Fact]
    public void CadaEntradaPermitidaCoincideConLaCantidadExactaDeLecturasSinProteccion()
    {
        var lecturas = LeerLecturas();

        var desparejas = LectoresPermitidos
            .Select(e => (e.Key, Esperadas: e.Value.Cantidad, Reales: lecturas.Count(
                l => l.Archivo == e.Key.Archivo && l.Linea == e.Key.Linea && !Protegida(l))))
            .Where(x => x.Reales != x.Esperadas)
            .Select(x => $"{x.Key.Archivo}: {x.Key.Linea} (esperadas {x.Esperadas}, reales {x.Reales})")
            .ToList();

        Assert.True(
            desparejas.Count == 0,
            "La cantidad de lecturas sin protección no coincide con la de LectoresPermitidos (un lector nuevo con el " +
            "mismo texto, o una aparición quitada):\n" + string.Join("\n", desparejas));
    }

    [Fact]
    public void NingunaEntradaDeLaListaDePermitidosQuedaHuerfana()
    {
        var lecturas = LeerLecturas();

        var huerfanas = LectoresPermitidos.Keys
            .Where(clave => !lecturas.Any(l => l.Archivo == clave.Archivo && l.Linea == clave.Linea))
            .Select(clave => $"{clave.Archivo}: {clave.Linea}")
            .ToList();

        Assert.True(huerfanas.Count == 0, "Entradas de LectoresPermitidos sin lectura que las respalde:\n" + string.Join("\n", huerfanas));
    }

    [Fact]
    public void LaListaDePermitidosNoExcusaLecturasQueYaExcluyenEspejos()
    {
        var lecturas = LeerLecturas();

        var redundantes = LectoresPermitidos.Keys
            .Where(clave => lecturas
                .Where(l => l.Archivo == clave.Archivo && l.Linea == clave.Linea)
                .All(Protegida))
            .Select(clave => $"{clave.Archivo}: {clave.Linea}")
            .ToList();

        Assert.True(redundantes.Count == 0, "Entradas de LectoresPermitidos que ya usan el predicado:\n" + string.Join("\n", redundantes));
    }

    [Fact]
    public void ElLibroIvaDeVentasNoExcluyeLosEspejosPorqueDebeVerLasFacturas()
    {
        var lecturas = LeerLecturas();

        foreach (var archivo in LectoresQueDebenVerEspejos)
        {
            var delArchivo = lecturas.Where(l => l.Archivo == archivo).ToList();

            Assert.NotEmpty(delArchivo);
            Assert.All(delArchivo, l => Assert.False(Protegida(l), $"{archivo} no debe excluir espejos: {l.Linea}"));
            Assert.All(delArchivo, l => Assert.True(
                LectoresPermitidos.ContainsKey((l.Archivo, l.Linea)),
                $"{archivo} lee comprobantes y debe estar justificado en LectoresPermitidos: {l.Linea}"));
        }
    }

    [Fact]
    public void ElPredicadoDeEspejoVivePorUnaSolaDefinicion()
    {
        var src = Path.Combine(RaizDelRepositorio.Resolver(), "src");
        var definiciones = new List<string>();

        foreach (var archivo in Directory.EnumerateFiles(src, "*.cs", SearchOption.AllDirectories))
        {
            var relativa = Normalizar(Path.GetRelativePath(src, archivo));
            if (relativa.Contains("/obj/") || relativa.Contains("/bin/")
                || relativa.StartsWith("Ways.Infrastructure/Persistencia/", StringComparison.Ordinal)
                || relativa == "Ways.Domain/Ventas/ConsultasDeComprobantesVenta.cs")
            {
                continue;
            }

            if (DefineElPredicado(File.ReadAllText(archivo)))
            {
                definiciones.Add(relativa);
            }
        }

        Assert.True(
            definiciones.Count == 0,
            "El predicado 'no es un espejo fiscal' se define una sola vez (ConsultasDeComprobantesVenta); copias en:\n" +
            string.Join("\n", definiciones));
    }

    private static readonly Regex DefinicionDelPredicado = new(
        @"\bIdVentaOrigen\s*(?:==|!=|is)\s*(?:not\s+)?null\b|\bIdVentaOrigen\s*\.\s*HasValue\b",
        RegexOptions.Compiled);

    private static bool DefineElPredicado(string texto) => DefinicionDelPredicado.IsMatch(texto);

    private static string Muestra(string sentencia) =>
        $"class C\n{{\n    void M()\n    {{\n        {sentencia}\n    }}\n}}\n";

    // ---- Autoverificación: si una expresión regular se rompe, estas pruebas se ponen rojas, no el barrido en silencio ----

    [Theory]
    [InlineData("var x = await db.ComprobantesVenta.Where(c => c.Total > 0).ToListAsync(ct);")]
    [InlineData("var x = await db.ItemsComprobanteVenta.ToListAsync(ct);")]
    [InlineData("var x = await db.PagosComprobante.ToListAsync(ct);")]
    [InlineData("var x = await db.Set<ComprobanteVenta>().ToListAsync(ct);")]
    [InlineData("var x = await db.Set<ItemComprobanteVenta>().ToListAsync(ct);")]
    [InlineData("var x = await db.Set<PagoComprobante>().ToListAsync(ct);")]
    [InlineData("var f = await conexion.QueryAsync(\"SELECT * FROM comprobantes_venta WHERE total > 0\");")]
    [InlineData("var f = await conexion.QueryAsync(\"SELECT * FROM x JOIN pagos_comprobante p ON p.id = 1\");")]
    public void ElBarridoClasificaUnLectorSinProteccionComoDesprotegido(string sentencia)
    {
        var lectura = Assert.Single(LeerLecturasDe("Ways.Application/Muestra.cs", Muestra(sentencia)));

        Assert.False(Protegida(lectura));
    }

    [Theory]
    [InlineData("var x = await db.ComprobantesVenta.SinEspejosFiscales().ToListAsync(ct);")]
    [InlineData("var x = await db.Set<ComprobanteVenta>().Where(ConsultasDeComprobantesVenta.NoEsEspejoFiscal).ToListAsync(ct);")]
    [InlineData("var f = await conexion.QueryAsync(\"SELECT * FROM comprobantes_venta WHERE id_venta_origen IS NULL\");")]
    public void ElBarridoClasificaUnLectorConElPredicadoComoProtegido(string sentencia)
    {
        var lectura = Assert.Single(LeerLecturasDe("Ways.Application/Muestra.cs", Muestra(sentencia)));

        Assert.True(Protegida(lectura));
    }

    [Fact]
    public void ElBarridoIgnoraEscriturasComentariosYUnPredicadoSoloComentado()
    {
        const string texto = "class C\n{\n    void M()\n    {\n" +
            "        db.ComprobantesVenta.Add(nuevo);\n" +
            "        // var x = await db.ComprobantesVenta.ToListAsync(ct);\n" +
            "        var y = await db.ComprobantesVenta\n" +
            "            // .SinEspejosFiscales()\n" +
            "            .ToListAsync(ct);\n" +
            "    }\n}\n";

        var lectura = Assert.Single(LeerLecturasDe("Ways.Application/Muestra.cs", texto));

        Assert.Equal("var y = await db.ComprobantesVenta", lectura.Linea);
        Assert.False(Protegida(lectura));
    }

    [Theory]
    [InlineData("c => c.IdVentaOrigen == null", true)]
    [InlineData("c.IdVentaOrigen != null", true)]
    [InlineData("c.IdVentaOrigen is null", true)]
    [InlineData("c.IdVentaOrigen is not null", true)]
    [InlineData("c.IdVentaOrigen.HasValue", true)]
    [InlineData("!c.IdVentaOrigen.HasValue", true)]
    [InlineData("comprobante.IdVentaOrigen = idVenta;", false)]
    [InlineData("var id = c.IdVentaOrigen;", false)]
    public void LaBusquedaDeDefinicionesDelPredicadoReconoceSusFormas(string codigo, bool esperado)
    {
        Assert.Equal(esperado, DefineElPredicado(codigo));
    }
}
