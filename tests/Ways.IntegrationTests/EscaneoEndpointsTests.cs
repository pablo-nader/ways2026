using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Ways.Application.Organizacion;
using Ways.Application.Usuarios;
using Ways.Application.Ventas;
using Ways.Domain.Articulos;
using Ways.Domain.Catalogos;
using Ways.Domain.Usuarios;
using Ways.Infrastructure.Multitenancy;
using Ways.Infrastructure.Seguridad;

namespace Ways.IntegrationTests;

/// <summary>
/// stage-5-pos-ventas, Slice 2 (task 2.14, design decisión 7, spec: codigos-barra / Scan
/// Resolution Rule) — <c>GET /api/articulos/escaneo</c> punta a punta contra Postgres real:
/// código corto, código largo, prefijo <c>N*</c>, artículo inactivo, código desconocido.
///
/// Gateada con <c>Politicas.OperacionDePos</c> (Vendedor + Admin, design decisión 6) por
/// construcción del grupo <c>ArticulosEndpoints</c>, re-gateado por la Slice 1.
/// </summary>
[Collection("Ways.IntegrationTests secuencial")]
public class EscaneoEndpointsTests(WaysApiFixture fixture) : IClassFixture<WaysApiFixture>
{
    private const string PasswordRoot = "root";
    private const string MailRoot = "test@test.com";
    private const string PasswordVendedor = "una-contraseña-larga";

    private async Task<(int IdTenant, int IdArea, int IdAlicuotaIva, string MailAdmin, string PasswordAdmin)>
        AprovisionarTenantAsync(string nombre)
    {
        using var root = fixture.CreateClient();
        var loginRoot = await root.PostAsJsonAsync("/api/auth/login", new SolicitudDeLogin(MailRoot, PasswordRoot));
        Assert.Equal(HttpStatusCode.OK, loginRoot.StatusCode);

        var mailAdmin = $"{nombre.ToLowerInvariant()}@ways.test";
        var solicitud = new SolicitudDeAprovisionamiento(nombre, $"{nombre} SA", "Local 1", mailAdmin);

        var respuesta = await root.PostAsJsonAsync("/api/plataforma/tenants", solicitud);
        Assert.Equal(HttpStatusCode.Created, respuesta.StatusCode);
        var resultado = await respuesta.Content.ReadFromJsonAsync<ResultadoAprovisionamiento>();
        Assert.NotNull(resultado);

        await using var db = fixture.CrearContextoDeAplicacion(TenantActualFijo.Plataforma);
        var ahora = DateTimeOffset.UtcNow;

        var area = new Area
        {
            IdTenant = resultado!.IdTenant, Nombre = $"{nombre}-area", Orden = 1, CreatedAt = ahora, UpdatedAt = ahora
        };
        db.Areas.Add(area);
        await db.SaveChangesAsync();

        var idAlicuotaIva = await db.AlicuotasIva.Select(a => a.Id).FirstAsync();

        return (resultado.IdTenant, area.Id, idAlicuotaIva, mailAdmin, resultado.PasswordTemporal);
    }

    private async Task<HttpClient> ClienteLogueadoAsync(string mail, string password)
    {
        var cliente = fixture.CreateClient();
        var login = await cliente.PostAsJsonAsync("/api/auth/login", new SolicitudDeLogin(mail, password));
        Assert.Equal(HttpStatusCode.OK, login.StatusCode);
        return cliente;
    }

    private async Task<string> SembrarVendedorAsync(int idTenant, string nombre)
    {
        var hasheador = new HasheadorPbkdf2();
        await using var db = fixture.CrearContextoDeAplicacion(TenantActualFijo.Plataforma);
        var ahora = DateTimeOffset.UtcNow;
        var mail = $"{nombre.ToLowerInvariant()}-vendedor@ways.test";

        db.Usuarios.Add(new Usuario
        {
            IdTenant = idTenant,
            NombreUsuario = "vendedor",
            Mail = mail,
            RolId = (int)RolConocido.Vendedor,
            PasswordHash = hasheador.Hashear(PasswordVendedor),
            PasswordAlgoritmo = hasheador.Algoritmo,
            PasswordActualizadoEl = ahora,
            CreatedAt = ahora,
            UpdatedAt = ahora
        });
        await db.SaveChangesAsync();

        return mail;
    }

