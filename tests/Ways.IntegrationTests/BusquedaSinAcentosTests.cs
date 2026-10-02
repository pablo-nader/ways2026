using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.EntityFrameworkCore;
using Ways.Application.Articulos;
using Ways.Application.Busqueda;
using Ways.Application.Clientes;
using Ways.Application.Organizacion;
using Ways.Application.Proveedores;
using Ways.Application.Usuarios;
using Ways.Domain.Articulos;
using Ways.Domain.Catalogos;
using Ways.Domain.Organizacion;
using Ways.Domain.Usuarios;
using Ways.Infrastructure.Multitenancy;

namespace Ways.IntegrationTests;

/// <summary>
/// Búsqueda de texto sin acentos ni mayúsculas (<c>sin_acentos()</c> + <c>BusquedaSinAcentos</c>) en
/// los cinco listados con búsqueda, contra Postgres real. Cada tenant siembra los mismos textos:
/// la aserción de conteo exacto prueba además que el tenant ajeno no se cuela. Nota de
/// alcance: <c>unaccent</c> mapea ñ a n, así que "nandu" encuentra "Ñandú" — es intencional y
/// los tests lo afirman por nombre.
/// </summary>
[Collection("Ways.IntegrationTests secuencial")]
public class BusquedaSinAcentosTests(WaysApiFixture fixture) : IClassFixture<WaysApiFixture>
{
    private const string PasswordRoot = "root";
    private const string MailRoot = "test@test.com";
    private const string PasswordUsuario = "una-contraseña-larga";

    private static readonly JsonSerializerOptions OpcionesJson = new()
    {
        PropertyNameCaseInsensitive = true,
        Converters = { new JsonStringEnumConverter() }
    };

    private sealed record Tenant(
        int IdTenant, int IdArea, int IdAlicuotaIva, int IdCondicionFiscal, int IdListaPrecio, HttpClient Admin);

    private async Task<Tenant> AprovisionarAsync(string nombre)
    {
        using var root = fixture.CreateClient();
        var loginRoot = await root.PostAsJsonAsync("/api/auth/login", new SolicitudDeLogin(MailRoot, PasswordRoot));
        Assert.Equal(HttpStatusCode.OK, loginRoot.StatusCode);

        var mailAdmin = $"{nombre.ToLowerInvariant()}@ways.test";
        var respuesta = await root.PostAsJsonAsync(
            "/api/plataforma/tenants",
            new SolicitudDeAprovisionamiento(nombre, $"{nombre} SA", "Local 1", mailAdmin, ModoPuntoVenta.Web));
        Assert.Equal(HttpStatusCode.Created, respuesta.StatusCode);
        var resultado = (await respuesta.Content.ReadFromJsonAsync<ResultadoAprovisionamiento>())!;

        await using var db = fixture.CrearContextoDeAplicacion(TenantActualFijo.Plataforma);
        var ahora = DateTimeOffset.UtcNow;
        var area = new Area { IdTenant = resultado.IdTenant, Nombre = $"{nombre}-area", Orden = 1, CreatedAt = ahora, UpdatedAt = ahora };
        db.Areas.Add(area);
        await db.SaveChangesAsync();

        var idAlicuotaIva = await db.AlicuotasIva.Select(a => a.Id).FirstAsync();
        var idCondicionFiscal = await db.CondicionesFiscales.Where(c => c.Codigo == "CF").Select(c => c.Id).SingleAsync();
        var idListaPrecio = await db.ListasPrecio
            .Where(l => l.IdTenant == resultado.IdTenant && l.EsDefault)
            .Select(l => l.Id)
            .SingleAsync();

        var admin = fixture.CreateClient();
        var login = await admin.PostAsJsonAsync("/api/auth/login", new SolicitudDeLogin(mailAdmin, resultado.PasswordTemporal));
        Assert.Equal(HttpStatusCode.OK, login.StatusCode);

        return new Tenant(resultado.IdTenant, area.Id, idAlicuotaIva, idCondicionFiscal, idListaPrecio, admin);
    }

    private static async Task<IReadOnlyList<T>> BuscarAsync<T>(HttpClient cliente, string ruta, string clave, string termino)
    {
        var pagina = await cliente.GetFromJsonAsync<PaginaDe<T>>(
            $"{ruta}?{clave}={Uri.EscapeDataString(termino)}&tamanio=100", OpcionesJson);
        return pagina!.Items;
    }

    // ---- clientes ------------------------------------------------------------------------------

