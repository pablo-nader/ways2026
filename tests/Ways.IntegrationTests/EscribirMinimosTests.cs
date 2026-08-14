using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Ways.Application.Abstracciones;
using Ways.Application.Organizacion;
using Ways.Application.Stock;
using Ways.Application.Usuarios;
using Ways.Domain.Articulos;
using Ways.Domain.Catalogos;
using Ways.Domain.Organizacion;
using Ways.Domain.Stock;
using Ways.Domain.Usuarios;
using Ways.Infrastructure.Multitenancy;

namespace Ways.IntegrationTests;

/// <summary>
/// stage-13-stock-inteligente, Slice 1 (tasks 1.13-1.17): <c>PUT /api/stock/minimos</c> punta a
/// punta — el mutation target del <c>SET</c> del upsert (1.9, spec stock: "Writing Reorder
/// Parameters..." escenario 2), el create-at-zero sin movimiento (1.13, escenario 1), la
/// operación de unmanage (1.15), los cinco códigos de refusal (1.16) y el rol (1.17 — el
/// Supervisor lee en <c>ExistenciasTests</c>/futuras slices, acá solo se confirma que NO puede
/// escribir).
/// </summary>
[Collection("Ways.IntegrationTests secuencial")]
public class EscribirMinimosTests(WaysApiFixture fixture) : IClassFixture<WaysApiFixture>
{
    private const string PasswordRoot = "root";
    private const string MailRoot = "test@test.com";
    private const string PasswordOtroRol = "otro-rol-password-larga";

    private static readonly JsonSerializerOptions OpcionesJson = new()
    {
        PropertyNameCaseInsensitive = true,
        Converters = { new System.Text.Json.Serialization.JsonStringEnumConverter() }
    };

    private sealed record Contexto(
        int IdTenant, int IdPuntoVenta, int IdArea, int IdAlicuotaIva,
        HttpClient Admin, HttpClient Supervisor, HttpClient Vendedor);

    private async Task<Contexto> PrepararAsync(string nombre)
    {
        var root = fixture.CreateClient();
        var loginRoot = await root.PostAsJsonAsync("/api/auth/login", new SolicitudDeLogin(MailRoot, PasswordRoot));
        Assert.Equal(HttpStatusCode.OK, loginRoot.StatusCode);

        var mailAdmin = $"{nombre.ToLowerInvariant()}@ways.test";
        var solicitud = new SolicitudDeAprovisionamiento(nombre, $"{nombre} SA", "Local 1", mailAdmin);
        var respuesta = await root.PostAsJsonAsync("/api/plataforma/tenants", solicitud);
        Assert.Equal(HttpStatusCode.Created, respuesta.StatusCode);
        var resultado = (await respuesta.Content.ReadFromJsonAsync<ResultadoAprovisionamiento>())!;

        var admin = fixture.CreateClient();
        var loginAdmin = await admin.PostAsJsonAsync(
            "/api/auth/login", new SolicitudDeLogin(mailAdmin, resultado.PasswordTemporal));
        Assert.Equal(HttpStatusCode.OK, loginAdmin.StatusCode);

        var supervisor = await CrearYLoguearAsync(admin, nombre, "supervisor", RolConocido.Supervisor);
        var vendedor = await CrearYLoguearAsync(admin, nombre, "vendedor", RolConocido.Vendedor);

        await using var db = fixture.CrearContextoDeAplicacion(new TenantActualFijo(ModoDeAcceso.Tenant, resultado.IdTenant));
        var ahora = DateTimeOffset.UtcNow;
        var idAlicuotaIva = await db.AlicuotasIva.Select(a => a.Id).FirstAsync();

        var area = new Area { IdTenant = resultado.IdTenant, Nombre = "Area minimos", Orden = 1, CreatedAt = ahora, UpdatedAt = ahora };
        db.Areas.Add(area);
        await db.SaveChangesAsync();

        return new Contexto(
            resultado.IdTenant, resultado.IdPuntoVenta, area.Id, idAlicuotaIva, admin, supervisor, vendedor);
    }

    private async Task<HttpClient> CrearYLoguearAsync(HttpClient admin, string nombre, string sufijo, RolConocido rol)
    {
        var corto = Guid.NewGuid().ToString("N")[..8];
        var mail = $"{nombre.ToLowerInvariant()}-{sufijo}@ways.test";
        var alta = await admin.PostAsJsonAsync("/api/usuarios", new CrearUsuario($"{sufijo}-{corto}", mail, (int)rol, PasswordOtroRol));
        Assert.Equal(HttpStatusCode.Created, alta.StatusCode);

        var cliente = fixture.CreateClient();
        var login = await cliente.PostAsJsonAsync("/api/auth/login", new SolicitudDeLogin(mail, PasswordOtroRol));
        Assert.Equal(HttpStatusCode.OK, login.StatusCode);
        return cliente;
    }

