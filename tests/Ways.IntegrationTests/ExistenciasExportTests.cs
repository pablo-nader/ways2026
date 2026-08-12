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
using Ways.Domain.Usuarios;
using Ways.Infrastructure.Multitenancy;

namespace Ways.IntegrationTests;

/// <summary>
/// stage-11-exportacion-reportes, Slice 9: <c>GET /api/reportes/stock/existencias/export</c> —
/// equality fila-por-fila con DOS filas de valores distintos y TODAS las columnas comparadas
/// (mutation-proof-tests regla 6: un test de igualdad con una sola fila o con columnas salteadas
/// deja pasar mutaciones de mapeo, hallazgo repetido cinco veces en esta misma etapa), más el rol
/// un escalón debajo del gate y el nombre de archivo determinístico del spec (A Supervisor Exports
/// Existencias).
/// </summary>
[Collection("Ways.IntegrationTests secuencial")]
public class ExistenciasExportTests(WaysApiFixture fixture) : IClassFixture<WaysApiFixture>
{
    private const string PasswordRoot = "root";
    private const string MailRoot = "test@test.com";
    private const string PasswordOtroRol = "otro-rol-password-larga";
    private const string ContentTypeXlsx =
        "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet";

    private static readonly JsonSerializerOptions OpcionesJson = new() { PropertyNameCaseInsensitive = true };

    private sealed record Contexto(
        int IdTenant, int IdPuntoVenta, int IdArea, int IdAlicuotaIva,
        HttpClient Admin, HttpClient Supervisor, HttpClient Vendedor);

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

        var supervisor = await CrearYLoguearAsync(admin, host, nombre, "supervisor", RolConocido.Supervisor);
        var vendedor = await CrearYLoguearAsync(admin, host, nombre, "vendedor", RolConocido.Vendedor);

        await using var db = fixture.CrearContextoDeAplicacion(new TenantActualFijo(ModoDeAcceso.Tenant, resultado.IdTenant));
        var ahora = DateTimeOffset.UtcNow;
        var idAlicuotaIva = await db.AlicuotasIva.Select(a => a.Id).FirstAsync();

        var area = new Area { IdTenant = resultado.IdTenant, Nombre = "Area existencias export", Orden = 1, CreatedAt = ahora, UpdatedAt = ahora };
        db.Areas.Add(area);
        await db.SaveChangesAsync();

        return new Contexto(
            resultado.IdTenant, resultado.IdPuntoVenta, area.Id, idAlicuotaIva, admin, supervisor, vendedor);
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

    private async Task SembrarStockAsync(Contexto ctx, int idArticulo, decimal cantidad)
    {
        await using var db = fixture.CrearContextoDeAplicacion(new TenantActualFijo(ModoDeAcceso.Tenant, ctx.IdTenant));
        db.Stock.Add(new Ways.Domain.Stock.Stock
        {
            IdTenant = ctx.IdTenant, IdPuntoVenta = ctx.IdPuntoVenta, IdArticulo = idArticulo, Cantidad = cantidad
        });
        await db.SaveChangesAsync();
    }

    private static Task<HttpResponseMessage> LlamarExportAsync(HttpClient cliente, int idPuntoVenta, string formato = "xlsx") =>
        cliente.GetAsync($"/api/reportes/stock/existencias/export?idPuntoVenta={idPuntoVenta}&formato={formato}");

    // ---- task 9.9: equality test — 2+ filas con valores distintos, TODAS las columnas ------------

