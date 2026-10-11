using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Ways.Application.Dominios;
using Ways.Domain.Organizacion;
using Ways.Infrastructure.Multitenancy;

namespace Ways.IntegrationTests;

/// <summary>
/// Resolución del tenant desde el host de la request, la guarda de sesión por host y el login bajo
/// el dominio de un comercio. Corre contra Postgres real: la resolución lee <c>dominios</c> en modo
/// login (política <c>dominios_login_lectura</c>) bajo el rol <c>ways_app</c>, sin <c>BYPASSRLS</c>.
/// </summary>
[Collection("Ways.IntegrationTests secuencial")]
public class DominiosPorTenantTests(WaysApiFixture fixture) : IClassFixture<WaysApiFixture>
{
    private const string MensajePaginaEstatica = "Este dominio todavía no está configurado en Ways.";

    private readonly ApoyoDeDominios apoyo = new(fixture);

    // ---- resolución del host ----------------------------------------------------------------------

    [Fact]
    public async Task UnHostVerificadoResuelveSuTenantYElSitioDevuelveElNombreDelComercio()
    {
        var tenant = await apoyo.SembrarTenantAsync("resuelve");
        var host = ApoyoDeDominios.HostUnico("resuelve");
        await apoyo.SembrarDominioAsync(tenant.IdTenant, host);

        using var cliente = apoyo.ClienteEnHost(host);
        var respuesta = await cliente.GetAsync("/api/publico/sitio");

        Assert.Equal(HttpStatusCode.OK, respuesta.StatusCode);
        var sitio = await ApoyoDeDominios.LeerJsonAsync(respuesta);
        Assert.False(sitio.GetProperty("esPlataforma").GetBoolean());
        Assert.Equal(tenant.Nombre, sitio.GetProperty("nombreComercio").GetString());
        Assert.True(sitio.GetProperty("admiteUsuario").GetBoolean());
    }

    [Fact]
    public async Task DosComerciosResuelvenCadaUnoSuPropioNombre()
    {
        var a = await apoyo.SembrarTenantAsync("sitio-a");
        var b = await apoyo.SembrarTenantAsync("sitio-b");
        var hostA = ApoyoDeDominios.HostUnico("sitio-a");
        var hostB = ApoyoDeDominios.HostUnico("sitio-b");
        await apoyo.SembrarDominioAsync(a.IdTenant, hostA);
        await apoyo.SembrarDominioAsync(b.IdTenant, hostB);

        using var clienteA = apoyo.ClienteEnHost(hostA);
        using var clienteB = apoyo.ClienteEnHost(hostB);

        var sitioA = await ApoyoDeDominios.LeerJsonAsync(await clienteA.GetAsync("/api/publico/sitio"));
        var sitioB = await ApoyoDeDominios.LeerJsonAsync(await clienteB.GetAsync("/api/publico/sitio"));

        Assert.Equal(a.Nombre, sitioA.GetProperty("nombreComercio").GetString());
        Assert.Equal(b.Nombre, sitioB.GetProperty("nombreComercio").GetString());
    }

    [Fact]
    public async Task ElPuertoDelHostNoCambiaElDominioQueResuelve()
    {
        var tenant = await apoyo.SembrarTenantAsync("puerto");
        var host = ApoyoDeDominios.HostUnico("puerto");
        await apoyo.SembrarDominioAsync(tenant.IdTenant, host);

        using var cliente = apoyo.ClienteEnHost($"{host}:8080");
        var respuesta = await cliente.GetAsync("/api/publico/sitio");

        Assert.Equal(HttpStatusCode.OK, respuesta.StatusCode);
        var sitio = await ApoyoDeDominios.LeerJsonAsync(respuesta);
        Assert.Equal(tenant.Nombre, sitio.GetProperty("nombreComercio").GetString());
    }