    private static async Task SembrarClientesAsync(Tenant t, string sufijo)
    {
        foreach (var (nombre, apellido, razonSocial) in new (string, string?, string?)[]
                 {
                     ("José", "Ñandú", null),
                     ("Oferta 100% real", null, null),
                     ("Oferta 1000 real", null, null),
                     ("Cod a_b", null, null),
                     ("Cod axb", null, null),
                     (@"Ruta c\d", null, null),
                     ("Ruta cXd", null, null),
                     ("Sociedad", null, "Panadería Ángeles"),
                 })
        {
            var respuesta = await t.Admin.PostAsJsonAsync(
                "/api/clientes",
                new AltaCliente(
                    $"{nombre} {sufijo}", apellido, razonSocial, null, null, t.IdCondicionFiscal,
                    null, null, null, null, null, null, t.IdListaPrecio));
            Assert.Equal(HttpStatusCode.Created, respuesta.StatusCode);
        }
    }

    [Fact]
    public async Task ClientesBuscanSinAcentosNiMayusculasYElTenantAjenoNoSeCuela()
    {
        var a = await AprovisionarAsync("BusqClientesA");
        var b = await AprovisionarAsync("BusqClientesB");
        await SembrarClientesAsync(a, "ka");
        await SembrarClientesAsync(b, "ka");

        foreach (var termino in new[] { "jose", "José", "JOSE", "JOSÉ", "nandu", "ÑANDÚ", "jose ka ñandú" })
        {
            var items = await BuscarAsync<ClienteListado>(a.Admin, "/api/clientes", "busqueda", termino);
            var unico = Assert.Single(items);
            Assert.Equal("José ka", unico.Nombre);
        }

        var porRazonSocial = await BuscarAsync<ClienteListado>(a.Admin, "/api/clientes", "busqueda", "panaderia angeles");
        Assert.Single(porRazonSocial);
    }

    [Fact]
    public async Task ClientesTratanPorcentajeGuionBajoYBarraInvertidaComoLiterales()
    {
        var a = await AprovisionarAsync("BusqClientesLiteral");
        await SembrarClientesAsync(a, "kb");

        Assert.Equal(["Oferta 100% real kb"], (await BuscarAsync<ClienteListado>(a.Admin, "/api/clientes", "busqueda", "100%")).Select(c => c.Nombre));
        Assert.Equal(["Cod a_b kb"], (await BuscarAsync<ClienteListado>(a.Admin, "/api/clientes", "busqueda", "a_b")).Select(c => c.Nombre));
        Assert.Equal([@"Ruta c\d kb"], (await BuscarAsync<ClienteListado>(a.Admin, "/api/clientes", "busqueda", @"c\d")).Select(c => c.Nombre));
    }

    // ---- proveedores ---------------------------------------------------------------------------

    private static async Task SembrarProveedoresAsync(Tenant t, string sufijo)
    {
        foreach (var (razonSocial, fantasia) in new (string, string?)[]
                 {
                     ("José Ñandú", null),
                     ("Oferta 100% real", null),
                     ("Oferta 1000 real", null),
                     ("Cod a_b", null),
                     ("Cod axb", null),
                     (@"Ruta c\d", null),
                     ("Ruta cXd", null),
                     ("Razón común", "Fantasía Ácida"),
                 })
        {
            var respuesta = await t.Admin.PostAsJsonAsync(
                "/api/proveedores",
                new AltaProveedor(
                    $"{razonSocial} {sufijo}", fantasia, null, t.IdCondicionFiscal, null, null, null, null, null, null,
                    null, null, null));
            Assert.Equal(HttpStatusCode.Created, respuesta.StatusCode);
        }
    }

    [Fact]
    public async Task ProveedoresBuscanSinAcentosNiMayusculasYElTenantAjenoNoSeCuela()
    {
        var a = await AprovisionarAsync("BusqProveedoresA");
        var b = await AprovisionarAsync("BusqProveedoresB");
        await SembrarProveedoresAsync(a, "kc");
        await SembrarProveedoresAsync(b, "kc");

        foreach (var termino in new[] { "jose", "José", "JOSE", "JOSÉ", "nandu", "ÑANDÚ" })
        {
            var items = await BuscarAsync<ProveedorListado>(a.Admin, "/api/proveedores", "busqueda", termino);
            Assert.Equal("José Ñandú kc", Assert.Single(items).RazonSocial);
        }

        var porFantasia = await BuscarAsync<ProveedorListado>(a.Admin, "/api/proveedores", "busqueda", "FANTASIA ACIDA");
        Assert.Single(porFantasia);
    }

    [Fact]
    public async Task ProveedoresTratanPorcentajeGuionBajoYBarraInvertidaComoLiterales()
    {
        var a = await AprovisionarAsync("BusqProveedoresLiteral");
        await SembrarProveedoresAsync(a, "kd");

        Assert.Equal(["Oferta 100% real kd"], (await BuscarAsync<ProveedorListado>(a.Admin, "/api/proveedores", "busqueda", "100%")).Select(p => p.RazonSocial));
        Assert.Equal(["Cod a_b kd"], (await BuscarAsync<ProveedorListado>(a.Admin, "/api/proveedores", "busqueda", "a_b")).Select(p => p.RazonSocial));
        Assert.Equal([@"Ruta c\d kd"], (await BuscarAsync<ProveedorListado>(a.Admin, "/api/proveedores", "busqueda", @"c\d")).Select(p => p.RazonSocial));
    }

