using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Ways.Domain.Organizacion;
using Ways.Infrastructure.Multitenancy;

namespace Ways.IntegrationTests;

/// <summary>
/// La lectura anónima de la vitrina: <c>GET /api/publico/vitrina</c> y <c>/logo</c> contestan según el
/// host de la request, leyendo en el modo de acceso <c>publico</c> bajo el rol <c>ways_app</c>. La prueba
/// de que el aislamiento es de la policy y no del filtro de EF está en <see cref="VitrinaRlsTests"/>;
/// acá se prueba el contrato del endpoint de punta a punta.
/// </summary>
[Collection("Ways.IntegrationTests secuencial")]
public class VitrinaPublicaTests(WaysApiFixture fixture) : IClassFixture<WaysApiFixture>
{
    private const string RutaVitrina = "/api/publico/vitrina";
    private const string RutaLogo = "/api/publico/vitrina/logo";

    private readonly ApoyoDeVitrinas apoyo = new(fixture);

    private static readonly string[] CamposDeLaVitrina =
        ["colorAcento", "descripcion", "logoVersion", "nombre", "sucursales", "tagline", "tema", "tieneLogo"];

    private static readonly string[] CamposDeLaSucursal =
        ["domicilio", "facebook", "horario", "instagram", "nombre", "web", "whatsapp"];

    // ---- la vitrina -------------------------------------------------------------------------------

    [Fact]
    public async Task ElHostDeUnComercioDevuelveSuVitrinaPublicadaConCadaCampoYSusSucursalesActivasPorNombre()
    {
        var a = await apoyo.SembrarComercioAsync("publica-a", razonSocial: "Razón A SA", nombreFantasia: "Almacén A");
        await apoyo.SembrarVitrinaAsync(
            a.IdTenant, a.IdEmpresa, publicada: true, tagline: "Todo cerca tuyo",
            descripcion: "Somos un almacén de barrio.\nDesde 1990.", colorAcento: "#1a2b3c", tema: Vitrina.TemaClaro);
        await apoyo.SembrarPuntoVentaAsync(
            a.IdTenant, a.IdEmpresa, "Sur", domicilio: "Calle Sur 1", horario: "Lun a Vie 8 a 20",
            whatsapp: "+54 9 11 1111-1111", instagram: "@almacen.sur", facebook: "fb.com/almacen.sur", web: "https://sur.test");
        await apoyo.SembrarPuntoVentaAsync(
            a.IdTenant, a.IdEmpresa, "Centro", domicilio: "Calle Centro 2", horario: "Todos los días 9 a 21",
            whatsapp: "+54 9 11 2222-2222", instagram: "@almacen.centro", facebook: "fb.com/almacen.centro", web: "https://centro.test");
        await apoyo.SembrarPuntoVentaAsync(a.IdTenant, a.IdEmpresa, "Norte");
        await apoyo.SembrarPuntoVentaAsync(a.IdTenant, a.IdEmpresa, "Borrada", domicilio: "Ya no existe", eliminado: true);

        // Otra empresa del mismo tenant, con vitrina sin publicar y su propio punto de venta: no es de la
        // vitrina publicada, así que ni su nombre ni su sucursal aparecen.
        var otraEmpresa = await apoyo.SembrarEmpresaAsync(a.IdTenant, "Otra empresa SA", "Otra marca");
        await apoyo.SembrarVitrinaAsync(a.IdTenant, otraEmpresa, publicada: false, tagline: "No publicada");
        await apoyo.SembrarPuntoVentaAsync(a.IdTenant, otraEmpresa, "De la otra empresa");

        using var cliente = apoyo.Dominios.ClienteEnHost(a.Host);
        var respuesta = await cliente.GetAsync(RutaVitrina);

        Assert.Equal(HttpStatusCode.OK, respuesta.StatusCode);
        Assert.Equal("public, max-age=60", respuesta.Headers.CacheControl?.ToString());

        var vitrina = await ApoyoDeDominios.LeerJsonAsync(respuesta);
        Assert.Equal(CamposDeLaVitrina, vitrina.EnumerateObject().Select(p => p.Name).Order(StringComparer.Ordinal));
        Assert.Equal("Almacén A", vitrina.GetProperty("nombre").GetString());
        Assert.Equal("Todo cerca tuyo", vitrina.GetProperty("tagline").GetString());
        Assert.Equal("Somos un almacén de barrio.\nDesde 1990.", vitrina.GetProperty("descripcion").GetString());
        Assert.Equal("#1a2b3c", vitrina.GetProperty("colorAcento").GetString());
        Assert.Equal("claro", vitrina.GetProperty("tema").GetString());
        Assert.False(vitrina.GetProperty("tieneLogo").GetBoolean());
        Assert.Equal(JsonValueKind.Null, vitrina.GetProperty("logoVersion").ValueKind);

        var sucursales = vitrina.GetProperty("sucursales").EnumerateArray().ToArray();
        string[] nombres = [.. sucursales.Select(s => s.GetProperty("nombre").GetString()!)];
        Assert.Equal(["Centro", "Norte", "Sur"], nombres);

        foreach (var sucursal in sucursales)
        {
            Assert.Equal(
                CamposDeLaSucursal, sucursal.EnumerateObject().Select(p => p.Name).Order(StringComparer.Ordinal));
        }

        var centro = sucursales[0];
        Assert.Equal("Calle Centro 2", centro.GetProperty("domicilio").GetString());
        Assert.Equal("Todos los días 9 a 21", centro.GetProperty("horario").GetString());
        Assert.Equal("+54 9 11 2222-2222", centro.GetProperty("whatsapp").GetString());
        Assert.Equal("@almacen.centro", centro.GetProperty("instagram").GetString());
        Assert.Equal("fb.com/almacen.centro", centro.GetProperty("facebook").GetString());
        Assert.Equal("https://centro.test", centro.GetProperty("web").GetString());

        var sur = sucursales[2];
        Assert.Equal("Calle Sur 1", sur.GetProperty("domicilio").GetString());
        Assert.Equal("Lun a Vie 8 a 20", sur.GetProperty("horario").GetString());
        Assert.Equal("+54 9 11 1111-1111", sur.GetProperty("whatsapp").GetString());
        Assert.Equal("@almacen.sur", sur.GetProperty("instagram").GetString());
        Assert.Equal("fb.com/almacen.sur", sur.GetProperty("facebook").GetString());
        Assert.Equal("https://sur.test", sur.GetProperty("web").GetString());

        var norte = sucursales[1];
        Assert.Equal(JsonValueKind.Null, norte.GetProperty("domicilio").ValueKind);
        Assert.Equal(JsonValueKind.Null, norte.GetProperty("web").ValueKind);
    }

