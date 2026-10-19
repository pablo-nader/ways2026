using System.ComponentModel;
using System.Net;
using System.Reflection;
using System.Text.Json;
using ModelContextProtocol.Server;
using Ways.Api.ConectorMcp;
using static Ways.IntegrationTests.ApoyoDeConectorMcp;

namespace Ways.IntegrationTests;

/// <summary>
/// El contrato publicado de todo el conector en un solo lugar: <c>tools/list</c> anuncia exactamente estas once
/// herramientas, con su título, sus cuatro anotaciones y sus argumentos. Una herramienta nueva, una que desaparece,
/// un argumento renombrado o una anotación cambiada rompen esta prueba hasta que la tabla diga lo mismo que el código.
/// </summary>
[Collection("Ways.IntegrationTests secuencial")]
public class ConectorMcpConjuntoDeHerramientasTests(WaysApiFixture fixture) : IClassFixture<WaysApiFixture>
{
    private readonly ApoyoDeConectorMcp _apoyo = new(fixture);

    private sealed record Esperada(
        string Nombre,
        string Titulo,
        bool SoloLectura,
        bool Destructiva,
        bool Idempotente,
        bool MundoAbierto,
        string[] Argumentos,
        string[] Requeridos);

    private static readonly string[] AlcanceDeVentas = ["desde", "empresa", "hasta", "periodo", "puntoVenta"];

    private static readonly Esperada[] Herramientas =
    [
        new(
            "articulo_info", "Ficha de artículo", true, false, true, false,
            ["articulo", "compras", "empresa"], ["articulo"]),
        new(
            "asociar_codigo_de_barras", "Asociar código de barras a un artículo", false, false, true, false,
            ["codigo", "confirmarCodigoNoEstandar", "idArticulo"], ["codigo", "idArticulo"]),
        new(
            "asociar_codigo_de_proveedor", "Asociar código de proveedor a un artículo", false, false, true, false,
            ["codigo", "idArticulo", "idProveedor"], ["codigo", "idArticulo", "idProveedor"]),
        new(
            "buscar_articulos", "Buscar artículos de una factura", true, false, true, false,
            ["idProveedor", "lineas"], ["idProveedor", "lineas"]),
        new("buscar_proveedor", "Buscar proveedor", true, false, true, false, ["cuit", "nombre"], []),
        new("contexto_de_compras", "Contexto de compras", true, false, true, false, [], []),
        new(
            "crear_borrador_de_compra", "Crear borrador de compra", false, false, false, false,
            [
                "discriminaIva", "fechaComprobante", "idProveedor", "idPuntoVenta", "idTipoComprobante", "ivaImpreso",
                "lineas", "numeroFactura", "numeroRemito", "observaciones", "percepciones", "preciosIncluyenIva",
                "puntoVentaFactura", "totalImpreso"
            ],
            ["fechaComprobante", "idProveedor", "idPuntoVenta", "idTipoComprobante", "lineas"]),
        new("quien_soy", "Quién soy", true, false, true, false, [], []),
        new(
            "ventas_agrupadas", "Ventas agrupadas", true, false, true, false,
            [.. AlcanceDeVentas.Append("agruparPor").Append("limite").Order(StringComparer.Ordinal)], ["agruparPor"]),
        new(
            "ventas_por_articulo", "Ventas por artículo", true, false, true, false,
            [
                .. AlcanceDeVentas
                    .Concat(
                        ["area", "articulo", "categoria", "grupo", "limite", "marca", "orden", "proveedor", "texto"])
                    .Order(StringComparer.Ordinal)
            ],
            []),
        new("ventas_resumen", "Resumen de ventas", true, false, true, false, AlcanceDeVentas, [])
    ];

    private static string[] Ordenados(IEnumerable<string> nombres) => [.. nombres.Order(StringComparer.Ordinal)];

    private static string[] Nombres(JsonElement esquema, string propiedad) =>
        !esquema.TryGetProperty(propiedad, out var valor)
            ? []
            : valor.ValueKind == JsonValueKind.Array
                ? Ordenados(valor.EnumerateArray().Select(r => r.GetString()!))
                : Ordenados(valor.EnumerateObject().Select(p => p.Name));

    private async Task<Dictionary<string, JsonElement>> ListarAsync(string nombre) =>
        (await ListarConInstruccionesAsync(nombre)).Herramientas;

    private async Task<(Dictionary<string, JsonElement> Herramientas, string Instrucciones)> ListarConInstruccionesAsync(
        string nombre)
    {
        var usuario = await _apoyo.SembrarUsuarioDeTenantAsync(nombre);
        await using var host = _apoyo.HostConConector(usuario.Mail);
        using var cliente = Cliente(host);
        var token = (await ObtenerTokensAsync(cliente, usuario.Mail)).GetProperty("access_token").GetString()!;

        var (estado, lista) = await LlamarMcpAsync(cliente, token, "tools/list");
        var (_, inicio) = await LlamarMcpAsync(cliente, token, "initialize", new
        {
            protocolVersion = "2025-11-25",
            capabilities = new { },
            clientInfo = new { name = "pruebas", version = "1.0" }
        });

        Assert.Equal(HttpStatusCode.OK, estado);
        return (
            lista!.Value.GetProperty("result").GetProperty("tools").EnumerateArray()
                .ToDictionary(h => h.GetProperty("name").GetString()!, h => h.Clone()),
            inicio!.Value.GetProperty("result").GetProperty("instructions").GetString()!);
    }

