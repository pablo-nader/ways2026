using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Ways.Application.Usuarios;
using Ways.Domain.Organizacion;
using Ways.Domain.Usuarios;
using Ways.Infrastructure.Multitenancy;
using Ways.Infrastructure.Seguridad;

namespace Ways.IntegrationTests;

/// <summary>
/// Siembra y clientes compartidos por las pruebas de dominios por tenant. Un host de prueba se arma
/// con la <c>BaseAddress</c> del cliente: el <c>TestServer</c> toma de ahí el header <c>Host</c> de
/// cada request, así que no hace falta tocar ninguna configuración del host de la API (los hosts de
/// prueba terminan en <c>.test</c> y no están en <c>Plataforma:Hosts</c>).
/// </summary>
internal sealed class ApoyoDeDominios(WaysApiFixture fixture)
{
    public const string PasswordRoot = "root";
    public const string MailRoot = "test@test.com";
    public const string PasswordPorDefecto = "una-contraseña-larga";

    public sealed record TenantSembrado(
        int IdTenant, string Nombre, string Mail, string Usuario, string Password);

    /// <summary>Host único por prueba: la caché de resolución es por host y vive lo que dura el host
    /// de la API, así que reusar uno entre pruebas contaminaría los casos de "todavía no resuelve".</summary>
    public static string HostUnico(string prefijo) =>
        $"{prefijo}-{Guid.NewGuid().ToString("N")[..8]}.test".ToLowerInvariant();

    /// <summary>Un tenant con un único usuario Admin, sembrado en modo plataforma con un hash real.</summary>
    public async Task<TenantSembrado> SembrarTenantAsync(
        string prefijo, string usuario = "admin", string password = PasswordPorDefecto)
    {
        // Arranca el host (idempotente): InicializadorDeBaseDeDatos siembra los roles que la FK
        // fk_usuarios_rol exige antes de insertar la cuenta.
        using var _ = fixture.CreateClient();

        var unico = $"{prefijo}-{Guid.NewGuid().ToString("N")[..8]}".ToLowerInvariant();
        var ahora = DateTimeOffset.UtcNow;
        var hasheador = new HasheadorPbkdf2();

        await using var db = fixture.CrearContextoDeAplicacion(TenantActualFijo.Plataforma);

        var tenant = new Tenant { Nombre = $"Comercio {unico}", CreatedAt = ahora, UpdatedAt = ahora };
        db.Tenants.Add(tenant);
        await db.SaveChangesAsync();

        var mail = $"{unico}@ways.test";
        db.Usuarios.Add(new Usuario
        {
            IdTenant = tenant.Id,
            NombreUsuario = usuario,
            Mail = mail,
            RolId = (int)RolConocido.Admin,
            PasswordHash = hasheador.Hashear(password),
            PasswordAlgoritmo = hasheador.Algoritmo,
            PasswordActualizadoEl = ahora,
            CreatedAt = ahora,
            UpdatedAt = ahora
        });
        await db.SaveChangesAsync();

        return new TenantSembrado(tenant.Id, tenant.Nombre, mail, usuario, password);
    }

    /// <summary>Inserta el dominio directo en la base (sin pasar por la API): es el estado de partida
    /// de las pruebas de resolución, no lo que se prueba.</summary>
    public async Task<int> SembrarDominioAsync(
        int idTenant, string host, bool verificado = true, bool principal = false, bool eliminado = false)
    {
        using var _ = fixture.CreateClient();

        var ahora = DateTimeOffset.UtcNow;

        await using var db = fixture.CrearContextoDeAplicacion(TenantActualFijo.Plataforma);

        var dominio = new Dominio
        {
            IdTenant = idTenant,
            Host = host,
            EsPrincipal = principal,
            VerificadoEn = verificado ? ahora : null,
            DeletedAt = eliminado ? ahora : null,
            CreatedAt = ahora,
            UpdatedAt = ahora
        };
        db.Dominios.Add(dominio);
        await db.SaveChangesAsync();

        return dominio.Id;
    }

    /// <param name="host">Sin esquema; puede llevar puerto.</param>
    /// <param name="cookies"><c>false</c> para manejar a mano el header <c>Cookie</c> (replay de una
    /// sesión emitida en otro host).</param>
    public HttpClient ClienteEnHost(string host, bool cookies = true) =>
        fixture.CreateClient(new WebApplicationFactoryClientOptions
        {
            BaseAddress = new Uri($"http://{host}"),
            HandleCookies = cookies,
            AllowAutoRedirect = false
        });

    /// <summary>Cliente en el host de la plataforma (<c>localhost</c>) con la sesión de root.</summary>
    public async Task<HttpClient> ClienteRootAsync()
    {
        var cliente = fixture.CreateClient();
        var login = await cliente.PostAsJsonAsync(
            "/api/auth/login", new { mail = MailRoot, password = PasswordRoot });
        Assert.Equal(HttpStatusCode.OK, login.StatusCode);
        return cliente;
    }

    /// <summary>Loguea por mail en el host de la plataforma y devuelve <c>ways.sesion=valor</c>, listo
    /// para mandar como header <c>Cookie</c> a otro host.</summary>
    public async Task<string> CookieDeSesionPorMailAsync(string mail, string password)
    {
        using var cliente = ClienteEnHost("localhost", cookies: false);
        var login = await cliente.PostAsJsonAsync("/api/auth/login", new { mail, password });
        Assert.Equal(HttpStatusCode.OK, login.StatusCode);
        return CookieDeSesion(login);
    }

    public static string CookieDeSesion(HttpResponseMessage respuesta)
    {
        var emitida = respuesta.Headers.GetValues("Set-Cookie")
            .Single(c => c.StartsWith("ways.sesion=", StringComparison.Ordinal));

        return emitida[..emitida.IndexOf(';')];
    }

    public static async Task<HttpResponseMessage> GetConCookieAsync(HttpClient cliente, string ruta, string cookie)
    {
        using var solicitud = new HttpRequestMessage(HttpMethod.Get, ruta);
        solicitud.Headers.Add("Cookie", cookie);
        return await cliente.SendAsync(solicitud);
    }

    public static async Task<string> CodigoDeErrorAsync(HttpResponseMessage respuesta) =>
        (await respuesta.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("codigo").GetString()!;

    public static async Task<JsonElement> LeerJsonAsync(HttpResponseMessage respuesta) =>
        await respuesta.Content.ReadFromJsonAsync<JsonElement>();

    public async Task<Dominio> LeerDominioAsync(int idDominio)
    {
        await using var db = fixture.CrearContextoDeAplicacion(TenantActualFijo.Plataforma);
        return await db.Dominios.IgnoreQueryFilters(["BajaLogica"]).AsNoTracking().SingleAsync(d => d.Id == idDominio);
    }
}
