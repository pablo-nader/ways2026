using System.Data.Common;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using ClosedXML.Excel;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.DependencyInjection;
using Ways.Application.Exportacion;
using Ways.Application.Organizacion;
using Ways.Application.Usuarios;
using Ways.Domain.Usuarios;
using Ways.Infrastructure.Multitenancy;
using Ways.Infrastructure.Persistencia;

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

        // tope+2 = 5 filas (no tope+1 = 4): con solo 4 filas, count real y count leído del
        // Take(tope+1) coinciden, y borrar el PRIMER GuardaDeTope.Exigir sobrevive (el segundo
        // Exigir, sobre crudas.Count, rechaza igual con el mismo "4"). Con 5 filas el Take(4)
        // trunca: si el primer Exigir se borra, el mutante reporta "4" (el truncado) en vez de "5"
        // (la cantidad REAL) y el assert de abajo lo discrimina.
        for (var i = 0; i < 5; i++)
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
        Assert.Contains("5", problema.GetProperty("title").GetString());
    }

    // ---- judgment-day slice 6 (juez B, finding 2): FormatoDeExportacion.Parsear en esta ruta -----

    /// <summary>Complementa <c>FormatoDeExportacionTests</c> (unitaria, sin fixture) — mismo patrón
    /// que <c>ReportesVentasResumenExportTests.UnFormatoNoSoportadoRechazaConProblemDetailsAtravesDelPipelineHttp</c>
    /// (líneas 228-240): sin este test, borrar la llamada a <see cref="FormatoDeExportacion.Parsear"/>
    /// en <c>/api/auditoria/export</c> sobrevive — un <c>formato=pdf</c> devolvería 200 XLSX.</summary>
    [Fact]
    public async Task UnFormatoNoSoportadoRechazaConProblemDetailsEnElExportDeAuditoria()
    {
        var ctx = await PrepararAsync(nameof(UnFormatoNoSoportadoRechazaConProblemDetailsEnElExportDeAuditoria), fixture);
        var hoy = new DateOnly(2026, 1, 1);

        var respuesta = await LlamarExportAsync(ctx.Admin, hoy, hoy, formato: "pdf");

        Assert.Equal(HttpStatusCode.BadRequest, respuesta.StatusCode);
        Assert.NotEqual(ContentTypeXlsx, respuesta.Content.Headers.ContentType?.MediaType);

        var problema = await respuesta.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("formato_no_soportado", problema.GetProperty("codigo").GetString());
    }

    // ---- judgment-day slice 6 (juez B, finding 3): borde EXACTO del tope (200, no 400) -----------

    /// <summary>Discriminador real del SEGUNDO <c>GuardaDeTope.Exigir</c> del lado del ÉXITO: sin
    /// este test, mutar ese segundo <c>Exigir</c> a <c>Exigir(crudas.Count, tope - 1)</c> sobrevive
    /// — <c>UnaExportacionQueSuperaElTopeSeRechazaConLaCantidadRealYNoGeneraArchivo</c> solo cubre
    /// el rechazo por ARRIBA del tope. Acá se exportan EXACTAMENTE <c>tope</c> filas y se espera
    /// 200 con el workbook completo (design decisión 6: exportar exactamente el tope es
    /// legítimo).</summary>
    [Fact]
    public async Task UnaExportacionDeExactamenteElTopeDeFilasSeAceptaCompleta()
    {
        using var factoryBajo = fixture.WithWebHostBuilder(builder =>
            builder.ConfigureServices(services =>
                services.Configure<OpcionesDeExportacion>(o => o.TopeDeFilas = 3)));

        var ctx = await PrepararAsync(nameof(UnaExportacionDeExactamenteElTopeDeFilasSeAceptaCompleta), factoryBajo);
        var dia = new DateOnly(2026, 1, 1);

        for (var i = 0; i < 3; i++)
        {
            await SembrarFilaAsync(
                ctx.IdTenant, ctx.IdPuntoVenta, ctx.IdActorAdmin, "precio.cambio", "articulo", 41 + i,
                new DateTimeOffset(2026, 1, 1, 12, 0, i, TimeSpan.Zero), null, $"{{\"monto\":{100 + i}}}");
        }

        var respuesta = await LlamarExportAsync(ctx.Admin, dia, dia);
        var cuerpoError = respuesta.IsSuccessStatusCode ? string.Empty : await respuesta.Content.ReadAsStringAsync();
        Assert.True(respuesta.StatusCode == HttpStatusCode.OK, cuerpoError);
        Assert.Equal(ContentTypeXlsx, respuesta.Content.Headers.ContentType?.MediaType);

        using var libro = new XLWorkbook(new MemoryStream(await respuesta.Content.ReadAsByteArrayAsync()));
        var hoja = libro.Worksheets.First();

        // Encabezado de tabla en la fila 6 (mismo layout que ExportadorXlsx), datos desde la 7: las
        // tope=3 filas ocupan 7-9, y la fila 10 debe quedar vacía — si el mutante rechazara de más
        // (Exigir(count, tope-1)) esta sección de arriba ya devolvió 400 y el test corta antes.
        const int primeraFilaDeDatos = 7;
        for (var i = 0; i < 3; i++)
        {
            Assert.False(hoja.Row(primeraFilaDeDatos + i).IsEmpty());
        }
        Assert.True(hoja.Row(primeraFilaDeDatos + 3).IsEmpty());
    }

    // ---- task 6.5: backstop de carrera del segundo GuardaDeTope.Exigir ---------------------------

    /// <summary>
    /// Discriminador real del SEGUNDO <c>GuardaDeTope.Exigir</c> (mutation target 6.5): con un
    /// <c>COUNT(*)</c> ya consistente con el tope, la prueba de arriba (6.7) NO puede detectar
    /// borrar el segundo <c>Exigir</c> — el primero ya rechaza antes de llegar a <c>Take</c>. Este
    /// test recrea la carrera que el segundo <c>Exigir</c> existe para atrapar (mismo patrón que
    /// <c>VentasListadoExportTests.UnaFilaInsertadaEntreElConteoYLaLecturaSigueRechazandoLaExportacion</c>):
    /// un <c>DbCommandInterceptor</c> retiene la SEGUNDA consulta que toca <c>auditoria</c> (la
    /// lectura <c>.Take(tope + 1)</c> — la primera es el <c>COUNT(*)</c>) e inserta una fila extra
    /// JUSTO ANTES de dejarla correr, así el <c>COUNT(*)</c> pasa el primer <c>Exigir</c> con
    /// exactamente <c>tope</c> filas pero la lectura trae <c>tope + 1</c>.
    /// </summary>
    [Fact]
    public async Task UnaFilaInsertadaEntreElConteoYLaLecturaSigueRechazandoLaExportacion()
    {
        var gate = new SemaphoreSlim(0, 1);
        Contexto? ctxRef = null;
        DateOnly dia = default;

        var interceptor = new InterceptorDeCarreraDeExportacion(async () =>
        {
            if (ctxRef is null)
            {
                return;
            }

            await SembrarFilaAsync(
                ctxRef.IdTenant, ctxRef.IdPuntoVenta, ctxRef.IdActorAdmin, "precio.cambio", "articulo", 999,
                new DateTimeOffset(dia.Year, dia.Month, dia.Day, 12, 0, 59, TimeSpan.Zero), null, "{\"monto\":999}");
            gate.Release();
        });

        using var factoryBajo = fixture.WithWebHostBuilder(builder =>
            builder.ConfigureServices(services =>
            {
                services.Configure<OpcionesDeExportacion>(o => o.TopeDeFilas = 3);
                services.AddDbContext<WaysDbContext>((_, options) => options.AddInterceptors(interceptor));
            }));

        var ctx = await PrepararAsync(nameof(UnaFilaInsertadaEntreElConteoYLaLecturaSigueRechazandoLaExportacion), factoryBajo);
        dia = new DateOnly(2026, 1, 1);
        ctxRef = ctx;

        for (var i = 0; i < 3; i++)
        {
            await SembrarFilaAsync(
                ctx.IdTenant, ctx.IdPuntoVenta, ctx.IdActorAdmin, "precio.cambio", "articulo", 41 + i,
                new DateTimeOffset(2026, 1, 1, 12, 0, i, TimeSpan.Zero), null, $"{{\"monto\":{100 + i}}}");
        }

        var respuesta = await LlamarExportAsync(ctx.Admin, dia, dia);

        Assert.True(await gate.WaitAsync(TimeSpan.FromSeconds(10)), "El interceptor de carrera nunca insertó la fila extra.");
        Assert.Equal(HttpStatusCode.BadRequest, respuesta.StatusCode);

        var problema = await respuesta.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("exportacion_demasiado_grande", problema.GetProperty("codigo").GetString());
        Assert.Contains("4", problema.GetProperty("title").GetString());
    }

    // ---- clase repo-wide: un límite con offset local del cliente no puede tirar 500 --------------

    /// <summary>
    /// Guard de la convención <c>NormalizacionAUtc</c> de <c>WaysDbContext</c>: Npgsql rechaza
    /// escribir un <c>DateTimeOffset</c> con offset ≠ 0 contra <c>timestamptz</c>, y esa
    /// restricción alcanza a los PARÁMETROS de la consulta. Sin la convención, este listado
    /// devuelve 500 (<c>"only offset 0 (UTC) is supported"</c>) con el payload que manda la web
    /// de verdad. Se prueba sobre el listado JSON y no sobre el export a propósito: es una forma
    /// de query distinta (paginada, con <c>CountAsync</c>), y la clase alcanza a los ~13 endpoints
    /// que filtran por una columna <c>DateTimeOffset</c>, no solo a los seis exports.
    ///
    /// R3 discrimina, además, que el converter REEXPRESE el offset (<see
    /// cref="DateTimeOffset.ToUniversalTime"/>) en vez de CORRER el instante (p. ej. <c>new
    /// DateTimeOffset(v.DateTime, TimeSpan.Zero)</c>, que descarta el offset original en lugar de
    /// convertirlo): se siembra ESTRICTAMENTE entre el borde real del rango pedido
    /// (<c>desde=00:00-03:00</c> = <c>03:00Z</c>) y el borde que un converter que corre el
    /// instante produciría (<c>00:00Z</c>, tres horas antes). Con la reexpresión correcta R3 queda
    /// AFUERA del rango; un converter que corra el instante la incluiría, y las otras dos filas
    /// (a 5h/4h del borde real) no alcanzan a discriminar ese error.
    /// </summary>
    [Fact]
    public async Task ElListadoJsonAceptaLimitesConElOffsetLocalDelClienteSinRomper()
    {
        var ctx = await PrepararAsync(nameof(ElListadoJsonAceptaLimitesConElOffsetLocalDelClienteSinRomper), fixture);

        // 2026-03-10 05:00Z = 02:00-03:00 del 10/3 → DENTRO del rango pedido.
        await SembrarFilaAsync(
            ctx.IdTenant, ctx.IdPuntoVenta, ctx.IdActorAdmin, "precio.cambio", "articulo", 41,
            new DateTimeOffset(2026, 3, 10, 5, 0, 0, TimeSpan.Zero), null, "{\"monto\":100}");
        // 2026-03-09 23:00Z = 20:00-03:00 del 9/3 → FUERA (antes del 10/3 00:00-03:00 = 03:00Z).
        await SembrarFilaAsync(
            ctx.IdTenant, ctx.IdPuntoVenta, ctx.IdActorAdmin, "precio.cambio", "articulo", 42,
            new DateTimeOffset(2026, 3, 9, 23, 0, 0, TimeSpan.Zero), null, "{\"monto\":200}");
        // 2026-03-10 01:00Z: ESTRICTAMENTE entre el borde real (03:00Z) y el borde corrido por un
        // converter que descarte el offset del cliente en vez de reexpresarlo (00:00Z) — sin esta
        // fila, un converter que corre el instante 3h pasa los 8 tests de la clase.
        await SembrarFilaAsync(
            ctx.IdTenant, ctx.IdPuntoVenta, ctx.IdActorAdmin, "precio.cambio", "articulo", 43,
            new DateTimeOffset(2026, 3, 10, 1, 0, 0, TimeSpan.Zero), null, "{\"monto\":300}");

        var desde = Uri.EscapeDataString("2026-03-10T00:00:00.000-03:00");
        var hasta = Uri.EscapeDataString("2026-03-10T23:59:59.999-03:00");
        var respuesta = await ctx.Admin.GetAsync($"/api/auditoria?desde={desde}&hasta={hasta}&tamanio=50");

        var cuerpoError = respuesta.IsSuccessStatusCode ? string.Empty : await respuesta.Content.ReadAsStringAsync();
        Assert.True(respuesta.StatusCode == HttpStatusCode.OK, cuerpoError);

        var pagina = JsonSerializer.Deserialize<PaginaRespuesta>(await respuesta.Content.ReadAsStringAsync(), OpcionesJson)!;
        Assert.Equal(1, pagina.Total);
        Assert.Equal(41, pagina.Items[0].IdEntidad);
    }

    // ---- clase repo-wide: la fecha MOSTRADA sale del offset del propio límite, no de UTC ---------

    /// <summary>
    /// El cliente real (<c>fechaIsoConOffset</c> en <c>auditoria.ts</c>/<c>reportes.ts</c>) manda
    /// <c>hasta</c> como <c>...T23:59:59.999-03:00</c>, no como <c>...Z</c>: TODOS los tests de
    /// export de este repo mandaban offset cero, y por eso
    /// <c>DateOnly.FromDateTime(hasta.UtcDateTime)</c> sobrevivió en las seis rutas de export —
    /// con offset cero el bug no se manifiesta. Con el offset real de Buenos_Aires,
    /// <c>23:59:59.999-03:00</c> del 31/1 es <c>02:59:59.999Z</c> del 1/2, así que el mutante
    /// muestra "2026-02-01" tanto en el "Período" del encabezado como en el nombre de archivo,
    /// mientras el filtrado de filas (que usa el <c>DateTimeOffset</c> crudo) sigue correcto.
    /// El extremo <c>desde</c> con offset POSITIVO (que se corre hacia atrás) lo cubre
    /// <c>FechaDelRangoTests</c>, unitario y sin fixture.
    /// </summary>
    [Fact]
    public async Task ElPeriodoYElNombreDeArchivoRespetanElOffsetDelClienteYNoElDiaUtc()
    {
        var ctx = await PrepararAsync(nameof(ElPeriodoYElNombreDeArchivoRespetanElOffsetDelClienteYNoElDiaUtc), fixture);

        await SembrarFilaAsync(
            ctx.IdTenant, ctx.IdPuntoVenta, ctx.IdActorAdmin, "precio.cambio", "articulo", 41,
            new DateTimeOffset(2026, 1, 15, 12, 0, 0, TimeSpan.Zero), null, "{\"monto\":100}");

        var desde = Uri.EscapeDataString("2026-01-01T00:00:00.000-03:00");
        var hasta = Uri.EscapeDataString("2026-01-31T23:59:59.999-03:00");
        var respuesta = await ctx.Admin.GetAsync(
            $"/api/auditoria/export?desde={desde}&hasta={hasta}&formato=xlsx");

        var cuerpoError = respuesta.IsSuccessStatusCode ? string.Empty : await respuesta.Content.ReadAsStringAsync();
        Assert.True(respuesta.StatusCode == HttpStatusCode.OK, cuerpoError);

        var diaElegido = new DateOnly(2026, 1, 1);
        var ultimoDiaElegido = new DateOnly(2026, 1, 31);
        var diaUtcIncorrecto = new DateOnly(2026, 2, 1);

        var disposicion = respuesta.Content.Headers.ContentDisposition?.ToString() ?? string.Empty;
        Assert.DoesNotContain(
            NombreDeArchivo.Construir("auditoria", "todos", diaElegido, diaUtcIncorrecto), disposicion);
        Assert.Contains(
            $"filename=\"{NombreDeArchivo.Construir("auditoria", "todos", diaElegido, ultimoDiaElegido)}\"",
            disposicion);

        // "Período" en la fila 3 del bloque de encabezado (ExportadorXlsx escribe 1-4, tabla en 6).
        using var libro = new XLWorkbook(new MemoryStream(await respuesta.Content.ReadAsByteArrayAsync()));
        var periodo = libro.Worksheets.First().Cell(3, 1).GetString();
        Assert.Equal("Período: 2026-01-01 a 2026-01-31", periodo);

        // El filtrado NUNCA dependió de esta derivación: la fila del 15/1 sigue estando.
        Assert.False(libro.Worksheets.First().Row(7).IsEmpty());
    }

    /// <summary>Retiene la SEGUNDA consulta que toca <c>auditoria</c> (la lectura <c>.Take(tope +
    /// 1)</c> — la primera es el <c>COUNT(*)</c>) e inyecta <paramref name="alSegundaConsulta"/>
    /// antes de dejarla correr. Cubre tanto <c>ReaderExecutingAsync</c> como
    /// <c>ScalarExecutingAsync</c>: si <c>CountAsync</c> se traduce a un escalar en vez de un
    /// reader, el contador compartido sigue contando en orden.</summary>
    private sealed class InterceptorDeCarreraDeExportacion(Func<Task> alSegundaConsulta) : DbCommandInterceptor
    {
        private int _coincidencias;

        public override async ValueTask<InterceptionResult<DbDataReader>> ReaderExecutingAsync(
            DbCommand command, CommandEventData eventData, InterceptionResult<DbDataReader> result,
            CancellationToken cancellationToken = default)
        {
            await ConsiderarAsync(command);
            return await base.ReaderExecutingAsync(command, eventData, result, cancellationToken);
        }

        public override async ValueTask<InterceptionResult<object>> ScalarExecutingAsync(
            DbCommand command, CommandEventData eventData, InterceptionResult<object> result,
            CancellationToken cancellationToken = default)
        {
            await ConsiderarAsync(command);
            return await base.ScalarExecutingAsync(command, eventData, result, cancellationToken);
        }

        private async Task ConsiderarAsync(DbCommand command)
        {
            if (!command.CommandText.Contains("auditoria", StringComparison.OrdinalIgnoreCase))
            {
                return;
            }

            if (Interlocked.Increment(ref _coincidencias) == 2)
            {
                await alSegundaConsulta();
            }
        }
    }
}
