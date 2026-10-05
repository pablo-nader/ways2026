using System.Collections.Concurrent;
using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.WebUtilities;
using Microsoft.Extensions.Logging;
using Ways.Domain.Organizacion;
using Ways.Domain.Usuarios;
using Ways.Infrastructure.Multitenancy;
using Ways.Infrastructure.Seguridad;

namespace Ways.IntegrationTests;

/// <summary>
/// Siembra y protocolo compartidos por las pruebas del conector MCP: un host derivado con el
/// conector encendido, usuarios de tenant, y los pasos del flujo OAuth (página de consentimiento,
/// código, canje, refresh) y de MCP tal como los hace Claude. Los valores de configuración se pasan
/// con <c>UseSetting</c> porque <c>Program.cs</c> lee <c>Mcp:*</c> antes de <c>Build()</c>, y esos
/// valores le llegan como argumentos de línea de comandos.
/// </summary>
internal sealed class ApoyoDeConectorMcp(WaysApiFixture fixture)
{
    public const string UrlPublica = "https://conector.ways.test";
    public const string RecursoMcp = UrlPublica + "/mcp";
    public const string Redireccion = "https://claude.ai/api/mcp/auth_callback";
    public const string IdCliente = "claude-ways";
    public const string Password = "una-contraseña-larga";
    public const string MailRoot = "test@test.com";
    public const string PasswordRoot = "root";
    public const string CampoAntiforgery = "__RequestVerificationToken";

    public sealed record UsuarioDeTenant(int IdTenant, string NombreTenant, int IdUsuario, string Mail);

    public sealed record Pkce(string Verificador, string Desafio);

    /// <param name="toleranciaDeReuso">Segundos de <c>Mcp:SegundosDeToleranciaDeReusoDeRefresh</c>;
    /// <c>null</c> deja la configuración sin ese valor (rige el default de OpenIddict).</param>
    public WebApplicationFactory<Program> HostConConector(
        string mailsHabilitados, int? toleranciaDeReuso = 0, Action<IWebHostBuilder>? configurarMas = null) =>
        fixture.WithWebHostBuilder(builder =>
        {
            builder.UseSetting("Mcp:Habilitado", "true");
            builder.UseSetting("Mcp:UrlPublica", UrlPublica);
            builder.UseSetting("Mcp:MailsHabilitados", mailsHabilitados);

            if (toleranciaDeReuso is { } segundos)
            {
                builder.UseSetting(
                    "Mcp:SegundosDeToleranciaDeReusoDeRefresh", segundos.ToString(CultureInfo.InvariantCulture));
            }

            configurarMas?.Invoke(builder);
        });

    /// <summary>Base <c>https</c>: con URL pública el conector trata sus rutas como https, y la cookie
    /// de antiforgery sale con <c>Secure</c>.</summary>
    public static HttpClient Cliente(WebApplicationFactory<Program> host) =>
        host.CreateClient(new WebApplicationFactoryClientOptions
        {
            BaseAddress = new Uri("https://localhost"),
            AllowAutoRedirect = false
        });

    /// <param name="delMismoTenantQue">Si se pasa, el usuario nuevo va en el tenant de ese usuario en
    /// lugar de uno nuevo.</param>
    public async Task<UsuarioDeTenant> SembrarUsuarioDeTenantAsync(string nombre, UsuarioDeTenant? delMismoTenantQue = null)
    {
        // El host base siembra los roles al arrancar; la fila de usuarios los referencia.
        using var _ = fixture.CreateClient();

        var unico = $"{nombre}-{Guid.NewGuid().ToString("N")[..8]}".ToLowerInvariant();
        var ahora = DateTimeOffset.UtcNow;
        var hasheador = new HasheadorPbkdf2();
        await using var db = fixture.CrearContextoDeAplicacion(TenantActualFijo.Plataforma);

        int idTenant;
        string nombreTenant;
        if (delMismoTenantQue is { } existente)
        {
            (idTenant, nombreTenant) = (existente.IdTenant, existente.NombreTenant);
        }
        else
        {
            var tenant = new Tenant { Nombre = $"Tenant {unico}", Estado = EstadoTenant.Activo, CreatedAt = ahora, UpdatedAt = ahora };
            db.Tenants.Add(tenant);
            await db.SaveChangesAsync();
            (idTenant, nombreTenant) = (tenant.Id, tenant.Nombre);
        }

        var usuario = new Usuario
        {
            IdTenant = idTenant,
            NombreUsuario = $"admin-{unico}",
            Mail = $"{unico}@ways.test",
            RolId = (int)RolConocido.Admin,
            PasswordHash = hasheador.Hashear(Password),
            PasswordAlgoritmo = hasheador.Algoritmo,
            PasswordActualizadoEl = ahora,
            CreatedAt = ahora,
            UpdatedAt = ahora
        };
        db.Usuarios.Add(usuario);
        await db.SaveChangesAsync();

        return new UsuarioDeTenant(idTenant, nombreTenant, usuario.Id, usuario.Mail);
    }

