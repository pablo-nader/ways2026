using System.Text.RegularExpressions;
using Ways.Application.Tests.Infraestructura;

namespace Ways.Application.Tests.Ventas;

/// <summary>
/// Facturación por medio de pago, T3: un comprobante fiscal espejo (<c>id_venta_origen</c>) es la factura de una
/// venta que ya está contada como ticket, así que todo lector que AGREGUE o LISTE ventas tiene que excluirlo con el
/// predicado compartido <c>SinEspejosFiscales</c> (o, en SQL crudo, <c>id_venta_origen IS NULL</c>).
///
/// El guard recorre <c>src/</c> y, para cada lectura de <c>ComprobantesVenta</c>, <c>ItemsComprobanteVenta</c> o
/// <c>PagosComprobante</c> (LINQ) y de <c>comprobantes_venta</c>, <c>items_comprobante_venta</c> o
/// <c>pagos_comprobante</c> (SQL crudo), exige una de dos cosas: que la sentencia contenga el predicado, o que la
/// línea esté en <see cref="LectoresPermitidos"/> con su justificación. Un lector nuevo que no hace ninguna de las
/// dos pone esta prueba en rojo. Una entrada de la lista que ya no coincide con ninguna lectura también la pone en
/// rojo, para que la lista no acumule fantasmas.
///
/// Alcance honesto: es un barrido de texto sobre sentencias (del renglón posterior al último que cierra una sentencia o un
/// bloque hasta el siguiente <c>;</c>), no un análisis semántico. Detecta el lector que olvida el predicado; no demuestra que el
/// predicado esté en la cláusula correcta (eso lo prueban los tests de integración de cada reporte).
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
    private static readonly IReadOnlyDictionary<(string Archivo, string Linea), string> LectoresPermitidos =
        new Dictionary<(string, string), string>
        {
            [("Ways.Application/Caja/LectorDeContenidoDeResumen.cs",
                "var comprobantesDelTurno = db.ComprobantesVenta")] =
                PorTurno,
            [("Ways.Application/Caja/LectorDeContenidoDeResumen.cs",
                "var totalesPorArea = await db.ItemsComprobanteVenta")] =
                PorTurno,
            [("Ways.Application/Caja/LectorDeContenidoDeResumen.cs",
                "db.ComprobantesVenta,")] =
                PorTurno,
            [("Ways.Application/Caja/LectorDeLineasDelTurno.cs",
                "var crudos = await db.ComprobantesVenta")] =
                PorTurno,
            [("Ways.Application/Caja/LectorDeMovimientosDelTurno.cs",
                "var pagosDelTurno = db.PagosComprobante")] =
                PorTurno,
            [("Ways.Application/Caja/LectorDeMovimientosDelTurno.cs",
                "db.ComprobantesVenta,")] =
                PorTurno,
            [("Ways.Application/Caja/LectorDeRendicionDeDispositivos.cs",
                "FROM comprobantes_venta cv")] =
                "Cuenta los comprobantes de la serie de un dispositivo por (punto de venta, tipo de ticket, rango de números): un espejo es de un tipo fiscal y numera por la serie fiscal.",
            [("Ways.Application/CuentaCorriente/LectorDeConsumosReliquidables.cs",
                "join c in db.ComprobantesVenta on m.IdComprobanteVenta equals (int?)c.Id")] =
                PorCuentaCorriente,
            [("Ways.Application/CuentaCorriente/LectorDeConsumosReliquidables.cs",
                "var items = await db.ItemsComprobanteVenta")] =
                PorId,
            [("Ways.Application/Fiscal/ServicioDeFacturacionFiscal.cs",
                "var comprobante = await db.ComprobantesVenta")] =
                PorId,
            [("Ways.Application/Fiscal/ServicioDeFacturacionFiscal.cs",
                "var actual = await db.ComprobantesVenta.AsNoTracking().FirstAsync(c => c.Id == idComprobante, ct);")] =
                PorId,
            [("Ways.Application/Fiscal/ServicioDeFacturacionFiscal.cs",
                "var items = await db.ItemsComprobanteVenta.AsNoTracking()")] =
                PorId,
            [("Ways.Application/Reportes/ServicioDeLibroIva.cs",
                "var consulta = db.ComprobantesVenta")] =
                LibroIva,
            [("Ways.Application/Reportes/ServicioDeLibroIva.cs",
                "var items = await db.ItemsComprobanteVenta")] =
                LibroIva,
            [("Ways.Application/Stock/ServicioDeLotes.cs",
                "? await db.ItemsComprobanteVenta")] =
                PorId,
            [("Ways.Application/Ventas/ServicioDePresupuestos.cs",
                "var idComprobanteVenta = await db.ComprobantesVenta.AsNoTracking()")] =
                "Busca la venta que convirtió un presupuesto: id_presupuesto_origen es único y lo lleva el ticket.",
            [("Ways.Application/Ventas/ServicioDeVentas.cs",
                "? await db.ComprobantesVenta.FirstOrDefaultAsync(c => c.Id == idAsociado, ct)")] =
                PorId,
            [("Ways.Application/Ventas/ServicioDeVentas.cs",
                "&& await db.ComprobantesVenta.AnyAsync(")] =
                PorClaveUnica,
            [("Ways.Application/Ventas/ServicioDeVentas.cs",
                "var vendidos = await db.ItemsComprobanteVenta")] =
                PorId,
            [("Ways.Application/Ventas/ServicioDeVentas.cs",
                "var comprobante = await db.ComprobantesVenta.FirstOrDefaultAsync(")] =
                PorClaveUnica,
            [("Ways.Application/Ventas/ServicioDeVentas.cs",
                "var items = await db.ItemsComprobanteVenta")] =
                PorId,
            [("Ways.Application/Ventas/ServicioDeVentas.cs",
                "var pagos = await db.PagosComprobante")] =
                PorId,
            [("Ways.Application/Ventas/ServicioDeVentas.cs",
                "var crudos = await db.ComprobantesVenta")] =
                PorTurno,
            [("Ways.Application/Ventas/ServicioDeVentas.cs",
                "var pagos = await db.PagosComprobante.AsNoTracking()")] =
                "Pagos de los ids de la página del listado, que ya salen sin espejos.",
            [("Ways.Application/Ventas/ServicioDeVentas.cs",
                "var comprobantePreLectura = await db.ComprobantesVenta.AsNoTracking().FirstOrDefaultAsync(c => c.Id == id, ct);")] =
                PorId,
            [("Ways.Application/Ventas/ServicioDeVentas.cs",
                "var existe = await db.ComprobantesVenta.AnyAsync(c => c.Id == id, ct);")] =
                PorId,
            [("Ways.Application/Ventas/ServicioDeVentas.cs",
                "await db.ComprobantesVenta.FirstOrDefaultAsync(c => c.Id == id, ct)")] =
                PorId,
            [("Ways.Application/Ventas/ServicioDeVentas.cs",
                "\"JOIN comprobantes_venta c ON c.id_turno_caja = t.id_turno_caja AND c.id_tenant = t.id_tenant \" +")] =
                "Resuelve el turno de UN comprobante por id.",
        };

    /// <summary>Las lecturas que tienen que VER los espejos: si alguien les agrega el predicado, el libro IVA ventas
    /// pierde las facturas.</summary>
    private static readonly string[] LectoresQueDebenVerEspejos =
    [
        "Ways.Application/Reportes/ServicioDeLibroIva.cs"
    ];

    private sealed record Lectura(string Archivo, string Linea, string Sentencia);

    private static readonly Regex LecturaLinq = new(
        @"\b(?:ComprobantesVenta|ItemsComprobanteVenta|PagosComprobante)\b(?!\s*\.\s*(?:Add|AddRange)\()",
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

            var contenido = File.ReadAllText(archivo).Replace("\r\n", "\n");
            foreach (var coincidencia in LecturaLinq.Matches(contenido).Concat(LecturaSql.Matches(contenido)))
            {
                var linea = LineaDe(contenido, coincidencia.Index);
                if (linea.StartsWith("//", StringComparison.Ordinal) || linea.StartsWith('*'))
                {
                    continue;
                }

                lecturas.Add(new Lectura(relativa, linea, SentenciaDe(contenido, coincidencia.Index)));
            }
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

            var contenido = File.ReadAllText(archivo);
            if (Regex.IsMatch(contenido, @"\bIdVentaOrigen\s*(?:==|!=|is)\s*(?:not\s+)?null\b"))
            {
                definiciones.Add(relativa);
            }
        }

        Assert.True(
            definiciones.Count == 0,
            "El predicado 'no es un espejo fiscal' se define una sola vez (ConsultasDeComprobantesVenta); copias en:\n" +
            string.Join("\n", definiciones));
    }
}