    /// <summary>Nombra el objetivo de mutación (mutation-proof-tests regla 6): el call site de
    /// <c>ExportacionDeReportes.De(Existencias, ContextoDeExportacion)</c> dentro del endpoint
    /// <c>/stock/existencias/export</c>. Dos artículos con nombre Y cantidad distintos, ambas filas
    /// comparadas contra el workbook — un test de una sola fila (el patrón que
    /// <c>ExportacionDeReportesTests</c> usa para los otros ocho mappers de la Slice 2) no habría
    /// detectado un <c>.Reverse()</c> ni un swap de columnas. Mutación aplicada
    /// (<c>.Reverse()</c> antes del <c>.Select</c> en el mapper de <c>Existencias</c>): este test
    /// pasó de FALLAR (fila 7 esperada = artículo A, fila 7 real = artículo B) a pasar al revertir
    /// — evidencia registrada en el resumen de apply.</summary>
    [Fact]
    public async Task ElExportEsIgualAlEndpointJsonParaLasDosFilas()
    {
        var ctx = await PrepararAsync(nameof(ElExportEsIgualAlEndpointJsonParaLasDosFilas));

        var idArticuloA = await SembrarArticuloAsync(ctx, "Aceite de girasol 900ml");
        await SembrarStockAsync(ctx, idArticuloA, 12m);
        var idArticuloB = await SembrarArticuloAsync(ctx, "Fideos guiseros 500g");
        await SembrarStockAsync(ctx, idArticuloB, 87.5m);

        var jsonRespuesta = await ctx.Admin.GetAsync($"/api/reportes/stock/existencias?idPuntoVenta={ctx.IdPuntoVenta}");
        Assert.Equal(HttpStatusCode.OK, jsonRespuesta.StatusCode);
        var existencias = JsonSerializer.Deserialize<Existencias>(await jsonRespuesta.Content.ReadAsStringAsync(), OpcionesJson)!;
        Assert.Equal(2, existencias.Filas.Count);

        var exportRespuesta = await LlamarExportAsync(ctx.Admin, ctx.IdPuntoVenta);
        var cuerpoError = exportRespuesta.IsSuccessStatusCode ? string.Empty : await exportRespuesta.Content.ReadAsStringAsync();
        Assert.True(exportRespuesta.StatusCode == HttpStatusCode.OK, cuerpoError);
        Assert.Equal(ContentTypeXlsx, exportRespuesta.Content.Headers.ContentType?.MediaType);

        using var libro = new XLWorkbook(new MemoryStream(await exportRespuesta.Content.ReadAsByteArrayAsync()));
        var hoja = libro.Worksheets.First();

        // Fila 6 = título de tabla, los datos empiezan en la fila 7 — EN ORDEN (OrderBy(IdArticulo)
        // del lado del servicio), ambas filas con TODAS sus columnas comparadas.
        const int primeraFilaDeDatos = 7;
        for (var i = 0; i < existencias.Filas.Count; i++)
        {
            var esperado = existencias.Filas[i];
            var fila = hoja.Row(primeraFilaDeDatos + i);
            Assert.Equal(esperado.IdArticulo, fila.Cell(1).GetValue<int>());
            Assert.Equal(esperado.Nombre, fila.Cell(2).GetString());
            Assert.Equal(esperado.Cantidad, fila.Cell(3).GetValue<decimal>());
        }

        // Sin fila de totales (design: existencias no suma cantidades de artículos distintos) — la
        // fila siguiente a la última fila de datos tiene que estar vacía.
        Assert.True(hoja.Cell(primeraFilaDeDatos + existencias.Filas.Count, 1).Value.IsBlank);
    }

    // ---- spec: A Supervisor Exports Existencias — 200 con nombre de archivo determinístico -------

    private sealed class RelojFijo(DateTimeOffset ahora) : IRelojDelSistema
    {
        public DateTimeOffset Ahora { get; } = ahora;
    }

