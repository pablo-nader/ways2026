using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using ClosedXML.Excel;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Ways.Application.Exportacion;
using Ways.Application.Organizacion;
using Ways.Application.Usuarios;
using Ways.Domain.Usuarios;
using Ways.Infrastructure.Multitenancy;

namespace Ways.IntegrationTests;

/// <summary>
/// stage-14-auditoria-trazabilidad, Slice 6: <c>GET /api/auditoria/export</c> — el sibling LISTADO
/// (design decisión 13, mismo patrón que <c>VentasListadoExportTests</c>): <c>GuardaDeTope.Exigir</c>
/// corre dos veces (sobre el <c>COUNT(*)</c> y sobre <c>.Take(tope + 1)</c>), mapeando desde la
/// MISMA <c>FilaDeAuditoria</c> que <c>GET /api/auditoria</c> (Slice 5, <c>AuditoriaConsultaTests</c>)
/// devuelve. Filas sembradas DIRECTO por <c>db.Auditoria.Add</c>, mismo criterio que Slice 5 — el
/// contenido de la fila no importa para este sibling, solo su mapeo 1:1.
/// </summary>
[Collection("Ways.IntegrationTests secuencial")]
public class AuditoriaExportTests(WaysApiFixture fixture) : IClassFixture<WaysApiFixture>
{
    private const string MailRoot = "test@test.com";
    private const string PasswordRoot = "root";
    private const string PasswordUsuario = "una-contraseña-larga";
    private const string ContentTypeXlsx =
        "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet";

    private static readonly JsonSerializerOptions OpcionesJson = new() { PropertyNameCaseInsensitive = true };

    private sealed record Contexto(int IdTenant, int IdPuntoVenta, int IdActorAdmin, HttpClient Admin);

    private sealed record FilaRespuesta(
        long IdAuditoria, DateTimeOffset CreadoEl, string Accion, string Entidad, int IdEntidad,
        int IdActor, string? Actor, int? IdPuntoVenta, JsonElement? ValorAnterior, JsonElement ValorNuevo);

    private sealed record PaginaRespuesta(List<FilaRespuesta> Items, int Total, int Pagina, int Tamanio);

    // ---- provisioning -------------------------------------------------------------------------

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