    public static Pkce NuevoPkce()
    {
        var verificador = WebEncoders.Base64UrlEncode(RandomNumberGenerator.GetBytes(32));
        var desafio = WebEncoders.Base64UrlEncode(SHA256.HashData(Encoding.ASCII.GetBytes(verificador)));
        return new Pkce(verificador, desafio);
    }

    /// <param name="cambios">Parámetros a reemplazar; un valor <c>null</c> saca el parámetro.</param>
    public static string UrlDeAutorizacion(Pkce pkce, IReadOnlyDictionary<string, string?>? cambios = null)
    {
        var parametros = new Dictionary<string, string?>
        {
            ["response_type"] = "code",
            ["client_id"] = IdCliente,
            ["redirect_uri"] = Redireccion,
            ["scope"] = "ways.mcp offline_access",
            ["state"] = "estado-de-prueba",
            ["code_challenge"] = pkce.Desafio,
            ["code_challenge_method"] = "S256",
            ["resource"] = RecursoMcp
        };

        foreach (var (clave, valor) in cambios ?? new Dictionary<string, string?>())
        {
            parametros[clave] = valor;
        }

        return QueryHelpers.AddQueryString(
            "/connect/authorize",
            parametros.Where(p => p.Value is not null).Select(p => new KeyValuePair<string, string?>(p.Key, p.Value)));
    }

    /// <summary>Abre la página de consentimiento y la envía con las credenciales dadas, como lo hace
    /// el navegador del usuario. Devuelve la respuesta del POST.</summary>
    public static async Task<HttpResponseMessage> AprobarAsync(
        HttpClient cliente, string urlDeAutorizacion, string mail, string password, bool conAntiforgery = true)
    {
        var pagina = await cliente.GetAsync(urlDeAutorizacion);
        Assert.Equal(HttpStatusCode.OK, pagina.StatusCode);

        var campos = CamposOcultos(await pagina.Content.ReadAsStringAsync());
        if (!conAntiforgery)
        {
            campos.RemoveAll(campo => campo.Key == CampoAntiforgery);
        }

        campos.Add(new("mail", mail));
        campos.Add(new("password", password));
        campos.Add(new("accion", "aprobar"));

        return await cliente.PostAsync("/connect/authorize", new FormUrlEncodedContent(campos));
    }

    public static List<KeyValuePair<string, string>> CamposOcultos(string html) =>
        [.. Regex.Matches(html, "<input type=\"hidden\" name=\"([^\"]*)\" value=\"([^\"]*)\">")
            .Select(m => new KeyValuePair<string, string>(
                WebUtility.HtmlDecode(m.Groups[1].Value), WebUtility.HtmlDecode(m.Groups[2].Value)))];

    /// <summary>El texto del aviso de error de la página, o <c>null</c> si no hay.</summary>
    public static string? MensajeDeError(string html) =>
        Regex.Match(html, "<p class=\"error\" role=\"alert\">(.*?)</p>") is { Success: true } aviso
            ? WebUtility.HtmlDecode(aviso.Groups[1].Value)
            : null;

    /// <summary>El código de autorización si la respuesta es la redirección a <paramref name="redireccion"/>;
    /// si no, <c>null</c>.</summary>
    public static string? CodigoDe(HttpResponseMessage respuesta, string redireccion = Redireccion) =>
        respuesta.StatusCode == HttpStatusCode.Redirect &&
        respuesta.Headers.Location is { } destino &&
        destino.AbsoluteUri.StartsWith(redireccion + "?", StringComparison.Ordinal) &&
        QueryHelpers.ParseQuery(destino.Query).TryGetValue("code", out var codigo)
            ? codigo.ToString()
            : null;

    /// <param name="recurso"><c>null</c> no manda el parámetro <c>resource</c>.</param>
    public static Task<HttpResponseMessage> CanjearAsync(
        HttpClient cliente,
        string codigo,
        Pkce pkce,
        string? recurso = RecursoMcp,
        string idCliente = IdCliente,
        string redireccion = Redireccion) =>
        cliente.PostAsync("/connect/token", Formulario(new Dictionary<string, string?>
        {
            ["grant_type"] = "authorization_code",
            ["code"] = codigo,
            ["redirect_uri"] = redireccion,
            ["client_id"] = idCliente,
            ["code_verifier"] = pkce.Verificador,
            ["resource"] = recurso
        }));

    /// <param name="alcances">Si se manda, OpenIddict acota el access token nuevo a esos alcances.</param>
    public static Task<HttpResponseMessage> RefrescarAsync(HttpClient cliente, string refreshToken, string? alcances = null) =>
        cliente.PostAsync("/connect/token", Formulario(new Dictionary<string, string?>
        {
            ["grant_type"] = "refresh_token",
            ["refresh_token"] = refreshToken,
            ["client_id"] = IdCliente,
            ["resource"] = RecursoMcp,
            ["scope"] = alcances
        }));

    private static FormUrlEncodedContent Formulario(Dictionary<string, string?> parametros) =>
        new(parametros.Where(p => p.Value is not null).Select(p => new KeyValuePair<string, string>(p.Key, p.Value!)));

