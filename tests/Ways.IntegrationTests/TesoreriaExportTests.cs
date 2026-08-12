using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using ClosedXML.Excel;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Ways.Application.Abstracciones;
using Ways.Application.Caja;
using Ways.Application.Exportacion;
using Ways.Application.Organizacion;
using Ways.Application.Usuarios;
using Ways.Domain.Caja;
using Ways.Domain.Usuarios;
using Ways.Infrastructure.Multitenancy;

namespace Ways.IntegrationTests;

/// <summary>
/// stage-11-exportacion-reportes, Slice 7: <c>GET /api/reportes/tesoreria/export</c> — la
/// tesorería es un LISTADO (design decisión 6), así que <c>GuardaDeTope</c> corre sobre un
/// <c>COUNT(*)</c> real, mismo patrón que <c>VentasListadoExportTests</c>. La guarda de tope en sí
/// ya tiene su evidencia de mutación registrada por la Slice 1b/3 (<see cref="GuardaDeTope"/> es
/// código compartido, no una cláusula nueva de esta slice) — acá solo se ejercita que el libro de
/// tesorería la dispara con la cantidad real.
/// </summary>
[Collection("Ways.IntegrationTests secuencial")]
public class TesoreriaExportTests(WaysApiFixture fixture) : IClassFixture<WaysApiFixture>
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
        int IdTenant, int IdPuntoVenta, int IdEmpleadoAdmin, HttpClient Admin, HttpClient Vendedor);

    private static async Task<Contexto> PrepararAsync(string nombre, WebApplicationFactory<Program> factory)
    {
        var root = factory.CreateClient();
        var loginRoot = await root.PostAsJsonAsync("/api/auth/login", new SolicitudDeLogin(MailRoot, PasswordRoot));
        Assert.Equal(HttpStatusCode.OK, loginRoot.StatusCode);

        var mailAdmin = $"{nombre.ToLowerInvariant()}@ways.test";
        var solicitud = new SolicitudDeAprovisionamiento(nombre, $"{nombre} SA", "Local 1", mailAdmin);
        var respuesta = await root.PostAsJsonAsync("/api/plataforma/tenants", solicitud);
        Assert.Equal(HttpStatusCode.Created, respuesta.StatusCode);
        var resultado = (await respuesta.Content.ReadFromJsonAsync<ResultadoAprovisionamiento>())!;

        var admin = factory.CreateClient();
        var loginAdmin = await admin.PostAsJsonAsync(
            "/api/auth/login", new SolicitudDeLogin(mailAdmin, resultado.PasswordTemporal));
        Assert.Equal(HttpStatusCode.OK, loginAdmin.StatusCode);

        var vendedor = await CrearYLoguearAsync(admin, factory, nombre);

        return new Contexto(resultado.IdTenant, resultado.IdPuntoVenta, resultado.IdUsuarioAdmin, admin, vendedor);
    }

    private static async Task<HttpClient> CrearYLoguearAsync(HttpClient admin, WebApplicationFactory<Program> factory, string nombre)
    {
        var corto = Guid.NewGuid().ToString("N")[..8];
        var mail = $"{nombre.ToLowerInvariant()}-vendedor@ways.test";
        var alta = await admin.PostAsJsonAsync(
            "/api/usuarios", new CrearUsuario($"vendedor-{corto}", mail, (int)RolConocido.Vendedor, PasswordOtroRol));
        Assert.Equal(HttpStatusCode.Created, alta.StatusCode);

        var cliente = factory.CreateClient();
        var login = await cliente.PostAsJsonAsync("/api/auth/login", new SolicitudDeLogin(mail, PasswordOtroRol));
        Assert.Equal(HttpStatusCode.OK, login.StatusCode);
        return cliente;
    }

    /// <summary>Siembra directo — mismo criterio time-safe que <c>TesoreriaTests</c> (mediodía
    /// UTC, nunca <c>DateTime.UtcNow</c> puro).</summary>
    private async Task<int> SembrarMovimientoAsync(
        Contexto ctx, DateOnly dia, decimal inicio, decimal ingreso, decimal egreso, decimal final, string concepto = "Cierre de turno")
    {
        await using var db = fixture.CrearContextoDeAplicacion(new TenantActualFijo(ModoDeAcceso.Tenant, ctx.IdTenant));
        var fecha = new DateTimeOffset(dia.Year, dia.Month, dia.Day, 12, 0, 0, TimeSpan.Zero);

        var movimiento = new MovimientoTesoreria
        {
            IdTenant = ctx.IdTenant,
            IdPuntoVenta = ctx.IdPuntoVenta,
            Fecha = fecha,
            Tipo = TipoMovimientoTesoreria.RetiroCaja,
            IdTurnoCaja = null,
            Concepto = concepto,
            Inicio = inicio,
            Ingreso = ingreso,
            Egreso = egreso,
            Final = final,
            IdEmpleado = ctx.IdEmpleadoAdmin
        };
        db.MovimientosTesoreria.Add(movimiento);
        await db.SaveChangesAsync();

        return movimiento.Id;
    }

    private static string ConstruirQuery(int idPuntoVenta, DateOnly desde, DateOnly hasta, string? formato) =>
        $"idPuntoVenta={idPuntoVenta}&desde={desde:yyyy-MM-dd}T00:00:00Z&hasta={hasta:yyyy-MM-dd}T23:59:59Z" +
        (formato is null ? string.Empty : $"&formato={formato}");

    private static Task<HttpResponseMessage> LlamarLibroAsync(HttpClient cliente, int idPuntoVenta, DateOnly desde, DateOnly hasta) =>
        cliente.GetAsync($"/api/reportes/tesoreria?{ConstruirQuery(idPuntoVenta, desde, hasta, null)}&tamanio=200");

    private static Task<HttpResponseMessage> LlamarExportAsync(
        HttpClient cliente, int idPuntoVenta, DateOnly desde, DateOnly hasta, string formato = "xlsx") =>
        cliente.GetAsync($"/api/reportes/tesoreria/export?{ConstruirQuery(idPuntoVenta, desde, hasta, formato)}");

    // ---- task 7.10: equality test, por fila ----------------------------------------------------

    [Fact]
    public async Task ElExportEsIgualAlLibroJsonFilaPorFila()
    {
        var ctx = await PrepararAsync(nameof(ElExportEsIgualAlLibroJsonFilaPorFila), fixture);
        var desde = new DateOnly(2026, 8, 1);
        var hasta = new DateOnly(2026, 8, 2);

        await SembrarMovimientoAsync(ctx, desde, 0m, 60m, 0m, 60m);
        await SembrarMovimientoAsync(ctx, desde, 60m, 40m, 0m, 100m);
        await SembrarMovimientoAsync(ctx, hasta, 100m, 60m, 15m, 145m, "Cierre de turno de mediodía");

        var jsonRespuesta = await LlamarLibroAsync(ctx.Admin, ctx.IdPuntoVenta, desde, hasta);
        Assert.Equal(HttpStatusCode.OK, jsonRespuesta.StatusCode);
        var libro = JsonSerializer.Deserialize<PaginaDeMovimientosTesoreria>(await jsonRespuesta.Content.ReadAsStringAsync(), OpcionesJson)!;
        Assert.Equal(3, libro.Items.Count);

        var exportRespuesta = await LlamarExportAsync(ctx.Admin, ctx.IdPuntoVenta, desde, hasta);
        var cuerpoError = exportRespuesta.IsSuccessStatusCode ? string.Empty : await exportRespuesta.Content.ReadAsStringAsync();
        Assert.True(exportRespuesta.StatusCode == HttpStatusCode.OK, cuerpoError);
        Assert.Equal(ContentTypeXlsx, exportRespuesta.Content.Headers.ContentType?.MediaType);

        var nombreEsperado = NombreDeArchivo.Construir("tesoreria", $"pv{ctx.IdPuntoVenta}", desde, hasta);
        var disposicion = exportRespuesta.Content.Headers.ContentDisposition?.ToString() ?? string.Empty;
        Assert.Contains($"filename=\"{nombreEsperado}\"", disposicion);

        using var libroXlsx = new XLWorkbook(new MemoryStream(await exportRespuesta.Content.ReadAsByteArrayAsync()));
        var hoja = libroXlsx.Worksheets.First();

        // Fila 6 = título de tabla; los datos empiezan en la fila 7, mismo orden de cadena que el
        // libro JSON (inicio/ingreso/egreso/final/concepto/empleado/fecha, design: Slice 7 task 7.5).
        var zonaArgentina = TimeZoneInfo.FindSystemTimeZoneById("America/Argentina/Buenos_Aires");
        const int primeraFilaDeDatos = 7;
        for (var i = 0; i < libro.Items.Count; i++)
        {
            var item = libro.Items[i];
            var fila = hoja.Row(primeraFilaDeDatos + i);
            Assert.Equal(item.Inicio, fila.Cell(1).GetValue<decimal>());
            Assert.Equal(item.Ingreso, fila.Cell(2).GetValue<decimal>());
            Assert.Equal(item.Egreso, fila.Cell(3).GetValue<decimal>());
            Assert.Equal(item.Final, fila.Cell(4).GetValue<decimal>());
            Assert.Equal(item.Concepto, fila.Cell(5).GetString());
            Assert.Equal(item.IdEmpleado, fila.Cell(6).GetValue<int>());
            Assert.Equal(TimeZoneInfo.ConvertTime(item.Fecha, zonaArgentina).DateTime, fila.Cell(7).GetValue<DateTime>());
        }
    }

    // ---- cap guard test (design decisión 6: la tesorería es un LISTADO, COUNT(*) real) --------

    [Fact]
    public async Task UnaExportacionQueSuperaElTopeSeRechazaConLaCantidadReal()
    {
        using var factoryBajo = fixture.WithWebHostBuilder(builder =>
            builder.ConfigureServices(services =>
                services.Configure<OpcionesDeExportacion>(o => o.TopeDeFilas = 3)));

        var ctx = await PrepararAsync(nameof(UnaExportacionQueSuperaElTopeSeRechazaConLaCantidadReal), factoryBajo);
        var dia = new DateOnly(2026, 8, 1);

        for (var i = 0; i < 4; i++)
        {
            await SembrarMovimientoAsync(ctx, dia, 0m, 10m + i, 0m, 10m + i);
        }

        var respuesta = await LlamarExportAsync(ctx.Admin, ctx.IdPuntoVenta, dia, dia);

        Assert.Equal(HttpStatusCode.BadRequest, respuesta.StatusCode);
        Assert.NotEqual(ContentTypeXlsx, respuesta.Content.Headers.ContentType?.MediaType);

        var problema = await respuesta.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("exportacion_demasiado_grande", problema.GetProperty("codigo").GetString());
        Assert.Contains("4", problema.GetProperty("title").GetString());
    }

    [Fact]
    public async Task UnaExportacionExactamenteEnElTopeSeAcepta()
    {
        using var factoryBajo = fixture.WithWebHostBuilder(builder =>
            builder.ConfigureServices(services =>
                services.Configure<OpcionesDeExportacion>(o => o.TopeDeFilas = 3)));

        var ctx = await PrepararAsync(nameof(UnaExportacionExactamenteEnElTopeSeAcepta), factoryBajo);
        var dia = new DateOnly(2026, 8, 1);

        for (var i = 0; i < 3; i++)
        {
            await SembrarMovimientoAsync(ctx, dia, 0m, 10m + i, 0m, 10m + i);
        }

        var respuesta = await LlamarExportAsync(ctx.Admin, ctx.IdPuntoVenta, dia, dia);

        Assert.Equal(HttpStatusCode.OK, respuesta.StatusCode);
    }

    // ---- task 7.11: rol un escalón debajo del gate, mitad export -------------------------------

    [Fact]
    public async Task UnVendedorEsRechazadoDelExportDeTesoreria()
    {
        var ctx = await PrepararAsync(nameof(UnVendedorEsRechazadoDelExportDeTesoreria), fixture);
        var hoy = new DateOnly(2026, 8, 1);

        var respuesta = await LlamarExportAsync(ctx.Vendedor, ctx.IdPuntoVenta, hoy, hoy);

        Assert.Equal(HttpStatusCode.Forbidden, respuesta.StatusCode);
    }
}