    [Theory]
    [InlineData("localhost")]
    [InlineData("127.0.0.1")]
    [InlineData("tauri.localhost")]
    public async Task LosHostsDeLaPlataformaDescribenLaPlataformaSinNombreNiUsuario(string host)
    {
        using var cliente = apoyo.ClienteEnHost(host);
        var respuesta = await cliente.GetAsync("/api/publico/sitio");

        Assert.Equal(HttpStatusCode.OK, respuesta.StatusCode);
        var sitio = await ApoyoDeDominios.LeerJsonAsync(respuesta);
        Assert.True(sitio.GetProperty("esPlataforma").GetBoolean());
        Assert.Equal(JsonValueKind.Null, sitio.GetProperty("nombreComercio").ValueKind);
        Assert.False(sitio.GetProperty("admiteUsuario").GetBoolean());
    }

    /// <summary>LA CLÁUSULA <c>verificado_en IS NOT NULL</c> de la resolución: la fila existe, está
    /// viva y es de un tenant vivo —el único motivo por el que no resuelve es que nadie la verificó—,
    /// así que borrar la cláusula la haría resolver.</summary>
    [Fact]
    public async Task UnHostSinVerificarNoResuelve404EnLaApiYPaginaEstaticaEnElResto()
    {
        var tenant = await apoyo.SembrarTenantAsync("sin-verificar");
        var host = ApoyoDeDominios.HostUnico("sin-verificar");
        await apoyo.SembrarDominioAsync(tenant.IdTenant, host, verificado: false);

        using var cliente = apoyo.ClienteEnHost(host);

        var enLaApi = await cliente.GetAsync("/api/publico/sitio");
        Assert.Equal(HttpStatusCode.NotFound, enLaApi.StatusCode);
        Assert.Equal("dominio_desconocido", await ApoyoDeDominios.CodigoDeErrorAsync(enLaApi));

        foreach (var ruta in new[] { "/", "/login", "/pos" })
        {
            var pagina = await cliente.GetAsync(ruta);

            Assert.Equal(HttpStatusCode.OK, pagina.StatusCode);
            Assert.Equal("text/html", pagina.Content.Headers.ContentType?.MediaType);
            Assert.Contains(MensajePaginaEstatica, await pagina.Content.ReadAsStringAsync());
        }
    }