        return new Contexto(resultado.IdTenant, resultado.IdPuntoVenta, resultado.IdUsuarioAdmin, admin);
    }

    private static async Task<HttpClient> CrearYLoguearSupervisorAsync(Contexto ctx, string nombre, WebApplicationFactory<Program> factory)
    {
        var corto = Guid.NewGuid().ToString("N")[..8];
        var mail = $"{nombre.ToLowerInvariant()}-supervisor@ways.test";
        var alta = await ctx.Admin.PostAsJsonAsync(
            "/api/usuarios", new CrearUsuario($"supervisor-{corto}", mail, (int)RolConocido.Supervisor, PasswordUsuario));
        Assert.Equal(HttpStatusCode.Created, alta.StatusCode);

        var cliente = factory.CreateClient();
        var login = await cliente.PostAsJsonAsync("/api/auth/login", new SolicitudDeLogin(mail, PasswordUsuario));
        Assert.Equal(HttpStatusCode.OK, login.StatusCode);
        return cliente;
    }

    /// <summary>El id de un usuario existente de plataforma (root) — usado para probar la celda
    /// <c>"#idActor"</c> (design decisión 14: <c>Actor</c> nulo nunca es celda vacía).</summary>
    private async Task<int> ObtenerIdDeUsuarioRootAsync(WebApplicationFactory<Program> factory)
    {
        using var _ = factory.CreateClient(); // arranca el host (siembra root)

        await using var db = fixture.CrearContextoDeAplicacion(TenantActualFijo.Plataforma);
        return await db.Usuarios.Where(u => u.Mail == MailRoot).Select(u => u.Id).FirstAsync();
    }

    private async Task<long> SembrarFilaAsync(
        int idTenant, int? idPuntoVenta, int idActor, string accion, string entidad, int idEntidad,
        DateTimeOffset creadoEl, string? valorAnterior, string valorNuevo)
    {
        await using var db = fixture.CrearContextoDeAplicacion(TenantActualFijo.Plataforma);

        var fila = new Domain.Auditoria.Auditoria
        {
            IdTenant = idTenant,
            IdPuntoVenta = idPuntoVenta,
            IdActor = idActor,
            Accion = accion,
            Entidad = entidad,
            IdEntidad = idEntidad,
            ValorAnterior = valorAnterior,
            ValorNuevo = valorNuevo,
            CreadoEl = creadoEl
        };
        db.Auditoria.Add(fila);
        await db.SaveChangesAsync();

        return fila.Id;
    }

    private static string ConstruirQuery(DateOnly desde, DateOnly hasta, string? formato) =>
        $"desde={desde:yyyy-MM-dd}T00:00:00Z&hasta={hasta:yyyy-MM-dd}T23:59:59Z" +
        (formato is null ? string.Empty : $"&formato={formato}");

    private static Task<HttpResponseMessage> LlamarListadoAsync(HttpClient cliente, DateOnly desde, DateOnly hasta) =>
        cliente.GetAsync($"/api/auditoria?{ConstruirQuery(desde, hasta, null)}&tamanio=50");

    private static Task<HttpResponseMessage> LlamarExportAsync(HttpClient cliente, DateOnly desde, DateOnly hasta, string formato = "xlsx") =>
        cliente.GetAsync($"/api/auditoria/export?{ConstruirQuery(desde, hasta, formato)}");

    // ---- task 6.8: paridad JSON↔XLSX celda por celda + fila de encabezados completa ------------

    [Fact]
    public async Task ElExportEsIgualAlListadoJsonCeldaPorCeldaConLosOchoEncabezadosEnOrden()
    {
        var ctx = await PrepararAsync(nameof(ElExportEsIgualAlListadoJsonCeldaPorCeldaConLosOchoEncabezadosEnOrden), fixture);
        var idActorRoot = await ObtenerIdDeUsuarioRootAsync(fixture);

        var desde = new DateOnly(2026, 1, 1);
        var hasta = new DateOnly(2026, 3, 31);

        // R1: con PV, actor Admin (nombre visible), valorAnterior NULL.
        await SembrarFilaAsync(
            ctx.IdTenant, ctx.IdPuntoVenta, ctx.IdActorAdmin, "precio.cambio", "articulo", 41,
            new DateTimeOffset(2026, 1, 1, 12, 0, 0, TimeSpan.Zero), null, "{\"monto\":100}");
        // R2: tenant-wide (PV NULL) — celda de PV vacía; actor root — Actor null, celda "#idActor".
        await SembrarFilaAsync(
            ctx.IdTenant, null, idActorRoot, "usuario.actualizacion", "usuario", ctx.IdActorAdmin,
            new DateTimeOffset(2026, 2, 1, 12, 0, 0, TimeSpan.Zero), "{\"estado\":\"activo\"}", "{\"estado\":\"bloqueado\"}");
        // R3: con PV, actor Admin, valorAnterior/valorNuevo con contenido distinto entre sí.
        await SembrarFilaAsync(
            ctx.IdTenant, ctx.IdPuntoVenta, ctx.IdActorAdmin, "stock.ajuste", "articulo", 42,
            new DateTimeOffset(2026, 3, 1, 12, 0, 0, TimeSpan.Zero), "{\"cantidad\":5}", "{\"cantidad\":8}");

        var jsonRespuesta = await LlamarListadoAsync(ctx.Admin, desde, hasta);
        Assert.Equal(HttpStatusCode.OK, jsonRespuesta.StatusCode);
        var pagina = JsonSerializer.Deserialize<PaginaRespuesta>(await jsonRespuesta.Content.ReadAsStringAsync(), OpcionesJson)!;
        Assert.Equal(3, pagina.Items.Count);

        var exportRespuesta = await LlamarExportAsync(ctx.Admin, desde, hasta);
        var cuerpoError = exportRespuesta.IsSuccessStatusCode ? string.Empty : await exportRespuesta.Content.ReadAsStringAsync();
        Assert.True(exportRespuesta.StatusCode == HttpStatusCode.OK, cuerpoError);
        Assert.Equal(ContentTypeXlsx, exportRespuesta.Content.Headers.ContentType?.MediaType);

        using var libro = new XLWorkbook(new MemoryStream(await exportRespuesta.Content.ReadAsByteArrayAsync()));
        var hoja = libro.Worksheets.First();

        // Fila 6 = encabezados (mutation-proof-tests regla 8): sin este assert, un swap de
        // "Valor anterior"/"Valor nuevo" pasa inadvertido — el test de igualdad de abajo solo lee
        // celdas por POSICIÓN, nunca por título.
        const int filaDeEncabezados = 6;
        Assert.Equal(
            ["Fecha", "Acción", "Entidad", "Id entidad", "Actor", "Punto de venta", "Valor anterior", "Valor nuevo"],
            Enumerable.Range(1, 8).Select(c => hoja.Cell(filaDeEncabezados, c).GetString()));

        // Datos desde la fila 7, mismo orden que el JSON (newest-first, ConstruirQuery).
        var zona = TimeZoneInfo.FindSystemTimeZoneById("America/Argentina/Buenos_Aires");
        const int primeraFilaDeDatos = 7;
        for (var i = 0; i < pagina.Items.Count; i++)
        {
            var item = pagina.Items[i];
            var fila = hoja.Row(primeraFilaDeDatos + i);

            Assert.Equal(TimeZoneInfo.ConvertTime(item.CreadoEl, zona).DateTime, fila.Cell(1).GetValue<DateTime>());
            Assert.Equal(item.Accion, fila.Cell(2).GetString());
            Assert.Equal(item.Entidad, fila.Cell(3).GetString());
            Assert.Equal(item.IdEntidad, fila.Cell(4).GetValue<int>());
            Assert.Equal(item.Actor ?? $"#{item.IdActor}", fila.Cell(5).GetString());

            if (item.IdPuntoVenta is { } pv)
            {
                Assert.Equal(pv, fila.Cell(6).GetValue<int>());
            }
            else
            {
                Assert.Empty(fila.Cell(6).GetString());
            }

            Assert.Equal(JsonSerializer.Serialize(item.ValorAnterior), fila.Cell(7).GetString());
            Assert.Equal(JsonSerializer.Serialize(item.ValorNuevo), fila.Cell(8).GetString());
        }
    }

    // ---- task 6.10: autorización — Supervisor rechazado en el export también --------------------

    [Fact]
    public async Task UnSupervisorEsRechazadoDelExportDeAuditoriaSinPoliticaPropiaEnLaRuta()
    {
        var ctx = await PrepararAsync(nameof(UnSupervisorEsRechazadoDelExportDeAuditoriaSinPoliticaPropiaEnLaRuta), fixture);
        var supervisor = await CrearYLoguearSupervisorAsync(
            ctx, nameof(UnSupervisorEsRechazadoDelExportDeAuditoriaSinPoliticaPropiaEnLaRuta), fixture);

        var hoy = new DateOnly(2026, 1, 1);
        var respuesta = await LlamarExportAsync(supervisor, hoy, hoy);

        Assert.Equal(HttpStatusCode.Forbidden, respuesta.StatusCode);
    }

    // ---- task 6.7: rechazo por tope (COUNT real, no una serie truncada) ---------------------------

    [Fact]
    public async Task UnaExportacionQueSuperaElTopeSeRechazaConLaCantidadRealYNoGeneraArchivo()
    {
        using var factoryBajo = fixture.WithWebHostBuilder(builder =>
            builder.ConfigureServices(services =>
                services.Configure<OpcionesDeExportacion>(o => o.TopeDeFilas = 3)));

        var ctx = await PrepararAsync(nameof(UnaExportacionQueSuperaElTopeSeRechazaConLaCantidadRealYNoGeneraArchivo), factoryBajo);
        var dia = new DateOnly(2026, 1, 1);

        for (var i = 0; i < 4; i++)
        {
            await SembrarFilaAsync(
                ctx.IdTenant, ctx.IdPuntoVenta, ctx.IdActorAdmin, "precio.cambio", "articulo", 41 + i,
                new DateTimeOffset(2026, 1, 1, 12, 0, i, TimeSpan.Zero), null, $"{{\"monto\":{100 + i}}}");
        }

        var respuesta = await LlamarExportAsync(ctx.Admin, dia, dia);

        Assert.Equal(HttpStatusCode.BadRequest, respuesta.StatusCode);
        Assert.NotEqual(ContentTypeXlsx, respuesta.Content.Headers.ContentType?.MediaType);

        var problema = await respuesta.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("exportacion_demasiado_grande", problema.GetProperty("codigo").GetString());
        Assert.Contains("4", problema.GetProperty("title").GetString());
    }
}