    private async Task<Articulo> SembrarArticuloAsync(
        int idTenant, int idArea, int idAlicuotaIva, string nombre,
        string codigoInterno, bool activo = true, string? codigoBarra = null)
    {
        await using var db = fixture.CrearContextoDeAplicacion(TenantActualFijo.Plataforma);
        var ahora = DateTimeOffset.UtcNow;

        var articulo = new Articulo
        {
            IdTenant = idTenant,
            CodigoInterno = codigoInterno,
            Nombre = nombre,
            IdArea = idArea,
            IdAlicuotaIva = idAlicuotaIva,
            UnidadVenta = UnidadVenta.Unidad,
            EsProducto = true,
            Activo = activo,
            CreatedAt = ahora,
            UpdatedAt = ahora
        };
        db.Articulos.Add(articulo);
        await db.SaveChangesAsync();

        if (codigoBarra is not null)
        {
            db.CodigosBarra.Add(new CodigoBarra
            {
                IdTenant = idTenant, IdArticulo = articulo.Id, Codigo = codigoBarra,
                CreatedAt = ahora, UpdatedAt = ahora
            });
            await db.SaveChangesAsync();
        }

        return articulo;
    }

    [Fact]
    public async Task UnCodigoCortoResuelvePorCodigoInterno()
    {
        var (idTenant, idArea, idAlicuotaIva, mailAdmin, passwordAdmin) =
            await AprovisionarTenantAsync(nameof(UnCodigoCortoResuelvePorCodigoInterno));
        await SembrarArticuloAsync(idTenant, idArea, idAlicuotaIva, "Café", "42");
        using var admin = await ClienteLogueadoAsync(mailAdmin, passwordAdmin);

        var respuesta = await admin.GetFromJsonAsync<ArticuloEscaneado>("/api/articulos/escaneo?entrada=42");

        Assert.NotNull(respuesta);
        Assert.Equal("42", respuesta!.CodigoInterno);
        Assert.Equal(1m, respuesta.Cantidad);
        Assert.Null(respuesta.CodigoBarra);
    }

    [Fact]
    public async Task UnCodigoLargoResuelvePorCodigosBarra()
    {
        var (idTenant, idArea, idAlicuotaIva, mailAdmin, passwordAdmin) =
            await AprovisionarTenantAsync(nameof(UnCodigoLargoResuelvePorCodigosBarra));
        await SembrarArticuloAsync(
            idTenant, idArea, idAlicuotaIva, "Gaseosa", "COD-1", codigoBarra: "7790001234567");
        using var admin = await ClienteLogueadoAsync(mailAdmin, passwordAdmin);

        var respuesta = await admin.GetFromJsonAsync<ArticuloEscaneado>(
            "/api/articulos/escaneo?entrada=7790001234567");

        Assert.NotNull(respuesta);
        Assert.Equal("COD-1", respuesta!.CodigoInterno);
        Assert.Equal("7790001234567", respuesta.CodigoBarra);
    }

    [Fact]
    public async Task UnPrefijoDeCantidadSePropagaEnLaRespuesta()
    {
        var (idTenant, idArea, idAlicuotaIva, mailAdmin, passwordAdmin) =
            await AprovisionarTenantAsync(nameof(UnPrefijoDeCantidadSePropagaEnLaRespuesta));
        await SembrarArticuloAsync(
            idTenant, idArea, idAlicuotaIva, "Agua", "COD-2", codigoBarra: "7790007654321");
        using var admin = await ClienteLogueadoAsync(mailAdmin, passwordAdmin);

        var respuesta = await admin.GetFromJsonAsync<ArticuloEscaneado>(
            "/api/articulos/escaneo?entrada=3*7790007654321");

        Assert.NotNull(respuesta);
        Assert.Equal(3m, respuesta!.Cantidad);
        Assert.Equal("COD-2", respuesta.CodigoInterno);
    }