    [Fact]
    public async Task SinNombreDeFantasiaLaVitrinaSeNombraConLaRazonSocial()
    {
        var comercio = await apoyo.SembrarComercioAsync("publica-razon", razonSocial: "Solo Razón SRL", nombreFantasia: null);
        await apoyo.SembrarVitrinaAsync(comercio.IdTenant, comercio.IdEmpresa, publicada: true);

        using var cliente = apoyo.Dominios.ClienteEnHost(comercio.Host);
        var vitrina = await ApoyoDeDominios.LeerJsonAsync(await cliente.GetAsync(RutaVitrina));

        Assert.Equal("Solo Razón SRL", vitrina.GetProperty("nombre").GetString());
        Assert.Equal("oscuro", vitrina.GetProperty("tema").GetString());
        Assert.Equal(JsonValueKind.Null, vitrina.GetProperty("colorAcento").ValueKind);
        Assert.Empty(vitrina.GetProperty("sucursales").EnumerateArray());
    }

    [Fact]
    public async Task CadaHostVeSoloLaVitrinaDeSuPropioTenant()
    {
        var a = await apoyo.SembrarComercioAsync("aislada-a", nombreFantasia: "Comercio A");
        var b = await apoyo.SembrarComercioAsync("aislada-b", nombreFantasia: "Comercio B");
        await apoyo.SembrarVitrinaAsync(a.IdTenant, a.IdEmpresa, publicada: true, tagline: "Slogan de A");
        await apoyo.SembrarVitrinaAsync(b.IdTenant, b.IdEmpresa, publicada: true, tagline: "Slogan de B");
        await apoyo.SembrarPuntoVentaAsync(a.IdTenant, a.IdEmpresa, "Local de A");
        await apoyo.SembrarPuntoVentaAsync(b.IdTenant, b.IdEmpresa, "Local de B");

        using var clienteA = apoyo.Dominios.ClienteEnHost(a.Host);
        using var clienteB = apoyo.Dominios.ClienteEnHost(b.Host);

        var cuerpoA = await (await clienteA.GetAsync(RutaVitrina)).Content.ReadAsStringAsync();
        var cuerpoB = await (await clienteB.GetAsync(RutaVitrina)).Content.ReadAsStringAsync();

        Assert.Contains("Comercio A", cuerpoA);
        Assert.Contains("Slogan de A", cuerpoA);
        Assert.Contains("Local de A", cuerpoA);
        Assert.DoesNotContain("Comercio B", cuerpoA);
        Assert.DoesNotContain("Local de B", cuerpoA);

        Assert.Contains("Comercio B", cuerpoB);
        Assert.Contains("Local de B", cuerpoB);
        Assert.DoesNotContain("Comercio A", cuerpoB);
        Assert.DoesNotContain("Local de A", cuerpoB);
    }

