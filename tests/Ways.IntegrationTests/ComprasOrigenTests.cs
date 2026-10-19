using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Nodes;
using ClosedXML.Excel;
using Microsoft.EntityFrameworkCore;
using Ways.Application.Abstracciones;
using Ways.Application.Compras;
using Ways.Application.Organizacion;
using Ways.Application.Usuarios;
using Ways.Domain.Articulos;
using Ways.Domain.Catalogos;
using Ways.Domain.Compras;
using Ways.Domain.Organizacion;
using Ways.Domain.Proveedores;
using Ways.Domain.Usuarios;
using Ways.Infrastructure.Multitenancy;
using Ways.Infrastructure.Persistencia;
using Ways.Infrastructure.Seguridad;

namespace Ways.IntegrationTests;

/// <summary>
/// Origen de la compra (<c>comprobantes_compra.origen</c>): lo fija solo la capa de aplicación al
/// crear el borrador (<c>CrearBorradorAsync(solicitud, origen)</c>); la API REST siempre crea
/// <c>Manual</c>, el PUT no lo cambia y el listado y su export lo filtran por <c>origen</c>.
/// </summary>
[Collection("Ways.IntegrationTests secuencial")]
public class ComprasOrigenTests(WaysApiFixture fixture) : IClassFixture<WaysApiFixture>
{
    private const string PasswordRoot = "root";
    private const string MailRoot = "test@test.com";
    private const string PasswordSecundario = "password-secundaria-larga";
    private const string RolApp = "ways_app";

    private static readonly JsonSerializerOptions OpcionesJson = new()
    {
        PropertyNameCaseInsensitive = true,
        Converters = { new System.Text.Json.Serialization.JsonStringEnumConverter() }
    };

    private sealed record Contexto(
        int IdTenant, int IdPuntoVenta, int IdAdmin, HttpClient Admin, HttpClient Supervisor, HttpClient Vendedor,
        int IdProveedor, int IdArticulo, int IdAlicuotaIva21, int IdTipoCFA);

    private async Task<Contexto> PrepararAsync(string nombre)
    {
        using var root = fixture.CreateClient();
        var loginRoot = await root.PostAsJsonAsync("/api/auth/login", new SolicitudDeLogin(MailRoot, PasswordRoot));
        Assert.Equal(HttpStatusCode.OK, loginRoot.StatusCode);

        var mailAdmin = $"{nombre.ToLowerInvariant()}@ways.test";
        var solicitud = new SolicitudDeAprovisionamiento(nombre, $"{nombre} SA", "Local 1", mailAdmin, ModoPuntoVenta.Web);
        var respuesta = await root.PostAsJsonAsync("/api/plataforma/tenants", solicitud);
        Assert.Equal(HttpStatusCode.Created, respuesta.StatusCode);
        var resultado = (await respuesta.Content.ReadFromJsonAsync<ResultadoAprovisionamiento>())!;

        var admin = fixture.CreateClient();
        var loginAdmin = await admin.PostAsJsonAsync(
            "/api/auth/login", new SolicitudDeLogin(mailAdmin, resultado.PasswordTemporal));
        Assert.Equal(HttpStatusCode.OK, loginAdmin.StatusCode);

        await using var db = fixture.CrearContextoDeAplicacion(TenantActualFijo.Plataforma);
        var ahora = DateTimeOffset.UtcNow;

        var area = new Area { IdTenant = resultado.IdTenant, Nombre = "Descarte-area", Orden = 1, CreatedAt = ahora, UpdatedAt = ahora };
        db.Areas.Add(area);
        await db.SaveChangesAsync();

        var idAlicuotaIva21 = await db.AlicuotasIva.Where(a => a.Nombre == "21%").Select(a => a.Id).FirstAsync();

        var condicionFiscal = new CondicionFiscal { Codigo = $"{nombre}-CF", Nombre = nombre, CreatedAt = ahora, UpdatedAt = ahora };
        db.CondicionesFiscales.Add(condicionFiscal);
        await db.SaveChangesAsync();

        var proveedor = new Proveedor
        {
            IdTenant = resultado.IdTenant, RazonSocial = nombre, IdCondicionFiscal = condicionFiscal.Id,
            Margen = 50m, CreatedAt = ahora, UpdatedAt = ahora
        };
        db.Proveedores.Add(proveedor);
        await db.SaveChangesAsync();

        var articulo = new Articulo
        {
            IdTenant = resultado.IdTenant, CodigoInterno = $"{nombre}-1-{Guid.NewGuid():N}", Nombre = "Articulo 1",
            IdArea = area.Id, IdAlicuotaIva = idAlicuotaIva21, UnidadVenta = UnidadVenta.Unidad, EsProducto = true,
            IdProveedorHabitual = proveedor.Id, CreatedAt = ahora, UpdatedAt = ahora
        };
        db.Articulos.Add(articulo);
        await db.SaveChangesAsync();

        var idTipoCFA = await db.TiposComprobante.Where(t => t.Codigo == "C-FA").Select(t => t.Id).SingleAsync();

        var supervisor = await CrearUsuarioAsync(db, resultado.IdTenant, nombre, "supervisor", RolConocido.Supervisor, ahora);
        var vendedor = await CrearUsuarioAsync(db, resultado.IdTenant, nombre, "vendedor", RolConocido.Vendedor, ahora);

        return new Contexto(
            resultado.IdTenant, resultado.IdPuntoVenta, resultado.IdUsuarioAdmin, admin, supervisor, vendedor,
            proveedor.Id, articulo.Id, idAlicuotaIva21, idTipoCFA);
    }