    private async Task<int> SembrarArticuloAsync(Contexto ctx, string nombre)
    {
        await using var db = fixture.CrearContextoDeAplicacion(new TenantActualFijo(ModoDeAcceso.Tenant, ctx.IdTenant));
        var ahora = DateTimeOffset.UtcNow;

        var articulo = new Articulo
        {
            IdTenant = ctx.IdTenant, CodigoInterno = $"{nombre}-{Guid.NewGuid():N}", Nombre = nombre,
            IdArea = ctx.IdArea, IdAlicuotaIva = ctx.IdAlicuotaIva, UnidadVenta = UnidadVenta.Unidad,
            EsProducto = true, CreatedAt = ahora, UpdatedAt = ahora
        };
        db.Articulos.Add(articulo);
        await db.SaveChangesAsync();
        return articulo.Id;
    }

    private async Task SembrarStockAsync(Contexto ctx, int idArticulo, decimal cantidad, decimal? minimo = null, decimal? reposicion = null)
    {
        await using var db = fixture.CrearContextoDeAplicacion(new TenantActualFijo(ModoDeAcceso.Tenant, ctx.IdTenant));
        db.Stock.Add(new Ways.Domain.Stock.Stock
        {
            IdTenant = ctx.IdTenant, IdPuntoVenta = ctx.IdPuntoVenta, IdArticulo = idArticulo, Cantidad = cantidad,
            Minimo = minimo, Reposicion = reposicion
        });
        await db.SaveChangesAsync();
    }

    private async Task<bool> ExisteFilaDeStockAsync(Contexto ctx, int idArticulo)
    {
        await using var db = fixture.CrearContextoDeAplicacion(new TenantActualFijo(ModoDeAcceso.Tenant, ctx.IdTenant));
        return await db.Stock.AnyAsync(s => s.IdArticulo == idArticulo && s.IdPuntoVenta == ctx.IdPuntoVenta);
    }

    private async Task<int> ContarMovimientosAsync(Contexto ctx, int idArticulo)
    {
        await using var db = fixture.CrearContextoDeAplicacion(new TenantActualFijo(ModoDeAcceso.Tenant, ctx.IdTenant));
        return await db.MovimientosStock.CountAsync(m => m.IdArticulo == idArticulo && m.IdPuntoVenta == ctx.IdPuntoVenta);
    }

    private static Task<HttpResponseMessage> EscribirAsync(HttpClient cliente, SolicitudDeMinimos solicitud) =>
        cliente.PutAsJsonAsync("/api/stock/minimos", solicitud);

    // ---- task 1.13 / spec stock escenario 1: crea la fila en cero, sin movimiento --------------

    [Fact]
    public async Task UnMinimoParaUnArticuloSinFilaDeStockLaCreaEnCeroSinMovimientos()
    {
        var ctx = await PrepararAsync(nameof(UnMinimoParaUnArticuloSinFilaDeStockLaCreaEnCeroSinMovimientos));
        var idArticulo = await SembrarArticuloAsync(ctx, "articulo-sin-fila");

        Assert.False(await ExisteFilaDeStockAsync(ctx, idArticulo));
        Assert.Equal(0, await ContarMovimientosAsync(ctx, idArticulo));

        var solicitud = new SolicitudDeMinimos(ctx.IdPuntoVenta, idArticulo, Minimo: 10m, Reposicion: null);
        var respuesta = await EscribirAsync(ctx.Admin, solicitud);
        var cuerpo = await respuesta.Content.ReadAsStringAsync();
        Assert.True(respuesta.StatusCode == HttpStatusCode.OK, cuerpo);

        var resultado = JsonSerializer.Deserialize<MinimosDeStock>(cuerpo, OpcionesJson)!;
        Assert.Equal(0m, resultado.Cantidad);
        Assert.Equal(10m, resultado.Minimo);
        Assert.Null(resultado.Reposicion);
        Assert.Equal(EstadoDeReposicion.Bajo, resultado.Estado);

        Assert.True(await ExisteFilaDeStockAsync(ctx, idArticulo));
        Assert.Equal(0, await ContarMovimientosAsync(ctx, idArticulo));
    }

    // ---- task 1.14 / spec stock escenario 2 / mutation target 1.9: no toca cantidad ni movimiento --