    [Theory]
    [InlineData("sin-vitrina")]
    [InlineData("sin-publicar")]
    [InlineData("vitrina-dada-de-baja")]
    [InlineData("empresa-dada-de-baja")]
    public async Task UnComercioSinVitrinaPublicadaViva404VitrinaNoPublicada(string caso)
    {
        var comercio = await apoyo.SembrarComercioAsync($"nopub-{caso}");

        switch (caso)
        {
            case "sin-publicar":
                await apoyo.SembrarVitrinaAsync(comercio.IdTenant, comercio.IdEmpresa, publicada: false, tagline: "Oculta");
                break;
            case "vitrina-dada-de-baja":
                await apoyo.SembrarVitrinaAsync(
                    comercio.IdTenant, comercio.IdEmpresa, publicada: true, tagline: "Baja", eliminada: true);
                break;
            case "empresa-dada-de-baja":
                var eliminada = await apoyo.SembrarEmpresaAsync(comercio.IdTenant, "Eliminada SA", eliminada: true);
                await apoyo.SembrarVitrinaAsync(comercio.IdTenant, eliminada, publicada: true, tagline: "Empresa baja");
                break;
        }

        using var cliente = apoyo.Dominios.ClienteEnHost(comercio.Host);
        var respuesta = await cliente.GetAsync(RutaVitrina);

        Assert.Equal(HttpStatusCode.NotFound, respuesta.StatusCode);
        Assert.Equal("vitrina_no_publicada", await ApoyoDeDominios.CodigoDeErrorAsync(respuesta));
        Assert.False(respuesta.Headers.CacheControl?.Public ?? false);
    }

    [Fact]
    public async Task ElHostDeLaPlataformaNoTieneVitrina404VitrinaNoPublicada()
    {
        // Aunque algún tenant tenga una vitrina publicada, el host de la plataforma no resuelve ninguno.
        var comercio = await apoyo.SembrarComercioAsync("plataforma-sin-vitrina");
        await apoyo.SembrarVitrinaAsync(comercio.IdTenant, comercio.IdEmpresa, publicada: true);

        foreach (var host in new[] { "localhost", "127.0.0.1" })
        {
            using var cliente = apoyo.Dominios.ClienteEnHost(host);

            var vitrina = await cliente.GetAsync(RutaVitrina);
            Assert.Equal(HttpStatusCode.NotFound, vitrina.StatusCode);
            Assert.Equal("vitrina_no_publicada", await ApoyoDeDominios.CodigoDeErrorAsync(vitrina));

            Assert.Equal(HttpStatusCode.NotFound, (await cliente.GetAsync(RutaLogo)).StatusCode);
        }
    }

    [Fact]
    public async Task ElHostDesconocidoSigueDandoDominioDesconocidoYNoVitrina()
    {
        using var cliente = apoyo.Dominios.ClienteEnHost(ApoyoDeDominios.HostUnico("vitrina-desconocido"));

        var respuesta = await cliente.GetAsync(RutaVitrina);

        Assert.Equal(HttpStatusCode.NotFound, respuesta.StatusCode);
        Assert.Equal("dominio_desconocido", await ApoyoDeDominios.CodigoDeErrorAsync(respuesta));
    }