    // ---- usuarios ------------------------------------------------------------------------------

    private static async Task SembrarUsuariosAsync(Tenant t, string sufijo)
    {
        foreach (var (usuario, mail) in new[]
                 {
                     ("josé.ñandú", "u1"),
                     ("oferta100%", "u2"),
                     ("oferta1000", "u3"),
                     ("cod_a_b", "u4"),
                     ("cod-axb", "u5"),
                     (@"ruta-c\d", "u6"),
                     ("ruta-cxd", "u7"),
                 })
        {
            var respuesta = await t.Admin.PostAsJsonAsync(
                "/api/usuarios",
                new CrearUsuario(usuario, $"{mail}-{sufijo}-{t.IdTenant}@ways.test", (int)RolConocido.Vendedor, PasswordUsuario));
            Assert.Equal(HttpStatusCode.Created, respuesta.StatusCode);
        }
    }

    [Fact]
    public async Task UsuariosBuscanSinAcentosNiMayusculasYElTenantAjenoNoSeCuela()
    {
        var a = await AprovisionarAsync("BusqUsuariosA");
        var b = await AprovisionarAsync("BusqUsuariosB");
        await SembrarUsuariosAsync(a, "ke");
        await SembrarUsuariosAsync(b, "ke");

        foreach (var termino in new[] { "jose", "José", "JOSE", "JOSÉ", "nandu", "ÑANDÚ" })
        {
            var items = await BuscarAsync<UsuarioListado>(a.Admin, "/api/usuarios", "busqueda", termino);
            Assert.Equal("josé.ñandú", Assert.Single(items).Usuario);
        }
    }

    [Fact]
    public async Task UsuariosTratanPorcentajeGuionBajoYBarraInvertidaComoLiterales()
    {
        var a = await AprovisionarAsync("BusqUsuariosLiteral");
        await SembrarUsuariosAsync(a, "kf");

        Assert.Equal(["oferta100%"], (await BuscarAsync<UsuarioListado>(a.Admin, "/api/usuarios", "busqueda", "100%")).Select(u => u.Usuario));
        Assert.Equal(["cod_a_b"], (await BuscarAsync<UsuarioListado>(a.Admin, "/api/usuarios", "busqueda", "a_b")).Select(u => u.Usuario));
        Assert.Equal([@"ruta-c\d"], (await BuscarAsync<UsuarioListado>(a.Admin, "/api/usuarios", "busqueda", @"c\d")).Select(u => u.Usuario));
    }

    // ---- artículos (listado y grilla) ----------------------------------------------------------

    private async Task SembrarArticulosAsync(Tenant t, string sufijo)
    {
        await using var db = fixture.CrearContextoDeAplicacion(TenantActualFijo.Plataforma);
        var ahora = DateTimeOffset.UtcNow;

        foreach (var (nombre, codigoInterno, codigoBarra) in new (string, string, string?)[]
                 {
                     ("ÁCIDO Ñandú", $"ACI-ÑÁ-{sufijo}", $"7790001{sufijo}ÁÉ"),
                     ("Oferta 100% real", $"OF-100-{sufijo}", null),
                     ("Oferta 1000 real", $"OF-1000-{sufijo}", null),
                     ("Cod a_b", $"CAB-{sufijo}", null),
                     ("Cod axb", $"CXB-{sufijo}", null),
                     (@"Ruta c\d", $"RUT-1-{sufijo}", null),
                     ("Ruta cXd", $"RUT-2-{sufijo}", null),
                 })
        {
            var articulo = new Articulo
            {
                IdTenant = t.IdTenant,
                CodigoInterno = codigoInterno,
                Nombre = $"{nombre} {sufijo}",
                IdArea = t.IdArea,
                IdAlicuotaIva = t.IdAlicuotaIva,
                UnidadVenta = UnidadVenta.Unidad,
                EsProducto = true,
                CreatedAt = ahora,
                UpdatedAt = ahora
            };
            db.Articulos.Add(articulo);
            await db.SaveChangesAsync();

            if (codigoBarra is not null)
            {
                db.CodigosBarra.Add(new CodigoBarra
                {
                    IdTenant = t.IdTenant, IdArticulo = articulo.Id, Codigo = codigoBarra, CreatedAt = ahora, UpdatedAt = ahora
                });
                await db.SaveChangesAsync();
            }
        }
    }