    /// <summary>Nombra la cláusula bajo prueba (mutation-proof-tests): la AUSENCIA de <c>
    /// cantidad</c> en el <c>SET</c> de <c>UpsertParametrosDeReposicionAsync</c>. Mutación
    /// aplicada (agregar <c>cantidad = EXCLUDED.cantidad</c> al SET, que con <c>VALUES (..., 0,
    /// ...)</c> pisa cualquier saldo con cero) — esta prueba pasó de FALLAR (<c>cantidad</c>
    /// resultante <c>0</c> en vez de <c>45</c>) a pasar al revertir. Evidencia registrada en el
    /// resumen de apply.</summary>
    [Fact]
    public async Task UnMinimoSobreUnaFilaExistenteNoTocaLaCantidadNiInsertaMovimientos()
    {
        var ctx = await PrepararAsync(nameof(UnMinimoSobreUnaFilaExistenteNoTocaLaCantidadNiInsertaMovimientos));
        var idArticulo = await SembrarArticuloAsync(ctx, "articulo-con-fila");
        await SembrarStockAsync(ctx, idArticulo, cantidad: 45m);

        Assert.Equal(0, await ContarMovimientosAsync(ctx, idArticulo));

        var solicitud = new SolicitudDeMinimos(ctx.IdPuntoVenta, idArticulo, Minimo: 10m, Reposicion: 60m);
        var respuesta = await EscribirAsync(ctx.Admin, solicitud);
        var cuerpo = await respuesta.Content.ReadAsStringAsync();
        Assert.True(respuesta.StatusCode == HttpStatusCode.OK, cuerpo);

        var resultado = JsonSerializer.Deserialize<MinimosDeStock>(cuerpo, OpcionesJson)!;
        Assert.Equal(45m, resultado.Cantidad);
        Assert.Equal(10m, resultado.Minimo);
        Assert.Equal(60m, resultado.Reposicion);

        Assert.Equal(0, await ContarMovimientosAsync(ctx, idArticulo));
    }

    // ---- task 1.15: ambos null limpia un par previamente seteado (unmanage) ---------------------

    [Fact]
    public async Task AmbosCamposNulosLimpianUnParPreviamenteSeteado()
    {
        var ctx = await PrepararAsync(nameof(AmbosCamposNulosLimpianUnParPreviamenteSeteado));
        var idArticulo = await SembrarArticuloAsync(ctx, "articulo-unmanage");
        await SembrarStockAsync(ctx, idArticulo, cantidad: 12m, minimo: 5m, reposicion: 20m);

        var solicitud = new SolicitudDeMinimos(ctx.IdPuntoVenta, idArticulo, Minimo: null, Reposicion: null);
        var respuesta = await EscribirAsync(ctx.Admin, solicitud);
        var cuerpo = await respuesta.Content.ReadAsStringAsync();
        Assert.True(respuesta.StatusCode == HttpStatusCode.OK, cuerpo);

        var resultado = JsonSerializer.Deserialize<MinimosDeStock>(cuerpo, OpcionesJson)!;
        Assert.Equal(12m, resultado.Cantidad);
        Assert.Null(resultado.Minimo);
        Assert.Null(resultado.Reposicion);
        Assert.Equal(EstadoDeReposicion.SinMinimo, resultado.Estado);
    }

    // ---- task 1.16: los cinco códigos de refusal, en memoria --------------------------------------

    [Fact]
    public async Task UnMinimoNegativoEsRechazado()
    {
        var ctx = await PrepararAsync(nameof(UnMinimoNegativoEsRechazado));
        var idArticulo = await SembrarArticuloAsync(ctx, "articulo-minimo-negativo");

        var respuesta = await EscribirAsync(ctx.Admin, new SolicitudDeMinimos(ctx.IdPuntoVenta, idArticulo, -1m, null));

        Assert.Equal(HttpStatusCode.BadRequest, respuesta.StatusCode);
        var problema = await respuesta.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("minimo_negativo", problema.GetProperty("codigo").GetString());
    }

    [Fact]
    public async Task UnaReposicionNegativaEsRechazadaConElMismoCodigoDeFamilia()
    {
        var ctx = await PrepararAsync(nameof(UnaReposicionNegativaEsRechazadaConElMismoCodigoDeFamilia));
        var idArticulo = await SembrarArticuloAsync(ctx, "articulo-reposicion-negativa");

        var respuesta = await EscribirAsync(ctx.Admin, new SolicitudDeMinimos(ctx.IdPuntoVenta, idArticulo, null, -5m));

        Assert.Equal(HttpStatusCode.BadRequest, respuesta.StatusCode);
        var problema = await respuesta.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("minimo_negativo", problema.GetProperty("codigo").GetString());
    }

    [Fact]
    public async Task UnaReposicionMenorQueElMinimoEsRechazada()
    {
        var ctx = await PrepararAsync(nameof(UnaReposicionMenorQueElMinimoEsRechazada));
        var idArticulo = await SembrarArticuloAsync(ctx, "articulo-reposicion-menor");

        var respuesta = await EscribirAsync(ctx.Admin, new SolicitudDeMinimos(ctx.IdPuntoVenta, idArticulo, 10m, 5m));

        Assert.Equal(HttpStatusCode.BadRequest, respuesta.StatusCode);
        var problema = await respuesta.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("reposicion_menor_que_minimo", problema.GetProperty("codigo").GetString());
    }