    private async Task<HttpClient> CrearUsuarioAsync(
        Ways.Infrastructure.Persistencia.WaysDbContext db, int idTenant, string nombre, string sufijo, RolConocido rol,
        DateTimeOffset ahora)
    {
        var hasheador = new HasheadorPbkdf2();
        var mail = $"{nombre.ToLowerInvariant()}-{sufijo}@ways.test";
        db.Usuarios.Add(new Usuario
        {
            IdTenant = idTenant, NombreUsuario = sufijo, Mail = mail, RolId = (int)rol,
            PasswordHash = hasheador.Hashear(PasswordSecundario), PasswordAlgoritmo = hasheador.Algoritmo,
            PasswordActualizadoEl = ahora, CreatedAt = ahora, UpdatedAt = ahora
        });
        await db.SaveChangesAsync();

        var cliente = fixture.CreateClient();
        var login = await cliente.PostAsJsonAsync("/api/auth/login", new SolicitudDeLogin(mail, PasswordSecundario));
        Assert.Equal(HttpStatusCode.OK, login.StatusCode);
        return cliente;
    }

    private sealed class RelojFijo(DateTimeOffset ahora) : IRelojDelSistema
    {
        public DateTimeOffset Ahora { get; } = ahora;
    }

    private sealed class ContextoFijo(int idTenant, int usuarioId) : IContextoDeUsuario
    {
        public bool EstaAutenticado => true;
        public int UsuarioId => usuarioId;
        public string NombreUsuario => "actor-de-prueba";
        public RolConocido Rol => RolConocido.Admin;
        public int? IdTenant { get; } = idTenant;
    }

    private static ServicioDeCompras CrearServicio(WaysDbContext db, int idTenant, int idUsuario)
    {
        var reloj = new RelojFijo(DateTimeOffset.UtcNow);
        var contexto = new ContextoFijo(idTenant, idUsuario);
        return new ServicioDeCompras(
            db, reloj, contexto,
            new Ways.Application.Precios.ServicioDePrecios(db, reloj, contexto),
            new Ways.Application.Parametros.ServicioDeParametros(
                db, reloj, new Ways.Application.Stock.ServicioDeLotes(db, reloj, contexto)));
    }

    private static SolicitudDeCompra Solicitud(Contexto ctx, string numeroExterno) =>
        new(
            ctx.IdProveedor, ctx.IdTipoCFA, ctx.IdPuntoVenta, numeroExterno, DateOnly.FromDateTime(DateTime.UtcNow), null,
            [new LineaDeCompraSolicitada(ctx.IdArticulo, "Item 1", 10m, null, null, 100m, 0m, ctx.IdAlicuotaIva21, true)]);

    private async Task<CompraDetalle> CrearConOrigenAsync(Contexto ctx, string numeroExterno, OrigenCompra origen)
    {
        await using var db = fixture.CrearContextoDeAplicacion(new TenantActualFijo(ModoDeAcceso.Tenant, ctx.IdTenant));
        return await CrearServicio(db, ctx.IdTenant, ctx.IdAdmin).CrearBorradorAsync(Solicitud(ctx, numeroExterno), origen);
    }