    [Fact]
    public async Task UnHostQueNoEstaCargadoNoResuelveNiSirveElFront()
    {
        using var cliente = apoyo.ClienteEnHost(ApoyoDeDominios.HostUnico("inexistente"));

        var enLaApi = await cliente.PostAsJsonAsync("/api/auth/login", new { mail = "a@b.test", password = "x" });
        Assert.Equal(HttpStatusCode.NotFound, enLaApi.StatusCode);
        Assert.Equal("dominio_desconocido", await ApoyoDeDominios.CodigoDeErrorAsync(enLaApi));

        var pagina = await cliente.GetAsync("/");
        Assert.Equal(HttpStatusCode.OK, pagina.StatusCode);
        Assert.Contains(MensajePaginaEstatica, await pagina.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task UnDominioDadoDeBajaNoResuelve()
    {
        var tenant = await apoyo.SembrarTenantAsync("dado-de-baja");
        var host = ApoyoDeDominios.HostUnico("dado-de-baja");
        await apoyo.SembrarDominioAsync(tenant.IdTenant, host, verificado: true, eliminado: true);

        using var cliente = apoyo.ClienteEnHost(host);

        Assert.Equal(HttpStatusCode.NotFound, (await cliente.GetAsync("/api/publico/sitio")).StatusCode);
    }

    [Fact]
    public async Task UnDominioVerificadoDeUnTenantDadoDeBajaNoResuelve()
    {
        var tenant = await apoyo.SembrarTenantAsync("tenant-de-baja");
        var host = ApoyoDeDominios.HostUnico("tenant-de-baja");
        await apoyo.SembrarDominioAsync(tenant.IdTenant, host);

        await using (var db = fixture.CrearContextoDeAplicacion(TenantActualFijo.Plataforma))
        {
            var fila = await db.Tenants.SingleAsync(t => t.Id == tenant.IdTenant);
            fila.DeletedAt = DateTimeOffset.UtcNow;
            fila.UpdatedAt = DateTimeOffset.UtcNow;
            await db.SaveChangesAsync();
        }

        using var cliente = apoyo.ClienteEnHost(host);

        Assert.Equal(HttpStatusCode.NotFound, (await cliente.GetAsync("/api/publico/sitio")).StatusCode);
    }

    [Fact]
    public async Task ElChequeoDeSaludRespondeSinImportarElHost()
    {
        using var cliente = apoyo.ClienteEnHost(ApoyoDeDominios.HostUnico("sondeo"));

        var respuesta = await cliente.GetAsync("/api/salud");

        Assert.Equal(HttpStatusCode.OK, respuesta.StatusCode);
    }

    [Fact]
    public async Task SiNoSePuedeResolverElDominioResponde503SinFiltrarElErrorYLaPlataformaSigueEnPie()
    {
        await using var host = fixture.WithWebHostBuilder(builder => builder.ConfigureServices(servicios =>
        {
            servicios.RemoveAll<IResolvedorDeDominios>();
            servicios.AddSingleton<IResolvedorDeDominios, ResolvedorQueFalla>();
        }));

        using var enUnComercio = host.CreateClient(new WebApplicationFactoryClientOptions
        {
            BaseAddress = new Uri($"http://{ApoyoDeDominios.HostUnico("base-caida")}")
        });
        using var enLaPlataforma = host.CreateClient();

        var enLaApi = await enUnComercio.GetAsync("/api/publico/sitio");
        Assert.Equal(HttpStatusCode.ServiceUnavailable, enLaApi.StatusCode);
        Assert.Equal("servicio_no_disponible", await ApoyoDeDominios.CodigoDeErrorAsync(enLaApi));
        Assert.DoesNotContain(ResolvedorQueFalla.Detalle, await enLaApi.Content.ReadAsStringAsync());

        var pagina = await enUnComercio.GetAsync("/");
        Assert.Equal(HttpStatusCode.ServiceUnavailable, pagina.StatusCode);
        Assert.Equal("text/html", pagina.Content.Headers.ContentType?.MediaType);

        Assert.Equal(HttpStatusCode.OK, (await enLaPlataforma.GetAsync("/api/publico/sitio")).StatusCode);
    }

    private sealed class ResolvedorQueFalla : IResolvedorDeDominios
    {
        public const string Detalle = "detalle-interno-de-la-base";

        public Task<DominioResuelto?> ResolverAsync(string host, CancellationToken ct = default) =>
            throw new InvalidOperationException(Detalle);

        public void Invalidar(string host)
        {
        }
    }

    // ---- caché de resolución e invalidación -------------------------------------------------------

    /// <summary>El 404 de un host sin verificar queda en la caché; verificarlo por la API tiene que
    /// invalidarlo, o el comercio seguiría sin resolver hasta que venza la vigencia.</summary>
    [Fact]
    public async Task VerificarUnDominioPorLaApiLoHaceResolverEnLaProximaRequest()
    {
        var tenant = await apoyo.SembrarTenantAsync("verifica");
        var host = ApoyoDeDominios.HostUnico("verifica");
        var idDominio = await apoyo.SembrarDominioAsync(tenant.IdTenant, host, verificado: false);

        using var cliente = apoyo.ClienteEnHost(host);
        Assert.Equal(HttpStatusCode.NotFound, (await cliente.GetAsync("/api/publico/sitio")).StatusCode);

        using var root = await apoyo.ClienteRootAsync();
        var verificar = await root.PostAsync(
            $"/api/plataforma/tenants/{tenant.IdTenant}/dominios/{idDominio}/verificar", content: null);
        Assert.Equal(HttpStatusCode.OK, verificar.StatusCode);

        Assert.Equal(HttpStatusCode.OK, (await cliente.GetAsync("/api/publico/sitio")).StatusCode);
    }

    [Fact]
    public async Task DarDeBajaUnDominioPorLaApiLoHaceDejarDeResolverEnLaProximaRequest()
    {
        var tenant = await apoyo.SembrarTenantAsync("da-de-baja");
        var host = ApoyoDeDominios.HostUnico("da-de-baja");
        var idDominio = await apoyo.SembrarDominioAsync(tenant.IdTenant, host);

        using var cliente = apoyo.ClienteEnHost(host);
        Assert.Equal(HttpStatusCode.OK, (await cliente.GetAsync("/api/publico/sitio")).StatusCode);

        using var root = await apoyo.ClienteRootAsync();
        var baja = await root.DeleteAsync($"/api/plataforma/tenants/{tenant.IdTenant}/dominios/{idDominio}");
        Assert.Equal(HttpStatusCode.NoContent, baja.StatusCode);

        Assert.Equal(HttpStatusCode.NotFound, (await cliente.GetAsync("/api/publico/sitio")).StatusCode);
    }

    // ---- guarda de sesión por host ----------------------------------------------------------------

    /// <summary>LA CLÁUSULA <c>PoliticaDeDominio.SesionCorrespondeAlHost</c> aplicada por
    /// <c>ValidadorDeSesion</c>: la misma cookie vale en el host de la plataforma y en el dominio de
    /// su tenant (así la causa del 401 no puede ser una cookie mal formada) y se rechaza —y se borra—
    /// en el dominio de otro comercio.</summary>
    [Fact]
    public async Task UnaCookieDeUnTenantNoValeEnElDominioDeOtroTenantYSeBorra()
    {
        var a = await apoyo.SembrarTenantAsync("guarda-a");
        var b = await apoyo.SembrarTenantAsync("guarda-b");
        var hostA = ApoyoDeDominios.HostUnico("guarda-a");
        var hostB = ApoyoDeDominios.HostUnico("guarda-b");
        await apoyo.SembrarDominioAsync(a.IdTenant, hostA);
        await apoyo.SembrarDominioAsync(b.IdTenant, hostB);

        var cookie = await apoyo.CookieDeSesionPorMailAsync(a.Mail, a.Password);

        using var enPlataforma = apoyo.ClienteEnHost("localhost", cookies: false);
        using var enA = apoyo.ClienteEnHost(hostA, cookies: false);
        using var enB = apoyo.ClienteEnHost(hostB, cookies: false);

        Assert.Equal(
            HttpStatusCode.OK, (await ApoyoDeDominios.GetConCookieAsync(enPlataforma, "/api/auth/me", cookie)).StatusCode);
        Assert.Equal(
            HttpStatusCode.OK, (await ApoyoDeDominios.GetConCookieAsync(enA, "/api/auth/me", cookie)).StatusCode);

        var enOtroTenant = await ApoyoDeDominios.GetConCookieAsync(enB, "/api/auth/me", cookie);

        Assert.Equal(HttpStatusCode.Unauthorized, enOtroTenant.StatusCode);
        Assert.Contains(
            enOtroTenant.Headers.GetValues("Set-Cookie"),
            c => c.StartsWith("ways.sesion=;", StringComparison.Ordinal));
    }

    [Fact]
    public async Task LaSesionDeRootVaEnCualquierHost()
    {
        var tenant = await apoyo.SembrarTenantAsync("root-en-comercio");
        var host = ApoyoDeDominios.HostUnico("root-en-comercio");
        await apoyo.SembrarDominioAsync(tenant.IdTenant, host);

        var cookie = await apoyo.CookieDeSesionPorMailAsync(ApoyoDeDominios.MailRoot, ApoyoDeDominios.PasswordRoot);

        using var enElComercio = apoyo.ClienteEnHost(host, cookies: false);
        var respuesta = await ApoyoDeDominios.GetConCookieAsync(enElComercio, "/api/auth/me", cookie);

        Assert.Equal(HttpStatusCode.OK, respuesta.StatusCode);
    }

    [Fact]
    public async Task ElBearerDeUnTenantTampocoVaEnElDominioDeOtroTenant()
    {
        var a = await apoyo.SembrarTenantAsync("bearer-a");
        var b = await apoyo.SembrarTenantAsync("bearer-b");
        var hostA = ApoyoDeDominios.HostUnico("bearer-a");
        var hostB = ApoyoDeDominios.HostUnico("bearer-b");
        await apoyo.SembrarDominioAsync(a.IdTenant, hostA);
        await apoyo.SembrarDominioAsync(b.IdTenant, hostB);

        using var enPlataforma = apoyo.ClienteEnHost("localhost", cookies: false);
        var login = await enPlataforma.PostAsJsonAsync(
            "/api/auth/login", new { mail = a.Mail, password = a.Password, solicitarBearer = true });
        Assert.Equal(HttpStatusCode.OK, login.StatusCode);
        var token = (await ApoyoDeDominios.LeerJsonAsync(login)).GetProperty("token").GetString()!;

        async Task<HttpStatusCode> MeEnAsync(string host)
        {
            using var cliente = apoyo.ClienteEnHost(host, cookies: false);
            cliente.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
            return (await cliente.GetAsync("/api/auth/me")).StatusCode;
        }

        Assert.Equal(HttpStatusCode.OK, await MeEnAsync("localhost"));
        Assert.Equal(HttpStatusCode.OK, await MeEnAsync(hostA));
        Assert.Equal(HttpStatusCode.Unauthorized, await MeEnAsync(hostB));
    }

    // ---- login bajo el dominio de un comercio -----------------------------------------------------

    [Fact]
    public async Task ElLoginPorUsuarioBajoElDominioDelComercioEmiteLaSesionDeEseTenant()
    {
        var a = await apoyo.SembrarTenantAsync("usuario-a", usuario: "caja", password: "clave-del-comercio-a");
        var b = await apoyo.SembrarTenantAsync("usuario-b", usuario: "caja", password: "clave-del-comercio-b");
        var hostA = ApoyoDeDominios.HostUnico("usuario-a");
        var hostB = ApoyoDeDominios.HostUnico("usuario-b");
        await apoyo.SembrarDominioAsync(a.IdTenant, hostA);
        await apoyo.SembrarDominioAsync(b.IdTenant, hostB);

        // Los dos comercios tienen un usuario "caja": lo único que dice cuál es, es el dominio.
        using var enA = apoyo.ClienteEnHost(hostA);
        var loginA = await enA.PostAsJsonAsync("/api/auth/login", new { usuario = "caja", password = a.Password });
        Assert.Equal(HttpStatusCode.OK, loginA.StatusCode);
        var autenticadoA = await ApoyoDeDominios.LeerJsonAsync(loginA);
        Assert.Equal(a.IdTenant, autenticadoA.GetProperty("idTenant").GetInt32());
        Assert.Equal("caja", autenticadoA.GetProperty("usuario").GetString());

        using var enB = apoyo.ClienteEnHost(hostB);
        var loginB = await enB.PostAsJsonAsync("/api/auth/login", new { usuario = "caja", password = b.Password });
        Assert.Equal(HttpStatusCode.OK, loginB.StatusCode);
        Assert.Equal(b.IdTenant, (await ApoyoDeDominios.LeerJsonAsync(loginB)).GetProperty("idTenant").GetInt32());

        // La cookie quedó emitida para ese dominio y la sesión sigue en pie.
        Assert.Equal(HttpStatusCode.OK, (await enA.GetAsync("/api/auth/me")).StatusCode);

        // La contraseña de un comercio no abre el usuario homónimo del otro.
        var cruzado = await enA.PostAsJsonAsync("/api/auth/login", new { usuario = "caja", password = b.Password });
        Assert.Equal(HttpStatusCode.Unauthorized, cruzado.StatusCode);
    }

    [Fact]
    public async Task ElLoginPorUsuarioBajoElHostDeLaPlataformaDevuelve400UsuarioRequiereDominio()
    {
        var tenant = await apoyo.SembrarTenantAsync("usuario-en-plataforma");

        using var cliente = apoyo.ClienteEnHost("localhost");
        var respuesta = await cliente.PostAsJsonAsync(
            "/api/auth/login", new { usuario = tenant.Usuario, password = tenant.Password });

        Assert.Equal(HttpStatusCode.BadRequest, respuesta.StatusCode);
        Assert.Equal("usuario_requiere_dominio", await ApoyoDeDominios.CodigoDeErrorAsync(respuesta));
    }

    [Theory]
    [InlineData("""{"usuario":"admin","mail":"a@b.test","password":"x"}""")]
    [InlineData("""{"password":"x"}""")]
    public async Task ElLoginExigeExactamenteUnoDeUsuarioOMail(string cuerpo)
    {
        var tenant = await apoyo.SembrarTenantAsync("uno-solo");
        var host = ApoyoDeDominios.HostUnico("uno-solo");
        await apoyo.SembrarDominioAsync(tenant.IdTenant, host);

        using var cliente = apoyo.ClienteEnHost(host);
        var respuesta = await cliente.PostAsync(
            "/api/auth/login", new StringContent(cuerpo, Encoding.UTF8, "application/json"));

        Assert.Equal(HttpStatusCode.BadRequest, respuesta.StatusCode);
        Assert.Equal("solicitud_invalida", await ApoyoDeDominios.CodigoDeErrorAsync(respuesta));
    }

    /// <summary>El mail de OTRO tenant es indistinguible de una contraseña incorrecta (mismo estado,
    /// código y mensaje), y ese intento no le suma un fallo —ni lo bloquea— a la cuenta ajena.</summary>
    [Fact]
    public async Task ElLoginPorMailBajoElDominioDeUnComercioSoloAceptaCuentasDeEseTenant()
    {
        var a = await apoyo.SembrarTenantAsync("mail-a");
        var b = await apoyo.SembrarTenantAsync("mail-b");
        var hostA = ApoyoDeDominios.HostUnico("mail-a");
        await apoyo.SembrarDominioAsync(a.IdTenant, hostA);

        using var enA = apoyo.ClienteEnHost(hostA);

        var deOtroTenant = await enA.PostAsJsonAsync("/api/auth/login", new { mail = b.Mail, password = b.Password });
        var contraseñaMala = await enA.PostAsJsonAsync("/api/auth/login", new { mail = a.Mail, password = "incorrecta" });

        Assert.Equal(HttpStatusCode.Unauthorized, deOtroTenant.StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, contraseñaMala.StatusCode);
        var problemaOtroTenant = await ApoyoDeDominios.LeerJsonAsync(deOtroTenant);
        var problemaContraseña = await ApoyoDeDominios.LeerJsonAsync(contraseñaMala);
        Assert.Equal(
            problemaContraseña.GetProperty("codigo").GetString(), problemaOtroTenant.GetProperty("codigo").GetString());
        Assert.Equal(
            problemaContraseña.GetProperty("title").GetString(), problemaOtroTenant.GetProperty("title").GetString());

        await using (var db = fixture.CrearContextoDeAplicacion(TenantActualFijo.Plataforma))
        {
            var cuentaDeB = await db.Usuarios.AsNoTracking().SingleAsync(u => u.IdTenant == b.IdTenant);
            Assert.Equal(0, cuentaDeB.IntentosFallidos);
        }

        // La misma cuenta entra por el host de la plataforma y por su propio dominio.
        using var enPlataforma = apoyo.ClienteEnHost("localhost");
        Assert.Equal(
            HttpStatusCode.OK,
            (await enPlataforma.PostAsJsonAsync("/api/auth/login", new { mail = b.Mail, password = b.Password })).StatusCode);
        Assert.Equal(
            HttpStatusCode.OK,
            (await enA.PostAsJsonAsync("/api/auth/login", new { mail = a.Mail, password = a.Password })).StatusCode);
    }

    [Fact]
    public async Task ElRootNoIniciaSesionPorMailBajoElDominioDeUnComercio()
    {
        var tenant = await apoyo.SembrarTenantAsync("root-no-entra");
        var host = ApoyoDeDominios.HostUnico("root-no-entra");
        await apoyo.SembrarDominioAsync(tenant.IdTenant, host);

        using var cliente = apoyo.ClienteEnHost(host);
        var respuesta = await cliente.PostAsJsonAsync(
            "/api/auth/login", new { mail = ApoyoDeDominios.MailRoot, password = ApoyoDeDominios.PasswordRoot });

        Assert.Equal(HttpStatusCode.Unauthorized, respuesta.StatusCode);
    }

    // ---- superficie pública -----------------------------------------------------------------------

    [Fact]
    public async Task ElSitioPublicoNoPideSesion()
    {
        using var cliente = apoyo.ClienteEnHost("localhost");

        var respuesta = await cliente.GetAsync("/api/publico/sitio");

        Assert.Equal(HttpStatusCode.OK, respuesta.StatusCode);
    }
}