    [Fact]
    public async Task UnMinimoConMasDeTresDecimalesEsRechazadoNoRedondeadoEnSilencio()
    {
        var ctx = await PrepararAsync(nameof(UnMinimoConMasDeTresDecimalesEsRechazadoNoRedondeadoEnSilencio));
        var idArticulo = await SembrarArticuloAsync(ctx, "articulo-minimo-decimales");

        var respuesta = await EscribirAsync(ctx.Admin, new SolicitudDeMinimos(ctx.IdPuntoVenta, idArticulo, 10.1234m, null));

        Assert.Equal(HttpStatusCode.BadRequest, respuesta.StatusCode);
        var problema = await respuesta.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("minimo_invalido", problema.GetProperty("codigo").GetString());
        Assert.False(await ExisteFilaDeStockAsync(ctx, idArticulo));
    }

    [Fact]
    public async Task UnaReposicionConMasDeTresDecimalesTambienEsRechazada()
    {
        var ctx = await PrepararAsync(nameof(UnaReposicionConMasDeTresDecimalesTambienEsRechazada));
        var idArticulo = await SembrarArticuloAsync(ctx, "articulo-reposicion-decimales");

        var respuesta = await EscribirAsync(ctx.Admin, new SolicitudDeMinimos(ctx.IdPuntoVenta, idArticulo, null, 50.5678m));

        Assert.Equal(HttpStatusCode.BadRequest, respuesta.StatusCode);
        var problema = await respuesta.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("minimo_invalido", problema.GetProperty("codigo").GetString());
    }

    [Fact]
    public async Task UnArticuloInexistenteEsRechazadoCon400()
    {
        var ctx = await PrepararAsync(nameof(UnArticuloInexistenteEsRechazadoCon400));

        var respuesta = await EscribirAsync(ctx.Admin, new SolicitudDeMinimos(ctx.IdPuntoVenta, 999_999, 10m, null));

        Assert.Equal(HttpStatusCode.BadRequest, respuesta.StatusCode);
        var problema = await respuesta.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("referencia_invalida", problema.GetProperty("codigo").GetString());
    }

    [Fact]
    public async Task UnPuntoDeVentaInexistenteEsRechazadoCon404()
    {
        var ctx = await PrepararAsync(nameof(UnPuntoDeVentaInexistenteEsRechazadoCon404));
        var idArticulo = await SembrarArticuloAsync(ctx, "articulo-pv-inexistente");

        var respuesta = await EscribirAsync(ctx.Admin, new SolicitudDeMinimos(999_999, idArticulo, 10m, null));

        Assert.Equal(HttpStatusCode.NotFound, respuesta.StatusCode);
        var problema = await respuesta.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("no_encontrado", problema.GetProperty("codigo").GetString());
    }

    // ---- task 1.17 / mutation target 1.12: Admin-only, apilado sobre OperacionDePos --------------

    /// <summary>Nombra la cláusula bajo prueba (mutation-proof-tests): <c>
    /// .RequireAuthorization(Politicas.GestionDeCatalogo)</c> sobre <c>PUT /minimos</c> — sin
    /// ella, el grupo solo exige <c>OperacionDePos</c>, que un Supervisor SÍ tiene. Mutación
    /// aplicada (borrar la línea) — esta prueba pasó de FALLAR (<c>200</c> en vez de <c>403</c>)
    /// a pasar al revertir. Evidencia registrada en el resumen de apply.</summary>
    [Fact]
    public async Task UnSupervisorEsRechazadoDeEscribirMinimos()
    {
        var ctx = await PrepararAsync(nameof(UnSupervisorEsRechazadoDeEscribirMinimos));
        var idArticulo = await SembrarArticuloAsync(ctx, "articulo-supervisor");

        var respuesta = await EscribirAsync(ctx.Supervisor, new SolicitudDeMinimos(ctx.IdPuntoVenta, idArticulo, 10m, null));

        Assert.Equal(HttpStatusCode.Forbidden, respuesta.StatusCode);
    }

    [Fact]
    public async Task UnVendedorEsRechazadoDeEscribirMinimos()
    {
        var ctx = await PrepararAsync(nameof(UnVendedorEsRechazadoDeEscribirMinimos));
        var idArticulo = await SembrarArticuloAsync(ctx, "articulo-vendedor");

        var respuesta = await EscribirAsync(ctx.Vendedor, new SolicitudDeMinimos(ctx.IdPuntoVenta, idArticulo, 10m, null));

        Assert.Equal(HttpStatusCode.Forbidden, respuesta.StatusCode);
    }
}
