using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using ClosedXML.Excel;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Ways.Application.Abstracciones;
using Ways.Application.Exportacion;
using Ways.Application.Organizacion;
using Ways.Application.Reportes;
using Ways.Application.Usuarios;
using Ways.Domain.Articulos;
using Ways.Domain.Catalogos;
using Ways.Domain.Stock;
using Ways.Domain.Usuarios;
using Ways.Infrastructure.Multitenancy;

namespace Ways.IntegrationTests;

/// <summary>
/// stage-12-lotes-vencimientos, Slice 13: <c>GET /api/reportes/stock/vencimientos/export</c> —
/// equality fila-por-fila con columnas discriminantes (mutation-proof-tests regla 6), el cap
/// (design decisión 17: LISTADO, `Contar → rechazar → .Take(tope + 1)`) y el 403 del gate. La
/// clasificación y el 403 del JSON viven en <see cref="VencimientosReporteTests"/>.
/// </summary>
[Collection("Ways.IntegrationTests secuencial")]
public class VencimientosExportTests(WaysApiFixture fixture) : IClassFixture<WaysApiFixture>
{
    private const string PasswordRoot = "root";
    private const string MailRoot = "test@test.com";
    private const string PasswordOtroRol = "otro-rol-password-larga";
    private const string ContentTypeXlsx =
        "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet";

    private static readonly JsonSerializerOptions OpcionesJson = new()
    {
        PropertyNameCaseInsensitive = true,
        Converters = { new System.Text.Json.Serialization.JsonStringEnumConverter() }
    };

    private sealed record Contexto(
        int IdTenant, int IdPuntoVenta, int IdArea, int IdAlicuotaIva,
        HttpClient Admin, HttpClient Vendedor);

    private async Task<Contexto> PrepararAsync(string nombre, WebApplicationFactory<Program>? factory = null)
    {
        var host = factory ?? fixture;
        var root = host.CreateClient();
        var loginRoot = await root.PostAsJsonAsync("/api/auth/login", new SolicitudDeLogin(MailRoot, PasswordRoot));
        Assert.Equal(HttpStatusCode.OK, loginRoot.StatusCode);

        var mailAdmin = $"{nombre.ToLowerInvariant()}@ways.test";
        var solicitud = new SolicitudDeAprovisionamiento(nombre, $"{nombre} SA", "Local 1", mailAdmin);
        var respuesta = await root.PostAsJsonAsync("/api/plataforma/tenants", solicitud);
        Assert.Equal(HttpStatusCode.Created, respuesta.StatusCode);
        var resultado = (await respuesta.Content.ReadFromJsonAsync<ResultadoAprovisionamiento>())!;

        var admin = host.CreateClient();
        var loginAdmin = await admin.PostAsJsonAsync(
            "/api/auth/login", new SolicitudDeLogin(mailAdmin, resultado.PasswordTemporal));
        Assert.Equal(HttpStatusCode.OK, loginAdmin.StatusCode);

        var vendedor = await CrearYLoguearAsync(admin, host, nombre, "vendedor", RolConocido.Vendedor);

        await using var db = fixture.CrearContextoDeAplicacion(new TenantActualFijo(ModoDeAcceso.Tenant, resultado.IdTenant));
        var ahora = DateTimeOffset.UtcNow;
        var idAlicuotaIva = await db.AlicuotasIva.Select(a => a.Id).FirstAsync();

        var area = new Area { IdTenant = resultado.IdTenant, Nombre = "Area vencimientos export", Orden = 1, CreatedAt = ahora, UpdatedAt = ahora };
        db.Areas.Add(area);
        await db.SaveChangesAsync();

        return new Contexto(resultado.IdTenant, resultado.IdPuntoVenta, area.Id, idAlicuotaIva, admin, vendedor);
    }