    [Fact]
    public async Task ToolsListAnunciaExactamenteLasOnceHerramientasConSuTituloSusAnotacionesYSusArgumentos()
    {
        var publicadas = await ListarAsync("mcp-conjunto-de-herramientas");

        Assert.Equal(Ordenados(Herramientas.Select(h => h.Nombre)), Ordenados(publicadas.Keys));
        foreach (var esperada in Herramientas)
        {
            Assert.Equal(Describir(esperada), Describir(Leida(esperada.Nombre, publicadas[esperada.Nombre])));
        }
    }

    private static Esperada Leida(string nombre, JsonElement herramienta)
    {
        var anotaciones = herramienta.GetProperty("annotations");
        var esquema = herramienta.GetProperty("inputSchema");
        return new Esperada(
            nombre,
            herramienta.GetProperty("title").GetString()!,
            anotaciones.GetProperty("readOnlyHint").GetBoolean(),
            anotaciones.GetProperty("destructiveHint").GetBoolean(),
            anotaciones.GetProperty("idempotentHint").GetBoolean(),
            anotaciones.GetProperty("openWorldHint").GetBoolean(),
            Nombres(esquema, "properties"),
            Nombres(esquema, "required"));
    }

    /// <summary>Una línea por herramienta: si algo difiere, el mensaje del assert dice qué y en cuál.</summary>
    private static string Describir(Esperada h) =>
        $"{h.Nombre} | {h.Titulo} | readOnly={h.SoloLectura} destructive={h.Destructiva} idempotent={h.Idempotente} " +
        $"openWorld={h.MundoAbierto} | argumentos: {string.Join(", ", h.Argumentos)} | " +
        $"requeridos: {string.Join(", ", h.Requeridos)}";

    /// <summary>Del lado del código: cada parámetro de un método <see cref="McpServerToolAttribute"/> que lleva
    /// <see cref="DescriptionAttribute"/> es un argumento y se publica con su nombre; los que no la llevan (el
    /// <c>log</c>, el <c>db</c>, los servicios, la autorización, el accesor, las urls y el token de cancelación) los
    /// inyecta el SDK y ninguno aparece en un <c>inputSchema</c>. Así un argumento nuevo sin descripción, o un
    /// servicio que el SDK no reconoce como tal, no pasa desapercibido.</summary>
    [Fact]
    public async Task CadaEsquemaPublicaSoloLosParametrosConDescripcionYNingunoDeInfraestructura()
    {
        var publicadas = await ListarAsync("mcp-conjunto-sin-infraestructura");
        var metodos = typeof(RegistroDelConectorMcp).Assembly.GetTypes()
            .Where(t => t.GetCustomAttribute<McpServerToolTypeAttribute>() is not null)
            .SelectMany(t => t.GetMethods(BindingFlags.Public | BindingFlags.Static))
            .Select(m => (Metodo: m, Atributo: m.GetCustomAttribute<McpServerToolAttribute>()))
            .Where(m => m.Atributo is not null)
            .ToList();

        Assert.Equal(Ordenados(publicadas.Keys), Ordenados(metodos.Select(m => m.Atributo!.Name!)));
        foreach (var (metodo, atributo) in metodos)
        {
            var argumentos = metodo.GetParameters()
                .Where(p => p.GetCustomAttribute<DescriptionAttribute>() is not null)
                .Select(p => p.Name!);

            Assert.Equal(
                Ordenados(argumentos), Nombres(publicadas[atributo!.Name!].GetProperty("inputSchema"), "properties"));
        }
    }

    /// <summary>Las instrucciones del servidor nombran cada herramienta que anuncia <c>tools/list</c>, y su frase de
    /// escritura nombra exactamente las que se anuncian sin solo lectura, en el orden en que la prosa las pone.</summary>
    [Fact]
    public async Task LasInstruccionesNombranCadaHerramientaYLaFraseDeEscrituraNombraLasQueNoSonDeSoloLectura()
    {
        var (publicadas, instrucciones) = await ListarConInstruccionesAsync("mcp-conjunto-instrucciones");

        Assert.All(publicadas.Keys, nombre => Assert.Matches($@"\b{nombre}\b", instrucciones));
        var deEscritura = publicadas
            .Where(h => !h.Value.GetProperty("annotations").GetProperty("readOnlyHint").GetBoolean())
            .Select(h => h.Key)
            .Order(StringComparer.Ordinal)
            .ToList();
        Assert.Equal(["asociar_codigo_de_barras", "asociar_codigo_de_proveedor", "crear_borrador_de_compra"], deEscritura);
        Assert.Contains(
            $"todas sus herramientas son de solo lectura salvo {deEscritura[2]}, {deEscritura[0]} y {deEscritura[1]}.",
            instrucciones,
            StringComparison.Ordinal);
    }
}