    /// <summary>Flujo completo para un usuario habilitado: devuelve la respuesta del token. Un
    /// <paramref name="recurso"/> o <paramref name="alcances"/> en <c>null</c> no se manda.</summary>
    public static async Task<JsonElement> ObtenerTokensAsync(
        HttpClient cliente,
        string mail,
        string? recurso = RecursoMcp,
        string? alcances = "ways.mcp offline_access",
        string idCliente = IdCliente,
        string redireccion = Redireccion)
    {
        var pkce = NuevoPkce();
        var url = UrlDeAutorizacion(pkce, new Dictionary<string, string?>
        {
            ["resource"] = recurso,
            ["scope"] = alcances,
            ["client_id"] = idCliente,
            ["redirect_uri"] = redireccion
        });
        var codigo = CodigoDe(await AprobarAsync(cliente, url, mail, Password), redireccion);
        Assert.NotNull(codigo);

        var canje = await CanjearAsync(cliente, codigo, pkce, recurso, idCliente, redireccion);
        Assert.Equal(HttpStatusCode.OK, canje.StatusCode);
        return await canje.Content.ReadFromJsonAsync<JsonElement>();
    }

    /// <summary>Un POST a <c>/mcp</c> con el formato 2025-11-25 (sin sesión). Devuelve el estado
    /// HTTP y el primer mensaje JSON-RPC de la respuesta (SSE o JSON), si lo hay.</summary>
    public static async Task<(HttpStatusCode Estado, JsonElement? Mensaje)> LlamarMcpAsync(
        HttpClient cliente, string? accessToken, string metodo, object? parametros = null)
    {
        using var pedido = new HttpRequestMessage(HttpMethod.Post, "/mcp")
        {
            Content = JsonContent.Create(
                new { jsonrpc = "2.0", id = 1, method = metodo, @params = parametros },
                options: new JsonSerializerOptions { DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull })
        };
        pedido.Headers.Accept.ParseAdd("application/json");
        pedido.Headers.Accept.ParseAdd("text/event-stream");
        pedido.Headers.Add("MCP-Protocol-Version", "2025-11-25");
        if (accessToken is not null)
        {
            pedido.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);
        }

        using var respuesta = await cliente.SendAsync(pedido);
        var texto = await respuesta.Content.ReadAsStringAsync();
        var datos = texto.Split('\n').FirstOrDefault(linea => linea.StartsWith("data:", StringComparison.Ordinal));
        var json = datos is not null ? datos[5..].Trim() : texto.TrimStart().StartsWith('{') ? texto : null;

        return (respuesta.StatusCode, json is null ? null : JsonSerializer.Deserialize<JsonElement>(json));
    }

    /// <summary>Un POST a <c>/mcp</c> con un cuerpo arbitrario, sin token.</summary>
    public static async Task<HttpStatusCode> PostearCuerpoCrudoAsync(HttpClient cliente, string cuerpo)
    {
        using var pedido = new HttpRequestMessage(HttpMethod.Post, "/mcp")
        {
            Content = new StringContent(cuerpo, Encoding.UTF8, "application/json")
        };
        pedido.Headers.Accept.ParseAdd("application/json");
        pedido.Headers.Accept.ParseAdd("text/event-stream");

        using var respuesta = await cliente.SendAsync(pedido);
        return respuesta.StatusCode;
    }

    /// <summary>Las líneas <c>Conector MCP: …</c> que dejó el middleware de diagnóstico.</summary>
    public static List<string> LineasDeDiagnostico(CapturaDeLogs captura) =>
        [.. captura.Entradas.Where(e => e.Categoria == "Ways.Api.ConectorMcp.Diagnostico").Select(e => e.Mensaje)];

    /// <summary>Texto que devuelve la herramienta <c>quien_soy</c>.</summary>
    public static async Task<string> QuienSoyAsync(HttpClient cliente, string accessToken)
    {
        var (estado, mensaje) = await LlamarMcpAsync(
            cliente, accessToken, "tools/call", new { name = "quien_soy", arguments = new { } });
        Assert.Equal(HttpStatusCode.OK, estado);

        return mensaje!.Value.GetProperty("result").GetProperty("content")[0].GetProperty("text").GetString()!;
    }
}

/// <summary>Proveedor de logs en memoria para los hosts derivados de las pruebas del conector.</summary>
internal sealed class CapturaDeLogs : ILoggerProvider
{
    public sealed record Entrada(string Categoria, LogLevel Nivel, string Mensaje);

    private readonly ConcurrentQueue<Entrada> _entradas = new();

    public IReadOnlyList<Entrada> Entradas => [.. _entradas];

    public ILogger CreateLogger(string categoryName) => new Logueador(this, categoryName);

    public void Dispose()
    {
    }

    private sealed class Logueador(CapturaDeLogs captura, string categoria) : ILogger
    {
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(
            LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter) =>
            captura._entradas.Enqueue(new Entrada(categoria, logLevel, formatter(state, exception)));
    }
}