    [Fact]
    public async Task UnaCookieDeSesionDeOtroTenantNoCambiaLoQueSeVeEnElHost()
    {
        var a = await apoyo.SembrarComercioAsync("cookie-a", nombreFantasia: "Cookie A");
        var b = await apoyo.SembrarComercioAsync("cookie-b", nombreFantasia: "Cookie B");
        await apoyo.SembrarVitrinaAsync(a.IdTenant, a.IdEmpresa, publicada: true);
        await apoyo.SembrarVitrinaAsync(b.IdTenant, b.IdEmpresa, publicada: true);
        var cookieDeB = await apoyo.Dominios.CookieDeSesionPorMailAsync(b.Tenant.Mail, b.Tenant.Password);

        using var cliente = apoyo.Dominios.ClienteEnHost(a.Host, cookies: false);
        var respuesta = await ApoyoDeDominios.GetConCookieAsync(cliente, RutaVitrina, cookieDeB);

        Assert.Equal(HttpStatusCode.OK, respuesta.StatusCode);
        Assert.Equal("Cookie A", (await ApoyoDeDominios.LeerJsonAsync(respuesta)).GetProperty("nombre").GetString());
    }

    // ---- el logo ----------------------------------------------------------------------------------

    [Fact]
    public async Task ElLogoSeSirveConSuTipoCacheDeUnaHoraYEtagYUnaCondicionalDa304SinCuerpo()
    {
        var comercio = await apoyo.SembrarComercioAsync("logo-etag");
        var bytes = ApoyoDeVitrinas.Imagen(Vitrina.TipoPng, largo: 300, relleno: 9);
        await apoyo.SembrarVitrinaAsync(comercio.IdTenant, comercio.IdEmpresa, publicada: true, logo: bytes);
        var guardada = (await apoyo.LeerVitrinaAsync(comercio.IdEmpresa))!;

        using var cliente = apoyo.Dominios.ClienteEnHost(comercio.Host);
        var respuesta = await cliente.GetAsync(RutaLogo);

        Assert.Equal(HttpStatusCode.OK, respuesta.StatusCode);
        Assert.Equal(bytes, await respuesta.Content.ReadAsByteArrayAsync());
        Assert.Equal("image/png", respuesta.Content.Headers.ContentType?.MediaType);
        Assert.Equal("public, max-age=3600", respuesta.Headers.CacheControl?.ToString());
        Assert.Equal("nosniff", respuesta.Headers.GetValues("X-Content-Type-Options").Single());

        var etagEsperado = $"\"{guardada.LogoActualizadoEn!.Value.UtcTicks:x}\"";
        Assert.Equal(etagEsperado, respuesta.Headers.ETag?.Tag);

        using var condicional = new HttpRequestMessage(HttpMethod.Get, RutaLogo);
        condicional.Headers.IfNoneMatch.Add(new EntityTagHeaderValue(etagEsperado));
        var noModificado = await cliente.SendAsync(condicional);

        Assert.Equal(HttpStatusCode.NotModified, noModificado.StatusCode);
        Assert.Empty(await noModificado.Content.ReadAsByteArrayAsync());
        Assert.Equal(etagEsperado, noModificado.Headers.ETag?.Tag);
        Assert.Equal("public, max-age=3600", noModificado.Headers.CacheControl?.ToString());

        using var otroEtag = new HttpRequestMessage(HttpMethod.Get, RutaLogo);
        otroEtag.Headers.IfNoneMatch.Add(new EntityTagHeaderValue("\"viejo\""));
        var distinto = await cliente.SendAsync(otroEtag);

        Assert.Equal(HttpStatusCode.OK, distinto.StatusCode);
        Assert.Equal(bytes, await distinto.Content.ReadAsByteArrayAsync());
    }