    private static async Task<CompraDetalle> CrearPorApiAsync(Contexto ctx, string numeroExterno)
    {
        var respuesta = await ctx.Admin.PostAsJsonAsync("/api/compras", Solicitud(ctx, numeroExterno));
        var cuerpo = await respuesta.Content.ReadAsStringAsync();
        Assert.True(respuesta.StatusCode == HttpStatusCode.Created, cuerpo);
        return JsonSerializer.Deserialize<CompraDetalle>(cuerpo, OpcionesJson)!;
    }

    private static async Task<JsonElement> DetalleAsync(Contexto ctx, int id) =>
        await ctx.Admin.GetFromJsonAsync<JsonElement>($"/api/compras/{id}");

    private static async Task<List<int>> IdsDelListadoAsync(Contexto ctx, string consulta)
    {
        var listado = await ctx.Admin.GetFromJsonAsync<JsonElement>($"/api/compras?tamanio=200{consulta}");
        return listado.GetProperty("items").EnumerateArray().Select(i => i.GetProperty("id").GetInt32()).ToList();
    }

    private async Task<OrigenCompra> OrigenEnBaseAsync(int idTenant, int idCompra)
    {
        await using var db = fixture.CrearContextoDeAplicacion(new TenantActualFijo(ModoDeAcceso.Tenant, idTenant));
        return await db.ComprobantesCompra.Where(c => c.Id == idCompra).Select(c => c.Origen).SingleAsync();
    }

    [Fact]
    public async Task UnBorradorCreadoPorElAsistenteExponeSuOrigenEnElDetalleYEnElListado()
    {
        var ctx = await PrepararAsync(nameof(UnBorradorCreadoPorElAsistenteExponeSuOrigenEnElDetalleYEnElListado));
        var asistida = await CrearConOrigenAsync(ctx, "0001-00000001", OrigenCompra.Asistente);

        Assert.Equal(OrigenCompra.Asistente, asistida.Origen);
        Assert.Equal("Asistente", (await DetalleAsync(ctx, asistida.Id)).GetProperty("origen").GetString());
        Assert.Equal(OrigenCompra.Asistente, await OrigenEnBaseAsync(ctx.IdTenant, asistida.Id));

        var listado = await ctx.Admin.GetFromJsonAsync<JsonElement>("/api/compras?tamanio=200");
        var fila = listado.GetProperty("items").EnumerateArray().Single(i => i.GetProperty("id").GetInt32() == asistida.Id);
        Assert.Equal("Asistente", fila.GetProperty("origen").GetString());
    }

    [Fact]
    public async Task ElFiltroDeOrigenDelListadoSeparaLasCargasDelAsistenteDeLasManuales()
    {
        var ctx = await PrepararAsync(nameof(ElFiltroDeOrigenDelListadoSeparaLasCargasDelAsistenteDeLasManuales));
        var asistida = await CrearConOrigenAsync(ctx, "0001-00000001", OrigenCompra.Asistente);
        var manual = await CrearPorApiAsync(ctx, "0001-00000002");

        var soloAsistente = await IdsDelListadoAsync(ctx, "&origen=Asistente");
        Assert.Contains(asistida.Id, soloAsistente);
        Assert.DoesNotContain(manual.Id, soloAsistente);

        var soloManual = await IdsDelListadoAsync(ctx, "&origen=Manual");
        Assert.Contains(manual.Id, soloManual);
        Assert.DoesNotContain(asistida.Id, soloManual);

        var todas = await IdsDelListadoAsync(ctx, string.Empty);
        Assert.Contains(asistida.Id, todas);
        Assert.Contains(manual.Id, todas);
    }

    [Fact]
    public async Task ElExportDeComprasRespetaElFiltroDeOrigen()
    {
        var ctx = await PrepararAsync(nameof(ElExportDeComprasRespetaElFiltroDeOrigen));
        var asistida = await CrearConOrigenAsync(ctx, "0001-00000001", OrigenCompra.Asistente);
        var manual = await CrearPorApiAsync(ctx, "0001-00000002");
        foreach (var id in new[] { asistida.Id, manual.Id })
        {
            var confirmada = await ctx.Admin.PostAsync($"/api/compras/{id}/confirmar", null);
            Assert.Equal(HttpStatusCode.OK, confirmada.StatusCode);
        }

        var hoy = DateTime.UtcNow.Date;
        var rango = $"desde={hoy.AddDays(-1):yyyy-MM-dd}T00:00:00Z&hasta={hoy.AddDays(1):yyyy-MM-dd}T00:00:00Z&formato=xlsx";

        var respuesta = await ctx.Admin.GetAsync($"/api/compras/export?{rango}&origen=Asistente");
        Assert.Equal(HttpStatusCode.OK, respuesta.StatusCode);
        using var libro = new XLWorkbook(new MemoryStream(await respuesta.Content.ReadAsByteArrayAsync()));
        var hoja = libro.Worksheets.First();

        const int primeraFilaDeDatos = 7;
        Assert.Equal("0001-00000001", hoja.Cell(primeraFilaDeDatos, 1).GetString());
        Assert.True(hoja.Cell(primeraFilaDeDatos + 1, 1).IsEmpty());

        Assert.Equal([asistida.Id], await IdsDelListadoAsync(ctx, "&origen=Asistente"));
    }