    private static async Task<HttpClient> CrearYLoguearAsync(
        HttpClient admin, WebApplicationFactory<Program> host, string nombre, string sufijo, RolConocido rol)
    {
        var corto = Guid.NewGuid().ToString("N")[..8];
        var mail = $"{nombre.ToLowerInvariant()}-{sufijo}@ways.test";
        var alta = await admin.PostAsJsonAsync("/api/usuarios", new CrearUsuario($"{sufijo}-{corto}", mail, (int)rol, PasswordOtroRol));
        Assert.Equal(HttpStatusCode.Created, alta.StatusCode);

        var cliente = host.CreateClient();
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

    private async Task<int> SembrarLoteAsync(
        Contexto ctx, int idArticulo, DateOnly? fechaVencimiento, decimal cantidad,
        bool esSinIdentificar = false, string? codigo = null)
    {
        await using var db = fixture.CrearContextoDeAplicacion(new TenantActualFijo(ModoDeAcceso.Tenant, ctx.IdTenant));
        var ahora = DateTimeOffset.UtcNow;

        var lote = new Lote
        {
            IdTenant = ctx.IdTenant,
            IdArticulo = idArticulo,
            Codigo = codigo ?? (esSinIdentificar ? ReglaDeLotes.CodigoSinIdentificar : fechaVencimiento!.Value.ToString("yyyy-MM-dd")),
            FechaVencimiento = fechaVencimiento,
            EsSinIdentificar = esSinIdentificar,
            CreatedAt = ahora,
            UpdatedAt = ahora
        };
        db.Lotes.Add(lote);
        await db.SaveChangesAsync();

        db.StockLotes.Add(new StockLote
        {
            IdArticulo = idArticulo, IdPuntoVenta = ctx.IdPuntoVenta, IdLote = lote.Id, IdTenant = ctx.IdTenant, Cantidad = cantidad
        });
        await db.SaveChangesAsync();

        return lote.Id;
    }

    private static Task<HttpResponseMessage> LlamarExportAsync(
        HttpClient cliente, int idPuntoVenta, string formato = "xlsx", int? dias = null) =>
        cliente.GetAsync(
            $"/api/reportes/stock/vencimientos/export?idPuntoVenta={idPuntoVenta}&formato={formato}"
            + (dias is { } valorDias ? $"&dias={valorDias}" : string.Empty));

    // ---- task 13.8: equality fila-por-fila, columnas discriminantes (mutation-proof-tests regla 6) --

    /// <summary>Nombra el objetivo de mutación (mutation-proof-tests regla 6): el call site de
    /// <c>ExportacionDeReportes.De(Vencimientos, ContextoDeExportacion)</c> dentro del endpoint
    /// <c>/stock/vencimientos/export</c>. Dos lotes con TODOS los valores distintos entre sí
    /// (artículo, nombre, id de lote, código, vencimiento — uno con fecha y uno sin ella, así la
    /// celda de fecha vacía también queda cubierta —, cantidad, estado), ambas filas comparadas
    /// contra el workbook columna por columna: un test de una sola fila o de columnas salteadas no
    /// habría detectado un <c>.Reverse()</c> ni un swap de columnas.</summary>
    [Fact]
    public async Task ElExportEsIgualAlEndpointJsonFilaPorFilaEnTodasLasColumnas()
    {
        var ctx = await PrepararAsync(nameof(ElExportEsIgualAlEndpointJsonFilaPorFilaEnTodasLasColumnas));

        var idArticuloA = await SembrarArticuloAsync(ctx, "Leche entera 1L");
        await SembrarLoteAsync(ctx, idArticuloA, new DateOnly(2026, 12, 31), 15m, codigo: "L-A");

        var idArticuloB = await SembrarArticuloAsync(ctx, "Dulce de leche 400g");
        await SembrarLoteAsync(ctx, idArticuloB, fechaVencimiento: null, 23.5m, esSinIdentificar: true);

        var jsonRespuesta = await ctx.Admin.GetAsync($"/api/reportes/stock/vencimientos?idPuntoVenta={ctx.IdPuntoVenta}");
        Assert.Equal(HttpStatusCode.OK, jsonRespuesta.StatusCode);
        var vencimientos = JsonSerializer.Deserialize<Vencimientos>(await jsonRespuesta.Content.ReadAsStringAsync(), OpcionesJson)!;
        Assert.Equal(2, vencimientos.Filas.Count);

        var exportRespuesta = await LlamarExportAsync(ctx.Admin, ctx.IdPuntoVenta);
        var cuerpoError = exportRespuesta.IsSuccessStatusCode ? string.Empty : await exportRespuesta.Content.ReadAsStringAsync();
        Assert.True(exportRespuesta.StatusCode == HttpStatusCode.OK, cuerpoError);
        Assert.Equal(ContentTypeXlsx, exportRespuesta.Content.Headers.ContentType?.MediaType);

        using var libro = new XLWorkbook(new MemoryStream(await exportRespuesta.Content.ReadAsByteArrayAsync()));
        var hoja = libro.Worksheets.First();

        // Fila 6 = título de tabla, los datos empiezan en la fila 7 — orden fecha_vencimiento ASC
        // NULLS LAST del lado del servicio, ambas filas con TODAS sus columnas comparadas.
        const int primeraFilaDeDatos = 7;
        for (var i = 0; i < vencimientos.Filas.Count; i++)
        {
            var esperado = vencimientos.Filas[i];
            var fila = hoja.Row(primeraFilaDeDatos + i);
            Assert.Equal(esperado.IdArticulo, fila.Cell(1).GetValue<int>());
            Assert.Equal(esperado.Articulo, fila.Cell(2).GetString());
            Assert.Equal(esperado.IdLote, fila.Cell(3).GetValue<int>());
            Assert.Equal(esperado.CodigoLote, fila.Cell(4).GetString());
            if (esperado.FechaVencimiento is { } fecha)
            {
                Assert.Equal(fecha.ToDateTime(TimeOnly.MinValue), fila.Cell(5).GetDateTime());
            }
            else
            {
                Assert.True(fila.Cell(5).Value.IsBlank);
            }

            Assert.Equal(esperado.Cantidad, fila.Cell(6).GetValue<decimal>());
            Assert.Equal(esperado.Estado.ToString(), fila.Cell(7).GetString());
        }

        // Sin fila de totales (design: sumar cantidades de lotes de artículos distintos no tiene
        // significado propio, mismo criterio que existencias) — la fila siguiente tiene que estar vacía.
        Assert.True(hoja.Cell(primeraFilaDeDatos + vencimientos.Filas.Count, 1).Value.IsBlank);
    }

    // ---- task 13.9: cap + backstop del +1 (design decisión 17, patrón slice 3 de la etapa 11) ------

    [Fact]
    public async Task UnaExportacionQueSuperaElTopeSeRechazaConLaCantidadReal()
    {
        using var factoryBajo = fixture.WithWebHostBuilder(builder =>
            builder.ConfigureServices(services =>
                services.Configure<OpcionesDeExportacion>(o => o.TopeDeFilas = 3)));

        var ctx = await PrepararAsync(nameof(UnaExportacionQueSuperaElTopeSeRechazaConLaCantidadReal), factoryBajo);
        var idArticulo = await SembrarArticuloAsync(ctx, "Articulo con muchos lotes");

        for (var i = 0; i < 4; i++)
        {
            await SembrarLoteAsync(ctx, idArticulo, new DateOnly(2027, 1, 1 + i), 1m, codigo: $"L-TOPE-{i}");
        }

        var respuesta = await LlamarExportAsync(ctx.Admin, ctx.IdPuntoVenta);

        Assert.Equal(HttpStatusCode.BadRequest, respuesta.StatusCode);
        Assert.NotEqual(ContentTypeXlsx, respuesta.Content.Headers.ContentType?.MediaType);

        var problema = await respuesta.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("exportacion_demasiado_grande", problema.GetProperty("codigo").GetString());
        Assert.Contains("4", problema.GetProperty("title").GetString());
    }

    // ---- task 13.10: 403 ---------------------------------------------------------------------------

    [Fact]
    public async Task UnVendedorEsRechazadoDelExportDeVencimientos()
    {
        var ctx = await PrepararAsync(nameof(UnVendedorEsRechazadoDelExportDeVencimientos));

        var respuesta = await LlamarExportAsync(ctx.Vendedor, ctx.IdPuntoVenta);

        Assert.Equal(HttpStatusCode.Forbidden, respuesta.StatusCode);
    }

    // ---- JD-FIX (judgment-day slice 13, juez B MAJOR): cobertura del override dias= --------------

    /// <summary>JD-FIX NOTE (judgment-day slice 13, juez B MAJOR): la misma rama
    /// <c>dias ?? await ResolverDiasAlertaAsync(...)</c> se repite en
    /// <c>ObtenerVencimientosParaExportacionAsync</c> — sin test tampoco. Se propaga el mismo
    /// <c>dias=45</c> explícito al JSON y al export (mismo lote a 40 días de "hoy", que con
    /// dias=45 clasifica <c>por_vencer</c>): la igualdad fila-por-fila entre ambos endpoints se
    /// mantiene con el override, evidencia de que <c>ObtenerVencimientosParaExportacionAsync</c>
    /// también respeta el parámetro y no solo el default resuelto.</summary>
    [Fact]
    public async Task ElExportPropagaElOverrideDeDiasYClasificaIgualQueElJson()
    {
        var ctx = await PrepararAsync(nameof(ElExportPropagaElOverrideDeDiasYClasificaIgualQueElJson));
        var idArticulo = await SembrarArticuloAsync(ctx, "Yogur bebible frutilla 900ml");

        // Reloj real (sin pinear): la fecha de vencimiento se ancla a "hoy real + 40 días" para no
        // depender de un reloj fijo — dias=45 explícito -> por_vencer (40 <= 45); el default (30)
        // habría dado vigente, así el test discrimina que el export usó el override, no el default.
        var hoyReal = DateOnly.FromDateTime(DateTime.UtcNow);
        var idLote = await SembrarLoteAsync(ctx, idArticulo, hoyReal.AddDays(40), cantidad: 3m, codigo: "L-EXPORT-OVERRIDE");

        var jsonRespuesta = await ctx.Admin.GetAsync(
            $"/api/reportes/stock/vencimientos?idPuntoVenta={ctx.IdPuntoVenta}&dias=45");
        Assert.Equal(HttpStatusCode.OK, jsonRespuesta.StatusCode);
        var vencimientos = JsonSerializer.Deserialize<Vencimientos>(
            await jsonRespuesta.Content.ReadAsStringAsync(), OpcionesJson)!;
        var filaJson = Assert.Single(vencimientos.Filas);
        Assert.Equal(idLote, filaJson.IdLote);
        Assert.Equal(EstadoDeVencimiento.PorVencer, filaJson.Estado);

        var exportRespuesta = await LlamarExportAsync(ctx.Admin, ctx.IdPuntoVenta, dias: 45);
        var cuerpoError = exportRespuesta.IsSuccessStatusCode ? string.Empty : await exportRespuesta.Content.ReadAsStringAsync();
        Assert.True(exportRespuesta.StatusCode == HttpStatusCode.OK, cuerpoError);

        using var libro = new XLWorkbook(new MemoryStream(await exportRespuesta.Content.ReadAsByteArrayAsync()));
        var hoja = libro.Worksheets.First();
        const int primeraFilaDeDatos = 7;
        var fila = hoja.Row(primeraFilaDeDatos);

        Assert.Equal(filaJson.IdArticulo, fila.Cell(1).GetValue<int>());
        Assert.Equal(filaJson.IdLote, fila.Cell(3).GetValue<int>());
        Assert.Equal(filaJson.Estado.ToString(), fila.Cell(7).GetString());
        Assert.True(hoja.Cell(primeraFilaDeDatos + vencimientos.Filas.Count, 1).Value.IsBlank);
    }
}