    [Fact]
    public async Task ArticulosBuscanPorNombreCodigoInternoYCodigoDeBarraSinAcentosNiMayusculasYElTenantAjenoNoSeCuela()
    {
        var a = await AprovisionarAsync("BusqArticulosA");
        var b = await AprovisionarAsync("BusqArticulosB");
        await SembrarArticulosAsync(a, "kg");
        await SembrarArticulosAsync(b, "kg");

        foreach (var termino in new[] { "acido", "ácido", "ACIDO", "ÁCIDO", "nandu", "aci-na", "ACI-ÑÁ", "kgae" })
        {
            var items = await BuscarAsync<ArticuloListado>(a.Admin, "/api/articulos", "busqueda", termino);
            Assert.Equal("ÁCIDO Ñandú kg", Assert.Single(items).Nombre);
        }
    }

    [Fact]
    public async Task ArticulosTratanPorcentajeGuionBajoYBarraInvertidaComoLiterales()
    {
        var a = await AprovisionarAsync("BusqArticulosLiteral");
        await SembrarArticulosAsync(a, "kh");

        Assert.Equal(["Oferta 100% real kh"], (await BuscarAsync<ArticuloListado>(a.Admin, "/api/articulos", "busqueda", "100%")).Select(x => x.Nombre));
        Assert.Equal(["Cod a_b kh"], (await BuscarAsync<ArticuloListado>(a.Admin, "/api/articulos", "busqueda", "a_b")).Select(x => x.Nombre));
        Assert.Equal([@"Ruta c\d kh"], (await BuscarAsync<ArticuloListado>(a.Admin, "/api/articulos", "busqueda", @"c\d")).Select(x => x.Nombre));
    }

    [Fact]
    public async Task LaGrillaDeArticulosFiltraNombreYCodigoSinAcentosNiMayusculasYElTenantAjenoNoSeCuela()
    {
        var a = await AprovisionarAsync("BusqGrillaA");
        var b = await AprovisionarAsync("BusqGrillaB");
        await SembrarArticulosAsync(a, "ki");
        await SembrarArticulosAsync(b, "ki");

        foreach (var termino in new[] { "acido", "ácido", "ACIDO", "ÁCIDO", "nandu" })
        {
            var items = await BuscarAsync<ArticuloGrillaFila>(a.Admin, "/api/articulos/grilla", "nombre", termino);
            Assert.Equal("ÁCIDO Ñandú ki", Assert.Single(items).Nombre);
        }

        foreach (var termino in new[] { "aci-na", "ACI-ÑÁ", "kiae" })
        {
            var items = await BuscarAsync<ArticuloGrillaFila>(a.Admin, "/api/articulos/grilla", "codigo", termino);
            Assert.Equal("ÁCIDO Ñandú ki", Assert.Single(items).Nombre);
        }
    }

    [Fact]
    public async Task LaGrillaDeArticulosTrataPorcentajeGuionBajoYBarraInvertidaComoLiterales()
    {
        var a = await AprovisionarAsync("BusqGrillaLiteral");
        await SembrarArticulosAsync(a, "kj");

        Assert.Equal(["Oferta 100% real kj"], (await BuscarAsync<ArticuloGrillaFila>(a.Admin, "/api/articulos/grilla", "nombre", "100%")).Select(x => x.Nombre));
        Assert.Equal(["Cod a_b kj"], (await BuscarAsync<ArticuloGrillaFila>(a.Admin, "/api/articulos/grilla", "nombre", "a_b")).Select(x => x.Nombre));
        Assert.Equal([@"Ruta c\d kj"], (await BuscarAsync<ArticuloGrillaFila>(a.Admin, "/api/articulos/grilla", "nombre", @"c\d")).Select(x => x.Nombre));
        Assert.Equal(["Cod a_b kj"], (await BuscarAsync<ArticuloGrillaFila>(a.Admin, "/api/articulos/grilla", "codigo", "CAB-k")).Select(x => x.Nombre));
        Assert.Equal(["Cod axb kj"], (await BuscarAsync<ArticuloGrillaFila>(a.Admin, "/api/articulos/grilla", "codigo", "CXB-")).Select(x => x.Nombre));
    }

    [Fact]
    public async Task LaConsultaGeneraSinAcentosSobreLaColumnaYElPatronConEscape()
    {
        await using var db = fixture.CrearContextoDeAplicacion(TenantActualFijo.Plataforma);
        var patron = BusquedaSinAcentos.PatronDeContiene("José");

        var sql = db.Clientes.Where(c => BusquedaSinAcentos.Coincide(c.Nombre, patron)).ToQueryString();

        Assert.Contains("lower(public.sin_acentos(c.nombre)) LIKE lower(public.sin_acentos(@patron)) ESCAPE '\\'", sql);
    }
}