    /// <summary>Reloj fijado a las 22:30 ART del 5/8 (01:30 UTC del 6/8): si el nombre de archivo
    /// usara el día UTC crudo en lugar del día de la zona resuelta del punto de venta, caería en
    /// el 6/8 — un día que todavía no empezó para ese punto de venta. Discrimina en cualquier
    /// horario real de ejecución, a diferencia de comparar contra <c>DateTime.UtcNow</c> (que
    /// reproduce el mismo bug que intenta probar).</summary>
    [Fact]
    public async Task UnSupervisorExportaLasExistenciasConUnNombreDeArchivoDeterministico()
    {
        using var factoryConRelojFijo = fixture.WithWebHostBuilder(builder =>
            builder.ConfigureServices(services =>
                services.AddSingleton<IRelojDelSistema>(
                    new RelojFijo(new DateTimeOffset(2026, 8, 6, 1, 30, 0, TimeSpan.Zero)))));

        var ctx = await PrepararAsync(
            nameof(UnSupervisorExportaLasExistenciasConUnNombreDeArchivoDeterministico), factoryConRelojFijo);
        var idArticulo = await SembrarArticuloAsync(ctx, "Yerba mate 1kg");
        await SembrarStockAsync(ctx, idArticulo, 5m);

        var respuesta = await LlamarExportAsync(ctx.Supervisor, ctx.IdPuntoVenta);
        var cuerpoError = respuesta.IsSuccessStatusCode ? string.Empty : await respuesta.Content.ReadAsStringAsync();
        Assert.True(respuesta.StatusCode == HttpStatusCode.OK, cuerpoError);

        var diaDelPuntoDeVenta = new DateOnly(2026, 8, 5);
        var nombreEsperado = NombreDeArchivo.Construir("existencias", $"pv{ctx.IdPuntoVenta}", diaDelPuntoDeVenta, diaDelPuntoDeVenta);
        var diaUtcIncorrecto = new DateOnly(2026, 8, 6);
        var nombreConDiaUtc = NombreDeArchivo.Construir("existencias", $"pv{ctx.IdPuntoVenta}", diaUtcIncorrecto, diaUtcIncorrecto);

        var disposicion = respuesta.Content.Headers.ContentDisposition?.ToString() ?? string.Empty;
        Assert.DoesNotContain(nombreConDiaUtc, disposicion);
        Assert.Contains($"filename=\"{nombreEsperado}\"", disposicion);
        Assert.Contains($"filename*=UTF-8''{nombreEsperado}", disposicion);
    }

    // ---- task 9.10: rol un escalón debajo del gate, mitad export ---------------------------------

    [Fact]
    public async Task UnVendedorEsRechazadoDelExportDeExistencias()
    {
        var ctx = await PrepararAsync(nameof(UnVendedorEsRechazadoDelExportDeExistencias));

        var respuesta = await LlamarExportAsync(ctx.Vendedor, ctx.IdPuntoVenta);

        Assert.Equal(HttpStatusCode.Forbidden, respuesta.StatusCode);
    }

    // ---- cap guard test — mismo patrón que TesoreriaExportTests: tope acotado vía
    // WithWebHostBuilder, cantidad real de filas en el mensaje de rechazo ---------------------------

    [Fact]
    public async Task UnaExportacionQueSuperaElTopeSeRechazaConLaCantidadReal()
    {
        using var factoryBajo = fixture.WithWebHostBuilder(builder =>
            builder.ConfigureServices(services =>
                services.Configure<OpcionesDeExportacion>(o => o.TopeDeFilas = 3)));

        var ctx = await PrepararAsync(nameof(UnaExportacionQueSuperaElTopeSeRechazaConLaCantidadReal), factoryBajo);

        for (var i = 0; i < 4; i++)
        {
            var idArticulo = await SembrarArticuloAsync(ctx, $"articulo-tope-{i}");
            await SembrarStockAsync(ctx, idArticulo, 1m);
        }

        var respuesta = await LlamarExportAsync(ctx.Admin, ctx.IdPuntoVenta);

        Assert.Equal(HttpStatusCode.BadRequest, respuesta.StatusCode);
        Assert.NotEqual(ContentTypeXlsx, respuesta.Content.Headers.ContentType?.MediaType);

        var problema = await respuesta.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("exportacion_demasiado_grande", problema.GetProperty("codigo").GetString());
        Assert.Contains("4", problema.GetProperty("title").GetString());
    }
}