    [Fact]
    public async Task UnArticuloInactivoNoResuelve()
    {
        var (idTenant, idArea, idAlicuotaIva, mailAdmin, passwordAdmin) =
            await AprovisionarTenantAsync(nameof(UnArticuloInactivoNoResuelve));
        await SembrarArticuloAsync(idTenant, idArea, idAlicuotaIva, "Descontinuado", "99", activo: false);
        using var admin = await ClienteLogueadoAsync(mailAdmin, passwordAdmin);

        var respuesta = await admin.GetAsync("/api/articulos/escaneo?entrada=99");

        Assert.Equal(HttpStatusCode.NotFound, respuesta.StatusCode);
    }

    [Fact]
    public async Task UnCodigoDesconocidoEsRechazado()
    {
        var (_, _, _, mailAdmin, passwordAdmin) =
            await AprovisionarTenantAsync(nameof(UnCodigoDesconocidoEsRechazado));
        using var admin = await ClienteLogueadoAsync(mailAdmin, passwordAdmin);

        var respuesta = await admin.GetAsync("/api/articulos/escaneo?entrada=noexiste123456789");

        Assert.Equal(HttpStatusCode.NotFound, respuesta.StatusCode);
        var problema = await respuesta.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("no_encontrado", problema.GetProperty("codigo").GetString());
    }

    /// <summary>Depende de la Slice 1 (auth policy, design decisión 6), que re-gateó el grupo
    /// de <c>ArticulosEndpoints</c> a <c>OperacionDePos</c>: un Vendedor ya puede escanear.</summary>
    [Fact]
    public async Task UnVendedorPuedeEscanear()
    {
        var (idTenant, idArea, idAlicuotaIva, _, _) =
            await AprovisionarTenantAsync(nameof(UnVendedorPuedeEscanear));
        await SembrarArticuloAsync(idTenant, idArea, idAlicuotaIva, "Café", "42");
        var mailVendedor = await SembrarVendedorAsync(idTenant, nameof(UnVendedorPuedeEscanear));
        using var vendedor = await ClienteLogueadoAsync(mailVendedor, PasswordVendedor);

        var respuesta = await vendedor.GetFromJsonAsync<ArticuloEscaneado>("/api/articulos/escaneo?entrada=42");

        Assert.NotNull(respuesta);
        Assert.Equal("42", respuesta!.CodigoInterno);
    }

    /// <summary>ADR-8: mismo 404 uniforme cross-tenant que el resto de <c>ArticulosEndpoints</c>
    /// — un artículo de otro tenant es indistinguible de uno inexistente, tanto por
    /// <c>codigo_interno</c> como por <c>codigos_barra</c>.</summary>
    [Fact]
    public async Task UnArticuloDeOtroTenantNoResuelve()
    {
        var (idTenantA, idAreaA, idAlicuotaIvaA, _, _) =
            await AprovisionarTenantAsync(nameof(UnArticuloDeOtroTenantNoResuelve) + "-A");
        await SembrarArticuloAsync(
            idTenantA, idAreaA, idAlicuotaIvaA, "Café", "77", codigoBarra: "7790009999999");

        var (_, _, _, mailAdminB, passwordAdminB) =
            await AprovisionarTenantAsync(nameof(UnArticuloDeOtroTenantNoResuelve) + "-B");
        using var adminB = await ClienteLogueadoAsync(mailAdminB, passwordAdminB);

        var respuestaCodigoInterno = await adminB.GetAsync("/api/articulos/escaneo?entrada=77");
        var respuestaCodigoBarra = await adminB.GetAsync("/api/articulos/escaneo?entrada=7790009999999");

        Assert.Equal(HttpStatusCode.NotFound, respuestaCodigoInterno.StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, respuestaCodigoBarra.StatusCode);
    }
}