    [Fact]
    public async Task ActualizarUnBorradorDelAsistenteConservaSuOrigen()
    {
        var ctx = await PrepararAsync(nameof(ActualizarUnBorradorDelAsistenteConservaSuOrigen));
        var asistida = await CrearConOrigenAsync(ctx, "0001-00000001", OrigenCompra.Asistente);

        var corregida = Solicitud(ctx, "0001-00000001") with { Observaciones = "Corregida contra la factura" };
        var respuesta = await ctx.Admin.PutAsJsonAsync($"/api/compras/{asistida.Id}", corregida);
        var cuerpo = await respuesta.Content.ReadAsStringAsync();
        Assert.True(respuesta.StatusCode == HttpStatusCode.OK, cuerpo);

        var actualizada = JsonSerializer.Deserialize<CompraDetalle>(cuerpo, OpcionesJson)!;
        Assert.Equal("Corregida contra la factura", actualizada.Observaciones);
        Assert.Equal(OrigenCompra.Asistente, actualizada.Origen);
        Assert.Equal(OrigenCompra.Asistente, await OrigenEnBaseAsync(ctx.IdTenant, asistida.Id));
    }

    [Fact]
    public async Task UnaCompraCreadaPorLaApiRestEsManualAunqueElCuerpoTraigaOrigen()
    {
        var ctx = await PrepararAsync(nameof(UnaCompraCreadaPorLaApiRestEsManualAunqueElCuerpoTraigaOrigen));

        foreach (var (numero, valor) in new[] { ("0001-00000001", "Asistente"), ("0001-00000002", "asistente"), ("0001-00000003", "1") })
        {
            var cuerpo = JsonSerializer.SerializeToNode(Solicitud(ctx, numero), OpcionesJson)!.AsObject();
            cuerpo["origen"] = valor;

            var respuesta = await ctx.Admin.PostAsJsonAsync("/api/compras", cuerpo);
            var texto = await respuesta.Content.ReadAsStringAsync();
            Assert.True(respuesta.StatusCode == HttpStatusCode.Created, texto);

            var creada = JsonSerializer.Deserialize<CompraDetalle>(texto, OpcionesJson)!;
            Assert.Equal(OrigenCompra.Manual, creada.Origen);
            Assert.Equal(OrigenCompra.Manual, await OrigenEnBaseAsync(ctx.IdTenant, creada.Id));
        }
    }

    [Fact]
    public async Task UnPutNoPuedeConvertirUnaCompraManualEnDelAsistente()
    {
        var ctx = await PrepararAsync(nameof(UnPutNoPuedeConvertirUnaCompraManualEnDelAsistente));
        var manual = await CrearPorApiAsync(ctx, "0001-00000001");

        var cuerpo = JsonSerializer.SerializeToNode(Solicitud(ctx, "0001-00000001"), OpcionesJson)!.AsObject();
        cuerpo["origen"] = "Asistente";
        var respuesta = await ctx.Admin.PutAsJsonAsync($"/api/compras/{manual.Id}", cuerpo);
        Assert.Equal(HttpStatusCode.OK, respuesta.StatusCode);

        Assert.Equal(OrigenCompra.Manual, await OrigenEnBaseAsync(ctx.IdTenant, manual.Id));
    }

    [Fact]
    public async Task UnOrigenInexistenteEnElFiltroDelListadoEsUnaSolicitudInvalida()
    {
        var ctx = await PrepararAsync(nameof(UnOrigenInexistenteEnElFiltroDelListadoEsUnaSolicitudInvalida));

        var respuesta = await ctx.Admin.GetAsync("/api/compras?origen=Robot");

        Assert.Equal(HttpStatusCode.BadRequest, respuesta.StatusCode);
    }
}