    [Fact]
    public async Task ElEtagYLaVersionSalenDeLaUltimaCargaDelLogoYCambianCuandoCambia()
    {
        var comercio = await apoyo.SembrarComercioAsync("logo-version");
        await apoyo.SembrarVitrinaAsync(
            comercio.IdTenant, comercio.IdEmpresa, publicada: true,
            logo: ApoyoDeVitrinas.Imagen(Vitrina.TipoPng, relleno: 1));
        using var cliente = apoyo.Dominios.ClienteEnHost(comercio.Host);

        var antes = (await apoyo.LeerVitrinaAsync(comercio.IdEmpresa))!.LogoActualizadoEn!.Value;
        var versionAntes = (await ApoyoDeDominios.LeerJsonAsync(await cliente.GetAsync(RutaVitrina)))
            .GetProperty("logoVersion").GetInt64();
        var etagAntes = (await cliente.GetAsync(RutaLogo)).Headers.ETag?.Tag;

        Assert.Equal(antes.ToUnixTimeSeconds(), versionAntes);
        Assert.Equal($"\"{antes.UtcTicks:x}\"", etagAntes);

        var despues = antes.AddHours(5).AddTicks(10);
        await using (var db = fixture.CrearContextoDeAplicacion(TenantActualFijo.Plataforma))
        {
            await db.Vitrinas
                .Where(v => v.IdEmpresa == comercio.IdEmpresa)
                .ExecuteUpdateAsync(s => s.SetProperty(v => v.LogoActualizadoEn, despues));
        }

        var guardadaDespues = (await apoyo.LeerVitrinaAsync(comercio.IdEmpresa))!.LogoActualizadoEn!.Value;
        var versionDespues = (await ApoyoDeDominios.LeerJsonAsync(await cliente.GetAsync(RutaVitrina)))
            .GetProperty("logoVersion").GetInt64();
        var etagDespues = (await cliente.GetAsync(RutaLogo)).Headers.ETag?.Tag;

        Assert.NotEqual(etagAntes, etagDespues);
        Assert.Equal($"\"{guardadaDespues.UtcTicks:x}\"", etagDespues);
        Assert.Equal(guardadaDespues.ToUnixTimeSeconds(), versionDespues);
        Assert.Equal(versionAntes + 5 * 3600, versionDespues);
    }

    [Fact]
    public async Task LaVitrinaConLogoLoAnunciaYSinLogoElLogoDa404()
    {
        var conLogo = await apoyo.SembrarComercioAsync("logo-anuncia");
        var sinLogo = await apoyo.SembrarComercioAsync("logo-no-anuncia");
        await apoyo.SembrarVitrinaAsync(
            conLogo.IdTenant, conLogo.IdEmpresa, publicada: true, logo: ApoyoDeVitrinas.Imagen(Vitrina.TipoPng));
        await apoyo.SembrarVitrinaAsync(sinLogo.IdTenant, sinLogo.IdEmpresa, publicada: true);

        using var clienteConLogo = apoyo.Dominios.ClienteEnHost(conLogo.Host);
        using var clienteSinLogo = apoyo.Dominios.ClienteEnHost(sinLogo.Host);

        var anuncia = await ApoyoDeDominios.LeerJsonAsync(await clienteConLogo.GetAsync(RutaVitrina));
        Assert.True(anuncia.GetProperty("tieneLogo").GetBoolean());
        Assert.NotEqual(JsonValueKind.Null, anuncia.GetProperty("logoVersion").ValueKind);

        // El logo del otro comercio nunca se sirve en este host.
        var ajeno = await clienteSinLogo.GetAsync(RutaLogo);
        Assert.Equal(HttpStatusCode.NotFound, ajeno.StatusCode);
        Assert.Equal("logo_no_encontrado", await ApoyoDeDominios.CodigoDeErrorAsync(ajeno));
    }

    [Fact]
    public async Task ElLogoDeUnaVitrinaSinPublicarNoSeSirve()
    {
        var comercio = await apoyo.SembrarComercioAsync("logo-sin-publicar");
        await apoyo.SembrarVitrinaAsync(
            comercio.IdTenant, comercio.IdEmpresa, publicada: false, logo: ApoyoDeVitrinas.Imagen(Vitrina.TipoPng));

        using var cliente = apoyo.Dominios.ClienteEnHost(comercio.Host);

        Assert.Equal(HttpStatusCode.NotFound, (await cliente.GetAsync(RutaLogo)).StatusCode);
    }
}
